using System.Collections.Concurrent;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Text.Json;
using System.Threading.Channels;
using Diagnostic.Service.Common;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using log4net;
using Microsoft.Extensions.Options;

namespace Diagnostic.Service.Hubs;

public class RetroManager : IHostedService
{
    private static readonly ILog _log = LogManager.GetLogger(typeof(RetroManager));

    private readonly ConcurrentDictionary<string, RetroSearchProcess> _searches = new();

    // Keep this reusable across starts/stops. No AvailableWaitHandle is used; disposing the
    // gate while another StopAsync is waiting would introduce a second teardown race.
    private readonly SemaphoreSlim _stopGate = new(1, 1);
    private ISubject<IList<DiagnosticMsg>>? _logSubject;
    private IDisposable? _logSubscription;
    private IRetroLogger? _logger;
    private Task? _loggingTask;
    private Subject<IList<DiagnosticMsg>>? _ownedLogSubject;
    private Channel<IList<DiagnosticMsg>>? _writeChannel;
    private long _writeQueueSize;

    public RetroManager(IOptions<DiagServiceSettings> config)
    {
        Options = config.Value;
    }

    public EventSink RetroEvents { get; } = EventSinkRepo.Default.GetSink("Retro Events", "Retro");

    // _logger and _writeChannel are only populated once StartAsync has run (and nulled out by
    // StopAsync). Every consumer below runs while the hosted service is started, so accessing them
    // before/after that is a programming error — surface it as a clear InvalidOperationException
    // rather than a bare NullReferenceException.
    private IRetroLogger Logger => _logger ?? throw new InvalidOperationException("RetroManager has not been started");

    private Channel<IList<DiagnosticMsg>> WriteChannel =>
        _writeChannel ?? throw new InvalidOperationException("RetroManager has not been started");

    public long WriteQueueSize => _writeQueueSize;
    public int ItemsInQueue => WriteChannel.Reader.CanCount ? WriteChannel.Reader.Count : -1;

    [ExtendedProperty]
    public DiagServiceSettings Options { get; set; }

    [RateProperty(ExposeTotal = false, ExposeRate = true)]
    public RateCounter EventsQueued { get; set; } = new(3);

    [RateProperty(ExposeTotal = false, ExposeRate = true)]
    public RateCounter EventsWritten { get; set; } = new(3);

    /// <summary>
    ///     Whether the active backend supports interactive per-record delete. Surfaced to the web
    ///     client (via WebHub.RetroSupportsDelete) so the UI can hide the delete affordance for
    ///     append-only backends such as Log Analytics.
    /// </summary>
    public bool SupportsDelete => Logger.SupportsDelete;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        DiagnosticManager.Register(this, "Retro Manager", "Retro");

        _writeQueueSize = 0;

        // 10k batches is a real backlog cap (1_000_000 batches x up-to-50 msgs was effectively
        // unbounded, so the drop mode never engaged and memory was uncapped during a logger
        // outage). FullMode.Wait, not DropWrite: a full Wait channel makes TryWrite return false,
        // so the subscription below only counts batches that were actually queued. DropWrite also
        // discards the incoming batch when full, but TryWrite still returns true for it — which
        // leaked WriteQueueSize upward on every dropped batch, since only the reader's finally
        // in TryLog ever decrements.
        _writeChannel = Channel.CreateBounded<IList<DiagnosticMsg>>(
            new BoundedChannelOptions(10_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait }
        );

        _ownedLogSubject = new Subject<IList<DiagnosticMsg>>();
        _logSubject = Subject.Synchronize(_ownedLogSubject);

        // Keep the subscription so StopAsync can dispose it (was discarded → leaked across restarts).
        _logSubscription = _logSubject
            .SelectMany(list => list)
            .Buffer(TimeSpan.FromSeconds(1), 50)
            .Where(evts => evts.Count != 0)
            .Subscribe(evts =>
            {
                if (_writeChannel.Writer.TryWrite(evts))
                {
                    Interlocked.Add(ref _writeQueueSize, evts.Count);
                }
            });

        _logger = Options.CreateRetroLogger();

        // Do NOT pass ApplicationStopping here: ApplicationStopping fires before StopAsync is
        // invoked, so the drain loop would exit early and drop queued messages. Instead, let
        // the loop run until the channel writer completes (signalled in StopAsync), with the
        // 5-second Task.WhenAny in StopAsync as the hard-cap.
        _loggingTask = Task.Run(() => RunLoop(CancellationToken.None), CancellationToken.None);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        // Stop accepting input, then let the drain loop flush what's already queued before the
        // host tears the process down. Previously StopAsync returned immediately without awaiting
        // the drain or disposing the Rx subscription, so queued messages were lost on recycle.
        Task? loggingTask;
        await _stopGate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            _logSubject?.OnCompleted();
            _logSubject = null;
            _logSubscription?.Dispose();
            _logSubscription = null;
            _ownedLogSubject?.Dispose();
            _ownedLogSubject = null;

