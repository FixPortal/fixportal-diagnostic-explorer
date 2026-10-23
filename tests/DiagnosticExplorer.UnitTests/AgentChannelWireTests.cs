using AwesomeAssertions;
using MessagePack;
using MessagePack.Resolvers;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     Round-trips a diagnostic response through the exact MessagePack options the agent channel
///     negotiates, for host shapes that a real application registers.
/// </summary>
/// <remarks>
///     <para>
///         These exist because the transfer types carry no serialization attributes any more.
///         Protobuf's UseProtoMembersOnly contract was an allowlist: a member without a
///         ProtoMember never reached the wire, whichever type it happened to hold. The contractless
///         resolver has no allowlist, so every public member of every transfer type is now the
///         agent channel's public surface, and a member typed as object drags the host's own
///         object graph onto it.
///     </para>
///     <para>
///         Every shape below failed to serialise at all while PropertyBag.SourceObject was public,
///         with the failure landing on the agent while it wrote the completion, so the service
///         never saw one. The internal-host case is not hypothetical: SystemStatus is internal and
///         DiagnosticHostingService registers it in every agent, so it was the FIRST poll of every
///         agent that broke.
///     </para>
/// </remarks>
public class AgentChannelWireTests
{
    private static readonly MessagePackSerializerOptions WireOptions = MessagePackSerializerOptions
        .Standard.WithResolver(ContractlessStandardResolver.Instance)
        .WithSecurity(MessagePackSecurity.UntrustedData);

    public static TheoryData<string, Func<object>> HostShapes =>
        new()
        {
            { "internal type, as SystemStatus is", () => new InternalHost() },
            { "interface-typed property", () => new InterfaceHost() },
            { "no public constructor", PrivateConstructorHost.Create },
            { "self-referencing", CyclicHost.Create },
        };

    [Theory]
    [MemberData(nameof(HostShapes))]
    public void DiagnosticResponse_ForAnyHostShape_SurvivesTheAgentChannel(string shape, Func<object> createHost)
    {
        var bag = DiagnosticManager.ObjectToPropertyBag(createHost(), "svc", "cat");
        DiagnosticResponse response = new() { PropertyBags = [bag] };

        var payload = MessagePackSerializer.Serialize(response, WireOptions, TestContext.Current.CancellationToken);
        var restored = MessagePackSerializer.Deserialize<DiagnosticResponse>(
            payload,
            WireOptions,
            TestContext.Current.CancellationToken
        );

        restored.PropertyBags.Should().ContainSingle(because: $"a host with {shape} must reach the service");
        restored.PropertyBags[0].Name.Should().Be("svc");
        restored.PropertyBags[0].GetProperty("Name")!.Value.Should().Be("host");
    }

    /// <summary>
    ///     The live host object stays on the agent. It is what every shape above tripped over, and
    ///     shipping it would put an application's own object graph on the wire besides.
    /// </summary>
    [Fact]
    public void PropertyBag_SourceObject_IsNotOnTheWire()
    {
        typeof(PropertyBag)
            .GetProperties()
            .Select(property => property.Name)
            .Should()
            .NotContain(nameof(PropertyBag.SourceObject));
    }

    internal sealed class InternalHost
    {
        public string Name { get; } = "host";
    }

    public sealed class InterfaceHost
    {
        public string Name { get; } = "host";
        public IDisposable Disposable { get; } = new NoopDisposable();

        private sealed class NoopDisposable : IDisposable
        {
            public void Dispose() { }
        }
    }

    public sealed class PrivateConstructorHost
    {
        private PrivateConstructorHost() { }

        public string Name { get; } = "host";

        public static object Create() => new PrivateConstructorHost();
    }

    public sealed class CyclicHost
    {
        public string Name { get; } = "host";
        public CyclicHost? Self { get; private set; }

        public static object Create()
        {
            CyclicHost host = new();
            host.Self = host;
            return host;
        }
    }
}
