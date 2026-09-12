using AwesomeAssertions;
using DiagnosticExplorer.Logging;
using Microsoft.Extensions.Logging;

// Fixture properties are consumed through reflection by DiagnosticManager.
// ReSharper disable UnusedMember.Local

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     The fluent configuration surface, asserted end to end: configure, render, read the output.
/// </summary>
/// <remarks>
///     Every test here goes through <see cref="DiagnosticManager.ObjectToPropertyBag" /> rather than
///     inspecting the configuration object. The risk this phase carried was a configuration API that
///     compiled and was never consulted — a host could configure everything and get silently
///     nothing — so asserting that the configuration was *stored* would have proved precisely the
///     wrong thing.
/// </remarks>
[Collection(DiagnosticConfigurationCollection.Name)]
public sealed class FluentConfigurationTests : IDisposable
{
    /// <summary>
    ///     Configuration is process-wide static state, and so is the getter cache it feeds. Each
    ///     test restores the default so ordering cannot leak one test's configuration into another.
    /// </summary>
    public void Dispose() => DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());

    private static Property[] Render(object obj) =>
        DiagnosticManager.ObjectToPropertyBag(obj, "svc", null).Categories.SelectMany(c => c.Properties).ToArray();

    [Fact]
    public void Configure_RenamingAProperty_ChangesTheRenderedName()
    {
        DiagnosticManager.Configure(c =>
            c.Configure<Widget>(t => t.Property(w => w.Serial).WithLabel("Serial number"))
        );

        Render(new Widget()).Select(p => p.Name).Should().Contain("Serial number").And.NotContain("Serial");
    }

    [Fact]
    public void Configure_SettingADescriptionAndCategory_ReachesTheRenderedProperty()
    {
        DiagnosticManager.Configure(c =>
            c.Configure<Widget>(t => t.Property(w => w.Serial).Description("The unit serial").WithCategory("Identity"))
        );

        PropertyBag bag = DiagnosticManager.ObjectToPropertyBag(new Widget(), "svc", null);

        Category identity = bag.Categories.FindByName("Identity");
        identity.Should().NotBeNull();
        identity.Properties.Single(p => p.Name == "Serial").Description.Should().Be("The unit serial");
    }

    /// <summary>Excluding a property must actually remove it, not merely record the intent.</summary>
    [Fact]
    public void Configure_ExcludingAProperty_RemovesItFromTheOutput()
    {
        DiagnosticManager.Configure(c => c.Configure<Widget>(t => t.Exclude(w => w.Serial)));

        Render(new Widget()).Select(p => p.Name).Should().NotContain("Serial");
    }

    /// <summary>
    ///     A delegate property has no PropertyInfo behind it, so it reaches the getter pipeline by a
    ///     different route than an attributed one — the branch that would silently render nothing if
    ///     the configuration were not consulted for delegate and custom properties too.
    /// </summary>
    [Fact]
    public void Configure_ADelegateProperty_IsRenderedAlongsideTheRealOnes()
    {
        DiagnosticManager.Configure(c =>
            c.Configure<Widget>(t => t.Property("Computed", w => w.Serial.Length).WithLabel("Serial length"))
        );

        Property computed = Render(new Widget()).Single(p => p.Name == "Serial length");

        computed.Value.Should().Be("4");
    }

    /// <summary>
    ///     adversarial-review 20260912T091500Z, H1: a delegate property that resolves to
    ///     <see cref="PropertyStrategy.Collection" /> has no <c>PropertyInfo</c>, and
    ///     <see cref="CollectionGetter" />'s constructor used to dereference it unconditionally --
    ///     one such property blanked every property of the type on every render.
    /// </summary>
    [Fact]
    public void Configure_ADelegateCollectionProperty_RendersACountInsteadOfThrowing()
    {
        DiagnosticManager.Configure(c =>
        {
            c.Configure<Widget>(t => t.Exclude(w => w.Tags));
            c.Configure<Widget>(t => t.Property("Tag count", w => w.Tags));
        });

        Property tags = Render(new Widget()).Single(p => p.Name == "Tag count");

        tags.Value.Should().Be("2");
    }

    /// <summary>
    ///     Getters are built once per type and bake the configuration in, so reconfiguring has to
    ///     invalidate the cache. Without that, a second configuration applies to types nothing has
    ///     touched yet and silently not to the rest.
    /// </summary>
    [Fact]
    public void UseConfiguration_AfterATypeHasAlreadyRendered_AppliesToTheNextRender()
    {
        Render(new Widget()).Select(p => p.Name).Should().Contain("Serial");

        DiagnosticManager.Configure(c => c.Configure<Widget>(t => t.Property(w => w.Serial).WithLabel("Renamed")));

        Render(new Widget()).Select(p => p.Name).Should().Contain("Renamed").And.NotContain("Serial");
    }

    /// <summary>
    ///     ExcludeAll then Include is the opt-in shape: everything off, then a named few back on.
    ///     It only works if an explicit Include outranks the blanket rule, so both halves are
    ///     asserted in one test.
    /// </summary>
    [Fact]
    public void Configure_ExcludeAllThenInclude_KeepsOnlyTheNamedProperty()
    {
        DiagnosticManager.Configure(c => c.Configure<Widget>(t => t.ExcludeAll().Include(w => w.Serial)));

        Render(new Widget()).Select(p => p.Name).Should().Equal("Serial");
    }

    /// <summary>
    ///     A custom property is neither a real property nor a delegate over one; it reaches the
    ///     pipeline through CustomPropertyGetter, a third route that would silently render nothing
    ///     if custom properties were not read off the type configuration.
    /// </summary>
    /// <remarks>
    ///     It renders under its own name. The inner projection is inlined only when the custom
    ///     property is configured expanded, which is a separate path through IInlineCustomObject.
    /// </remarks>
    [Fact]
    public void Configure_ACustomProperty_IsRendered()
    {
        DiagnosticManager.Configure(c =>
            c.Configure<Widget>(t => t.Custom("Summary", o => o.Property("Label", w => w.Serial)))
        );

        Render(new Widget()).Select(p => p.Name).Should().Contain("Summary");
    }

    /// <summary>
    ///     DiagnosticManager.Enabled is a public, directly-settable toggle. Applying a configuration
    ///     that says nothing about it must leave it alone — otherwise a host that turned diagnostics
    ///     off, then reconfigured something unrelated, would silently have them switched back on.
    /// </summary>
    [Fact]
    public void UseConfiguration_SayingNothingAboutEnabled_LeavesItAlone()
    {
        bool original = DiagnosticManager.Enabled;
        try
        {
            DiagnosticManager.Enabled = false;

            DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());

            DiagnosticManager.Enabled.Should().BeFalse();
        }
        finally
        {
            DiagnosticManager.Enabled = original;
        }
    }

    /// <summary>And it is still applied when the configuration does say so.</summary>
    [Fact]
    public void UseConfiguration_ConfiguringEnabled_AppliesIt()
    {
        bool original = DiagnosticManager.Enabled;
        try
        {
            DiagnosticManager.Configure(c => c.ConfigureHosting(h => h.Enabled(false)));

            DiagnosticManager.Enabled.Should().BeFalse();
        }
        finally
        {
            DiagnosticManager.Enabled = original;
        }
    }

    [Fact]
    public void UseConfiguration_AppliesEventRetentionToTheDefaultRepo()
    {
        DiagnosticManager.Configure(configure =>
            configure.ConfigureHosting(hosting =>
                hosting.EventRetention(retention => retention.WithMaxEventsPerSink(1))
            )
        );
        EventSink sink = EventSinkRepo.Default.GetSink(Guid.NewGuid().ToString("N"), "Configured");

        sink.Info("one");
        sink.Info("two");

        sink.Events.Select(@event => @event.Message).Should().Equal("two");
    }

    [Fact]
    public void GetRegisteredObjects_WithoutAServiceProvider_RerunsExplicitRegistrationCallbacks()
    {
        var firstWidget = new Widget();
        var secondWidget = new Widget();
        int calls = 0;
        DiagnosticManager.Configure(configure =>
            configure.RegisterObjects(registrar =>
            {
                calls++;
                registrar.Register(calls == 1 ? firstWidget : secondWidget, "Configured", "Widget");
            })
        );

        RegisteredObject[] first = DiagnosticManager.GetRegisteredObjects();
        RegisteredObject[] second = DiagnosticManager.GetRegisteredObjects();

        calls.Should().Be(2);
        first.Should().ContainSingle().Which.Object.Should().BeSameAs(firstWidget);
        second.Should().ContainSingle().Which.Object.Should().BeSameAs(secondWidget);
    }

    [Fact]
    public void GetRegisteredObjects_WithAServiceProvider_ResolvesConfiguredServices()
    {
        var widget = new Widget();
        DiagnosticManager.Configure(configure =>
            configure.RegisterObjects(registrar => registrar.RegisterService<Widget>("Configured", "Service"))
        );

        RegisteredObject[] registered = DiagnosticManager.GetRegisteredObjects(new SingleServiceProvider(widget));

        registered.Should().ContainSingle().Which.Object.Should().BeSameAs(widget);
    }

    [Fact]
    public void GetRegisteredObjects_WithoutAServiceProvider_ExplainsHowToRegisterDynamicServices()
    {
        DiagnosticManager.Configure(configure =>
            configure.RegisterObjects(registrar => registrar.RegisterService<Widget>("Configured", "Service"))
        );

        Action get = () => DiagnosticManager.GetRegisteredObjects();

        get.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("*RegisterService requires an IServiceProvider from DI hosting*RegisterObjects/Register*");
    }

    [Fact]
    public void GetRegisteredObjects_WithAProviderMissingTheService_IdentifiesTheServiceType()
    {
        DiagnosticManager.Configure(configure =>
            configure.RegisterObjects(registrar => registrar.RegisterService<Widget>("Configured", "Service"))
        );

        Action get = () => DiagnosticManager.GetRegisteredObjects(new EmptyServiceProvider());

        get.Should()
            .Throw<InvalidOperationException>()
            .WithMessage("No service for type '*Widget*' has been registered.");
    }

    [Fact]
    public void UseConfiguration_WithAConflictingLiveRouter_LeavesTheCurrentConfigurationUntouched()
    {
        DiagnosticConfiguration current = DiagnosticManager.CurrentConfiguration;
        using var router = new EventSinkRouter(
            new EventSinkRouteOptions()
                .UseMatchMode(EventSinkRouteMatchMode.FirstMatch)
                .Route("Existing", route => route.To("Logs", "Existing")),
            DiagnosticManager.LogEventStore
        );
        var replacement = new DiagnosticConfiguration();
        replacement.ConfigureEventRouting(routes =>
            routes
                .UseMatchMode(EventSinkRouteMatchMode.AllMatches)
                .Route("Replacement", route => route.To("Logs", "Replacement"))
        );

        Action use = () => DiagnosticManager.UseConfiguration(replacement);

        use.Should().Throw<InvalidOperationException>();
        DiagnosticManager.CurrentConfiguration.Should().BeSameAs(current);
    }

    [Fact]
    public void Configure_WithoutAConfigureAction_Throws()
    {
        Action configure = () => DiagnosticManager.Configure(null!);

        configure.Should().Throw<ArgumentNullException>();
    }

    /// <summary>
    ///     An unattributed DateTime renders its date by default. Routing dates through the
    ///     configuration pipeline must not change that: DateGetter defaults ExposeDate to true while
    ///     DatePropertyAttribute defaults it to false, so handing the getter a default attribute
    ///     instead of null would switch the date off for every unattributed date property.
    /// </summary>
    [Fact]
    public void UnattributedDateProperty_StillRendersItsDate()
    {
        Render(new Widget()).Select(p => p.Name).Should().Contain("Created");
    }

#pragma warning disable S1144, S2325
    private sealed class Widget
    {
        public string Serial => "AB12";
        public DateTime Created => new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        public List<string> Tags => ["a", "b"];
    }

    private sealed class SingleServiceProvider : IServiceProvider
    {
        private readonly Widget _widget;

        public SingleServiceProvider(Widget widget) => _widget = widget;

        public object? GetService(Type serviceType) => serviceType == typeof(Widget) ? _widget : null;
    }

    private sealed class EmptyServiceProvider : IServiceProvider
    {
        public object? GetService(Type serviceType) => null;
    }
#pragma warning restore S1144, S2325
}
