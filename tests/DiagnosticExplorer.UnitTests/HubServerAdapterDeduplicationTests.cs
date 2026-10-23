using System.Net;
using AwesomeAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     An action must run once per operator request, however many invocations carry it.
/// </summary>
/// <remarks>
///     <para>
///         The service bounds how long IT waits and the agent never learns the caller gave up, so a
///         slow action reports failure while its body is still running. An operator who retries
///         then drives <c>MethodInfo.Invoke</c> against the host's objects a second time — and
///         nothing on that path is naturally idempotent.
///     </para>
///     <para>
///         The interleaving here is the one that matters: the retry arrives while the first attempt
///         is still executing. It has to JOIN that attempt, which is why the request is recorded
///         before the adapter's request gate is taken — waiting on the gate would serialise the
///         duplicate behind the original and then run it.
///     </para>
/// </remarks>
[Collection(DiagnosticConfigurationCollection.Name)]
public sealed class HubServerAdapterDeduplicationTests : IDisposable
{
    private static readonly Type AdapterType =
        typeof(RegistrationHandler).Assembly.GetType("DiagnosticExplorer.HubServerAdapter")
        ?? throw new InvalidOperationException("DiagnosticExplorer.HubServerAdapter not found");

    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(30);

    private readonly SlowOperation _target = new();

    public HubServerAdapterDeduplicationTests() =>
        DiagnosticManager.Register(_target, "Dedup", "HubServerAdapterDeduplicationTests");

    public void Dispose()
    {
        _target.Release();
        DiagnosticManager.Unregister(_target);
    }

    [Fact]
    public async Task ExecuteOperation_RetriedWithTheSameRequestId_RunsTheOperationOnce()
    {
        using IDisposable adapter = CreateAdapter();

        Task<OperationResponse> first = ExecuteOperation(adapter, "request-1");
        await _target.Started.Task.WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        // Arrives while the first is still inside the operation body — the window the service's
        // timeout opens, and the only one worth guarding.
        Task<OperationResponse> retry = ExecuteOperation(adapter, "request-1");

        _target.Release();
        OperationResponse[] responses = await Task.WhenAll(first, retry)
            .WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        _target.CallCount.Should().Be(1);
        responses[0].IsSuccess.Should().BeTrue();
        responses[1].Should().BeSameAs(responses[0]);
    }

    [Fact]
    public async Task ExecuteOperation_WithADifferentRequestId_RunsAgain()
    {
        using IDisposable adapter = CreateAdapter();
        _target.Release();

        await ExecuteOperation(adapter, "request-1").WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        await ExecuteOperation(adapter, "request-2").WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        // A fresh gesture is a fresh intent. Deduplicating on content rather than on the request
        // would make a deliberate second click silently do nothing.
        _target.CallCount.Should().Be(2);
    }

    /// <summary>
    ///     An agent talking to a service that predates the field gets the old behaviour rather than
    ///     one bucket that every unidentified action collapses into.
    /// </summary>
    [Fact]
    public async Task ExecuteOperation_WithNoRequestId_RunsEveryTime()
    {
        using IDisposable adapter = CreateAdapter();
        _target.Release();

        await ExecuteOperation(adapter, "").WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        await ExecuteOperation(adapter, "").WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        _target.CallCount.Should().Be(2);
    }

    /// <summary>
    ///     Two different actions sharing an id are still two actions.
    /// </summary>
    /// <remarks>
    ///     Nothing in the protocol says an id is unique across methods - only that a retry carries
    ///     the id of the attempt it repeats. An unscoped key would let the second action join the
    ///     first and hand back ITS result, so the caller is told the edit succeeded while the edit
    ///     never ran. Silent and wrong, which is worse than running twice.
    /// </remarks>
    [Fact]
    public async Task SetPropertyAndExecuteOperation_SharingARequestId_EachRunOnItsOwn()
    {
        using IDisposable adapter = CreateAdapter();
        _target.Release();

        OperationResponse operation = await ExecuteOperation(adapter, "shared-id")
            .WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);
        OperationResponse edit = await SetProperty(adapter, "shared-id", "42")
            .WaitAsync(SignalTimeout, TestContext.Current.CancellationToken);

        operation.IsSuccess.Should().BeTrue();
        edit.IsSuccess.Should().BeTrue();
        _target.CallCount.Should().Be(1);
        _target.Threshold.Should().Be(42);
    }

    private static Task<OperationResponse> SetProperty(IDisposable adapter, string requestId, string value)
    {
        object?[] args =
        [
            requestId,
            Array.Empty<string>(),
            $"HubServerAdapterDeduplicationTests|Dedup||{nameof(SlowOperation.Threshold)}",
            value,
            CancellationToken.None,
        ];

        return (Task<OperationResponse>)(
            AdapterType.GetMethod("SetProperty")!.Invoke(adapter, args)
            ?? throw new InvalidOperationException("SetProperty returned null")
        );
    }

    private static Task<OperationResponse> ExecuteOperation(IDisposable adapter, string requestId)
    {
        object?[] args =
        [
            requestId,
            Array.Empty<string>(),
            "HubServerAdapterDeduplicationTests|Dedup",
            $"{nameof(SlowOperation.Run)}()",
            Array.Empty<string>(),
            CancellationToken.None,
        ];

        return (Task<OperationResponse>)(
            AdapterType.GetMethod("ExecuteOperation")!.Invoke(adapter, args)
            ?? throw new InvalidOperationException("ExecuteOperation returned null")
        );
    }

    private static IDisposable CreateAdapter()
    {
        HubConnection hub = Substitute.For<HubConnection>(
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IHubProtocol>(),
            new IPEndPoint(IPAddress.Loopback, 5000),
            Substitute.For<IServiceProvider>(),
            NullLoggerFactory.Instance
        );

        return (IDisposable)(
            Activator.CreateInstance(AdapterType, hub, null)
            ?? throw new InvalidOperationException("Failed to construct HubServerAdapter")
        );
    }

    private sealed class SlowOperation
    {
        private readonly ManualResetEventSlim _release = new(false);
        private int _callCount;

        public TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        [DiagnosticProperty(AllowSet = true)]
        public int Threshold { get; set; }

        public void Release() => _release.Set();

        [DiagnosticMethod]
        public void Run()
        {
            Interlocked.Increment(ref _callCount);
            Started.TrySetResult();
            _release.Wait(SignalTimeout);
        }
    }
}
