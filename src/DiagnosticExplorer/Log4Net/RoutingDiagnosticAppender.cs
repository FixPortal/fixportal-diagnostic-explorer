using DiagnosticExplorer.Logging;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer.Log4Net;

/// <summary>
///     Routes log4net events into a <see cref="LogEventStore" /> through an
///     <see cref="EventSinkRouter" />, so a log4net host feeds the same live event stream the other
///     logging-framework adapters do.
/// </summary>
/// <remarks>
///     This appender lives in the <c>DiagnosticExplorer</c> assembly rather than a separate package
///     because log4net binds appenders by assembly-qualified name from XML configuration, and the
///     consuming estate has upwards of 1,700 such bindings naming this assembly.
/// </remarks>
public class RoutingDiagnosticAppender : AppenderSkeleton
{
    private const int MaxMessageLength = 150;
    private readonly LogEventStore _eventStore;
    private EventSinkRouter _router;
    private bool _closed;

    public RoutingDiagnosticAppender()
        : this(null) { }

    public RoutingDiagnosticAppender(LogEventStore eventStore)
    {
        _eventStore = eventStore ?? DiagnosticManager.LogEventStore;
        PatternLayout layout = new("%-4timestamp [%thread] %-5level %logger %ndc - %message%newline");
        layout.ActivateOptions();
        // ReSharper disable once VirtualMemberCallInConstructor
        // log4net declares Layout virtual and the base constructor leaves it unset; assigning it here is the documented way to install a layout.
        Layout = layout;
    }

    public string ConfigurationFile { get; set; } = "config.json";
    public string ConfigurationSection { get; set; } = "DiagnosticExplorer:Routing";
    public EventSinkRouteOptions RoutingOptions { get; set; }

    public override void ActivateOptions()
    {
        if (_closed)
        {
            return;
        }

        base.ActivateOptions();
        EventSinkRouteOptions options = RoutingOptions ?? LoadRoutingOptions();
        if (_router == null)
        {
            _router = new EventSinkRouter(options, _eventStore);
        }
        else
        {
            _router.Reconfigure(options);
        }
        _closed = false;
    }

    protected override void OnClose()
    {
        _router?.Dispose();
        _router = null;
        _closed = true;
        base.OnClose();
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        if (_router == null)
        {
            if (_closed)
            {
                return;
            }

            ActivateOptions();
        }
        LogLevel level = (LogLevel)loggingEvent.Level.ToMicrosoftOrdinal();
        // ReSharper disable once PossibleNullReferenceException
        // Only null when the appender is closed, and the branch above returns in that case.
        if (!_router.IsEnabled(loggingEvent.LoggerName, level))
        {
            return;
        }
        string renderedMessage = loggingEvent.RenderedMessage;
        _router.Route(
            new EventSinkLogEvent(loggingEvent.LoggerName, level, GetHeadline(renderedMessage), GetDetail(loggingEvent))
        );
    }

    private EventSinkRouteOptions LoadRoutingOptions()
    {
        if (string.IsNullOrWhiteSpace(ConfigurationFile))
        {
            throw new InvalidOperationException("A routing configuration file is required.");
        }
        if (string.IsNullOrWhiteSpace(ConfigurationSection))
        {
            throw new InvalidOperationException("A routing configuration section is required.");
        }
        if (!File.Exists(ConfigurationFile))
        {
            throw new FileNotFoundException("The routing configuration file was not found.", ConfigurationFile);
        }
        IConfiguration configuration = new ConfigurationBuilder()
            .AddJsonFile(ConfigurationFile, optional: false, reloadOnChange: false)
            .Build();
        IConfigurationSection section = configuration.GetSection(ConfigurationSection);
        if (!section.Exists())
        {
            throw new InvalidOperationException(
                $"The routing configuration section '{ConfigurationSection}' was not found in '{ConfigurationFile}'."
            );
        }
        return section.Get<EventSinkRouteOptions>()
            ?? throw new InvalidOperationException(
                $"The routing configuration section '{ConfigurationSection}' is invalid."
            );
    }

    private static string GetHeadline(string message)
    {
        if (string.IsNullOrEmpty(message))
        {
            return message;
        }
        int newLine = message.IndexOfAny(['\r', '\n']);
        if (newLine >= 0)
        {
            message = message.Substring(0, newLine);
        }
        return message.Length <= MaxMessageLength ? message : message.Substring(0, MaxMessageLength) + "...";
    }

    private string GetDetail(LoggingEvent loggingEvent)
    {
        string detail = RenderLoggingEvent(loggingEvent);

        // Guarded on the exception itself, matching the other three adapters. Upstream compares it
        // against MessageObject instead, which differs for every ordinary event, so an empty
        // exception plus a newline was appended to all of them.
        if (loggingEvent.ExceptionObject != null)
        {
            detail += Environment.NewLine + loggingEvent.ExceptionObject;
        }
        return detail;
    }
}
