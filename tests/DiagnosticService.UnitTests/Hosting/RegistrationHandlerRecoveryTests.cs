using System.Reflection;
using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DiagnosticService.UnitTests.Hosting;

/// <summary>
///     D1: <see cref="RegistrationHandler" /> must recover from a failed hub connection or
///     registration attempt and keep retrying until it succeeds, then stop retrying once
///     cancelled. <c>ProgramHostedTests</c> only exercises a successful initial registration; this
///     injects a real transport failure on the very first request through a controlled
///     <see cref="DelegatingHandler" /> in front of the real in-process SignalR server, so recovery
///     runs through the handler's actual retry loop rather than a mocked connection.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class RegistrationHandlerRecoveryTests
{
    [Fact]
    public async Task Start_WhenFirstConnectionAttemptFails_RetriesAndRegisters()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
            }
        );
        // A fresh HubConnection (and thus a fresh HttpMessageHandlerFactory call) is built for
        // every OpenHub retry, and each failed attempt disposes its own connection/handler — so
        // the counter must be shared externally while a NEW wrapper+inner handler is built per
        // attempt, rather than a single handler instance being reused (and disposed) across
        // retries.
        SharedCounter attemptCount = new();
        Registration registration = new()
        {
            ProcessName = "recovery-test-process",
            MachineName = "recovery-test-machine",
            InstanceId = "recovery-test-instance",
        };
        RegistrationHandler handler = new(new Uri(factory.Server.BaseAddress, "/diagnostics").ToString(), registration);

        try
        {
            handler.Start(options =>
                options.HttpMessageHandlerFactory = _ => new FailOnceHandler(
                    factory.Server.CreateHandler(),
                    attemptCount
                )
            );

            RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
            await WaitUntilRegistered(manager, registration.InstanceId!, TestContext.Current.CancellationToken);

            attemptCount
                .Value.Should()
                .BeGreaterThan(1, "the first attempt must have failed before the retry succeeded");
        }
        finally
        {
            await handler.Stop();
        }
    }

    [Fact]
    public async Task Stop_AfterFirstConnectionAttemptFails_StopsRetryingRatherThanKeepRegistering()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
            }
        );
        // Fails every attempt: the handler must never reach a registered state, and Stop() must
        // still complete promptly rather than hang waiting on a connection that never comes up.
        Registration registration = new()
        {
            ProcessName = "never-registers-process",
            MachineName = "recovery-test-machine",
            InstanceId = "never-registers-instance",
        };
        RegistrationHandler handler = new(new Uri(factory.Server.BaseAddress, "/diagnostics").ToString(), registration);

        SharedCounter attemptCount = new();
        handler.Start(options =>
            options.HttpMessageHandlerFactory = _ => new AlwaysFailHandler(factory.Server.CreateHandler(), attemptCount)
        );
        // Positive signal, not a sleep: at least one attempt really reached the transport and
        // failed before we stop, so the loop was running rather than never started.
        await WaitUntil(
            () => attemptCount.Value >= 1,
            "a failed connection attempt",
            TestContext.Current.CancellationToken
        );

        Task stopTask = handler.Stop();
        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
        );

        completed.Should().Be(stopTask, "Stop() must complete promptly instead of hanging on a wedged retry loop");
        await stopTask; // propagate a fault from Stop() rather than letting WhenAny's success mask it

        // Stop() awaits the registration loop, so once it has returned no further attempt can
        // be made; the count is final.
        var attemptsAtStop = attemptCount.Value;
        attemptsAtStop.Should().BeGreaterThanOrEqualTo(1);
        RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
        manager.GetProcesses().Should().NotContain(p => p.InstanceId == registration.InstanceId);
    }

    [Fact]
    public async Task Start_AfterSuccessfulRegistration_RenewsTheSameLiveConnection()
    {
        using CancellationTokenSource ceiling = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        CancellationToken ct = ceiling.Token;
        using ProgramHostedTests.DiagnosticServiceFactory owner = new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
            }
        );
        RenewalWitness witness = new();
        using var factory = owner.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<IHostedService>();
                services.AddSingleton(witness);
                services.AddSignalR(options => options.AddFilter<RenewalWitness>());
            })
        );
        Registration registration = new()
        {
            ProcessName = "renewal-process",
            MachineName = "renewal-machine",
            InstanceId = "renewal-" + Guid.NewGuid().ToString("N"),
        };
        RegistrationHandler handler = new(new Uri(factory.Server.BaseAddress, "/diagnostics").ToString(), registration);
        Task? registrationLoop = null;
        Exception? primary = null;
        try
        {
            handler.Start(options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler());
            registrationLoop =
                typeof(RegistrationHandler)
                    .GetField("_registrationLoop", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .GetValue(handler) as Task;
            registrationLoop.Should().NotBeNull();
            RenewalSnapshot first = await witness.First.Task.WaitAsync(ct);
            first.InstanceId.Should().Be(registration.InstanceId);
            first.ConnectionId.Should().NotBeNullOrWhiteSpace();

            Task completed = await Task.WhenAny(witness.Second.Task, registrationLoop).WaitAsync(ct);
            ct.ThrowIfCancellationRequested();
            if (completed == registrationLoop)
            {
                await registrationLoop;
            }
            witness
                .Second.Task.IsCompletedSuccessfully.Should()
                .BeTrue("successful registration must renew on the next cycle instead of ending its actual loop");
            RenewalSnapshot second = await witness.Second.Task;
            second.CompletedCalls.Should().Be(2);
            second.InstanceId.Should().Be(first.InstanceId);
            second.ConnectionId.Should().Be(first.ConnectionId);
            RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
            var process = manager.GetProcesses().Single(p => p.InstanceId == registration.InstanceId);
            process.State.Should().Be(OnlineState.Online);
            process.ConnectionId.Should().Be(first.ConnectionId);
            manager.GetClientHandler(first.ConnectionId).Should().NotBeNull();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            // Stop cancels the producer's own scheduled delay and joins both actual owned loops.
            // Observe a captured loop even if it already faulted; no pending helper tasks/barriers.
            List<Exception> cleanup = [];
            try
            {
                await handler.Stop();
            }
            catch (Exception error)
            {
                cleanup.Add(error);
            }
            if (registrationLoop is not null)
            {
                try
                {
                    await registrationLoop;
                }
                catch (OperationCanceledException)
                {
                    // Stop cancels the owned registration delay; its cancellation is expected.
                }
                catch (Exception error)
                {
                    cleanup.Add(error);
                }
            }
            foreach (Exception error in cleanup)
            {
                Console.WriteLine("D1RenewalCleanupFailure: " + error);
            }
            if (primary is null && cleanup.Count != 0)
            {
                throw new AggregateException("D1RenewalCleanupFailure", cleanup);
            }
        }
    }

    private sealed record RenewalSnapshot(string InstanceId, string ConnectionId, int CompletedCalls);

    private sealed class RenewalWitness : IHubFilter
    {
        private int _completedCalls;
        public TaskCompletionSource<RenewalSnapshot> First { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<RenewalSnapshot> Second { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next
        )
        {
            object? result = await next(invocationContext);
            if (invocationContext.HubMethodName == nameof(DiagnosticHub.Register))
            {
                var response = (RpcResult<RegistrationResponse>)result!;
                response
                    .IsSuccess.Should()
                    .BeTrue("the actual hub registration must succeed before renewal is observed");
                response.Response.RenewTimeSeconds = 1;
                var request = (Registration)invocationContext.HubMethodArguments[0]!;
                int count = Interlocked.Increment(ref _completedCalls);
                RenewalSnapshot snapshot = new(request.InstanceId!, invocationContext.Context.ConnectionId, count);
                if (count == 1)
                {
                    First.TrySetResult(snapshot);
                }
                else if (count == 2)
                {
                    Second.TrySetResult(snapshot);
                }
            }
            return result;
        }
    }

    private static async Task WaitUntil(Func<bool> condition, string description, CancellationToken cancellationToken)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!condition())
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"never observed: {description}");
            }
        }
    }

    private static async Task WaitUntilRegistered(
        RealtimeManager manager,
        string instanceId,
        CancellationToken cancellationToken
    )
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (!manager.GetProcesses().Any(p => p.InstanceId == instanceId && p.ConnectionId != null))
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(20), deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException($"process with InstanceId {instanceId} never registered");
            }
        }
    }

    /// <summary>
    ///     Fails the very first outgoing request across every instance sharing <paramref name="counter" />,
    ///     then delegates every subsequent one. A new instance (wrapping a fresh inner handler) is
    ///     built per retry, since each failed HubConnection disposes its own handler.
    /// </summary>
    private sealed class FailOnceHandler(HttpMessageHandler inner, SharedCounter counter) : DelegatingHandler(inner)
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            if (counter.Increment() == 1)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable);
            }

            return await base.SendAsync(request, cancellationToken);
        }
    }

    private sealed class SharedCounter
    {
        private int _value;

        public int Value => _value;

        public int Increment() => Interlocked.Increment(ref _value);
    }

    /// <summary>Fails every outgoing request.</summary>
    private sealed class AlwaysFailHandler(HttpMessageHandler inner, SharedCounter counter) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            counter.Increment();
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
