using AwesomeAssertions;
using DiagnosticExplorer.Logging;
using DiagnosticExplorer.NLog;
using NLog;
using NLog.Config;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;
using NLogLevel = NLog.LogLevel;

namespace DiagnosticExplorer.UnitTests.Logging;

/// <summary>
///     The NLog target. NLog has six levels that map one-for-one onto the Microsoft scheme, so the
///     interesting parts here are the fallback category (NLog permits an empty logger name, the
///     router does not) and NLog's own event properties reaching the detail pane.
/// </summary>
/// <remarks>
///     Each test builds its own <see cref="LogFactory" /> rather than touching
///     <see cref="LogManager" />, whose configuration is process-wide static state that parallel
///     test classes would contend over.
/// </remarks>
public class DiagnosticExplorerTargetTests
{
    private static EventSinkRouteOptions RoutesFor(string pattern) =>
        new EventSinkRouteOptions().Route(pattern, route => route.To("Logs", "App"));

    [Fact]
    public void Write_PublishesAMatchingEventWithItsCategoryAndLevel()
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("Widgets"), store);

        factory.GetLogger("Widgets.Component").Warn("Paint failed");

        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.LoggerCategory.Should().Be("Widgets.Component");
        published.Level.Should().Be((int)MicrosoftLogLevel.Warning);
        published.Message.Should().Be("Paint failed");
    }

    [Fact]
    public void Write_WhenNoRouteMatches_PublishesNothing()
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("Widgets"), store);

        factory.GetLogger("Gadgets").Warn("Paint failed");

        Replay(store).Should().BeEmpty();
    }

    [Theory]
    [InlineData("Trace", MicrosoftLogLevel.Trace)]
    [InlineData("Debug", MicrosoftLogLevel.Debug)]
    [InlineData("Info", MicrosoftLogLevel.Information)]
    [InlineData("Warn", MicrosoftLogLevel.Warning)]
    [InlineData("Error", MicrosoftLogLevel.Error)]
    [InlineData("Fatal", MicrosoftLogLevel.Critical)]
    public void Write_MapsEveryNLogLevel(string levelName, MicrosoftLogLevel expected)
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("*"), store);

        factory.GetLogger("Widgets").Log(NLogLevel.FromString(levelName), "message");

        Replay(store).Should().ContainSingle().Subject.Level.Should().Be((int)expected);
    }

    /// <summary>
    ///     NLog lets an event carry no logger name. The router matches on category, so an empty one
    ///     would silently never match any route; the fallback keeps those events reachable.
    /// </summary>
    [Fact]
    public void Write_WhenTheLoggerNameIsEmpty_UsesTheFallbackCategory()
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("Application"), store);

        factory.GetLogger(string.Empty).Info("message");

        Replay(store).Should().ContainSingle().Subject.LoggerCategory.Should().Be("Application");
    }

    [Fact]
    public void Write_FoldsEventPropertiesAndTheExceptionIntoDetail()
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("Widgets"), store);
        var logEvent = new LogEventInfo(NLogLevel.Error, "Widgets", "Paint failed")
        {
            Exception = new InvalidOperationException("boom"),
        };
        logEvent.Properties["WidgetId"] = 42;

        factory.GetLogger("Widgets").Log(logEvent);

        string detail = Replay(store).Should().ContainSingle().Subject.Detail!;
        detail.Should().Contain("Property.WidgetId: 42").And.Contain("boom");
    }

    /// <summary>
    ///     A multi-line message is cut at the first newline for the event list, with the whole
    ///     message kept in the detail pane.
    /// </summary>
    [Fact]
    public void Write_SplitsAMultiLineMessageIntoHeadlineAndDetail()
    {
        var store = new LogEventStore();
        using LogFactory factory = FactoryFor(RoutesFor("Widgets"), store);

        factory.GetLogger("Widgets").Info("first line\nsecond line");

        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.Message.Should().Be("first line");
        published.Detail.Should().Contain("second line");
    }

    /// <summary>
    ///     NLog constructs targets from XML configuration through the parameterless constructor, so
    ///     the default has to be usable — publishing into the process-wide stream, matching nothing
    ///     until routes are configured.
    /// </summary>
    [Fact]
    public void ParameterlessConstructor_ProducesATargetWithNoRoutes()
    {
        var target = new DiagnosticExplorerTarget();

        target.Options.Routes.Should().BeEmpty();
        target.FallbackCategory.Should().Be("Application");
    }

    [Fact]
    public void AddDiagnosticExplorer_RegistersTheTargetUnderTheGivenName()
    {
        LoggingConfiguration configuration = new();

        DiagnosticExplorerTarget target = configuration.AddDiagnosticExplorer("Diags", RoutesFor("*"));

        configuration.FindTargetByName("Diags").Should().BeSameAs(target);
    }

    [Fact]
    public void AddDiagnosticExplorer_WithoutOptions_UsesTheCurrentConfiguration()
    {
        try
        {
            DiagnosticManager.Configure(configure =>
                configure.ConfigureEventRouting(routes => routes.Route("Widgets", route => route.To("Logs", "App")))
            );
            LoggingConfiguration configuration = new();

            LoggingConfiguration returned = configuration.AddDiagnosticExplorer();

            returned.Should().BeSameAs(configuration);
            configuration.FindTargetByName("DiagnosticExplorer").Should().NotBeNull();
            int before = DiagnosticManager.LogEventStore.CreateInitialization().ReplayEvents.Length;
            using (LogFactory factory = new())
            {
                factory.Configuration = configuration;
                factory.GetLogger("Widgets").Info("Painted");
            }

            DiagnosticManager.LogEventStore.CreateInitialization().ReplayEvents.Should().HaveCount(before + 1);
        }
        finally
        {
            DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());
        }
    }

    [Fact]
    public void ClosingTheTarget_RemovesItsRoutingContribution()
    {
        var store = new LogEventStore();
        LogFactory factory = FactoryFor(RoutesFor("Widgets"), store);

        factory.Dispose();

        store.CreateInitialization().Routing.Routes.Should().BeEmpty();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void AddDiagnosticExplorer_WithoutATargetName_Throws(string? targetName)
    {
        LoggingConfiguration configuration = new();

        Action add = () => configuration.AddDiagnosticExplorer(targetName!, RoutesFor("*"));

        add.Should().Throw<ArgumentException>();
    }

    private static LogFactory FactoryFor(EventSinkRouteOptions options, LogEventStore store)
    {
        var target = new DiagnosticExplorerTarget(options, store);
        LoggingConfiguration configuration = new();
        configuration.AddTarget("DiagnosticExplorer", target);
        configuration.AddRuleForAllLevels(target);
        return new LogFactory { Configuration = configuration };
    }

    private static LogStreamEvent[] Replay(LogEventStore store)
    {
        using LogEventStore.LogEventStoreSubscription subscription = store.CreateSubscription();
        return subscription.Initialization!.ReplayEvents;
    }
}
