using DiagnosticExplorer.Logging;
using Serilog;
using Serilog.Configuration;
using Serilog.Core;
using Serilog.Events;

namespace DiagnosticExplorer.Serilog;

/// <summary>
///     Registers <see cref="DiagnosticExplorerSink" /> on a Serilog <see cref="LoggerConfiguration" />.
/// </summary>
public static class LoggerSinkConfigurationExtensions
{
    public static LoggerConfiguration DiagnosticExplorer(
        this LoggerSinkConfiguration sinkConfiguration,
        string fallbackCategory = "Application",
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null
    )
    {
        return sinkConfiguration.DiagnosticExplorer(
            DiagnosticManager.CurrentConfiguration.RuntimeOptions.Routing,
            fallbackCategory,
            restrictedToMinimumLevel,
            levelSwitch
        );
    }

    /// <param name="sinkConfiguration">The sink configuration being extended.</param>
    /// <param name="options">The routes that decide which sink and category each event is filed under.</param>
    /// <param name="fallbackCategory">The category used when no route claims an event.</param>
    /// <param name="restrictedToMinimumLevel">The minimum level for events passed to the sink.</param>
    /// <param name="levelSwitch">A switch that changes the minimum level at runtime.</param>
    /// <param name="eventStore">
    ///     The store to publish into. Defaults to <see cref="DiagnosticManager.LogEventStore" />, the
    ///     process-wide stream. Upstream's overload has no such parameter; it is here so a host — or
    ///     a test — can target a stream of its own.
    /// </param>
    public static LoggerConfiguration DiagnosticExplorer(
        this LoggerSinkConfiguration sinkConfiguration,
        EventSinkRouteOptions options,
        string fallbackCategory = "Application",
        LogEventLevel restrictedToMinimumLevel = LevelAlias.Minimum,
        LoggingLevelSwitch? levelSwitch = null,
        LogEventStore? eventStore = null
    )
    {
        if (sinkConfiguration == null)
        {
            throw new ArgumentNullException(nameof(sinkConfiguration));
        }

        return sinkConfiguration.Sink(
            new DiagnosticExplorerSink(options, fallbackCategory, eventStore),
            restrictedToMinimumLevel,
            levelSwitch
        );
    }
}
