using System.Diagnostics;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;
using log4net;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;

namespace DiagnosticExplorer;

public class RegistrationHandler
{
    private static readonly ILog _log = LogManager.GetLogger(typeof(RegistrationHandler));

    private readonly string _url;
    private readonly Registration _registration;
    private readonly string? _apiKey;
    private readonly IServiceProvider? _serviceProvider;

    // _connLock guards the _connection/_hubAdapter pair. They are mutated from three racing
    // contexts — the registration loop (OpenHub/CloseConnection), the SignalR Closed event
    // (HandleClosed) and Stop — so each teardown captures-and-nulls the pair atomically to
    // guarantee a given connection/adapter is disposed exactly once.
    private readonly object _connLock = new();
    private HubServerAdapter? _hubAdapter;
    private HubConnection? _connection;

    private CancellationTokenSource? _stopToken;
    private Task? _registrationLoop;
    private Task? _loggingTask;
    private readonly Subject<DiagnosticMsg> _ownedLogSubject = new();
    private ISubject<DiagnosticMsg>? _logSubject;
    private IDisposable? _logSubscription;
    private Channel<IList<DiagnosticMsg>>? _logChannel;

    private Action<HttpConnectionOptions>? _configureHttp;

    public RegistrationHandler(string url, Registration registration, string? apiKey = null)
        : this(url, registration, apiKey, null) { }

    public RegistrationHandler(string url, Registration registration, string? apiKey, IServiceProvider? serviceProvider)
    {
        _url = url;
        _registration = registration;
        _apiKey = apiKey;
        _serviceProvider = serviceProvider;
        _logSubject = Subject.Synchronize(_ownedLogSubject);

        // F8: never send the API key over a cleartext transport. Fail fast at construction rather
        // than silently leaking it on http/ws negotiate + WebSocket requests.
        if (!string.IsNullOrEmpty(_apiKey) && !IsSecureUrl(_url))
        {
            throw new ArgumentException(
                $"An API key is configured but the diagnostic hub URL '{_url}' is not https/wss — the key would be transmitted in cleartext. Use a TLS URL or clear the API key.",
                nameof(url)
            );
        }
    }

