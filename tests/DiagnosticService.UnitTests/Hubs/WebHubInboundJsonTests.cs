using System.Collections.Concurrent;
using System.Collections.Immutable;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AwesomeAssertions;
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
public sealed class WebHubInboundJsonTests
{
    [Fact]
    public async Task SetProperty_LiteralJson_BindsCurrentRequestFields()
    {
        await using var browser = new Browser();
        await browser.ConnectAsync();
        JsonElement response = await browser.Connection.InvokeAsync<JsonElement>(
            "SetProperty",
            new
            {
                id = "missing-property-process",
                requestId = "property-attempt-17",
                objectPaths = new[] { "Parent|First", "Child|Second" },
                path = "Settings|Timeout",
                value = "value-73",
            },
            TestContext.Current.CancellationToken
        );
        ActionSnapshot request = browser.Witness.Last.Should().BeOfType<ActionSnapshot>().Subject;
        request.Id.Should().Be("missing-property-process");
        request.RequestId.Should().Be("property-attempt-17");
        request.ObjectPaths.Should().Equal("Parent|First", "Child|Second");
        request.Path.Should().Be("Settings|Timeout");
        request.Value.Should().Be("value-73");
        response.GetProperty("isSuccess").GetBoolean().Should().BeFalse();
        response.GetProperty("errorMessage").GetString().Should().Be("Process missing-property-process not found");
    }

    [Fact]
    public async Task ExecuteOperation_LiteralJson_BindsCurrentRequestFields()
    {
        await using var browser = new Browser();
        await browser.ConnectAsync();
        JsonElement response = await browser.Connection.InvokeAsync<JsonElement>(
            "ExecuteOperation",
            new
            {
                id = "missing-operation-process",
                requestId = "operation-attempt-29",
                objectPaths = new[] { "Service|Root", "Worker|Leaf" },
                path = "Control|Actions",
                operation = "RestartSelected",
                arguments = new[] { "argument-first", "argument-second" },
            },
            TestContext.Current.CancellationToken
        );
        ActionSnapshot request = browser.Witness.Last.Should().BeOfType<ActionSnapshot>().Subject;
        request.Id.Should().Be("missing-operation-process");
        request.RequestId.Should().Be("operation-attempt-29");
        request.ObjectPaths.Should().Equal("Service|Root", "Worker|Leaf");
        request.Path.Should().Be("Control|Actions");
        request.Operation.Should().Be("RestartSelected");
        request.Arguments.Should().Equal("argument-first", "argument-second");
        response.GetProperty("isSuccess").GetBoolean().Should().BeFalse();
        response.GetProperty("errorMessage").GetString().Should().Be("Process missing-operation-process not found");
    }

    [Fact]
    public async Task GetDrillDown_LiteralJson_BindsCurrentRequestFields()
    {
        await using var browser = new Browser();
        await browser.ConnectAsync();
        JsonElement response = await browser.Connection.InvokeAsync<JsonElement>(
            "GetDrillDown",
            new
            {
                id = "missing-drilldown-process",
                objectPaths = new[] { "Objects|First", "Items|Second" },
                jsonHover = true,
                excludeEventViews = false,
            },
            TestContext.Current.CancellationToken
        );
        DrillDownSnapshot request = browser.Witness.Last.Should().BeOfType<DrillDownSnapshot>().Subject;
        request.Id.Should().Be("missing-drilldown-process");
        request.ObjectPaths.Should().Equal("Objects|First", "Items|Second");
        request.JsonHover.Should().BeTrue();
        request.ExcludeEventViews.Should().BeFalse();
        response.GetProperty("errorMessage").GetString().Should().Be("Process missing-drilldown-process not found");
        // Opposite flags distinguish both names and preserve explicit false values.
        await browser.Connection.InvokeAsync<JsonElement>(
            "GetDrillDown",
            new
            {
                id = "missing-drilldown-control",
                objectPaths = new[] { "Control|Root" },
                jsonHover = false,
                excludeEventViews = true,
            },
            TestContext.Current.CancellationToken
        );
        DrillDownSnapshot control = browser.Witness.Last.Should().BeOfType<DrillDownSnapshot>().Subject;
        control.Id.Should().Be("missing-drilldown-control");
        control.ObjectPaths.Should().Equal("Control|Root");
        control.JsonHover.Should().BeFalse();
        control.ExcludeEventViews.Should().BeTrue();
    }

