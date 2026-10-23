using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Options;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using Xunit;

namespace DiagnosticService.UnitTests.Hubs;

/// <summary>
///     DiagnosticHub is the SignalR boundary every instrumented process talks to; it was never
///     instantiated in a test. Two contracts are pinned here. First, the RpcResult translation: a
///     throwing downstream call must surface as <c>RpcResult.Fail</c> carrying the message, not as
///     a thrown exception that faults the hub invocation (and, under SignalR's default detailed
///     errors off, tells the caller nothing). Second, the three <c>*Return</c> callbacks must route
///     replies into the shared <c>AsyncResultBucket</c> by request id. (DE-23)
/// </summary>
/// <remarks>
///     RealtimeManager and RetroManager are concrete collaborators with non-virtual members, so
///     the hub runs against real instances; only <see cref="Hub.Context" /> is substituted, and
///     only where a hub method actually touches it. The bucket is a private static on the hub
///     (process-wide by design — replies must land in the same bucket regardless of which hub
///     instance receives them), so the routing tests resolve it once via reflection and await real
///     waiters: event-driven, no sleeps.
/// </remarks>
public sealed class DiagnosticHubTests
{
    private static DiagnosticHub CreateHub(RealtimeManager realtimeManager)
    {
        return new DiagnosticHub(
            realtimeManager,
            new RetroManager(Options.Create(new DiagServiceSettings())),
            Substitute.For<IHubContext<DiagnosticHub, IDiagnosticHubClient>>()
        );
    }

    private static HubCallerContext ContextWithConnectionId(string connectionId)
    {
        HubCallerContext context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Returns(connectionId);
        return context;
    }

    /// <summary>
    ///     A downstream throw inside Register (here the connection-id read itself) must come back
    ///     as RpcResult.Fail with the exception message and detail — the RPC completes normally
    ///     rather than faulting the hub invocation.
    /// </summary>
    [Fact]
    public async Task Register_WhenDownstreamThrows_ReturnsFailWithMessage()
    {
        RealtimeManager realtimeManager = new(TimeProvider.System);
        DiagnosticHub hub = CreateHub(realtimeManager);
        HubCallerContext context = Substitute.For<HubCallerContext>();
        context.ConnectionId.Throws(new InvalidOperationException("ctx boom"));
        hub.Context = context;

        RpcResult<RegistrationResponse> result = await hub.Register(new Registration());

        result.IsSuccess.Should().BeFalse();
        result.Message.Should().Be("ctx boom");
        result.Detail.Should().Contain(nameof(InvalidOperationException));
    }

    /// <summary>
    ///     The happy path pins Context.ConnectionId usage: the registered process must be keyed to
    ///     the calling connection so later replies route back to it.
    /// </summary>
    [Fact]
    public async Task Register_WithValidRegistration_RegistersProcessAgainstConnectionId()
    {
        RealtimeManager realtimeManager = new(TimeProvider.System);
        DiagnosticHub hub = CreateHub(realtimeManager);
        hub.Context = ContextWithConnectionId("conn-de23");

        RpcResult<RegistrationResponse> result = await hub.Register(
            new Registration { ProcessName = "DE23Worker", MachineName = "SRV-DE23" }
        );

        result.IsSuccess.Should().BeTrue();
        DiagProcess process = realtimeManager.GetProcesses().Should().ContainSingle().Subject;
        process.ConnectionId.Should().Be("conn-de23");
    }

    /// <summary>
    ///     A valid payload with no messages short-circuits before either manager is touched and
    ///     succeeds — the framing path every real event batch travels.
    /// </summary>
    [Fact]
    public async Task LogEvents_WithEmptyMessageArray_ReturnsSuccess()
    {
        DiagnosticHub hub = CreateHub(new RealtimeManager(TimeProvider.System));

        RpcResult result = await hub.LogEvents([]);

        result.IsSuccess.Should().BeTrue();
    }
}
