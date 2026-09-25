using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Reflection;
using AwesomeAssertions;
using Diagnostic.Service;
using Diagnostic.Service.ClientHandlers;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace DiagnosticService.UnitTests.Hosting;

[Collection(ProcessEnvironmentCollection.Name)]
public sealed class ProgramHostedTests
{
    private const string TestApiKey = "test-api-key-42";
    private const string TestOrigin = "http://localhost:2803";
    private const string TestSpaProxy = "http://localhost:4201";

    [Fact]
    [SuppressMessage(
        "ReSharper",
        "AccessToDisposedClosure",
        Justification = "The assertion invokes the closure synchronously before the factory is disposed."
    )]
    public void ApiKeyModeWithoutKeys_FailsAtStartup()
    {
        using var factory = CreateFactory(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:Security:AuthMode"] = nameof(AuthMode.ApiKey),
                ["DiagServiceSettings:Security:AllowedCorsOrigins:0"] = TestOrigin,
            }
        );

        Action act = () => _ = factory.Server.BaseAddress;

        act.Should().Throw<InvalidOperationException>().WithMessage("*no non-empty ApiKeys are configured*");
    }

    [Theory]
    [InlineData("/web-hub")]
    [InlineData("/diagnostics")]
    public async Task ApiKeyModeRejectsAnonymousHubConnection(string hubPath)
    {
        using var factory = CreateAuthenticatedFactory();
        await using var connection = CreateConnection(factory, hubPath, null);

        var exception = await Record.ExceptionAsync(() => connection.StartAsync(TestContext.Current.CancellationToken));

        exception.Should().NotBeNull();
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    [Theory]
    [InlineData("/web-hub")]
    [InlineData("/diagnostics")]
    public async Task ApiKeyModeAcceptsValidHubConnection(string hubPath)
    {
        using var factory = CreateAuthenticatedFactory();
        await using var connection = CreateConnection(factory, hubPath, TestApiKey);

        await connection.StartAsync(TestContext.Current.CancellationToken);

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    /// <summary>
    ///     (DE-3) A presented key that does not match any configured key must be rejected — the
    ///     handler's <c>if (!valid)</c> branch is the only thing standing between a wrong-but-non-empty
    ///     key and a hub connection.
    /// </summary>
    [Theory]
    [InlineData("/web-hub")]
    [InlineData("/diagnostics")]
    public async Task ApiKeyModeRejectsWrongApiKeyHubConnection(string hubPath)
    {
        using var factory = CreateAuthenticatedFactory();
        await using var connection = CreateConnection(factory, hubPath, "wrong-api-key-99");

        var exception = await Record.ExceptionAsync(() => connection.StartAsync(TestContext.Current.CancellationToken));

        exception.Should().NotBeNull();
        connection.State.Should().Be(HubConnectionState.Disconnected);
    }

    /// <summary>
    ///     (DE-3) The documented <c>X-Diag-ApiKey</c> header extraction path has no fallback — a
    ///     client presenting the valid key via the header (rather than the SignalR bearer token /
    ///     access_token query pair) must be accepted on both hubs.
    /// </summary>
    [Theory]
    [InlineData("/web-hub")]
    [InlineData("/diagnostics")]
    public async Task ApiKeyModeAcceptsHeaderApiKeyHubConnection(string hubPath)
    {
        using var factory = CreateAuthenticatedFactory();
        await using var connection = CreateHeaderConnection(factory, hubPath, TestApiKey);

        await connection.StartAsync(TestContext.Current.CancellationToken);

        connection.State.Should().Be(HubConnectionState.Connected);
    }

    /// <summary>
    ///     (DE-5) CORS does not police the WebSocket upgrade (F9), so the pipeline middleware
    ///     validates the Origin header on the hub paths. A cross-origin browser holding a valid key
    ///     must still get 403. The key must be present: <c>UseAuthorization</c> runs before the
    ///     Origin middleware, so an unauthenticated request 401s before the 403 branch is reachable.
    /// </summary>
    [Fact]
    public async Task DisallowedOriginWithValidKey_ForbiddenOnHubPath()
    {
        using var factory = CreateAuthenticatedFactory();
        using var client = factory.CreateClient();
        using var request = CreateHubOriginRequest("http://evil.example");

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().Be(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     (DE-5) Control for the 403 case: an allowlisted Origin with a valid key must pass the
    ///     Origin middleware — the request may still fail downstream (a plain GET is not a hub
    ///     handshake), but anything other than 403 proves the allowlist accepted it.
    /// </summary>
    [Fact]
    public async Task AllowedOriginWithValidKey_NotForbiddenOnHubPath()
    {
        using var factory = CreateAuthenticatedFactory();
        using var client = factory.CreateClient();
        using var request = CreateHubOriginRequest(TestOrigin);

        using var response = await client.SendAsync(request, TestContext.Current.CancellationToken);

        response.StatusCode.Should().NotBe(HttpStatusCode.Forbidden);
    }

    /// <summary>
    ///     (DE-18) ApiKey mode with an empty <c>AllowedCorsOrigins</c> allowlist must fail closed at
    ///     startup — otherwise the service would boot key auth alongside credentialed any-origin CORS.
    /// </summary>
    [Fact]
    [SuppressMessage(
        "ReSharper",
        "AccessToDisposedClosure",
        Justification = "The assertion invokes the closure synchronously before the factory is disposed."
    )]
    public void ApiKeyModeWithoutCorsOrigins_FailsAtStartup()
    {
        using var factory = CreateFactory(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:Security:AuthMode"] = nameof(AuthMode.ApiKey),
                ["DiagServiceSettings:Security:ApiKeys:0"] = TestApiKey,
            }
        );

        Action act = () => _ = factory.Server.BaseAddress;

        act.Should().Throw<InvalidOperationException>().WithMessage("*AllowedCorsOrigins is empty*");
    }

    /// <summary>
    ///     (DE-20) With UseSpaProxy=false the service serves the SPA from SpaDirectory, so
    ///     Program.cs refuses to boot when that directory does not exist — otherwise a production
    ///     deploy missing diagnostics-web/dist would start and fail on every request. Every other
    ///     fixture here sets UseSpaProxy=true, which skips the guard.
    /// </summary>
    [Fact]
    [SuppressMessage(
        "ReSharper",
        "AccessToDisposedClosure",
        Justification = "The assertion invokes the closure synchronously before the factory is disposed."
    )]
    public void SpaProxyDisabledWithMissingSpaDirectory_FailsAtStartup()
    {
        using var factory = CreateFactory(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:UseSpaProxy"] = "false",
                ["DiagServiceSettings:SpaDirectory"] = Path.Combine(
                    Path.GetTempPath(),
                    $"de20-missing-spa-directory-{Guid.NewGuid():N}"
                ),
            }
        );

        Action act = () => _ = factory.Server.BaseAddress;

        act.Should().Throw<InvalidOperationException>().WithMessage("*Diagnostics SPA directory not found*");
    }

    private static HttpRequestMessage CreateHubOriginRequest(string origin)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, "/web-hub");
        request.Headers.Add("X-Diag-ApiKey", TestApiKey);
        request.Headers.Add("Origin", origin);
        return request;
    }

    [Fact]
    public void EnvironmentVariablesOverrideJsonConfiguration()
    {
        const string variableName = "DiagServiceSettings__RetroConnection";
        const string variableValue = "environment-override";
        using EnvironmentVariableScope environment = new(variableName, variableValue);
        using var factory = CreateFactory();

        var configuration = factory.Services.GetRequiredService<IConfiguration>();

        configuration["DiagServiceSettings:RetroConnection"].Should().Be(variableValue);
    }

    /// <summary>
    ///     A client result must actually reach a connected agent and come back.
    /// </summary>
    /// <remarks>
    ///     <para>
    ///         This is the test whose absence let the client-results conversion merge broken. Every
    ///         other test of this path substitutes IDiagnosticHubClient, and a substitute answers
    ///         whatever it is told to; only a real hub, a real connection and a real invocation can
    ///         see that the proxy the service captured cannot invoke at all.
    ///     </para>
    ///     <para>
    ///         It fails with "Client results inside OnConnectedAsync Hub methods are not allowed."
    ///         if the handler is built from Clients.Caller, because SignalR hands out a
    ///         NoInvokeSingleClientProxy for the duration of OnConnectedAsync and the captured
    ///         reference never becomes usable. It also exercises the real wire framing, which no
    ///         other test that CI runs does.
    ///     </para>
    /// </remarks>
    [Fact]
    public async Task GetDiagnostics_OverARealConnection_ReturnsTheAgentsResponse()
    {
        using var factory = CreateAuthenticatedFactory();
        await using var agent = CreateConnection(factory, "/diagnostics", TestApiKey);

        DiagnosticResponse expected = new() { PropertyBags = [new PropertyBag("agent-bag")] };
        agent.On(nameof(IDiagnosticHubClient.GetDiagnostics), () => expected);

        await agent.StartAsync(TestContext.Current.CancellationToken);

        var manager = factory.Services.GetRequiredService<RealtimeManager>();
        var handler = await WaitForClientHandler(manager, agent.ConnectionId!);

        var response = await handler.GetDiagnostics(TestContext.Current.CancellationToken);

        response.PropertyBags.Should().ContainSingle().Which.Name.Should().Be("agent-bag");
    }

    [Fact]
    public async Task ConfiguredService_OverARealAdapter_StaysLiveAndCanBeActedOn()
    {
        var configured = new HostedWidget();
        var legacy = new LegacyWidget();
        DiagnosticConfiguration originalConfiguration = DiagnosticManager.CurrentConfiguration;
        bool originalEnabled = DiagnosticManager.Enabled;
        using var factory = CreateFactory(new Dictionary<string, string?> { ["DiagnosticExplorer:Enabled"] = "false" });
        var services = new ServiceCollection();
        services.AddSingleton(configured);
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["DiagnosticExplorer:Enabled"] = "true",
                    ["DiagnosticExplorer:Uri"] = new Uri(factory.Server.BaseAddress, "/diagnostics").ToString(),
                }
            )
            .Build();
        services.ConfigureDiagnosticExplorer(
            configuration,
            configure =>
            {
                configure.RegisterObjects(registrar => registrar.RegisterService<HostedWidget>("Configured", "Widget"));
                configure.Configure<HostedWidget>(type => type.Property(widget => widget.Child).WithDrillDown());
            },
            options => options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler()
        );
        using ServiceProvider provider = services.BuildServiceProvider();
        IHostedService agent = provider.GetServices<IHostedService>().OfType<DiagnosticHostingService>().Single();

        DiagnosticManager.Register(legacy, "Legacy", "Object");

        try
        {
            await agent.StartAsync(TestContext.Current.CancellationToken);
            RealtimeManager manager = factory.Services.GetRequiredService<RealtimeManager>();
            DiagnosticClientHandler handler = await WaitForRegisteredClientHandler(manager);

            DiagnosticResponse diagnostics = await handler.GetDiagnostics(TestContext.Current.CancellationToken);
            diagnostics.PropertyBags.Select(bag => (bag.Category, bag.Name)).Should().Contain(("Configured", "Widget"));
            diagnostics.PropertyBags.Select(bag => (bag.Category, bag.Name)).Should().Contain(("Object", "Legacy"));

            DrillDownResponse drillDown = await handler.GetDrillDown(
                new DrillDownRequest { ObjectPaths = ["Configured|Widget||Child"] }
            );
            drillDown.ErrorMessage.Should().BeNull();
            drillDown
                .Diagnostics.PropertyBags.SelectMany(bag => bag.Categories)
                .SelectMany(category => category.Properties)
                .Single(property => property.Name == "Name")
                .Value.Should()
                .Be("child");

            OperationResponse set = await handler.SetProperty("set-widget", [], "Configured|Widget||Value", "7");
            set.IsSuccess.Should().BeTrue();
            configured.Value.Should().Be(7);

            DiagnosticResponse changed = await handler.GetDiagnostics(TestContext.Current.CancellationToken);
            changed
                .PropertyBags.Single(bag => bag.Category == "Configured" && bag.Name == "Widget")
                .Categories.SelectMany(category => category.Properties)
                .Single(property => property.Name == "Value")
                .Value.Should()
                .Be("7");

            OperationResponse execute = await handler.ExecuteOperation(
                "increment-widget",
                [],
                "Configured|Widget",
                "Increment()",
                []
            );
            execute.IsSuccess.Should().BeTrue();
            configured.Value.Should().Be(8);
        }
        finally
        {
            await agent.StopAsync(TestContext.Current.CancellationToken);
            DiagnosticManager.Unregister(legacy);
            DiagnosticManager.UseConfiguration(originalConfiguration);
            DiagnosticManager.Enabled = originalEnabled;
        }
    }

    /// <summary>
    ///     Waits for the SERVER to register the handler, which is not what StartAsync signals.
    /// </summary>
    /// <remarks>
    ///     StartAsync completes on the handshake response; the handler is created later, inside
    ///     DiagnosticHub.OnConnectedAsync. Reading it straight after StartAsync therefore returns
    ///     null a fraction of the time — measured at roughly one connection in two hundred against
    ///     this service, and a contended CI runner widens that. Polling the server's own state is
    ///     the completion signal; the timeout is generous because it only has to bound a failure.
    /// </remarks>
    private static async Task<DiagnosticClientHandler> WaitForClientHandler(
        RealtimeManager manager,
        string connectionId
    )
    {
        var getClientHandler = typeof(RealtimeManager).GetMethod(
            "GetClientHandler",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;

        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            if (getClientHandler.Invoke(manager, [connectionId]) is DiagnosticClientHandler handler)
            {
                return handler;
            }

            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
            }
            catch (OperationCanceledException) when (deadline.IsCancellationRequested)
            {
                throw new TimeoutException("the hub should register the agent's handler on connect");
            }
        }
    }

    private static async Task<DiagnosticClientHandler> WaitForRegisteredClientHandler(RealtimeManager manager)
    {
        using CancellationTokenSource deadline = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        while (true)
        {
            var process = manager.GetProcesses().SingleOrDefault(candidate => candidate.ConnectionId != null);
            if (process?.ConnectionId != null)
            {
                return await WaitForClientHandler(manager, process.ConnectionId);
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), deadline.Token);
        }
    }

    private static DiagnosticServiceFactory CreateAuthenticatedFactory()
    {
        return CreateFactory(
            new Dictionary<string, string?>
            {
                ["DiagServiceSettings:Security:AuthMode"] = nameof(AuthMode.ApiKey),
                ["DiagServiceSettings:Security:ApiKeys:0"] = TestApiKey,
                ["DiagServiceSettings:Security:AllowedCorsOrigins:0"] = TestOrigin,
            }
        );
    }

    private static DiagnosticServiceFactory CreateFactory(IReadOnlyDictionary<string, string?>? overrides = null)
    {
        Dictionary<string, string?> settings = new()
        {
            ["DiagServiceSettings:UseSpaProxy"] = "true",
            ["DiagServiceSettings:SpaProxy"] = TestSpaProxy,
            ["DiagServiceSettings:SpaDirectory"] = Path.GetTempPath(),
        };

        if (overrides != null)
        {
            foreach (var (key, value) in overrides)
            {
                settings[key] = value;
            }
        }

        return new DiagnosticServiceFactory(settings);
    }

    private static HubConnection CreateConnection(
        WebApplicationFactory<Program> factory,
        string hubPath,
        string? apiKey
    )
    {
        var baseAddress = factory.Server.BaseAddress;
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, hubPath),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.AccessTokenProvider = apiKey == null ? null : () => Task.FromResult<string?>(apiKey);
                }
            )
            .Build();
    }

    private static HubConnection CreateHeaderConnection(
        WebApplicationFactory<Program> factory,
        string hubPath,
        string apiKey
    )
    {
        var baseAddress = factory.Server.BaseAddress;
        return new HubConnectionBuilder()
            .WithUrl(
                new Uri(baseAddress, hubPath),
                options =>
                {
                    options.HttpMessageHandlerFactory = _ => factory.Server.CreateHandler();
                    options.Headers["X-Diag-ApiKey"] = apiKey;
                }
            )
            .Build();
    }

    private sealed class DiagnosticServiceFactory : WebApplicationFactory<Program>
    {
        private readonly EnvironmentVariableScope[] _environment;

        public DiagnosticServiceFactory(IReadOnlyDictionary<string, string?> settings)
        {
            _environment = settings
                .Select(setting => new EnvironmentVariableScope(setting.Key.Replace(":", "__"), setting.Value))
                .ToArray();
        }

        protected override void Dispose(bool disposing)
        {
            base.Dispose(disposing);

            if (disposing)
            {
                foreach (var variable in _environment.Reverse())
                {
                    variable.Dispose();
                }
            }
        }
    }

    private sealed class HostedWidget
    {
        [DiagnosticProperty(AllowSet = true)]
        public int Value { get; set; }

        [DiagnosticProperty]
        public HostedChild Child { get; } = new();

        [DiagnosticMethod]
        public void Increment() => Value++;
    }

    private sealed class HostedChild
    {
        [DiagnosticProperty]
        public string Name => "child";
    }

    private sealed class LegacyWidget
    {
        [DiagnosticProperty]
        public string Name => "legacy";
    }

    private sealed class EnvironmentVariableScope : IDisposable
    {
        private readonly string _name;
        private readonly string? _originalValue;

        public EnvironmentVariableScope(string name, string? value)
        {
            _name = name;
            _originalValue = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
        }

        public void Dispose()
        {
            Environment.SetEnvironmentVariable(_name, _originalValue);
        }
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ProcessEnvironmentCollection
{
    public const string Name = "Process environment";

    private ProcessEnvironmentCollection() { }
}
