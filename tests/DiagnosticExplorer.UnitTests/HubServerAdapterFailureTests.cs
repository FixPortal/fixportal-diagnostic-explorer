using System.Net;
using System.Reflection;
using AwesomeAssertions;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     HubServerAdapter's RPC wrappers must surface a failed <see cref="RpcResult" /> as an
///     <see cref="InvalidOperationException" /> instead of treating a failed round trip as
///     success — otherwise a rejected Register is mistaken for a live registration and a
///     failed Deregister/LogEvents passes silently. Removing the IsSuccess check turns these
///     red. (DE-15)
///     HubServerAdapter is internal and DiagnosticExplorer.Hosting grants no
///     InternalsVisibleTo, so the adapter is reached by reflection; its HubConnection is
///     ctor-injected, which is the seam that makes the fake possible without production
///     changes.
/// </summary>
public class HubServerAdapterFailureTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);
    private static readonly Type AdapterType =
        typeof(RegistrationHandler).Assembly.GetType("DiagnosticExplorer.HubServerAdapter")
        ?? throw new InvalidOperationException("DiagnosticExplorer.HubServerAdapter not found");

    /// <summary>
    ///     A failed RpcResult from the hub must throw InvalidOperationException carrying the
    ///     hub's failure message — the round trip failed even though the transport succeeded.
    ///     (DE-15)
    /// </summary>
    [Theory]
    [InlineData("Register")]
    [InlineData("Deregister")]
    [InlineData("LogEvents")]
    public async Task FailedRpcResult_ThrowsInvalidOperationException(string methodName)
    {
        HubConnection hub = CreateHubSubstitute();
        hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                RpcResult failure =
                    callInfo.Arg<Type>() == typeof(RpcResult)
                        ? RpcResult.Fail("request-1", "hub exploded", "detail")
                        : RpcResult<RegistrationResponse>.Fail("request-1", "hub exploded", "detail");
                return Task.FromResult<object?>(failure);
            });

        using IDisposable adapter = CreateAdapter(hub);
        Task invocation = Invoke(adapter, methodName);

        Func<Task> act = () => invocation;

        await act.Should().ThrowAsync<InvalidOperationException>().WithMessage("hub exploded");
    }

    /// <summary>
    ///     A successful Register round trip returns the hub's RegistrationResponse untouched.
    ///     (DE-15)
    /// </summary>
    [Fact]
    public async Task Register_SuccessfulRpcResult_ReturnsResponse()
    {
        RegistrationResponse response = new(TimeSpan.FromSeconds(30));
        HubConnection hub = CreateHubSubstitute();
        hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object?>(RpcResult<RegistrationResponse>.Success(response)));

        using IDisposable adapter = CreateAdapter(hub);

        RegistrationResponse result = await (Task<RegistrationResponse>)Invoke(adapter, "Register");

        result.Should().BeSameAs(response);
    }

    /// <summary>
    ///     A successful Deregister/LogEvents round trip completes quietly — the throw must be
    ///     conditional on IsSuccess, not unconditional. (DE-15)
    /// </summary>
    [Theory]
    [InlineData("Deregister")]
    [InlineData("LogEvents")]
    public async Task SuccessfulRpcResult_CompletesWithoutThrowing(string methodName)
    {
        HubConnection hub = CreateHubSubstitute();
        hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult<object?>(RpcResult.Success("request-1")));

        using IDisposable adapter = CreateAdapter(hub);
        Task invocation = Invoke(adapter, methodName);

        Func<Task> act = () => invocation;

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    ///     Tearing the adapter down must not turn an in-flight request into an
    ///     ObjectDisposedException.
    /// </summary>
    /// <remarks>
    ///     RegistrationHandler.DisposeConnection disposes the adapter BEFORE the connection, so an
    ///     invocation that arrived a moment earlier can still be inside the request gate. Disposing
    ///     the gate under it would throw from the release in the finally and replace whatever the
    ///     request actually returned — or escape unobserved on the receive-loop task. The gate is
    ///     therefore never disposed, which is safe because nothing touches AvailableWaitHandle.
    /// </remarks>
    [Fact]
    public async Task GetDiagnostics_AfterDispose_StillCompletes()
    {
        HubConnection hub = CreateHubSubstitute();
        IDisposable adapter = CreateAdapter(hub);

        adapter.Dispose();

        Task invocation = (Task)AdapterType.GetMethod("GetDiagnostics")!.Invoke(adapter, [CancellationToken.None])!;

        Func<Task> act = () => invocation;

        await act.Should().NotThrowAsync();
    }

    /// <summary>
    ///     The initialization goes out without its replay, and the replay follows in frames.
    /// </summary>
    /// <remarks>
    ///     Sending the replay inside the initialization put the agent's whole retained window —
    ///     5 000 events by default — into one hub message against a 10 MB receive cap. Measured at
    ///     2 000-byte details that is 10.43 MB, and the burst that fills the window is the one
    ///     carrying stack traces, so the frame was largest exactly when it was needed. Over the cap
    ///     the invocation faults, and a fault there is not cancellation, so delivery ends for the
    ///     life of the connection.
    /// </remarks>
    [Fact]
    public async Task SubscribeEvents_SendsTheInitializationWithoutItsReplay()
    {
        HubConnection hub = CreateHubSubstitute();
        List<(string Method, object?[] Args)> sends = [];
        hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(callInfo =>
            {
                lock (sends)
                {
                    sends.Add((callInfo.ArgAt<string>(0), callInfo.ArgAt<object?[]>(2)));
                }

                return Task.FromResult<object?>(null);
            });

        // More than one frame's worth, so the batching is exercised rather than assumed.
        const int retained = 250;

        // Its own store, not DiagnosticManager's. Publishing into the process-wide singleton would
        // leak into any other test that reads it, which is why the rest of this assembly's tests
        // build their own too.
        LogEventStore store = new();
        foreach (var index in Enumerable.Range(0, retained))
        {
            store.Publish(new EventSinkLogEvent("App", LogLevel.Information, $"event-{index}"));
        }

        using IDisposable adapter = CreateAdapter(hub, store);
        await (Task)AdapterType.GetMethod("SubscribeEvents")!.Invoke(adapter, [])!;

        await WaitUntil(() => SendsOf(sends, nameof(IDiagnosticHubServer.StreamLogEvents)).Count >= 3);

        var initializations = SendsOf(sends, nameof(IDiagnosticHubServer.InitializeLogStream));
        initializations.Should().ContainSingle();
        ((LogStreamInitialization)initializations[0].Args[0]!)
            .ReplayEvents.Should()
            .BeEmpty("the replay travels as StreamLogEvents frames, not inside the initialization");

        var frames = SendsOf(sends, nameof(IDiagnosticHubServer.StreamLogEvents));
        frames.Should().HaveCountGreaterThanOrEqualTo(3, "250 events cannot fit in one 100-event frame");
        frames.Should().OnlyContain(send => ((LogStreamEvent[])send.Args[0]!).Length <= 100);
    }

    [Fact]
    public async Task Dispose_WhileStreamSendIsInFlight_KeepsItsCancellationSourceUsable()
    {
        using CancellationTokenSource ceiling = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        TaskCompletionSource<(string Method, CancellationToken Token)> entered = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        TaskCompletionSource<object?> release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        HubConnection hub = CreateHubSubstitute();
        hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                entered.TrySetResult((call.ArgAt<string>(0), call.Arg<CancellationToken>()));
                return release.Task;
            });
        IDisposable adapter = CreateAdapter(hub, new LogEventStore());
        Task? subscribe = null;
        Task? stream = null;
        CancellationTokenSource? source = null;
        Exception? primary = null;
        try
        {
            subscribe = (Task)AdapterType.GetMethod("SubscribeEvents")!.Invoke(adapter, [])!;
            stream = ReadAdapterField<Task>(adapter, "_writeEventTask");
            source = ReadAdapterField<CancellationTokenSource>(adapter, "_writeEventCancel");
            await subscribe.WaitAsync(ceiling.Token);
            var invocation = await entered.Task.WaitAsync(ceiling.Token);
            invocation.Method.Should().Be(nameof(IDiagnosticHubServer.InitializeLogStream));
            invocation.Token.Should().Be(source.Token);

            adapter.Dispose();

            source.IsCancellationRequested.Should().BeTrue();
            invocation.Token.IsCancellationRequested.Should().BeTrue();
            release.Task.IsCompleted.Should().BeFalse("the actual send reply is still positively held");
            stream.IsCompleted.Should().BeFalse("the actual send cannot finish before its held reply");
            Func<CancellationToken> readToken = () => source.Token;
            readToken
                .Should()
                .NotThrow("the stream cancellation source must remain usable until the in-flight send completes")
                .Which.Should()
                .Be(invocation.Token);
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await FinishStreamLifetimeCase(adapter, hub, subscribe, stream, source, release, primary);
        }
    }

    private static T ReadAdapterField<T>(IDisposable adapter, string name)
        where T : class =>
        AdapterType.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(adapter) as T
        ?? throw new InvalidOperationException("Missing adapter field: " + name);

    private static async Task FinishStreamLifetimeCase(
        IDisposable adapter,
        HubConnection hub,
        Task? subscribe,
        Task? stream,
        CancellationTokenSource? source,
        TaskCompletionSource<object?> release,
        Exception? primary
    )
    {
        using CancellationTokenSource cleanupCeiling = new(TimeSpan.FromSeconds(30));
        List<Exception> failures = [];
        await CaptureStreamCleanup(
            () =>
            {
                adapter.Dispose();
                return Task.CompletedTask;
            },
            failures
        );
        await CaptureStreamCleanup(
            () =>
                source is not null && !source.IsCancellationRequested
                    ? source.CancelAsync().WaitAsync(cleanupCeiling.Token)
                    : Task.CompletedTask,
            failures
        );
        release.TrySetResult(null);
        await ObserveStreamTask(subscribe, source, cleanupCeiling.Token, failures);
        bool streamObserved = await ObserveStreamTask(stream, source, cleanupCeiling.Token, failures);
        if (streamObserved)
        {
            await CaptureStreamCleanup(
                () =>
                {
                    source?.Dispose();
                    return Task.CompletedTask;
                },
                failures
            );
            await CaptureStreamCleanup(() => hub.DisposeAsync().AsTask().WaitAsync(cleanupCeiling.Token), failures);
        }
        if (failures.Count != 0)
        {
            AggregateException cleanup = new("StreamCtsCleanupFailure: owned stream cleanup failed", failures);
            if (primary is null)
            {
                throw cleanup;
            }
            throw new AggregateException(
                "StreamCtsCleanupFailure: primary failure and owned stream cleanup failure",
                primary,
                cleanup
            );
        }
    }

    private static async Task CaptureStreamCleanup(Func<Task> cleanup, List<Exception> failures)
    {
        try
        {
            await cleanup();
        }
        catch (Exception error)
        {
            failures.Add(error);
        }
    }

    private static async Task<bool> ObserveStreamTask(
        Task? task,
        CancellationTokenSource? source,
        CancellationToken cancel,
        List<Exception> failures
    )
    {
        if (task is null)
        {
            return true;
        }
        try
        {
            await task.WaitAsync(cancel);
            return true;
        }
        catch (OperationCanceledException) when (task.IsCanceled && source?.IsCancellationRequested == true)
        {
            // Cancellation of the captured owned stream is expected during teardown.
            return true;
        }
        catch (Exception error)
        {
            failures.Add(error);
            return task.IsCompleted;
        }
    }

    private static List<(string Method, object?[] Args)> SendsOf(
        List<(string Method, object?[] Args)> sends,
        string method
    )
    {
        lock (sends)
        {
            return [.. sends.Where(send => send.Method == method)];
        }
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.Add(SignalTimeout);
        while (!condition())
        {
            DateTime.UtcNow.Should().BeBefore(deadline, "the adapter should have sent its frames");
            await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
        }
    }

    // HubConnection (SignalR.Client 8.x) is concrete with virtual RPC methods and no
    // parameterless ctor, so the substitute must be given its five ctor dependencies.
    private static HubConnection CreateHubSubstitute()
    {
        return Substitute.For<HubConnection>(
            Substitute.For<IConnectionFactory>(),
            Substitute.For<IHubProtocol>(),
            new IPEndPoint(IPAddress.Loopback, 5000),
            Substitute.For<IServiceProvider>(),
            NullLoggerFactory.Instance
        );
    }

    private static IDisposable CreateAdapter(HubConnection hub, LogEventStore? eventStore = null)
    {
        return (IDisposable)(
            Activator.CreateInstance(AdapterType, hub, eventStore)
            ?? throw new InvalidOperationException("Failed to construct HubServerAdapter")
        );
    }

    private static Task Invoke(IDisposable adapter, string methodName)
    {
        object?[] args = methodName switch
        {
            "Register" => [new Registration(), CancellationToken.None],
            "Deregister" => [new Registration(), CancellationToken.None],
            // A typed array now, not a protobuf blob: MessagePack frames the messages on the wire.
            "LogEvents" => [new DiagnosticMsg[] { new() }, CancellationToken.None],
            _ => throw new ArgumentOutOfRangeException(nameof(methodName)),
        };

        return (Task)(
            AdapterType.GetMethod(methodName)!.Invoke(adapter, args)
            ?? throw new InvalidOperationException($"{methodName} returned null")
        );
    }
}
