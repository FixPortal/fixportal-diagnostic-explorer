using AwesomeAssertions;
using Diagnostic.Service.ClientHandlers;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using Microsoft.AspNetCore.SignalR;
using NSubstitute;
using Xunit;

namespace DiagnosticService.UnitTests;

public class DiagnosticClientHandlerTests
{
    /// <summary>
    ///     The caller's cancellation must reach SignalR, not merely release this await.
    /// </summary>
    /// <remarks>
    ///     SignalR's typed proxy substitutes CancellationToken.None when the client interface
    ///     declares no trailing token, and a client result invoked that way has no deadline and no
    ///     way to be abandoned: the pending invocation lives until the AGENT's connection ends,
    ///     which is a different connection from the caller's. The stub below cancels the way
    ///     SignalR's ClientResultsManager does, so this test can only pass if a live token is
    ///     being handed to the invocation.
    /// </remarks>
    [Fact]
    public async Task GetDiagnostics_WhenTheCallerCancelsMidFlight_CancelsTheInvocation()
    {
        var client = Substitute.For<IDiagnosticHubClient>();
        client.GetDiagnostics(Arg.Any<CancellationToken>()).Returns(call => NeverCompletes(call));

        using var handler = new DiagnosticClientHandler(CallerContext(), client);
        using CancellationTokenSource caller = new();

        Task<DiagnosticResponse> pending = handler.GetDiagnostics(caller.Token);
        pending.IsCompleted.Should().BeFalse("the agent has not answered yet");

        await caller.CancelAsync();

        Func<Task> act = async () => await pending;
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>An already-cancelled caller never reaches the agent at all.</summary>
    [Fact]
    public async Task GetDiagnostics_WhenTheCallerHasAlreadyCancelled_Throws()
    {
        var client = Substitute.For<IDiagnosticHubClient>();
        client.GetDiagnostics(Arg.Any<CancellationToken>()).Returns(call => NeverCompletes(call));

        using var handler = new DiagnosticClientHandler(CallerContext(), client);
        using CancellationTokenSource caller = new();
        await caller.CancelAsync();

        Func<Task> act = async () => await handler.GetDiagnostics(caller.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    /// <summary>
    ///     An agent that never sends a completion still resolves, on the request ceiling.
    /// </summary>
    /// <remarks>
    ///     This is the case the deleted AsyncResultBucket covered with its 10 second timeout and
    ///     SignalR does not: the agent is connected and healthy, it simply never answers this one
    ///     invocation - because the response failed to frame, say. Without the ceiling the request
    ///     loop parks forever on a connection nothing will tear down.
    ///
    ///     It must surface as a timeout rather than a cancellation: the request loop filters
    ///     OperationCanceledException out of the error it pushes to the browser, so reporting it
    ///     as cancellation would leave a healthy-looking panel of stale figures instead.
    /// </remarks>
    [Fact]
    public async Task GetDiagnostics_WhenTheAgentNeverCompletes_ReportsATimeoutNotACancellation()
    {
        var client = Substitute.For<IDiagnosticHubClient>();
        client.GetDiagnostics(Arg.Any<CancellationToken>()).Returns(call => NeverCompletes(call));

        using var handler = new DiagnosticClientHandler(CallerContext(), client, TimeSpan.FromMilliseconds(20));

        Func<Task> act = async () => await handler.GetDiagnostics(CancellationToken.None);

        await act.Should().ThrowAsync<TimeoutException>();
    }

    /// <summary>
    ///     A real agent fault that lands while the caller's token happens to be cancelled is still
    ///     a fault, and must travel.
    /// </summary>
    /// <remarks>
    ///     Deciding on token state alone would relabel it as cancellation, and the request loop
    ///     filters cancellation out of what it pushes to the browser — so a serialization failure
    ///     on the agent would vanish entirely, which is the failure this whole sequence of fixes
    ///     started with.
    /// </remarks>
    [Fact]
    public async Task GetDiagnostics_WhenTheAgentFaultsAsTheCallerCancels_SurfacesTheAgentsFault()
    {
        var client = Substitute.For<IDiagnosticHubClient>();
        client
            .GetDiagnostics(Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                var token = call.Arg<CancellationToken>();
                TaskCompletionSource<DiagnosticResponse> completion = new(
                    TaskCreationOptions.RunContinuationsAsynchronously
                );
                token.Register(() => completion.TrySetException(new InvalidOperationException("agent boom")));
                return completion.Task;
            });

        using var handler = new DiagnosticClientHandler(CallerContext(), client);
        using CancellationTokenSource caller = new();

        Task<DiagnosticResponse> pending = handler.GetDiagnostics(caller.Token);
        await caller.CancelAsync();

        Func<Task> act = async () => await pending;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("agent boom");
    }

    /// <summary>
    ///     Stands in for SignalR's ClientResultsManager: completes only when the invocation's own
    ///     token is cancelled, never on its own.
    /// </summary>
    /// <remarks>
    ///     It faults with HubException, not OperationCanceledException, because that is what a real
    ///     hub does — observed against one — and it is why the handler tells the two outcomes apart
    ///     by token state rather than by exception type. A stub that cancelled its task instead
    ///     would let a type-based filter pass here while being dead in production.
    /// </remarks>
    private static Task<DiagnosticResponse> NeverCompletes(NSubstitute.Core.CallInfo call)
    {
        var token = call.Arg<CancellationToken>();
        TaskCompletionSource<DiagnosticResponse> completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
        token.Register(() => completion.TrySetException(new HubException("Invocation canceled by the server.")));
        return completion.Task;
    }

    private static HubCallerContext CallerContext()
    {
        var callerContext = Substitute.For<HubCallerContext>();
        callerContext.ConnectionId.Returns("connection-1");
        callerContext.ConnectionAborted.Returns(CancellationToken.None);
        return callerContext;
    }

    [Fact]
    public async Task InitializeLogStream_WhenPublishedConcurrently_DoesNotOverlapObserverCallbacks()
    {
        var handler = CreateHandler();
        using OverlapDetectingObserver<LogStreamInitialization> observer = new();

        using var subscription = handler.LogStreamInitialized.Subscribe(observer);

        var publishes = StartConcurrentPublishes(
            24,
            handler,
            static (target, index) =>
                target.InitializeLogStream(new LogStreamInitialization { StreamId = $"set-{index}" })
        );

        try
        {
            observer.WaitUntilCallbackEntered(TestContext.Current.CancellationToken);
        }
        finally
        {
            observer.ReleaseCallbacks();
            try
            {
                await Task.WhenAll(publishes);
            }
            finally
            {
                handler.Dispose();
            }
        }

        observer.OverlapDetected.Should().BeFalse();
        observer.SeenValues.Should().HaveCount(24);
    }

    [Fact]
    public async Task StreamLogEvents_WhenPublishedConcurrently_DoesNotOverlapObserverCallbacks()
    {
        var handler = CreateHandler();
        using OverlapDetectingObserver<LogStreamEvent[]> observer = new();

        using var subscription = handler.LogStreamEvents.Subscribe(observer);

        var publishes = StartConcurrentPublishes(
            24,
            handler,
            static (target, index) =>
                target.StreamLogEvents(new[] { new LogStreamEvent { StreamId = $"stream-{index}" } })
        );

        try
        {
            observer.WaitUntilCallbackEntered(TestContext.Current.CancellationToken);
        }
        finally
        {
            observer.ReleaseCallbacks();
            try
            {
                await Task.WhenAll(publishes);
            }
            finally
            {
                handler.Dispose();
            }
        }

        observer.OverlapDetected.Should().BeFalse();
        observer.SeenValues.Should().HaveCount(24);
    }

    private static DiagnosticClientHandler CreateHandler()
    {
        var client = Substitute.For<IDiagnosticHubClient>();
        return new DiagnosticClientHandler(CallerContext(), client);
    }

    private static Task[] StartConcurrentPublishes<TState>(int count, TState state, Action<TState, int> publish)
    {
        ManualResetEventSlim start = new(false);
        var tasks = Enumerable
            .Range(0, count)
            .Select(index =>
                Task.Run(() =>
                {
                    start.Wait();
                    publish(state, index);
                })
            )
            .ToArray();

        start.Set();
        _ = Task.WhenAll(tasks).ContinueWith(_ => start.Dispose(), TaskScheduler.Default);
        return tasks;
    }

    private sealed class OverlapDetectingObserver<T> : IObserver<T>, IDisposable
    {
        private readonly ManualResetEventSlim _callbackEntered = new(false);
        private readonly ManualResetEventSlim _releaseCallbacks = new(false);
        private readonly List<T> _seenValues = [];
        private int _activeNotifications;

        public bool OverlapDetected { get; private set; }
        public IReadOnlyList<T> SeenValues
        {
            get
            {
                lock (_seenValues)
                {
                    return _seenValues.ToArray();
                }
            }
        }

        public void Dispose()
        {
            _callbackEntered.Dispose();
            _releaseCallbacks.Dispose();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value)
        {
            if (Interlocked.Increment(ref _activeNotifications) > 1)
            {
                OverlapDetected = true;
            }

            try
            {
                _callbackEntered.Set();
                _releaseCallbacks.Wait(TimeSpan.FromSeconds(30));
                lock (_seenValues)
                {
                    _seenValues.Add(value);
                }
            }
            finally
            {
                Interlocked.Decrement(ref _activeNotifications);
            }
        }

        public void WaitUntilCallbackEntered(CancellationToken cancellationToken)
        {
            _callbackEntered.Wait(cancellationToken);
        }

        public void ReleaseCallbacks()
        {
            _releaseCallbacks.Set();
        }
    }
}
