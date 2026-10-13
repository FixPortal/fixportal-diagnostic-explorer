using System.Text.Json;
using System.Text.Json.Serialization;
using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using DiagnosticService.UnitTests.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace DiagnosticService.UnitTests.Hubs;

/// <summary>
///     F1: <see cref="WebHubContractTests" /> pins method NAMES only. This connects a real
///     <see cref="HubConnection" /> over the actual configured browser JSON protocol
///     (<c>Program.cs</c>'s <c>AddJsonProtocol</c>: <c>JsonStringEnumConverter</c> +
///     case-insensitive property names) against an in-process server, and inspects the raw
///     deserialized <see cref="JsonElement" /> payloads — so a DTO property rename or an enum
///     encoding change that still compiles is caught here even though it would still deserialize
///     into a same-shaped .NET client. The separate agent channel (<see cref="AgentChannelWireTests" />)
///     speaks MessagePack, not this JSON protocol, and is not evidence for this boundary.
/// </summary>
[Collection(ProcessEnvironmentCollection.Name)]
public sealed class WebHubJsonWireTests
{
    /// <summary>
    ///     SetProperty's <see cref="OperationResponse" /> callback must keep its exact camelCase
    ///     field names and never throw a rejected promise (DE-12); pins the actual wire shape the
    ///     SPA's diag-hub.service.ts parses.
    /// </summary>
    [Fact]
    public async Task SetProperty_ProcessNotFound_ReturnsJsonShapedOperationResponse()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }
        );
        await using HubConnection connection = CreateWebConnection(factory);
        await connection.StartAsync(TestContext.Current.CancellationToken);

        JsonElement response = await connection.InvokeAsync<JsonElement>(
            "SetProperty",
            new
            {
                id = "no-such-process",
                path = "a|b||c",
                value = "1",
                requestId = "",
            },
            TestContext.Current.CancellationToken
        );

        response.GetProperty("isSuccess").GetBoolean().Should().BeFalse();
        response.GetProperty("errorMessage").GetString().Should().Be("Process no-such-process not found");
    }

    /// <summary>
    ///     UpdateProcess pushes a <see cref="DiagProcess" />; its <c>state</c> enum must serialize
    ///     as the configured string form (JsonStringEnumConverter), not a bare integer, and the
    ///     field must be reachable under the camelCase name the SPA's RealtimeModel.ts reads.
    /// </summary>
    [Fact]
    public async Task UpdateProcess_Push_SerializesStateAsStringEnum()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = new(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }
        );
        RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
        manager.Register(
            new Registration
            {
                ProcessName = "wire-test-process",
                MachineName = "wire-test-machine",
                InstanceId = "wire-test-instance",
            }
        );
        var processId = manager.GetProcesses().Single(p => p.InstanceId == "wire-test-instance").Id;

        await using HubConnection connection = CreateWebConnection(factory);
        TaskCompletionSource<JsonElement> pushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = connection.On<JsonElement>("UpdateProcess", element => pushed.TrySetResult(element));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        await connection.InvokeAsync("SetSubscriptions", new[] { processId }, TestContext.Current.CancellationToken);

        // Trigger a push by re-publishing the already-online process.
        manager.ProcessChanged.OnNext(manager.GetProcesses().Single(p => p.Id == processId));

        JsonElement process = await pushed.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        process.GetProperty("id").GetString().Should().Be(processId);
        process
            .GetProperty("state")
            .GetString()
            .Should()
            .Be("Online", "OnlineState must serialize as its string name, not the numeric 2");
    }

    /// <summary>
    ///     F3: the initial <c>SetProcesses</c> push a browser receives on connect is an array of
    ///     <see cref="DiagProcess" /> whose fields the SPA reads by camelCase name
    ///     (RealtimeModel.ts: <c>userName</c>, <c>machineName</c>, <c>processName</c>), with the
    ///     enums as strings.
    /// </summary>
    [Fact]
    public async Task SetProcesses_OnConnect_PushesCamelCaseProcessArrayWithStringEnums()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = CreateFactory();
        RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
        var processId = RegisterWireProcess(manager);

        await using HubConnection connection = CreateWebConnection(factory);
        TaskCompletionSource<JsonElement> pushed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = connection.On<JsonElement>("SetProcesses", element => pushed.TrySetResult(element));
        await connection.StartAsync(TestContext.Current.CancellationToken);

        JsonElement processes = await pushed.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );

        processes.ValueKind.Should().Be(JsonValueKind.Array);
        JsonElement process = processes.EnumerateArray().Single(p => p.GetProperty("id").GetString() == processId);
        process.GetProperty("processName").GetString().Should().Be("wire-test-process");
        process.GetProperty("machineName").GetString().Should().Be("wire-test-machine");
        process.GetProperty("userName").GetString().Should().Be("wire-test-user");
        process.GetProperty("state").GetString().Should().Be("Online");
        process.GetProperty("registrationMode").GetString().Should().Be("Auto");
    }

    /// <summary>
    ///     F3: removing a process pushes <c>RemoveProcess</c> carrying the bare process id string
    ///     (not the process object).
    /// </summary>
    [Fact]
    public async Task RemoveProcess_Push_CarriesTheBareProcessId()
    {
        using ProgramHostedTests.DiagnosticServiceFactory factory = CreateFactory();
        RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
        var processId = RegisterWireProcess(manager);

        await using HubConnection connection = CreateWebConnection(factory);
        TaskCompletionSource<JsonElement> removed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<JsonElement> ready = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var readyRegistration = connection.On<JsonElement>(
            "SetProcesses",
            element => ready.TrySetResult(element)
        );
        using var registration = connection.On<JsonElement>("RemoveProcess", element => removed.TrySetResult(element));
        await connection.StartAsync(TestContext.Current.CancellationToken);
        // The initial process list is sent only after the handler has subscribed to removals, so
        // waiting for it guarantees this client is listening before the removal fires.
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

        manager.RemoveProcess(processId);

        JsonElement payload = await removed.Task.WaitAsync(
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken
        );
        payload.ValueKind.Should().Be(JsonValueKind.String);
        payload.GetString().Should().Be(processId);
    }

    private static ProgramHostedTests.DiagnosticServiceFactory CreateFactory()
    {
        return new ProgramHostedTests.DiagnosticServiceFactory(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
                ["ASPNETCORE_ENVIRONMENT"] = "Development",
            }
        );
    }

    private static string RegisterWireProcess(RealtimeManager manager)
    {
        manager.Register(
            new Registration
            {
                ProcessName = "wire-test-process",
                MachineName = "wire-test-machine",
                UserName = "wire-test-user",
                InstanceId = "wire-test-instance",
            }
        );
        return manager.GetProcesses().Single(p => p.InstanceId == "wire-test-instance").Id;
    }

    // Mirrors what the browser client actually sends: TS DTO fields are camelCase by convention,
    // and enums are the string name (matching the server's own AddJsonProtocol configuration).
    // The server's PropertyNameCaseInsensitive=true would mask a naming mismatch if the client
    // here used .NET's PascalCase default, so this must match the SPA's casing, not the server's
    // tolerance for it.
    private static HubConnection CreateWebConnection(ProgramHostedTests.DiagnosticServiceFactory factory)
    {
        var baseAddress = factory.Server.BaseAddress;
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, "/web-hub"),
                options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()
            )
            .AddJsonProtocol(options =>
            {
                options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
                options.PayloadSerializerOptions.Converters.Add(new JsonStringEnumConverter());
            })
            .Build();
    }
}
