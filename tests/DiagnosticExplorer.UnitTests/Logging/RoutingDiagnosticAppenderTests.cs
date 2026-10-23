using AwesomeAssertions;
using DiagnosticExplorer.Log4Net;
using DiagnosticExplorer.Logging;
using log4net;
using log4net.Appender;
using log4net.Core;
using log4net.Repository;
using log4net.Repository.Hierarchy;
using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer.UnitTests.Logging;

/// <summary>
///     The log4net appender is the one adapter that stays inside the DiagnosticExplorer assembly,
///     because log4net binds appenders by assembly-qualified name from XML and the consuming estate
///     has some 1,700 such bindings naming it.
/// </summary>
/// <remarks>
///     Every test hands the appender its own store, so nothing here touches
///     <see cref="DiagnosticManager.LogEventStore" />.
/// </remarks>
[Collection(DiagnosticConfigurationCollection.Name)]
public class RoutingDiagnosticAppenderTests
{
    /// <summary>
    ///     The appender folds the log4net level before asking the router, so a Warn event has to
    ///     satisfy a route written in Microsoft levels. This is the seam where the two level
    ///     schemes meet and the easiest place for an off-by-one to hide.
    /// </summary>
    [Fact]
    public void Append_RoutesAMatchingEventIntoTheStore()
    {
        var store = new LogEventStore();
        var options = new EventSinkRouteOptions().Route(
            "Widgets",
            route => route.AtLeast(LogLevel.Warning).To("Widgets", "Widget Events")
        );
        var appender = new TestAppender(store) { RoutingOptions = options };
        appender.ActivateOptions();

        appender.AppendForTest(Event("Widgets.Component", Level.Warn, "Paint failed"));

        LogStreamEvent[] replay = Replay(store);
        replay.Should().ContainSingle();
        replay[0].LoggerCategory.Should().Be("Widgets.Component");
        replay[0].Level.Should().Be((int)LogLevel.Warning);
    }

    [Fact]
    public void Append_WhenNoRouteMatches_PublishesNothing()
    {
        var store = new LogEventStore();
        var options = new EventSinkRouteOptions().Route(
            "Widgets",
            route => route.AtLeast(LogLevel.Error).To("Widgets", "Widget Events")
        );
        var appender = new TestAppender(store) { RoutingOptions = options };
        appender.ActivateOptions();

        appender.AppendForTest(Event("Widgets.Component", Level.Info, "Painted"));

        Replay(store).Should().BeEmpty();
    }