    private static bool IsSecureUrl(string url)
    {
        return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri)
            && (
                string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.Equals(uri.Scheme, "wss", StringComparison.OrdinalIgnoreCase)
            );
    }

    public void Start(Action<HttpConnectionOptions>? configureHttp = null)
    {
        _configureHttp = configureHttp;
        _stopToken = new CancellationTokenSource();
        _logChannel = Channel.CreateBounded<IList<DiagnosticMsg>>(
            new BoundedChannelOptions(10_000)
            {
                FullMode = BoundedChannelFullMode.DropWrite,
                SingleReader = true,
                SingleWriter = false,
            }
        );

        ISubject<DiagnosticMsg> logSubject =
            _logSubject ?? throw new InvalidOperationException("Registration handler has already stopped.");
        _logSubscription = logSubject
            .Buffer(TimeSpan.FromSeconds(2), 50)
            .Where(evts => evts.Count != 0)
            .Subscribe(evts => _logChannel?.Writer.TryWrite(evts));

        _registrationLoop = Task.Run(() => RunRegistrationProcess(_stopToken.Token), _stopToken.Token);
        _loggingTask = Task.Run(() => RunLoggingProcess(_stopToken.Token), _stopToken.Token);

        Debug.WriteLine($"Diagnostics RegistrationHandler for {_url} started");
    }

    private async Task RunLoggingProcess(CancellationToken cancel)
    {
        Channel<IList<DiagnosticMsg>> logChannel =
            _logChannel ?? throw new InvalidOperationException("Registration handler has not started.");

        // Loop until the channel writer is completed (Stop() completes it before cancelling the
        // stop token so queued messages are not dropped). ReadAsync(None) throws ChannelClosedException
        // when the writer is complete and no items remain — that is the natural exit signal.
        // The cancel token is still checked while waiting for the hub adapter so we don't block
        // indefinitely during a hub-less shutdown.
        while (true)
        {
            IList<DiagnosticMsg> messages;
            try
            {
                messages = await logChannel.Reader.ReadAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                break;
            }

            try
            {
                Stopwatch watch2 = Stopwatch.StartNew();
                while (_hubAdapter == null)
                {
                    if (cancel.IsCancellationRequested)
                    {
                        return;
                    }

                    await Task.Delay(TimeSpan.FromSeconds(1), CancellationToken.None);
                }

                // Snapshot: _hubAdapter can be nulled by HandleClosed/CloseConnection between
                // the wait above and the send below.
                HubServerAdapter? adapter = _hubAdapter;
                if (adapter == null)
                {
                    continue;
                }

                Debug.WriteLine($"RegistrationHandler sending {messages.Count} messages");
                await adapter
                    .LogEvents(messages, cancel.IsCancellationRequested ? CancellationToken.None : cancel)
                    .ConfigureAwait(false);
                watch2.Stop();
                Debug.WriteLine(
                    $"RegistrationHandler sent {messages.Count} messages, send took {watch2.ElapsedMilliseconds}ms"
                );
            }
            catch (Exception ex)
            {
                // Keep delivery failures out of the diagnostic log channel that just failed.
                Trace.TraceError($"RegistrationHandler failed to log {messages.Count} messages: {ex}");
            }
        }

        Debug.WriteLine($"RunLoggingProcess HAS NOW STOPPED");
    }

    private async Task RunRegistrationProcess(CancellationToken cancelToken)
    {
        TimeSpan delay = TimeSpan.Zero;

        while (!cancelToken.IsCancellationRequested)
        {
            try
            {
                if (delay != TimeSpan.Zero)
                {
                    await Task.Delay(delay, cancelToken);
                }

                delay = TimeSpan.FromSeconds(5);

                cancelToken.ThrowIfCancellationRequested();

                await OpenHub(cancelToken);

                cancelToken.ThrowIfCancellationRequested();

                HubServerAdapter? adapter;
                lock (_connLock)
                {
                    adapter = _hubAdapter;
                }

                if (adapter == null)
                {
                    continue;
                }

                RegistrationResponse response = await adapter.Register(_registration, cancelToken);

                delay =
                    response.RenewTimeSeconds <= 0
                        ? TimeSpan.FromSeconds(20)
                        : TimeSpan.FromSeconds(response.RenewTimeSeconds);
            }
            catch (Exception ex)
            {
                // On a genuine stop, leave the connection intact: Stop() owns teardown (it
                // deregisters while the adapter is still valid, then disposes). Closing here would
                // null _hubAdapter and make Stop's Deregister silently no-op. (M26)
                if (cancelToken.IsCancellationRequested)
                {
                    return;
                }

                //Something went wrong, so kill the connection and try again
                await CloseConnection();

                Debug.WriteLine($"RunRegistrationProcess exception {ex.Message}");
                string errorMessage =
                    $"DiagnosticHostingService.RegistrationHandler for {_url} encountered an exception";
                _log.Warn(errorMessage, ex);
            }
        }
    }

    private Task CloseConnection()
    {
        return DisposeConnection(TakeConnection());
    }

    private async Task OpenHub(CancellationToken cancelToken)
    {
        lock (_connLock)
        {
            if (_hubAdapter != null)
            {
                return;
            }
        }

        Debug.WriteLine("Diagnostic RegistrationHandler constructing connection");
        HubConnection connection = new HubConnectionBuilder()
            .WithUrl(
                _url,
                options =>
                {
                    // Apply the caller's HTTP customisation first (e.g. M23 opt-in integrated auth via
                    // UseDefaultCredentials), THEN layer the API key on top so the required credential
                    // always wins — a caller configureHttp that incidentally sets AccessTokenProvider
                    // can no longer silently drop the key. (M23 / F4)
                    _configureHttp?.Invoke(options);

                    // H1: when an API key is configured, send it via the access-token mechanism —
                    // "Authorization: Bearer <key>" on negotiate and "access_token" on the WS upgrade.
                    if (!string.IsNullOrEmpty(_apiKey))
                    {
                        // ReSharper disable once RedundantTypeArgumentsOfMethod
                        // The type argument states the nullable delegate return type; inferring it gives Task<string>, which the compiler rejects.
                        options.AccessTokenProvider = () => Task.FromResult<string?>(_apiKey);
                    }
                }
            )
            // The agent channel speaks MessagePack, which is what replaces the protobuf-plus-gzip
            // framing this transport used to apply by hand. Contractless keeps the DTOs free of
            // serialization attributes; the resolver and security profile must match the service's
            // registration exactly or negotiation succeeds and deserialization then fails.
            .AddMessagePackProtocol(options =>
            {
                options.SerializerOptions = MessagePackSerializerOptions
                    .Standard.WithResolver(ContractlessStandardResolver.Instance)
                    .WithSecurity(MessagePackSecurity.UntrustedData);
            })
            .Build();

        connection.Closed += HandleClosed;

        try
        {
            Debug.WriteLine("Diagnostic RegistrationHandler starting connection");
            await connection.StartAsync(cancelToken);

            Debug.WriteLine("Diagnostic RegistrationHandler connection started");
            HubServerAdapter adapter = new HubServerAdapter(connection, null, _serviceProvider);

            lock (_connLock)
            {
                _connection = connection;
                _hubAdapter = adapter;
            }
        }
        catch
        {
            connection.Closed -= HandleClosed;
            await connection.DisposeAsync();
            throw;
        }
    }

    private async Task HandleClosed(Exception? ex)
    {
        Debug.WriteLine($"RegistrationHandler.HandleClosed {ex?.Message}");
        await DisposeConnection(TakeConnection());
    }

    // Atomically detach the current connection/adapter pair. Whichever of CloseConnection /
    // HandleClosed / Stop wins the lock gets the live instances; the losers get nulls and no-op,
    // so a given connection+adapter is torn down exactly once.
    private (HubConnection? Connection, HubServerAdapter? Adapter) TakeConnection()
    {
        lock (_connLock)
        {
            HubConnection? connection = _connection;
            HubServerAdapter? adapter = _hubAdapter;
            _connection = null;
            _hubAdapter = null;
            return (connection, adapter);
        }
    }

    private async Task DisposeConnection((HubConnection? Connection, HubServerAdapter? Adapter) taken)
    {
        try
        {
            taken.Adapter?.Dispose(); // M25: tear the adapter down too, not just the connection.
        }
        catch (Exception ex)
        {
            Trace.TraceError("RegistrationHandler.DisposeConnection HubServerAdapter.Dispose: " + ex);
        }

        if (taken.Connection == null)
        {
            return;
        }

        // Detach the Closed handler before disposing so DisposeAsync's own Closed callback can't
        // re-enter HandleClosed and race this teardown. (M28)
        taken.Connection.Closed -= HandleClosed;
        try
        {
            await taken.Connection.DisposeAsync();
        }
        catch (Exception ex)
        {
            Trace.TraceError("RegistrationHandler.DisposeConnection HubConnection.DisposeAsync: " + ex);
        }
    }

    public async Task Stop()
    {
        try
        {
            Task? loopTask = _registrationLoop;
            Task? logTask = _loggingTask;

            // Complete the subject and channel writer BEFORE cancelling the stop token so
            // RunLoggingProcess can drain buffered messages. Cancelling first caused ReadAllAsync
            // to exit early and drop queued log items.
            ISubject<DiagnosticMsg>? logSubject = _logSubject;
            _logSubject = null;
            logSubject?.OnCompleted();

            _logChannel?.Writer.TryComplete();

            CancellationTokenSource? stopToken = _stopToken;
            _stopToken = null;
#if NET48
            stopToken?.Cancel();
#else
            if (stopToken is not null)
            {
                await stopToken.CancelAsync();
            }
#endif

            _registrationLoop = null;
            _loggingTask = null;

            // Drain both background tasks before tearing the connection down. (M26)
            try
            {
                if (loopTask != null)
                {
                    await loopTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation during Stop; not a fault
            }
            catch (Exception ex)
            {
                _log.Error("Registration loop faulted during Stop", ex);
            }

            try
            {
                if (logTask != null)
                {
                    await logTask.ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                // expected on cancellation during Stop; not a fault
            }
            catch (Exception ex)
            {
                _log.Error("Logging task faulted during Stop", ex);
            }

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            await Deregister(cts.Token);

            stopToken?.Dispose();
        }
        finally
        {
            await CloseConnection();
            _logChannel = null;
            _logSubscription?.Dispose();
            _ownedLogSubject.Dispose();
        }
    }

    private async Task Deregister(CancellationToken cancel = default)
    {
        // Snapshot under the lock: HandleClosed could null _hubAdapter concurrently.
        HubServerAdapter? adapter;
        lock (_connLock)
        {
            adapter = _hubAdapter;
        }

        try
        {
            if (adapter != null)
            {
                _log.Info("DiagnosticHostingService Deregistered");
                await adapter.Deregister(_registration, cancel);
                Debug.WriteLine("Deregistered successfully");
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Failed to deregister {ex}");
            _log.Error(ex);
        }
    }

    public void LogEvent(DiagnosticMsg evt)
    {
        // Snapshot: Stop() completes and nulls _logSubject; a log event arriving during/after
        // shutdown must be a no-op, not an NRE. (M27)
        ISubject<DiagnosticMsg>? subject = _logSubject;
        subject?.OnNext(evt);
    }
}
