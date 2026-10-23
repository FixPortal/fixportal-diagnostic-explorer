using System.Reactive.Subjects;
using Diagnostic.Service.Common;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using Microsoft.AspNetCore.SignalR;

namespace Diagnostic.Service.ClientHandlers;

public sealed class DiagnosticClientHandler : IDiagnosticClient, IDisposable
{
    /// <summary>The ceiling the deleted AsyncResultBucket applied to every request.</summary>
    /// <remarks>
    ///     It bounds the POLL, which is a read and can be reissued freely. Operations get
    ///     <see cref="DefaultOperationTimeout" /> instead.
    /// </remarks>
    public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The ceiling for the two operations a person triggers.</summary>
    /// <remarks>
    ///     Deliberately much longer than the poll's. Abandoning the wait does not abandon the work:
    ///     the agent never receives the caller's token, so a timed-out ExecuteOperation still runs
    ///     to completion with its result discarded, and a person who retries has then run it twice.
    ///     A ceiling short enough to trip on an operation merely queued behind a poll would make
    ///     that the common case rather than the pathological one. It stays finite so that an agent
    ///     which has stopped answering altogether still resolves.
    /// </remarks>
    public static readonly TimeSpan DefaultOperationTimeout = TimeSpan.FromMinutes(2);

    private readonly HubCallerContext _callerContext;
    private readonly IDiagnosticHubClient _client;
    private readonly Subject<LogStreamInitialization> _logStreamInitializedSubject = new();
    private readonly Subject<LogStreamEvent[]> _logStreamEventsSubject = new();
    private readonly ISubject<LogStreamInitialization> _logStreamInitialized;
    private readonly ISubject<LogStreamEvent[]> _logStreamEvents;
    private readonly TimeSpan _operationTimeout;
    private readonly TimeSpan _requestTimeout;
    private int _disposed;

    public DiagnosticClientHandler(
        HubCallerContext callerContext,
        IDiagnosticHubClient client,
        TimeSpan? requestTimeout = null,
        TimeSpan? operationTimeout = null
    )
    {
        _client = client;
        _callerContext = callerContext;
        _requestTimeout = requestTimeout ?? DefaultRequestTimeout;
        _operationTimeout = operationTimeout ?? DefaultOperationTimeout;
        _logStreamInitialized = Subject.Synchronize(_logStreamInitializedSubject);
        _logStreamEvents = Subject.Synchronize(_logStreamEventsSubject);
        ConnectionId = callerContext.ConnectionId;
    }

    public string ConnectionId { get; }
    public IObservable<LogStreamInitialization> LogStreamInitialized => _logStreamInitialized;
    public IObservable<LogStreamEvent[]> LogStreamEvents => _logStreamEvents;

    // These three are SignalR client results. The request id and the shared response bucket are
    // gone: SignalR correlates the invocation itself and faults it when the AGENT's connection
    // ends.
    //
    // What SignalR does NOT supply is the bucket's timeout. Without a token the typed proxy
    // substitutes CancellationToken.None and the invocation waits forever, so an agent that is
    // connected and healthy but never completes this one call parks the caller permanently. Each
    // call therefore passes a real token, linked with a ceiling.
    public Task<DiagnosticResponse> GetDiagnostics(CancellationToken cancel)
    {
        return Invoke(_client.GetDiagnostics, cancel, _requestTimeout);
    }

    // No caller token on these two, deliberately. The only one available is the AGENT's
    // ConnectionAborted — this handler is built in DiagnosticHub.OnConnectedAsync from the agent's
    // own context, not the browser's — and SignalR already faults a client result when the agent
    // goes away. Passing it does not add a bound; it SUBTRACTS information. An operation whose
    // agent dies mid-flight then reports "The operation was canceled." in place of SignalR's
    // "Connection 'x' disconnected.", and a person reading "canceled" is the one most likely to
    // retry — into the double-run described on DefaultOperationTimeout. The ceiling is what bounds
    // these.
    // A drilldown is a read, like the poll, and carries the poll's ceiling rather than the
    // operation one: nothing is mutated, so reissuing it after a timeout costs only the work.
    public Task<DrillDownResponse> GetDrillDown(DrillDownRequest request)
    {
        return Invoke(token => _client.GetDrillDown(request, token), CancellationToken.None, _requestTimeout);
    }