    /// <summary>
    ///     log4net calls Append without necessarily having called ActivateOptions first — a
    ///     hand-constructed appender, or a configurator that skips it. Losing those events would be
    ///     silent, so the appender activates itself on first use.
    /// </summary>
    [Fact]
    public void Append_WithoutActivateOptions_StillRoutes()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };

        appender.AppendForTest(Event("Widgets", Level.Info, "Painted"));

        Replay(store).Should().ContainSingle();
    }

    /// <summary>
    ///     The event list shows one line per event, so a multi-line message is cut at the first
    ///     newline and a very long one is elided. The whole rendered message still reaches Detail.
    /// </summary>
    [Fact]
    public void Append_TruncatesTheHeadlineButKeepsTheWholeMessageInDetail()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();
        string message = "first line\r\nsecond line";

        appender.AppendForTest(Event("Widgets", Level.Info, message));

        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.Message.Should().Be("first line");
        published.Detail.Should().Contain("second line");
    }

    [Fact]
    public void Append_ElidesAHeadlineLongerThanTheLimit()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();

        appender.AppendForTest(Event("Widgets", Level.Info, new string('x', 200)));

        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.Message.Should().Be(new string('x', 150) + "...");
    }

    /// <summary>
    ///     ActivateOptions falls back to reading a JSON configuration file when no routes were set
    ///     in code. A missing file must fail loudly at configuration time rather than leaving an
    ///     appender that silently drops everything.
    /// </summary>
    [Fact]
    public void ActivateOptions_WithNoRoutesAndNoConfigurationFile_Throws()
    {
        var appender = new TestAppender(new LogEventStore())
        {
            ConfigurationFile = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json"),
        };

        Action activate = appender.ActivateOptions;

        activate.Should().Throw<FileNotFoundException>();
    }

    [Fact]
    public void ActivateOptions_ReadsRoutesFromAConfigurationFile()
    {
        var store = new LogEventStore();
        string path = Path.Join(Path.GetTempPath(), $"{Guid.NewGuid():N}.json");
        File.WriteAllText(
            path,
            """
            {
              "DiagnosticExplorer": {
                "Routing": {
                  "Routes": [
                    { "CategoryPattern": "Widgets", "Destinations": [ "Widgets/Widget Events" ] }
                  ]
                }
              }
            }
            """
        );

        try
        {
            var appender = new TestAppender(store) { ConfigurationFile = path };
            appender.ActivateOptions();

            appender.AppendForTest(Event("Widgets", Level.Info, "Painted"));

            Replay(store).Should().ContainSingle();
        }
        finally
        {
            File.Delete(path);
        }
    }

    /// <summary>
    ///     An event with no exception must not have one appended. log4net leaves ExceptionObject
    ///     null in that case, and the guard has to test that directly — comparing it against
    ///     MessageObject instead is true for every ordinary event, which appends a trailing blank
    ///     line to all of them.
    /// </summary>
    [Fact]
    public void Append_WithNoException_LeavesNothingAppendedToTheDetail()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();

        appender.AppendForTest(Event("Widgets", Level.Info, "Painted"));

        // The layout ends every rendered event with a newline, so the detail ending in exactly one
        // is the assertion: a second one is the appended empty exception.
        LogStreamEvent published = Replay(store).Should().ContainSingle().Subject;
        published.Detail.Should().EndWith("Painted" + Environment.NewLine);
    }

    [Fact]
    public void Append_WithAnException_AppendsItToTheDetail()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();

        appender.AppendForTest(Event("Widgets", Level.Error, "Paint failed", new InvalidOperationException("boom")));

        Replay(store).Should().ContainSingle().Subject.Detail.Should().Contain("boom");
    }

    [Fact]
    public void Close_RemovesTheAppendersRoutingContribution()
    {
        var store = new LogEventStore();
        var appender = new TestAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();

        appender.Close();

        store.CreateInitialization().Routing.Routes.Should().BeEmpty();
    }

    [Fact]
    public void ActivateOptions_AfterTerminalClose_DoesNotRegisterAnUnusableAppender()
    {
        var store = new LogEventStore();
        var appender = new RoutingDiagnosticAppender(store)
        {
            RoutingOptions = new EventSinkRouteOptions().Route(
                "Widgets",
                route => route.To("Widgets", "Widget Events")
            ),
        };
        appender.ActivateOptions();
        appender.DoAppend(Event("Widgets", Level.Info, "before close"));
        appender.Close();

        appender.ActivateOptions();
        appender.DoAppend(Event("Widgets", Level.Info, "after close"));

        store.CreateInitialization().Routing.Routes.Should().BeEmpty();
        Replay(store).Select(@event => @event.Message).Should().Equal("before close");
    }

    [Fact]
    public void ConfigureDiagnosticExplorer_PreservesExistingAppendersAndReplacesOnlyItsOwn()
    {
        ILoggerRepository repository = LogManager.CreateRepository(Guid.NewGuid().ToString("N"), typeof(Hierarchy));
        var hierarchy = (Hierarchy)repository;
        var existing = new MemoryAppender { Name = "DiagnosticExplorer.Routing" };
        existing.ActivateOptions();
        hierarchy.Root.Level = Level.All;
        hierarchy.Root.AddAppender(existing);
        hierarchy.Configured = true;

        try
        {
            DiagnosticManager.Configure(configure =>
                configure.ConfigureEventRouting(routes => routes.Route("Widgets", route => route.To("Logs", "App")))
            );

            repository.ConfigureDiagnosticExplorer();
            repository.ConfigureDiagnosticExplorer();
            LogManager.GetLogger(repository.Name, "Widgets").Info("survives");

            hierarchy.Root.Appenders.Should().Contain(existing);
            hierarchy.Root.Appenders.OfType<RoutingDiagnosticAppender>().Should().ContainSingle();
            existing.GetEvents().Should().ContainSingle();
        }
        finally
        {
            LogManager.ShutdownRepository(repository.Name);
            DiagnosticManager.UseConfiguration(new DiagnosticConfiguration());
        }
    }

    private static LoggingEvent Event(string loggerName, Level level, string message, Exception? exception = null)
    {
        ILoggerRepository repository = LogManager.GetRepository(typeof(RoutingDiagnosticAppenderTests).Assembly);
        return new LoggingEvent(
            typeof(RoutingDiagnosticAppenderTests),
            repository,
            loggerName,
            level,
            message,
            exception
        );
    }

    private static LogStreamEvent[] Replay(LogEventStore store)
    {
        using LogEventStore.LogEventStoreSubscription subscription = store.CreateSubscription();
        return subscription.Initialization!.ReplayEvents;
    }

    /// <summary>Exposes the protected Append so a test can drive the appender directly.</summary>
    private sealed class TestAppender : RoutingDiagnosticAppender
    {
        public TestAppender(LogEventStore store)
            : base(store) { }

        public void AppendForTest(LoggingEvent loggingEvent) => Append(loggingEvent);
    }
}
