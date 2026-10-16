using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using AwesomeAssertions;
using Diagnostic.Service;
using Diagnostic.Service.ClientHandlers;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using MessagePack;
using MessagePack.Resolvers;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DiagnosticService.UnitTests.Hosting;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class AgentDisconnectCompositionTests(ITestOutputHelper output)
{
    [Fact]
    public async Task AbortOneRegisteredAgent_MarksOnlyItOfflineAndRemovesItsHandler()
    {
        await using var agents = await Agents.ConnectAsync();
        await agents.AbortAAsync();
        agents.AssertOnlyAWasRemoved();
        await agents.AssertBAnswersAsync();
    }

    [Fact]
    public async Task AbortAgentWithInFlightResult_PreservesDisconnectFaultAndSiblingUsability()
    {
        await using var agents = await Agents.ConnectAsync();
        Task<DiagnosticResponse> pending = agents.HandlerA.GetDiagnostics(CancellationToken.None);
        await WithinAsync(agents.ACallEntered.Task);
        pending.IsCompleted.Should().BeFalse("the real agent has entered its gated callback without answering");

        await agents.AbortAAsync();
        Exception? failure = await Record.ExceptionAsync(async () => await WithinAsync(pending));
        // DiagnosticClientHandler deliberately preserves SignalR's disconnection fault rather
        // than relabelling an agent disconnect as caller cancellation (its Invoke contract).
        failure.Should().NotBeNull();
        string diagnostic =
            $"Disconnect fault: type={failure.GetType().FullName}; message={failure.Message}; inner={failure.InnerException?.GetType().FullName}: {failure.InnerException?.Message}";
        output.WriteLine(diagnostic.Length <= 2048 ? diagnostic : diagnostic[..2048]);
        failure.Should().NotBeAssignableTo<OperationCanceledException>();
        failure.Should().NotBeAssignableTo<TimeoutException>();
        failure.Message.Should().Contain(agents.ConnectionA).And.Contain("disconnected");
        agents.AssertOnlyAWasRemoved();
        await agents.AssertBAnswersAsync();
    }

    private static Task<T> WithinAsync<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private sealed record DisconnectSnapshot(
        string ProcessId,
        OnlineState State,
        string? ConnectionId,
        string? Message,
        bool HandlerPresent
    );

    // An independent transport-lifecycle witness. It observes the actual hub's completed
    // OnDisconnected path; it never calls manager teardown or invokes a synthetic event.
    private sealed class DisconnectWitness(RealtimeManager manager) : IHubFilter
    {
        private readonly ConcurrentDictionary<
            string,
            (string ProcessId, TaskCompletionSource<DisconnectSnapshot> Completion)
        > _watched = new();

        public Task<DisconnectSnapshot> Watch(string connectionId, string processId)
        {
            TaskCompletionSource<DisconnectSnapshot> completion = new(
                TaskCreationOptions.RunContinuationsAsynchronously
            );
            _watched.TryAdd(connectionId, (processId, completion)).Should().BeTrue();
            return completion.Task;
        }

        public async Task OnDisconnectedAsync(
            HubLifetimeContext context,
            Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next
        )
        {
            await next(context, exception);
            if (_watched.TryRemove(context.Context.ConnectionId, out var watched))
            {
                DiagProcess process = manager.GetProcesses().Single(p => p.Id == watched.ProcessId);
                watched.Completion.TrySetResult(
                    new DisconnectSnapshot(
                        process.Id,
                        process.State,
                        process.ConnectionId,
                        process.Message,
                        manager.GetClientHandler(context.Context.ConnectionId) is not null
                    )
                );
            }
        }
    }

    private sealed class Agents : IAsyncDisposable
    {
        private readonly ProgramHostedTests.DiagnosticServiceFactory _owner;
        private readonly WebApplicationFactory<Program> _factory;
        private readonly HubConnection _a;
        private readonly HubConnection _b;
        private readonly IDisposable _aCallback;
        private readonly IDisposable _bCallback;
        private readonly TaskCompletionSource<DiagnosticResponse> _releaseA = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly TaskCompletionSource<Exception?> _aClosed = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        private readonly RealtimeManager _manager;
        private string _processA = null!;
        private string _processB = null!;
        private string _connectionA = null!;
        private string _connectionB = null!;
        private DisconnectSnapshot? _disconnect;

        private Agents()
        {
            _owner = new ProgramHostedTests.DiagnosticServiceFactory(
                new Dictionary<string, string?>
                {
                    ["DiagServiceSettings:UseSpaProxy"] = "false",
                    ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
                    ["ASPNETCORE_ENVIRONMENT"] = "Development",
                }
            );
            _factory = _owner.WithWebHostBuilder(builder =>
                builder.ConfigureTestServices(services =>
                {
                    services.RemoveAll<IHostedService>();
                    services.AddSingleton<DisconnectWitness>();
                    services.AddSignalR(options => options.AddFilter<DisconnectWitness>());
                })
            );
            _manager = _factory.Services.GetRequiredService<RealtimeManager>();
            _a = ConnectTo(_factory);
            _b = ConnectTo(_factory);
            _aCallback = _a.On(
                "GetDiagnostics",
                () =>
                {
                    ACallEntered.TrySetResult(true);
                    return _releaseA.Task;
                }
            );
            _bCallback = _b.On(
                "GetDiagnostics",
                () =>
                    Task.FromResult(
                        new DiagnosticResponse { PropertyBags = [new PropertyBag("sibling-B", "disconnect-control")] }
                    )
            );
            _a.Closed += OnAClosed;
        }

        public DiagnosticClientHandler HandlerA { get; private set; } = null!;
        public string ConnectionA => _connectionA;
        public TaskCompletionSource<bool> ACallEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public static async Task<Agents> ConnectAsync()
        {
            Agents agents = new();
            try
            {
                await agents._a.StartAsync(TestContext.Current.CancellationToken);
                await agents._b.StartAsync(TestContext.Current.CancellationToken);
                await agents.RegisterAsync(agents._a, "disconnect-A");
                await agents.RegisterAsync(agents._b, "disconnect-B");
                agents._connectionA = agents._a.ConnectionId!;
                agents._connectionB = agents._b.ConnectionId!;
                agents._connectionA.Should().NotBeNullOrWhiteSpace().And.NotBe(agents._connectionB);
                DiagProcess a = agents._manager.GetProcesses().Single(p => p.InstanceId == "disconnect-A");
                DiagProcess b = agents._manager.GetProcesses().Single(p => p.InstanceId == "disconnect-B");
                a.State.Should().Be(OnlineState.Online);
                b.State.Should().Be(OnlineState.Online);
                a.ConnectionId.Should().Be(agents._connectionA);
                b.ConnectionId.Should().Be(agents._connectionB);
                agents._processA = a.Id;
                agents._processB = b.Id;
                agents.HandlerA = agents._manager.GetClientHandler(agents._connectionA)!;
                agents.HandlerA.Should().NotBeNull();
                agents._manager.GetClientHandler(agents._connectionB).Should().NotBeNull();
                return agents;
            }
            catch
            {
                await agents.DisposeAsync();
                throw;
            }
        }

        private async Task RegisterAsync(HubConnection connection, string instanceId)
        {
            var result = await connection.InvokeAsync<RpcResult<RegistrationResponse>>(
                "Register",
                new Registration
                {
                    InstanceId = instanceId,
                    ProcessName = "agent-" + instanceId,
                    MachineName = "fixture-machine",
                },
                TestContext.Current.CancellationToken
            );
            result.IsSuccess.Should().BeTrue("the actual server Register invocation must complete before disconnect");
        }

        public async Task AbortAAsync()
        {
            Task<DisconnectSnapshot> serverCompleted = _factory
                .Services.GetRequiredService<DisconnectWitness>()
                .Watch(_connectionA, _processA);
            HandlerA.CloseConnection();
            _disconnect = await WithinAsync(serverCompleted);
            await WithinAsync(_aClosed.Task);
            _a.State.Should().Be(HubConnectionState.Disconnected);
        }

        public void AssertOnlyAWasRemoved()
        {
            _disconnect.Should().NotBeNull();
            _disconnect.ProcessId.Should().Be(_processA);
            _disconnect.State.Should().Be(OnlineState.Offline);
            _disconnect.ConnectionId.Should().BeNull();
            _disconnect.Message.Should().Be("Offline");
            _disconnect.HandlerPresent.Should().BeFalse();
            DiagProcess a = _manager.GetProcesses().Single(p => p.Id == _processA);
            a.State.Should().Be(OnlineState.Offline);
            a.ConnectionId.Should().BeNull();
            _manager.GetClientHandler(_connectionA).Should().BeNull();
            DiagProcess b = _manager.GetProcesses().Single(p => p.Id == _processB);
            b.State.Should().Be(OnlineState.Online);
            b.ConnectionId.Should().Be(_connectionB);
            _manager.GetClientHandler(_connectionB).Should().NotBeNull();
        }

        public async Task AssertBAnswersAsync()
        {
            DiagnosticResponse response = await WithinAsync(
                _manager.GetClientHandler(_connectionB)!.GetDiagnostics(CancellationToken.None)
            );
            response.PropertyBags.Should().ContainSingle().Which.Name.Should().Be("sibling-B");
            response.PropertyBags.Single().Category.Should().Be("disconnect-control");
            _b.State.Should().Be(HubConnectionState.Connected);
        }

        private Task OnAClosed(Exception? exception)
        {
            _aClosed.TrySetResult(exception);
            return Task.CompletedTask;
        }

        [SuppressMessage(
            "Reliability",
            "S6966:Awaitable method should be used",
            Justification = "The existing owner restores process environment in its synchronous Dispose(bool) override."
        )]
        public async ValueTask DisposeAsync()
        {
            _releaseA.TrySetResult(new DiagnosticResponse());
            _a.Closed -= OnAClosed;
            _aCallback.Dispose();
            _bCallback.Dispose();
            try
            {
                try
                {
                    await _a.DisposeAsync();
                }
                finally
                {
                    await _b.DisposeAsync();
                }
            }
            finally
            {
                try
                {
                    await _factory.DisposeAsync();
                }
                finally
                {
                    _owner.Dispose();
                }
            }
        }

        private static HubConnection ConnectTo(WebApplicationFactory<Program> factory) =>
            new HubConnectionBuilder()
                .WithUrl(
                    new Uri(factory.Server.BaseAddress, "/diagnostics"),
                    options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()
                )
                .AddMessagePackProtocol(options =>
                    options.SerializerOptions = MessagePackSerializerOptions
                        .Standard.WithResolver(ContractlessStandardResolver.Instance)
                        .WithSecurity(MessagePackSecurity.UntrustedData)
                )
                .Build();
    }
}