    public Task<OperationResponse> SetProperty(string requestId, string[] objectPaths, string path, string? value)
    {
        return Invoke(
            token => _client.SetProperty(requestId, objectPaths, path, value!, token),
            CancellationToken.None,
            _operationTimeout
        );
    }

    public Task<OperationResponse> ExecuteOperation(
        string requestId,
        string[] objectPaths,
        string path,
        string operation,
        string[] arguments
    )
    {
        return Invoke(
            token => _client.ExecuteOperation(requestId, objectPaths, path, operation, arguments, token),
            CancellationToken.None,
            _operationTimeout
        );
    }

    /// <summary>
    ///     Invokes a client result under the caller's token, bounded by the request timeout.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         The linked source is disposed on every path, so a long-lived caller token does not
    ///         accumulate one registration per request.
    ///     </para>
    ///     <para>
    ///         The two outcomes are separated because DiagnosticSubscription.RunLoop filters
    ///         OperationCanceledException out of the error it pushes to the browser - a caller that
    ///         gave up is not a fault - while an agent that stopped answering IS one and must not
    ///         leave a healthy-looking panel of stale figures.
    ///     </para>
    ///     <para>
    ///         Which of the two it is cannot be read off the exception type. Cancelling a client
    ///         result raises HubException("Invocation canceled by the server."), not
    ///         OperationCanceledException, whichever token did the cancelling - observed against a
    ///         real hub - so only the token state separates them.
    ///     </para>
    ///     <para>
    ///         Token state alone is not enough to decide there WAS a cancellation, though. An agent
    ///         fault that arrives while the token happens to be cancelled is a real fault and must
    ///         travel: relabelling it would make RunLoop drop it and leave the browser with no sign
    ///         of it. So the exception must also be cancellation-shaped. Both shapes are admitted -
    ///         a token cancelled after the invocation is sent comes back as HubException, one
    ///         already cancelled when it is issued never reaches the agent and fails locally as
    ///         OperationCanceledException - and the original is kept as the inner exception either
    ///         way.
    ///     </para>
    /// </remarks>
    private async Task<T> Invoke<T>(Func<CancellationToken, Task<T>> invoke, CancellationToken cancel, TimeSpan timeout)
    {
        using CancellationTokenSource linked = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        linked.CancelAfter(timeout);
        try
        {
            return await invoke(linked.Token);
        }
        catch (Exception ex) when (IsCancellationShaped(ex) && cancel.IsCancellationRequested)
        {
            throw new OperationCanceledException("The caller stopped waiting for the agent.", ex, cancel);
        }
        catch (Exception ex) when (IsCancellationShaped(ex) && linked.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The agent did not answer within {timeout.TotalSeconds:F0}s on connection {ConnectionId}.",
                ex
            );
        }
    }

    private static bool IsCancellationShaped(Exception ex) => ex is HubException or OperationCanceledException;

    public async Task SubscribeEvents()
    {
        await _client.SubscribeEvents();
    }

    public async Task UnsubscribeEvents()
    {
        await _client.UnsubscribeEvents();
    }

    public event EventHandler? Disconnected;

    public void Arm()
    {
        _callerContext.ConnectionAborted.Register(() => Disconnected?.Invoke(this, EventArgs.Empty));
    }

    // InitializeLogStream/StreamLogEvents can be invoked concurrently for a single client under
    // MaximumParallelInvocationsPerClient; both subjects are wrapped in Subject.Synchronize (see
    // field declarations) so their OnNext is already serialized. (A6)
    public void InitializeLogStream(LogStreamInitialization initialization)
    {
        _logStreamInitialized.OnNext(initialization);
    }

    public void StreamLogEvents(LogStreamEvent[] events)
    {
        _logStreamEvents.OnNext(events);
    }

    public void CloseConnection()
    {
        _callerContext.Abort();
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _logStreamInitializedSubject.Dispose();
        _logStreamEventsSubject.Dispose();
    }
}
