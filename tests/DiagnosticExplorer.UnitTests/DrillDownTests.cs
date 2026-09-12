using AwesomeAssertions;
using DiagnosticExplorer.Logging;
using Microsoft.Extensions.Logging;

// Fixture properties are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     Drilldown, asserted through the same entry point the agent's hub calls.
/// </summary>
/// <remarks>
///     <para>
///         Two things here are worth more than the rest. The first is the gate: a drilldown is
///         opt-in configuration, and the browser is the only thing that reads that opt-in when
///         choosing what to offer. A request is just a path, so if the agent does not re-check the
///         opt-in, "drilldowns are opt-in" is a statement about the UI and not about the process —
///         any object-valued property becomes readable, and a JSON hover returns its whole graph.
///     </para>
///     <para>
///         The second is that a chain resolves by re-rendering, holding nothing between calls. That
///         is what these tests pin by asserting on the SECOND hop: it can only resolve if the first
///         hop's value was rendered with the drilldown configuration, which is the one place the
///         ambient render mode has to be right.
///     </para>
/// </remarks>
[Collection(DiagnosticConfigurationCollection.Name)]
public sealed class DrillDownTests : IDisposable
{
    /// <summary>
    ///     Configuration is process-wide static state, and so is the getter cache it feeds. Each
    ///     test restores the default so ordering cannot leak one test's configuration into another.
    /// </summary>
    public void Dispose() => DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());

    private static RegisteredObject[] Registered(object host) => [new(host, "Svc", "Host")];

    private static DrillDownResponse DrillTo(object host, params string[] objectPaths) =>
        DiagnosticManager.GetDrillDown(Registered(host), new DrillDownRequest { ObjectPaths = [.. objectPaths] });

    private static Property[] PropertiesOf(DrillDownResponse response) =>
        [.. response.Diagnostics.PropertyBags.SelectMany(bag => bag.Categories).SelectMany(cat => cat.Properties)];

    /// <summary>
    ///     The gate. Nothing about this property was ever offered as a drilldown, and the request
    ///     names it directly rather than going through the UI that would not have offered it.
    /// </summary>
    [Fact]
    public void GetDrillDown_APropertyNoConfigurationOpenedUp_IsRefused()
    {
        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Engine");

        response.ErrorMessage.Should().Contain("not available for drilldown");
        response.Diagnostics.PropertyBags.Should().BeEmpty();
    }

    [Fact]
    public void GetDrillDown_AConfiguredProperty_RendersTheValuesOwnDiagnostics()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown()));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Engine");

        response.ErrorMessage.Should().BeNull();
        response.DisplayedCount.Should().Be(1);
        response.TotalCount.Should().Be(1);
        response.IsTruncated.Should().BeFalse();
        PropertiesOf(response).Select(p => p.Name).Should().Contain("Rpm");
    }

    /// <summary>
    ///     A drilldown reads its own type configuration, which is normally the fuller of the two —
    ///     a summary line in the list, the whole object in the popup.
    /// </summary>
    [Fact]
    public void GetDrillDown_ATypeConfiguredForDrillDown_UsesThatConfigurationNotTheMainViewOne()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.Configure<Engine>(t => t.Exclude(e => e.SerialNumber));
            c.ConfigureDrillDown<Engine>(t => t.Property(e => e.SerialNumber).WithLabel("Serial"));
        });

        string[] mainView =
        [
            .. DiagnosticManager
                .ObjectToPropertyBag(new Engine(), "Svc", "Host")
                .Categories.SelectMany(cat => cat.Properties)
                .Select(p => p.Name),
        ];
        string[] drillDown = [.. PropertiesOf(DrillTo(new Host(), "Svc|Host||Engine")).Select(p => p.Name)];

        mainView.Should().NotContain("SerialNumber").And.NotContain("Serial");
        drillDown.Should().Contain("Serial");
    }

    /// <summary>
    ///     The getter list is cached per type, and the two render modes produce different lists for
    ///     the same type. One cache key would hand whichever view ran first to the other — and the
    ///     main view running first is the ordinary case, since that is where the operator clicks.
    /// </summary>
    [Fact]
    public void GetDrillDown_AfterTheMainViewRenderedTheSameType_StillReadsTheDrillDownConfiguration()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.Configure<Engine>(t => t.Exclude(e => e.SerialNumber));
            c.ConfigureDrillDown<Engine>(t => t.Property(e => e.SerialNumber).WithLabel("Serial"));
        });

        DiagnosticManager.ObjectToPropertyBag(new Engine(), "Svc", "Warm");

        PropertiesOf(DrillTo(new Host(), "Svc|Host||Engine")).Select(p => p.Name).Should().Contain("Serial");
    }

    /// <summary>
    ///     A second hop resolves against the diagnostics the first hop produced. It can only be
    ///     found if that render used the drilldown configuration, so this is the ambient render
    ///     mode asserted from the outside.
    /// </summary>
    [Fact]
    public void GetDrillDown_AChainOfTwo_ResolvesTheSecondAgainstTheFirstsOwnDiagnostics()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.ConfigureDrillDown<Engine>(t => t.Property(e => e.Vendor).WithDrillDown());
        });

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Engine", "DrillDown|Engine||Vendor");

        response.ErrorMessage.Should().BeNull();
        PropertiesOf(response).Should().ContainSingle(p => p.Name == "Contact" && p.Value == "sales@example.com");
    }

    [Fact]
    public void GetDrillDown_AChainDeeperThanTheLimit_IsRefusedWithoutRenderingAnything()
    {
        string[] paths = [.. Enumerable.Repeat("Svc|Host||Engine", DiagnosticManager.MaxDrillDownDepth + 1)];

        DrillDownResponse response = DrillTo(new Host(), paths);

        response.ErrorMessage.Should().Contain($"limit of {DiagnosticManager.MaxDrillDownDepth}");
    }

    [Theory]
    [InlineData]
    [InlineData("")]
    [InlineData("   ")]
    public void GetDrillDown_AnEmptyOrBlankPath_IsRefused(params string[] paths)
    {
        DrillTo(new Host(), paths).ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDrillDown_ANullRequest_IsRefusedRatherThanThrowing()
    {
        DiagnosticManager.GetDrillDown(Registered(new Host()), null).ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDrillDown_ACollectionLongerThanItsCeiling_TruncatesAndReportsTheRealTotal()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Orders).WithDrillDown(maxItems: 2)));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Orders");

        response.DisplayedCount.Should().Be(2);
        response.TotalCount.Should().Be(5);
        response.IsTruncated.Should().BeTrue();
        response.Diagnostics.PropertyBags.Should().HaveCount(2);
    }

    /// <summary>
    ///     Without a Count the only honest total is the one counted — and stopping at the ceiling
    ///     means the end was never reached, so there is no total to give.
    /// </summary>
    [Fact]
    public void GetDrillDown_ALazySequenceThatTruncates_ReportsNoTotalRatherThanTheDisplayedCount()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Lazy).WithDrillDown(maxItems: 2)));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Lazy");

        response.DisplayedCount.Should().Be(2);
        response.IsTruncated.Should().BeTrue();
        response.TotalCount.Should().BeNull();
    }

    [Fact]
    public void GetDrillDown_ALazySequenceInsideTheCeiling_ReportsTheCountItReached()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Lazy).WithDrillDown(maxItems: 50)));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Lazy");

        response.IsTruncated.Should().BeFalse();
        response.TotalCount.Should().Be(response.DisplayedCount);
    }

    /// <summary>A scalar has no properties of its own, so it is wrapped in something that has.</summary>
    [Fact]
    public void GetDrillDown_ACollectionOfScalars_WrapsEachItemSoItHasSomethingToRender()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Tags).WithDrillDown()));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Tags");

        PropertiesOf(response).Select(p => p.Value).Should().BeEquivalentTo("alpha", "beta");
    }

    /// <summary>
    ///     A string is a leaf, and it is enumerable - so a drilldown that treated it as a
    ///     collection would render one item per character. It is refused instead, even though the
    ///     host asked for one.
    /// </summary>
    [Fact]
    public void GetDrillDown_AStringValue_IsRefusedRatherThanRenderedPerCharacter()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Name).WithDrillDown()));

        DrillDownResponse response = DrillTo(new Host(), "Svc|Host||Name");

        response.ErrorMessage.Should().Contain("not available for drilldown");
        response.DisplayedCount.Should().Be(0);
    }

    [Fact]
    public void GetDrillDown_AJsonHoverOnAPropertyOnlyOpenedForDrillDown_IsRefused()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown()));

        DrillDownResponse response = DiagnosticManager.GetDrillDown(
            Registered(new Host()),
            new DrillDownRequest { ObjectPaths = ["Svc|Host||Engine"], JsonHover = true }
        );

        response.Json.Should().BeNull();
        response.ErrorMessage.Should().Contain("not available for drilldown");
    }

    /// <summary>
    ///     adversarial-review 20260912T091500Z, H2: <c>GetBagTarget</c> used to gate
    ///     <see cref="DrillDownAccess.Json" /> on the same <c>CanDrillDown</c> check as ordinary
    ///     inspection, so any bag reachable by drilldown had its whole public object graph
    ///     JSON-serializable regardless of whether any property on it was ever opted into
    ///     <c>WithJsonHover()</c> -- exactly the exposure <see cref="GetPropertyTarget" />'s
    ///     separate <c>CanJsonHover</c> check exists to prevent one level up.
    /// </summary>
    [Fact]
    public void GetDrillDown_AJsonHoverOnANestedBagOnlyOpenedForDrillDown_IsRefused()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown()));

        DrillDownResponse response = DiagnosticManager.GetDrillDown(
            Registered(new Host()),
            new DrillDownRequest { ObjectPaths = ["Svc|Host||Engine", "DrillDown|Engine"], JsonHover = true }
        );

        response.Json.Should().BeNull();
        response.ErrorMessage.Should().Contain("not available for JSON view");
    }

    [Fact]
    public void GetDrillDown_AJsonHoverOnAPropertyConfiguredForIt_ReturnsTheSerializedValue()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Engine).WithJsonHover()));

        DrillDownResponse response = DiagnosticManager.GetDrillDown(
            Registered(new Host()),
            new DrillDownRequest { ObjectPaths = ["Svc|Host||Engine"], JsonHover = true }
        );

        response.ErrorMessage.Should().BeNull();
        response.Json.Should().Contain("\"Rpm\"");
    }

    /// <summary>
    ///     Refused rather than cut: half a JSON document is not JSON, and a client parsing it would
    ///     report a syntax error instead of the size.
    /// </summary>
    [Fact]
    public void GetDrillDown_AJsonHoverOverTheSizeCeiling_IsRefusedRatherThanTruncated()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Bulk).WithJsonHover()));

        DrillDownResponse response = DiagnosticManager.GetDrillDown(
            Registered(new Host()),
            new DrillDownRequest { ObjectPaths = ["Svc|Host||Bulk"], JsonHover = true }
        );

        response.Json.Should().BeNull();
        response.ErrorMessage.Should().Contain($"{DiagnosticManager.MaxJsonHoverLength:N0} character limit");
    }

    /// <summary>
    ///     A cyclic graph is ordinary in a live process — a parent holding children that point back
    ///     — and must not take the agent down with a stack overflow.
    /// </summary>
    [Fact]
    public void GetDrillDown_AJsonHoverOverACycle_SerializesRatherThanRecursingForever()
    {
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Cyclic).WithJsonHover()));

        DrillDownResponse response = DiagnosticManager.GetDrillDown(
            Registered(new Host()),
            new DrillDownRequest { ObjectPaths = ["Svc|Host||Cyclic"], JsonHover = true }
        );

        response.ErrorMessage.Should().BeNull();
        response.Json.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void GetDrillDown_ATypeWithEventRoutes_ReturnsThemAsViewDefinitions()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.ConfigureDrillDown<Engine>(t =>
                t.Route(
                    "App.Engine",
                    LoggerNameMatchMode.Prefix,
                    r => r.To("Engine", "Activity").AtLeast(LogLevel.Warning)
                )
            );
        });

        DrillDownEventViewDefinition view = DrillTo(new Host(), "Svc|Host||Engine")
            .EventViews.Should()
            .ContainSingle()
            .Subject;

        view.Category.Should().Be("Engine");
        view.Name.Should().Be("Activity");
        DrillDownEventMatcher matcher = view.Matchers.Should().ContainSingle().Subject;
        matcher.LoggerName.Should().Be("App.Engine");
        matcher.LoggerNameMatchMode.Should().Be(LoggerNameMatchMode.Prefix);
        matcher.MinLevel.Should().Be((int)LogLevel.Warning);
    }

    /// <summary>
    ///     Every item of a collection resolves the same static route, and one table listing that
    ///     matcher once per item would admit every event once per item.
    /// </summary>
    [Fact]
    public void GetDrillDown_ACollectionWhoseItemsShareARoute_MergesIntoOneTableWithOneMatcher()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Orders).WithDrillDown());
            c.ConfigureDrillDown<Order>(t =>
                t.Route("App.Orders", LoggerNameMatchMode.Prefix, r => r.To("Orders", "Activity"))
            );
        });

        DrillDownEventViewDefinition view = DrillTo(new Host(), "Svc|Host||Orders")
            .EventViews.Should()
            .ContainSingle()
            .Subject;

        view.Matchers.Should().ContainSingle();
    }

    /// <summary>
    ///     A route whose logger name is derived per object is the case that must NOT be merged:
    ///     each item admits only its own events.
    /// </summary>
    [Fact]
    public void GetDrillDown_ACollectionWhoseItemsResolveDifferentLoggers_KeepsOneMatcherPerItem()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Orders).WithDrillDown(maxItems: 3));
            c.ConfigureDrillDown<Order>(t =>
                t.Route(o => $"App.Orders.{o.Id}", LoggerNameMatchMode.Exact, r => r.To("Orders", "Activity"))
            );
        });

        DrillDownEventViewDefinition view = DrillTo(new Host(), "Svc|Host||Orders")
            .EventViews.Should()
            .ContainSingle()
            .Subject;

        view.Matchers.Select(m => m.LoggerName).Should().BeEquivalentTo("App.Orders.0", "App.Orders.1", "App.Orders.2");
    }

    [Fact]
    public void GetDrillDown_ExcludingEventViews_SkipsResolvingThem()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.ConfigureDrillDown<Engine>(t =>
                t.Route("App.Engine", LoggerNameMatchMode.Prefix, r => r.To("Engine", "Activity"))
            );
        });

        DiagnosticManager
            .GetDrillDown(
                Registered(new Host()),
                new DrillDownRequest { ObjectPaths = ["Svc|Host||Engine"], ExcludeEventViews = true }
            )
            .EventViews.Should()
            .BeEmpty();
    }

    /// <summary>
    ///     An action triggered inside a drilldown carries the chain that opened it. Resolved against
    ///     the registered objects instead, the same relative path would miss — or, as here, hit the
    ///     wrong object's identically named property.
    /// </summary>
    [Fact]
    public void SetProperty_InsideADrillDown_WritesToTheDrilledObjectNotTheSameNamedPropertyOnTheRoot()
    {
        Host host = new();
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t =>
            {
                t.Property(h => h.Engine).WithDrillDown();
                t.Property(h => h.Name).AllowSet();
            });
            c.ConfigureDrillDown<Engine>(t => t.Property(e => e.Name).AllowSet());
        });

        OperationResponse response = DiagnosticManager.SetProperty(
            Registered(host),
            ["Svc|Host||Engine"],
            "DrillDown|Engine||Name",
            "Replaced"
        );

        response.ErrorMessage.Should().BeNull();
        response.IsSuccess.Should().BeTrue();
        host.Engine.Name.Should().Be("Replaced");
        host.Name.Should().Be("host");
    }

    [Fact]
    public void SetProperty_WithNoChain_BehavesAsTheMainView()
    {
        Host host = new();
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Name).AllowSet()));

        DiagnosticManager.SetProperty(Registered(host), [], "Svc|Host||Name", "Renamed").IsSuccess.Should().BeTrue();

        host.Name.Should().Be("Renamed");
    }

    [Fact]
    public void SetProperty_ThroughAChainThatNoLongerResolves_FailsAsALookupRatherThanThrowing()
    {
        OperationResponse response = DiagnosticManager.SetProperty(
            Registered(new Host()),
            ["Svc|Host||Missing"],
            "DrillDown|Engine||Vendor",
            "Replaced"
        );

        response.IsSuccess.Should().BeFalse();
        response.ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void ExecuteOperation_InsideADrillDown_RunsAgainstTheDrilledObject()
    {
        Host host = new();
        DiagnosticManager.Configure(c => c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown()));

        // Matched by signature, taken from the drilldown's own response: an operation set that did
        // not survive the drilldown render would leave nothing to invoke. Selected by name rather
        // than by Single(), because the drilled object renders its nested objects and their
        // operation sets too.
        DrillDownResponse drillDown = DrillTo(host, "Svc|Host||Engine");
        string signature = drillDown
            .Diagnostics.OperationSets.SelectMany(set => set.Operations)
            .Select(operation => operation.Signature)
            .Single(sig => sig.StartsWith(nameof(Engine.Restart), StringComparison.Ordinal));
        OperationResponse response = DiagnosticManager.ExecuteOperation(
            Registered(host),
            ["Svc|Host||Engine"],
            "DrillDown|Engine",
            signature,
            []
        );

        response.IsSuccess.Should().BeTrue();
        host.Engine.RestartCount.Should().Be(1);
        host.RestartCount.Should().Be(0);
    }

    /// <summary>
    ///     An operation inside a drilldown resolves its path by re-rendering the drilled object,
    ///     and that render has to use the drilldown configuration. The popup is the fuller of the
    ///     two views, so the object an operation runs on is routinely one only that configuration
    ///     places where the path names it.
    /// </summary>
    /// <remarks>
    ///     The category here exists ONLY under <c>ConfigureDrillDown</c>. Rendered in the main
    ///     view's mode the path cannot resolve at all, which is what separates this from the
    ///     bag-level case where both modes yield the same object.
    /// </remarks>
    [Fact]
    public void ExecuteOperation_InsideADrillDown_ResolvesAPathOnlyTheDrillDownConfigurationExposes()
    {
        Host host = new();
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Engine).WithDrillDown());
            c.ConfigureDrillDown<Engine>(t => t.Property(e => e.Vendor).WithCategory("Deep"));
        });

        OperationResponse response = DiagnosticManager.ExecuteOperation(
            Registered(host),
            ["Svc|Host||Engine"],
            "DrillDown|Engine|Deep|Vendor",
            "Refresh()",
            []
        );

        response.ErrorMessage.Should().BeNull();
        response.IsSuccess.Should().BeTrue();
        host.Engine.Vendor.RefreshCount.Should().Be(1);
    }

    /// <summary>
    ///     An item path names a position, and a position is not an identity. If the host drops an
    ///     earlier item between the render and the action, the same index points at a different
    ///     object - so the path must stop resolving rather than quietly address its neighbour.
    /// </summary>
    [Fact]
    public void SetProperty_ThroughAnItemPathWhoseCollectionShifted_FailsRatherThanHittingItsNeighbour()
    {
        Host host = new();
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Orders).WithDrillDown());
            c.ConfigureDrillDown<Order>(t => t.Property(o => o.Status).AllowSet());
        });

        // The path the browser would have been handed for the third order.
        string itemPath = ItemPath(DrillTo(host, "Svc|Host||Orders"), 2);
        Order intendedTarget = host.Orders[2];
        Order neighbour = host.Orders[3];

        host.Orders.RemoveAt(0);

        OperationResponse response = DiagnosticManager.SetProperty(
            Registered(host),
            ["Svc|Host||Orders", itemPath],
            "DrillDown|Order||Status",
            "Cancelled"
        );

        response.IsSuccess.Should().BeFalse();
        intendedTarget.Status.Should().Be("New");
        neighbour.Status.Should().Be("New");
    }

    /// <summary>The fence must not cost an unchanged collection its ordinary resolution.</summary>
    [Fact]
    public void SetProperty_ThroughAnItemPathOnAnUnchangedCollection_WritesToThatItem()
    {
        Host host = new();
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Host>(t => t.Property(h => h.Orders).WithDrillDown());
            c.ConfigureDrillDown<Order>(t => t.Property(o => o.Status).AllowSet());
        });

        string itemPath = ItemPath(DrillTo(host, "Svc|Host||Orders"), 2);

        OperationResponse response = DiagnosticManager.SetProperty(
            Registered(host),
            ["Svc|Host||Orders", itemPath],
            "DrillDown|Order||Status",
            "Cancelled"
        );

        response.ErrorMessage.Should().BeNull();
        host.Orders[2].Status.Should().Be("Cancelled");
        host.Orders[3].Status.Should().Be("New");
    }

    /// <summary>
    ///     The fence rides in the item name, so it must survive the round trip a client makes:
    ///     read the bag's category and name, join them into a path, send it back.
    /// </summary>
    private static string ItemPath(DrillDownResponse response, int index)
    {
        PropertyBag bag = response.Diagnostics.PropertyBags[index];
        bag.Name.Should().Contain(DiagnosticManager.ItemFenceSeparator.ToString());
        return $"{bag.Category}|{bag.Name}";
    }

    private sealed class Host
    {
        public string Name { get; set; } = "host";
        public Engine Engine { get; } = new();
        public Vendor Vendor { get; } = new() { Name = "Host vendor" };
        public List<Order> Orders { get; } = [.. Enumerable.Range(0, 5).Select(i => new Order { Id = i })];
        public IEnumerable<Order> Lazy => Orders.Where(o => o.Id < 4);
        public string[] Tags { get; } = ["alpha", "beta"];
        public string[] Bulk { get; } = [.. Enumerable.Repeat(new string('x', 1024), 1024)];
        public Node Cyclic { get; } = Node.Ring();
        public int RestartCount { get; private set; }

        [DiagnosticMethod]
        public void Restart() => RestartCount++;
    }

    private sealed class Engine
    {
        public string Name { get; set; } = "engine";
        public string SerialNumber { get; set; } = "SN-1";
        public int Rpm { get; set; } = 1500;
        public Vendor Vendor { get; } = new() { Name = "Engine vendor" };
        public int RestartCount { get; private set; }

        [DiagnosticMethod]
        public void Restart() => RestartCount++;
    }

    private sealed class Vendor
    {
        public string Name { get; set; } = "";
        public string Contact { get; set; } = "sales@example.com";
        public int RefreshCount { get; private set; }

        [DiagnosticMethod]
        public void Refresh() => RefreshCount++;
    }

    private sealed class Order
    {
        public int Id { get; set; }
        public string Status { get; set; } = "New";
    }

    private sealed class Node
    {
        public string Name { get; set; } = "";
        public Node? Next { get; set; }

        public static Node Ring()
        {
            Node first = new() { Name = "first" };
            first.Next = new Node { Name = "second", Next = first };
            return first;
        }
    }
}