            _writeChannel?.Writer.TryComplete();
            loggingTask = _loggingTask;
        }
        finally
        {
            _stopGate.Release();
        }

        // Every caller still awaits the drain under its original shutdown budget, including
        // callers which arrived after another stop had already completed subject teardown.
        if (loggingTask != null)
        {
            await Task.WhenAny(loggingTask, Task.Delay(TimeSpan.FromSeconds(5), cancellationToken));
        }
    }

    private async Task RunLoop(CancellationToken cancel)
    {
        try
        {
            await foreach (var messages in WriteChannel.Reader.ReadAllAsync(cancel))
            {
                try
                {
                    await TryLog(messages, cancel);
                }
                catch (OperationCanceledException) when (cancel.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    _log.Error("Uncaught exception in retro-logging worker loop", ex);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected shutdown
        }
    }

    private async Task TryLog(IList<DiagnosticMsg> messages, CancellationToken cancel)
    {
        try
        {
            for (var i = 0; i < 10; i++)
            {
                try
                {
                    await Logger.WriteMessages(messages, cancel);
                    EventsWritten.Register(messages.Count);
                    return;
                }
                catch (Exception ex) when (!cancel.IsCancellationRequested)
                {
                    _log.Error(ex);
                    await Task.Delay(TimeSpan.FromSeconds(1), cancel);
                }
            }
        }
        finally
        {
            // Decrement once whether the batch was written or abandoned after the retries: the count
            // was incremented on enqueue, so a permanently-failing logger would otherwise leak
            // WriteQueueSize upward forever (it never recovers after a sustained outage). (A8)
            Interlocked.Add(ref _writeQueueSize, -1 * messages.Count);
        }
    }

    public IAsyncEnumerable<RetroMsg[]> GetRetroLog(RetroQuery query, CancellationToken cancel)
    {
        return Logger.GetMessages(query, cancel);
    }

    public void LogEvents(IList<DiagnosticMsg> messages)
    {
        var logSubject = _logSubject;

        if (logSubject != null)
        {
            logSubject.OnNext(messages);
            // Do NOT increment _writeQueueSize here: the Rx subscription increments it when the
            // batch is actually written to the channel, and TryWrite returns false when the
            // channel is full, so a dropped batch is never counted. Counting it here too
            // double-counted the backlog and never reversed on a dropped write.
            EventsQueued.Register(messages.Count);
        }
    }

    public Task StartRetroSearch(RetroQuery query, string connectionId, IWebHubClient client)
    {
        RetroEvents.Info(
            $"Retro search starting for connection {connectionId}",
            JsonSerializer.SerializeToElement(query).ToString()
        );

        RetroSearchProcess search = new(this, connectionId, client, query);
        search.Finished += HandleSearchFinished;

        RetroSearchProcess? displaced = null;
        _searches.AddOrUpdate(
            connectionId,
            _ => search,
            (_, old) =>
            {
                displaced = old;
                return search;
            }
        );

        displaced?.Cancel();

        search.Start();
        return Task.CompletedTask;
    }

    public Task<long> RetroDelete(string[] idList)
    {
        RetroEvents.Info($"Retro delete starting {idList.Length} messages");

        return Logger.Delete(idList);
    }

    private void HandleSearchFinished(object? sender, EventArgs e)
    {
        var search = (RetroSearchProcess)sender!;
        // Remove the finished search so completed searches don't linger in the registry until the
        // connection starts another or disconnects. Pair-remove so a newer search that already
        // replaced this entry is left intact. (A3)
        if (_searches.TryRemove(new KeyValuePair<string, RetroSearchProcess>(search.ClientId, search)))
        {
            search.Dispose();
        }

        RetroEvents.Info(
            $"Retro search complete for connection {search.ClientId} in {search.SearchTime.TotalSeconds:N2}s",
            JsonSerializer.SerializeToElement(search.Query).ToString()
        );
    }

    public Task CancelConnectionSearch(string connectionId)
    {
        // Called on client disconnect: cancel and drop any in-flight retro search for the connection
        // so a disconnected client's search isn't left running or lingering in the registry. (A3)
        if (_searches.TryRemove(connectionId, out var running))
        {
            running.Cancel();
        }

        return Task.CompletedTask;
    }

    public Task CancelRetroSearch(int searchId, string connectionId)
    {
        if (_searches.TryGetValue(connectionId, out var running) && running.Query.SearchId == searchId)
        {
            running.Cancel();
            _searches.TryRemove(new KeyValuePair<string, RetroSearchProcess>(connectionId, running));
        }

        return Task.CompletedTask;
    }
}
