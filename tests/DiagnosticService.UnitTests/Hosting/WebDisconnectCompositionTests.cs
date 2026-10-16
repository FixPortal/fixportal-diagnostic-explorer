using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using AwesomeAssertions;
using Diagnostic.Service.ClientHandlers;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using NSubstitute;
using Xunit;

namespace DiagnosticService.UnitTests.Hosting;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WebDisconnectCompositionTests
{
    [Fact]
    public async Task DisconnectSubscribedWebClient_RemovesOnlyItsMembership()
    {
        await using var browsers = new Browsers();
        await browsers.ConnectAsync();
        browsers.Manager.Register(
            new Registration
            {
                InstanceId = "web-disconnect-process",
                ProcessName = "web-process",
                MachineName = "owned-test",
            }
        );
        string process = browsers.Manager.GetProcesses().Single().Id;
        (
            await browsers.A.InvokeAsync<bool>(
                "SetSubscriptions",
                new[] { process },
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeTrue();
        (
            await browsers.B.InvokeAsync<bool>(
                "SetSubscriptions",
                new[] { process },
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeTrue();
        DiagnosticSubscription subscription = browsers.Manager.Subscriptions.Single(item => item.ProcessId == process);
        subscription.HasWebClient(browsers.IdA).Should().BeTrue();
        subscription.HasWebClient(browsers.IdB).Should().BeTrue();
        browsers.Witness.Capture = () =>
            new Snapshot(
                subscription.HasWebClient(browsers.IdA),
                subscription.HasWebClient(browsers.IdB),
                false,
                false
            );

        Snapshot after = await browsers.DisconnectAAsync();

        after.APresent.Should().BeFalse("actual WebHub disconnect must remove A from its subscription");
        after.BPresent.Should().BeTrue();
        (
            await browsers.B.InvokeAsync<bool>(
                "SetSubscriptions",
                new[] { process },
                TestContext.Current.CancellationToken
            )
        )
            .Should()
            .BeTrue();
    }

    [Fact]
    public async Task DisconnectWebOwner_CancelsOnlyItsActualRetroSearch()
    {
        await using var browsers = new Browsers();
        await browsers.ConnectAsync();
        using QueryGate a = new();
        using QueryGate b = new();
        IRetroLogger logger = Substitute.For<IRetroLogger>();
        logger
            .GetMessages(Arg.Any<RetroQuery>(), Arg.Any<CancellationToken>())
            .Returns(call =>
                ((RetroQuery)call[0]).SearchId == 101
                    ? a.Read((CancellationToken)call[1])
                    : b.Read((CancellationToken)call[1])
            );
        Field(typeof(RetroManager), "_logger").SetValue(browsers.Retro, logger);
        ConcurrentDictionary<string, RetroSearchProcess> map =
            (ConcurrentDictionary<string, RetroSearchProcess>)
                Field(typeof(RetroManager), "_searches").GetValue(browsers.Retro)!;
        RetroSearchProcess? searchA = null;
        RetroSearchProcess? searchB = null;
        TaskCompletionSource<bool> finishedA = NewCompletion();
        TaskCompletionSource<bool> finishedB = NewCompletion();
        void OnA(object? sender, EventArgs args) => finishedA.TrySetResult(true);
        void OnB(object? sender, EventArgs args) => finishedB.TrySetResult(true);
        try
        {
            await browsers.A.InvokeAsync(
                "StartRetroSearch",
                new RetroQuery { SearchId = 101 },
                TestContext.Current.CancellationToken
            );
            searchA = map[browsers.IdA];
            searchA.Finished += OnA;
            await Within(a.Entered.Task);
            await browsers.B.InvokeAsync(
                "StartRetroSearch",
                new RetroQuery { SearchId = 202 },
                TestContext.Current.CancellationToken
            );
            searchB = map[browsers.IdB];
            searchB.Finished += OnB;
            await Within(b.Entered.Task);
            CancellationTokenSource tokenA = (CancellationTokenSource)
                Field(typeof(RetroSearchProcess), "_cancelToken").GetValue(searchA)!;
            CancellationTokenSource tokenB = (CancellationTokenSource)
                Field(typeof(RetroSearchProcess), "_cancelToken").GetValue(searchB)!;
            tokenA.IsCancellationRequested.Should().BeFalse();
            tokenB.IsCancellationRequested.Should().BeFalse();
            browsers.Witness.Capture = () =>
                new Snapshot(
                    map.ContainsKey(browsers.IdA),
                    map.TryGetValue(browsers.IdB, out var current) && ReferenceEquals(current, searchB),
                    tokenA.IsCancellationRequested,
                    tokenB.IsCancellationRequested
                );

            Snapshot after = await browsers.DisconnectAAsync();

            after.APresent.Should().BeFalse("actual WebHub disconnect must remove its owned retro search");
            after.ACancelled.Should().BeTrue();
            after.BPresent.Should().BeTrue();
            after.BCancelled.Should().BeFalse();
            await browsers.B.InvokeAsync("CancelRetroSearch", 202, TestContext.Current.CancellationToken);
            map.Should().NotContainKey(browsers.IdB);
            tokenB.IsCancellationRequested.Should().BeTrue("the surviving real connection can cancel its own search");
        }
        finally
        {
            // Release query tasks even after a failed state assertion; Finished alone does not join ExecuteQuery.
            a.Release.TrySetResult(true);
            b.Release.TrySetResult(true);
            try
            {
                searchA?.Cancel();
                searchB?.Cancel();
                Task joinA = searchA is null ? Task.CompletedTask : Task.WhenAll(a.Completed.Task, finishedA.Task);
                Task joinB = searchB is null ? Task.CompletedTask : Task.WhenAll(b.Completed.Task, finishedB.Task);
                await Within(Task.WhenAll(joinA, joinB));
            }
            finally
            {
                if (searchA is not null)
                {
                    searchA.Finished -= OnA;
                    searchA.Dispose();
                }
                if (searchB is not null)
                {
                    searchB.Finished -= OnB;
                    searchB.Dispose();
                }
            }
        }
    }

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

    private sealed record Snapshot(bool APresent, bool BPresent, bool ACancelled, bool BCancelled);

    private sealed class QueryGate : IDisposable
    {
        public TaskCompletionSource<bool> Entered { get; } = NewCompletion();
        public TaskCompletionSource<bool> Release { get; } = NewCompletion();
        public TaskCompletionSource<bool> Completed { get; } = NewCompletion();

        public async IAsyncEnumerable<RetroMsg[]> Read([EnumeratorCancellation] CancellationToken cancellationToken)
        {
            try
            {
                Entered.TrySetResult(true);
                await Release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
            finally
            {
                Completed.TrySetResult(true);
            }
        }

        public void Dispose() => Release.TrySetResult(true);
    }

    private sealed class DisconnectWitness : IHubFilter
    {
        public Func<Snapshot>? Capture { get; set; }
        public string? Watched { get; set; }
        public TaskCompletionSource<Snapshot> Completed { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task OnDisconnectedAsync(
            HubLifetimeContext context,
            Exception? exception,
            Func<HubLifetimeContext, Exception?, Task> next
        )
        {
            await next(context, exception);
            if (context.Hub is WebHub && context.Context.ConnectionId == Watched)
            {
                try
                {
                    Completed.TrySetResult(Capture!());
                }
                catch (Exception failure)
                {
                    Completed.TrySetException(failure);
                }
            }
        }
    }

    private sealed class Browsers : IAsyncDisposable
    {
        private readonly ProgramHostedTests.DiagnosticServiceFactory _owner;
        private readonly WebApplicationFactory<Diagnostic.Service.Program> _factory;

        public Browsers()
        {
            _owner = new(
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
            A = CreateConnection();
            B = CreateConnection();
        }

        public HubConnection A { get; }
        public HubConnection B { get; }
        public string IdA { get; private set; } = "";
        public string IdB { get; private set; } = "";
        public RealtimeManager Manager => _factory.Services.GetRequiredService<RealtimeManager>();
        public RetroManager Retro => _factory.Services.GetRequiredService<RetroManager>();
        public DisconnectWitness Witness => _factory.Services.GetRequiredService<DisconnectWitness>();

        public async Task ConnectAsync()
        {
            await Ready(A);
            await Ready(B);
            IdA = A.ConnectionId!;
            IdB = B.ConnectionId!;
            IdA.Should().NotBeNullOrWhiteSpace();
            IdB.Should().NotBeNullOrWhiteSpace().And.NotBe(IdA);
        }

        public async Task<Snapshot> DisconnectAAsync()
        {
            Witness.Watched = IdA;
            await Within(A.StopAsync(CancellationToken.None));
            return await Witness.Completed.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
        }

        private static async Task Ready(HubConnection connection)
        {
            TaskCompletionSource<bool> ready = NewCompletion();
            using var callback = connection.On<System.Text.Json.JsonElement>(
                "SetProcesses",
                _ => ready.TrySetResult(true)
            );
            await connection.StartAsync(TestContext.Current.CancellationToken);
            await Within(ready.Task);
        }

        private HubConnection CreateConnection() =>
            new HubConnectionBuilder()
                .WithUrl(
                    new Uri(_factory.Server.BaseAddress, "/web-hub"),
                    options => options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler()
                )
                .Build();

        [SuppressMessage(
            "Reliability",
            "S6966:Awaitable method should be used",
            Justification = "The existing owner restores process environment in its synchronous Dispose(bool) override."
        )]
        public async ValueTask DisposeAsync()
        {
            try
            {
                try
                {
                    await A.DisposeAsync();
                }
                finally
                {
                    await B.DisposeAsync();
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
    }
}