    [Fact]
    public async Task StartRetroSearch_LiteralJson_ReachesActualLoggerWithCurrentQueryFields()
    {
        await using var browser = new Browser();
        await browser.ConnectAsync();
        TaskCompletionSource<QuerySnapshot> entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> release = NewCompletion();
        TaskCompletionSource<bool> iteratorCompleted = NewCompletion();
        TaskCompletionSource<bool> finished = NewCompletion();
        IRetroLogger logger = Substitute.For<IRetroLogger>();
        logger
            .GetMessages(Arg.Any<RetroQuery>(), Arg.Any<CancellationToken>())
            .Returns(call => Read((RetroQuery)call[0], (CancellationToken)call[1]));
        Field(typeof(RetroManager), "_logger").SetValue(browser.Retro, logger);
        ConcurrentDictionary<string, RetroSearchProcess> searches =
            (ConcurrentDictionary<string, RetroSearchProcess>)
                Field(typeof(RetroManager), "_searches").GetValue(browser.Retro)!;
        RetroSearchProcess? search = null;
        void OnFinished(object? sender, EventArgs args) => finished.TrySetResult(true);
        try
        {
            await browser.Connection.InvokeAsync(
                "StartRetroSearch",
                new
                {
                    searchId = 317,
                    maxRecords = 43,
                    minLevel = 5,
                    startDate = "2026-01-02T03:04:05Z",
                    endDate = "2026-02-03T04:05:06Z",
                    machine = "machine-distinct",
                    process = "process-distinct",
                    user = "user-distinct",
                    message = "message-distinct",
                },
                TestContext.Current.CancellationToken
            );
            search = searches[browser.Connection.ConnectionId!];
            search.Finished += OnFinished;
            QuerySnapshot actual = await entered.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );
            actual
                .Should()
                .Be(
                    new QuerySnapshot(
                        317,
                        43,
                        5,
                        new DateTime(2026, 1, 2, 3, 4, 5, DateTimeKind.Utc),
                        new DateTime(2026, 2, 3, 4, 5, 6, DateTimeKind.Utc),
                        "machine-distinct",
                        "process-distinct",
                        "user-distinct",
                        "message-distinct"
                    )
                );
            browser.Witness.Last.Should().Be(actual);
        }
        finally
        {
            try
            {
                await browser.Retro.CancelConnectionSearch(browser.Connection.ConnectionId!);
            }
            finally
            {
                release.TrySetResult(true);
                try
                {
                    if (search is not null)
                    {
                        await Within(Task.WhenAll(iteratorCompleted.Task, finished.Task));
                    }
                }
                finally
                {
                    if (search is not null)
                    {
                        search.Finished -= OnFinished;
                        search.Dispose();
                    }
                }
            }
        }

        async IAsyncEnumerable<RetroMsg[]> Read(
            RetroQuery query,
            [EnumeratorCancellation] CancellationToken cancellationToken
        )
        {
            try
            {
                entered.TrySetResult(CaptureQuery(query));
                await release.Task;
                cancellationToken.ThrowIfCancellationRequested();
                yield break;
            }
            finally
            {
                iteratorCompleted.TrySetResult(true);
            }
        }
    }

    private sealed record ActionSnapshot(
        string Id,
        string RequestId,
        ImmutableArray<string> ObjectPaths,
        string Path,
        string? Value,
        string? Operation,
        ImmutableArray<string> Arguments
    );

    private sealed record DrillDownSnapshot(
        string Id,
        ImmutableArray<string> ObjectPaths,
        bool JsonHover,
        bool ExcludeEventViews
    );

    private sealed record QuerySnapshot(
        int SearchId,
        int MaxRecords,
        int MinLevel,
        DateTime StartDate,
        DateTime EndDate,
        string? Machine,
        string? Process,
        string? User,
        string? Message
    );

    private static QuerySnapshot CaptureQuery(RetroQuery query) =>
        new(
            query.SearchId,
            query.MaxRecords,
            query.MinLevel,
            query.StartDate,
            query.EndDate,
            query.Machine,
            query.Process,
            query.User,
            query.Message
        );

    private static TaskCompletionSource<bool> NewCompletion() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task Within(Task task) => task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);

    private static FieldInfo Field(Type type, string name) =>
        type.GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;

    private sealed class RequestWitness : IHubFilter
    {
        public object? Last { get; private set; }

        public async ValueTask<object?> InvokeMethodAsync(
            HubInvocationContext invocationContext,
            Func<HubInvocationContext, ValueTask<object?>> next
        )
        {
            if (invocationContext.Hub is WebHub && invocationContext.HubMethodArguments.Count == 1)
            {
                Last = invocationContext.HubMethodArguments[0] switch
                {
                    SetPropertyRequest request => new ActionSnapshot(
                        request.Id,
                        request.RequestId,
                        [.. request.ObjectPaths],
                        request.Path,
                        request.Value,
                        null,
                        []
                    ),
                    ExecuteOperationRequest request => new ActionSnapshot(
                        request.Id,
                        request.RequestId,
                        [.. request.ObjectPaths],
                        request.Path,
                        null,
                        request.Operation,
                        [.. request.Arguments]
                    ),
                    ProcessDrillDownRequest request => new DrillDownSnapshot(
                        request.Id,
                        [.. request.ObjectPaths],
                        request.JsonHover,
                        request.ExcludeEventViews
                    ),
                    RetroQuery query => CaptureQuery(query),
                    _ => null,
                };
            }
            return await next(invocationContext);
        }
    }

    private sealed class Browser : IAsyncDisposable
    {
        private readonly ProgramHostedTests.DiagnosticServiceFactory _owner;
        private readonly WebApplicationFactory<Diagnostic.Service.Program> _factory;

        public Browser()
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
                    services.AddSingleton<RequestWitness>();
                    services.AddSignalR(options => options.AddFilter<RequestWitness>());
                })
            );
            Connection = new HubConnectionBuilder()
                .WithUrl(
                    new Uri(_factory.Server.BaseAddress, "/web-hub"),
                    options => options.HttpMessageHandlerFactory = _ => _factory.Server.CreateHandler()
                )
                .Build();
        }

        public HubConnection Connection { get; }
        public RequestWitness Witness => _factory.Services.GetRequiredService<RequestWitness>();
        public RetroManager Retro => _factory.Services.GetRequiredService<RetroManager>();

        public async Task ConnectAsync()
        {
            TaskCompletionSource<bool> ready = NewCompletion();
            using var registration = Connection.On<JsonElement>("SetProcesses", _ => ready.TrySetResult(true));
            await Connection.StartAsync(TestContext.Current.CancellationToken);
            await Within(ready.Task);
            Connection.ConnectionId.Should().NotBeNullOrWhiteSpace();
        }

        [SuppressMessage(
            "Reliability",
            "S6966:Awaitable method should be used",
            Justification = "The existing owner restores process environment in its synchronous Dispose(bool) override."
        )]
        public async ValueTask DisposeAsync()
        {
            try
            {
                await Connection.DisposeAsync();
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
