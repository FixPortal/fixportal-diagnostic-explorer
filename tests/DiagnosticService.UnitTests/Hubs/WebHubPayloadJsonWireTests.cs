using System.Text.Json;
using AwesomeAssertions;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using DiagnosticService.UnitTests.Hosting;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using MongoDB.Bson;
using Xunit;
using RetroMsg = Diagnostic.Service.Transport.RetroMsg;

namespace DiagnosticService.UnitTests.Hubs;

// Protocol-boundary tests: real Program JSON protocol and connection, not manager lifecycle or a browser.
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WebHubPayloadJsonWireTests
{
    [Fact]
    public async Task ShowDiagnostics_Push_PreservesNestedBrowserPropertyAndOperationFields()
    {
        using var owner = CreateFactory();
        using var factory = CreateProtocolFactory(owner);
        await using var connection = CreateConnection(factory);
        TaskCompletionSource<(string Id, JsonElement Payload)> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var callback = connection.On<string, JsonElement>(
            "ShowDiagnostics",
            (id, payload) => received.TrySetResult((id, payload.Clone()))
        );
        await StartReadyAsync(connection);

        await Client(factory, connection)
            .ShowDiagnostics(
                "wire-diagnostics-A",
                new DiagnosticResponse
                {
                    PropertyBags =
                    [
                        new PropertyBag("engine-A", "runtime-A")
                        {
                            Categories =
                            [
                                new Category("capacity-A")
                                {
                                    Properties =
                                    [
                                        new DiagnosticExplorer.Property("queue-A", "17", "pending-A")
                                        {
                                            OperationSet = "actions-A",
                                            CanSet = true,
                                            CanDrillDown = true,
                                        },
                                    ],
                                },
                            ],
                        },
                    ],
                    OperationSets =
                    [
                        new OperationSet
                        {
                            Id = "actions-A",
                            Operations =
                            [
                                new Operation
                                {
                                    Signature = "Reset(Int32)",
                                    ReturnType = "Void",
                                    Description = "reset-A",
                                    Parameters = [new OperationParameter("limit", "Int32")],
                                },
                            ],
                        },
                    ],
                }
            );

        var (id, payload) = await ReceiveAsync(received.Task);
        id.Should().Be("wire-diagnostics-A");
        var bag = Field(payload, "propertyBags").EnumerateArray().Single();
        Field(bag, "name").GetString().Should().Be("engine-A");
        Field(bag, "category").GetString().Should().Be("runtime-A");
        var category = Field(bag, "categories").EnumerateArray().Single();
        Field(category, "name").GetString().Should().Be("capacity-A");
        var property = Field(category, "properties").EnumerateArray().Single();
        Field(property, "name").GetString().Should().Be("queue-A");
        Field(property, "value").GetString().Should().Be("17");
        Field(property, "description").GetString().Should().Be("pending-A");
        Field(property, "operationSet").GetString().Should().Be("actions-A");
        Field(property, "canSet").GetBoolean().Should().BeTrue();
        Field(property, "canDrillDown").GetBoolean().Should().BeTrue();
        var operations = Field(payload, "operationSets").EnumerateArray().Single();
        Field(operations, "id").GetString().Should().Be("actions-A");
        var operation = Field(operations, "operations").EnumerateArray().Single();
        Field(operation, "signature").GetString().Should().Be("Reset(Int32)");
        Field(operation, "returnType").GetString().Should().Be("Void");
        Field(operation, "description").GetString().Should().Be("reset-A");
        var parameter = Field(operation, "parameters").EnumerateArray().Single();
        Field(parameter, "name").GetString().Should().Be("limit");
        Field(parameter, "type").GetString().Should().Be("Int32");
    }

    [Fact]
    public async Task InitializeLogStream_Push_PreservesRoutingReplayAndRetentionMetadata()
    {
        using var owner = CreateFactory();
        using var factory = CreateProtocolFactory(owner);
        await using var connection = CreateConnection(factory);
        TaskCompletionSource<(string Id, JsonElement Payload)> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var callback = connection.On<string, JsonElement>(
            "InitializeLogStream",
            (id, payload) => received.TrySetResult((id, payload.Clone()))
        );
        await StartReadyAsync(connection);

        await Client(factory, connection)
            .InitializeLogStream(
                "wire-stream-init-B",
                new LogStreamInitialization
                {
                    StreamId = "stream-B",
                    HighWatermark = 207,
                    MaxEvents = 53,
                    MaxAgeMinutes = 7.5,
                    ReplayEvents = [CreateEvent("stream-B", 203, 2, "replay-B", "2026-10-12T10:11:12Z")],
                    Routing = new LogStreamRoutingConfiguration
                    {
                        MatchMode = EventSinkRouteMatchMode.MostSpecific,
                        Routes =
                        [
                            new LogStreamRoute
                            {
                                Order = 17,
                                LoggerName = "Fixture.Database.",
                                LoggerNameMatchMode = LoggerNameMatchMode.Prefix,
                                MinLevel = 2,
                                MaxLevel = 5,
                                StopProcessing = true,
                                Destinations =
                                [
                                    new LogStreamRouteDestination
                                    {
                                        Category = new LogStreamRouteValue
                                        {
                                            Source = RouteValueSource.Fixed,
                                            Value = "database-B",
                                        },
                                        Name = new LogStreamRouteValue { Source = RouteValueSource.LoggerSuffix },
                                    },
                                ],
                            },
                        ],
                    },
                }
            );

        var (id, payload) = await ReceiveAsync(received.Task);
        id.Should().Be("wire-stream-init-B");
        Field(payload, "streamId").GetString().Should().Be("stream-B");
        Field(payload, "highWatermark").GetInt64().Should().Be(207);
        Field(payload, "maxEvents").GetInt32().Should().Be(53);
        Field(payload, "maxAgeMinutes").GetDouble().Should().Be(7.5);
        var routing = Field(payload, "routing");
        Field(routing, "matchMode").GetString().Should().Be("MostSpecific");
        var route = Field(routing, "routes").EnumerateArray().Single();
        Field(route, "order").GetInt32().Should().Be(17);
        Field(route, "loggerName").GetString().Should().Be("Fixture.Database.");
        Field(route, "loggerNameMatchMode").GetString().Should().Be("Prefix");
        Field(route, "minLevel").GetInt32().Should().Be(2);
        Field(route, "maxLevel").GetInt32().Should().Be(5);
        Field(route, "stopProcessing").GetBoolean().Should().BeTrue();
        var destination = Field(route, "destinations").EnumerateArray().Single();
        Field(Field(destination, "category"), "source").GetString().Should().Be("Fixed");
        Field(Field(destination, "category"), "value").GetString().Should().Be("database-B");
        Field(Field(destination, "name"), "source").GetString().Should().Be("LoggerSuffix");
        AssertEvent(
            Field(payload, "replayEvents").EnumerateArray().Single(),
            "stream-B",
            203,
            2,
            "replay-B",
            "2026-10-12T10:11:12Z"
        );
    }

    [Fact]
    public async Task StreamLogEvents_Push_PreservesBothLiteralEventPayloads()
    {
        using var owner = CreateFactory();
        using var factory = CreateProtocolFactory(owner);
        await using var connection = CreateConnection(factory);
        TaskCompletionSource<(string Id, JsonElement Payload)> received = new(
            TaskCreationOptions.RunContinuationsAsynchronously
        );
        using var callback = connection.On<string, JsonElement>(
            "StreamLogEvents",
            (id, payload) => received.TrySetResult((id, payload.Clone()))
        );
        await StartReadyAsync(connection);
        await Client(factory, connection)
            .StreamLogEvents(
                "wire-stream-live-C",
                [
                    CreateEvent("stream-C", 103, 3, "live-C-one", "2026-10-12T11:12:13Z"),
                    CreateEvent("stream-C", 109, 5, "live-C-two", "2026-10-12T12:13:14Z"),
                ]
            );

        var (id, payload) = await ReceiveAsync(received.Task);
        id.Should().Be("wire-stream-live-C");
        payload.ValueKind.Should().Be(JsonValueKind.Array);
        payload.GetArrayLength().Should().Be(2);
        AssertEvent(payload[0], "stream-C", 103, 3, "live-C-one", "2026-10-12T11:12:13Z");
        AssertEvent(payload[1], "stream-C", 109, 5, "live-C-two", "2026-10-12T12:13:14Z");
    }

    [Fact]
    public async Task ProcessSearchResults_Push_UsesBrowserMessageIdsWithoutMongoRecordIds()
    {
        using var owner = CreateFactory();
        using var factory = CreateProtocolFactory(owner);
        await using var connection = CreateConnection(factory);
        TaskCompletionSource<JsonElement> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var callback = connection.On<JsonElement>(
            "ProcessSearchResults",
            payload => received.TrySetResult(payload.Clone())
        );
        await StartReadyAsync(connection);
        await Client(factory, connection)
            .ProcessSearchResults(
                new RetroSearchResult
                {
                    SearchId = 301,
                    Info = "batch-D",
                    Results =
                    [
                        CreateRetroMessage("0123456789abcdef01234567", 2, "one", "2026-10-12T13:14:15Z"),
                        CreateRetroMessage("89abcdef0123456701234567", 5, "two", "2026-10-12T14:15:16Z"),
                    ],
                }
            );

        var payload = await ReceiveAsync(received.Task);
        Field(payload, "searchId").GetInt32().Should().Be(301);
        Field(payload, "info").GetString().Should().Be("batch-D");
        var results = Field(payload, "results");
        results.ValueKind.Should().Be(JsonValueKind.Array);
        results.GetArrayLength().Should().Be(2);
        AssertRetroMessage(results[0], "0123456789abcdef01234567", 2, "one", "2026-10-12T13:14:15Z");
        AssertRetroMessage(results[1], "89abcdef0123456701234567", 5, "two", "2026-10-12T14:15:16Z");
    }

    private static LogStreamEvent CreateEvent(
        string stream,
        long sequence,
        int level,
        string marker,
        string timestamp
    ) =>
        new()
        {
            StreamId = stream,
            Sequence = sequence,
            Level = level,
            TimestampUtc = DateTime.Parse(
                timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind
            ),
            LoggerCategory = "logger-" + marker,
            Message = "message-" + marker,
            Detail = "detail-" + marker,
            EventId = level + 40,
            EventName = "event-" + marker,
        };

    private static void AssertEvent(
        JsonElement payload,
        string stream,
        long sequence,
        int level,
        string marker,
        string timestamp
    )
    {
        Field(payload, "streamId").GetString().Should().Be(stream);
        Field(payload, "sequence").GetInt64().Should().Be(sequence);
        Field(payload, "timestampUtc").GetString().Should().Be(timestamp);
        Field(payload, "loggerCategory").GetString().Should().Be("logger-" + marker);
        Field(payload, "level").GetInt32().Should().Be(level);
        Field(payload, "message").GetString().Should().Be("message-" + marker);
        Field(payload, "detail").GetString().Should().Be("detail-" + marker);
        Field(payload, "eventId").GetInt32().Should().Be(level + 40);
        Field(payload, "eventName").GetString().Should().Be("event-" + marker);
    }

    private static RetroMsg CreateRetroMessage(string recordId, int level, string marker, string timestamp) =>
        new()
        {
            RecordId = ObjectId.Parse(recordId),
            Level = level,
            Date = DateTime.Parse(
                timestamp,
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind
            ),
            Machine = "machine-" + marker,
            Process = "process-" + marker,
            User = "user-" + marker,
            Category = "category-" + marker,
            Message = "message-" + marker,
        };

    private static void AssertRetroMessage(
        JsonElement payload,
        string msgId,
        int level,
        string marker,
        string timestamp
    )
    {
        Field(payload, "msgId").GetString().Should().Be(msgId);
        payload
            .EnumerateObject()
            .Select(property => property.Name)
            .Should()
            .NotContain(name => string.Equals(name, "recordId", StringComparison.OrdinalIgnoreCase));
        Field(payload, "level").GetInt32().Should().Be(level);
        Field(payload, "date").GetString().Should().Be(timestamp);
        Field(payload, "machine").GetString().Should().Be("machine-" + marker);
        Field(payload, "process").GetString().Should().Be("process-" + marker);
        Field(payload, "user").GetString().Should().Be("user-" + marker);
        Field(payload, "category").GetString().Should().Be("category-" + marker);
        Field(payload, "message").GetString().Should().Be("message-" + marker);
    }

    private static JsonElement Field(JsonElement payload, string name)
    {
        payload.TryGetProperty(name, out var value).Should().BeTrue($"the browser reads the literal '{name}' field");
        return value;
    }

    private static Task<T> ReceiveAsync<T>(Task<T> task) =>
        task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

    private static async Task StartReadyAsync(HubConnection connection)
    {
        TaskCompletionSource<JsonElement> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = connection.On<JsonElement>(
            "SetProcesses",
            payload => ready.TrySetResult(payload.Clone())
        );
        await connection.StartAsync(TestContext.Current.CancellationToken);
        (await ReceiveAsync(ready.Task)).ValueKind.Should().Be(JsonValueKind.Array);
        connection.ConnectionId.Should().NotBeNullOrWhiteSpace();
    }

    private static IWebHubClient Client(
        WebApplicationFactory<Diagnostic.Service.Program> factory,
        HubConnection connection
    ) =>
        factory
            .Services.GetRequiredService<IHubContext<WebHub, IWebHubClient>>()
            .Clients.Client(connection.ConnectionId!);

    private static ProgramHostedTests.DiagnosticServiceFactory CreateFactory() =>
        new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }
        );

    // No timers, agent discovery or Mongo logger startup. Keep Program's actual hub/JSON/manager registrations.
    private static WebApplicationFactory<Diagnostic.Service.Program> CreateProtocolFactory(
        ProgramHostedTests.DiagnosticServiceFactory owner
    ) =>
        owner.WithWebHostBuilder(builder =>
            builder.ConfigureTestServices(services => services.RemoveAll<IHostedService>())
        );

    private static HubConnection CreateConnection(WebApplicationFactory<Diagnostic.Service.Program> factory) =>
        new HubConnectionBuilder()
            .WithUrl(
                new Uri(factory.Server.BaseAddress, "/web-hub"),
                options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()
            )
            .Build();
}
