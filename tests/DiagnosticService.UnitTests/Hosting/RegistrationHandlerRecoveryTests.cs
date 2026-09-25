using AwesomeAssertions;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.DependencyInjection;
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

        handler.Start(options =>
            options.HttpMessageHandlerFactory = _ => new AlwaysFailHandler(factory.Server.CreateHandler())
        );
        // Give the loop a couple of failed attempts before stopping.
        await Task.Delay(TimeSpan.FromMilliseconds(200), TestContext.Current.CancellationToken);

        Task stopTask = handler.Stop();
        var completed = await Task.WhenAny(
            stopTask,
            Task.Delay(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
        );

        completed.Should().Be(stopTask, "Stop() must complete promptly instead of hanging on a wedged retry loop");
        await stopTask; // propagate a fault from Stop() rather than letting WhenAny's success mask it

        RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
        manager.GetProcesses().Should().NotContain(p => p.InstanceId == registration.InstanceId);
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
    private sealed class AlwaysFailHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        )
        {
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.ServiceUnavailable));
        }
    }
}
