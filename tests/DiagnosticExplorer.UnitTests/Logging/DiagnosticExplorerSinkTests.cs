using AwesomeAssertions;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using DiagnosticExplorer.Serilog;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using MicrosoftLogLevel = Microsoft.Extensions.Logging.LogLevel;

namespace DiagnosticExplorer.UnitTests.Logging;

/// <summary>
///     The Serilog sink. Serilog has no logger name — the nearest thing is the SourceContext
///     property that <c>ForContext&lt;T&gt;</c> attaches — so deriving a routable category from an
///     event is this adapter's distinguishing job.
/// </summary>
[Collection(DiagnosticConfigurationCollection.Name)]
public class DiagnosticExplorerSinkTests
{
    private static EventSinkRouteOptions RoutesFor(string pattern) =>
        new EventSinkRouteOptions().Route(pattern, route => route.To("Logs", "App"));

    [Fact]
    public void Emit_UsesSourceContextAsTheCategory()
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("Widgets"), store);

        logger.ForContext("SourceContext", "Widgets.Component").Warning("Paint failed");

        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.LoggerCategory.Should().Be("Widgets.Component");
        published.Level.Should().Be((int)MicrosoftLogLevel.Warning);
        published.Message.Should().Be("Paint failed");
    }

    /// <summary>
    ///     An event with no SourceContext — anything logged off the bare root logger — still has to
    ///     land somewhere routable rather than being dropped for having an empty category.
    /// </summary>
    [Fact]
    public void Emit_WithoutSourceContext_UsesTheFallbackCategory()
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("Application"), store);

        logger.Information("Painted");

        Replay(store).Should().ContainSingle().Subject.LoggerCategory.Should().Be("Application");
    }

    [Fact]
    public void Emit_WhenNoRouteMatches_PublishesNothing()
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("Widgets"), store);

        logger.ForContext("SourceContext", "Gadgets").Warning("Paint failed");

        Replay(store).Should().BeEmpty();
    }

    [Theory]
    [InlineData(LogEventLevel.Verbose, MicrosoftLogLevel.Trace)]
    [InlineData(LogEventLevel.Debug, MicrosoftLogLevel.Debug)]
    [InlineData(LogEventLevel.Information, MicrosoftLogLevel.Information)]
    [InlineData(LogEventLevel.Warning, MicrosoftLogLevel.Warning)]
    [InlineData(LogEventLevel.Error, MicrosoftLogLevel.Error)]
    [InlineData(LogEventLevel.Fatal, MicrosoftLogLevel.Critical)]
    public void Emit_MapsEverySerilogLevel(LogEventLevel level, MicrosoftLogLevel expected)
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("*"), store);

        logger.Write(level, "message");

        Replay(store).Should().ContainSingle().Subject.Level.Should().Be((int)expected);
    }

    /// <summary>
    ///     Structured properties reach the detail pane, minus SourceContext, which is already shown
    ///     as the event's category.
    /// </summary>
    [Fact]
    public void Emit_FoldsPropertiesIntoDetailButDropsSourceContext()
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("Widgets"), store);

        logger.ForContext("SourceContext", "Widgets").Information("Painted {WidgetId}", 42);

        string detail = Replay(store).Should().ContainSingle().Subject.Detail!;
        detail.Should().Contain("Property.WidgetId: 42");
        detail.Should().NotContain("SourceContext");
    }

    [Fact]
    public void Emit_FoldsTheExceptionIntoDetail()
    {
        var store = new LogEventStore();
        using Logger logger = LoggerFor(RoutesFor("*"), store);

        logger.Error(new InvalidOperationException("boom"), "Paint failed");

        Replay(store).Should().ContainSingle().Subject.Detail.Should().Contain("boom");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public void Constructor_WithoutAFallbackCategory_Throws(string? fallbackCategory)
    {
        Action construct = () => _ = new DiagnosticExplorerSink(RoutesFor("*"), fallbackCategory!);

        construct.Should().Throw<ArgumentException>();
    }

    [Fact]
    public void Dispose_RemovesTheSinksRoutingContribution()
    {
        var store = new LogEventStore();
        var sink = new DiagnosticExplorerSink(RoutesFor("Widgets"), eventStore: store);

        sink.Dispose();

        store.CreateInitialization().Routing.Routes.Should().BeEmpty();
    }

    [Fact]
    public void DiagnosticExplorer_WithoutOptions_UsesTheCurrentConfiguration()
    {
        try
        {
            DiagnosticManager.Configure(configure =>
                configure.ConfigureEventRouting(routes => routes.Route("Widgets", route => route.To("Logs", "App")))
            );
            int before = DiagnosticManager.LogEventStore.CreateInitialization().ReplayEvents.Length;
            using Logger logger = new LoggerConfiguration().WriteTo.DiagnosticExplorer().CreateLogger();

            logger.ForContext("SourceContext", "Widgets").Information("Painted");

            DiagnosticManager.LogEventStore.CreateInitialization().ReplayEvents.Should().HaveCount(before + 1);
        }
        finally
        {
            DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());
        }
    }

    /// <summary>
    ///     Goes through the WriteTo.DiagnosticExplorer extension rather than constructing the sink
    ///     directly, so the registration path is covered too.
    /// </summary>
    private static Logger LoggerFor(EventSinkRouteOptions options, LogEventStore store) =>
        new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.DiagnosticExplorer(options, eventStore: store)
            .CreateLogger();

    private static LogStreamEvent[] Replay(LogEventStore store)
    {
        using LogEventStore.LogEventStoreSubscription subscription = store.CreateSubscription();
        return subscription.Initialization!.ReplayEvents;
    }
}
