using log4net.Core;

namespace DiagnosticExplorer.Log4Net;

[DiagnosticClass(AttributedPropertiesOnly = true, DeclaringTypeOnly = false)]
public class ForwardingAppender : ForwardingAppenderBase
{
    protected override void Append(LoggingEvent loggingEvent)
    {
        Guard.NotNull(loggingEvent, nameof(loggingEvent));

        EventsIn.Register(1);

        PerformAppend(loggingEvent);
    }

    protected override void Append(LoggingEvent[] loggingEvents)
    {
        Guard.NotNull(loggingEvents, nameof(loggingEvents));

        if (loggingEvents.Length == 0)
        {
            throw new ArgumentException("loggingEvents array must not be empty", nameof(loggingEvents));
        }

        if (loggingEvents.Length == 1)
        {
            EventsIn.Register(1);
            PerformAppend(loggingEvents[0]);
            return;
        }

        EventsIn.Register(loggingEvents.Length);
        PerformAppend(loggingEvents);
    }

    protected void PerformAppend(LoggingEvent loggingEvent)
    {
        loggingEvent.Fix = FixFlags.All;
        List<AppenderProxy> proxies;
        lock (_lock)
        {
            proxies = Proxies;
        }

        if (proxies != null)
        {
            Parallel.ForEach(proxies, appender => PerformAppend(appender, loggingEvent));
        }
    }

    protected void PerformAppend(LoggingEvent[] loggingEvents)
    {
        foreach (var loggingEvent in loggingEvents)
        {
            loggingEvent.Fix = FixFlags.All;
        }

        List<AppenderProxy> proxies;
        lock (_lock)
        {
            proxies = Proxies;
        }

        if (proxies != null)
        {
            Parallel.ForEach(proxies, appender => PerformAppend(appender, loggingEvents));
        }
    }

    protected void PerformAppend(AppenderProxy appender, LoggingEvent loggingEvent)
    {
        if (appender.TryAppend(loggingEvent))
        {
            EventsOut.Register(1);
        }
        else
        {
            RecordAppenderError(appender);
            EventsErrored.Register(1);
        }
    }

    private void PerformAppend(AppenderProxy appender, LoggingEvent[] loggingEvents)
    {
        if (appender.TryAppend(loggingEvents))
        {
            EventsOut.Register(loggingEvents.Length);
        }
        else
        {
            EventsErrored.Register(loggingEvents.Length);
            RecordAppenderError(appender);
        }
    }

    private void RecordAppenderError(AppenderProxy appender)
    {
        LogLogError(GetType(), $"appender [{appender.Name}] has an error.");
    }
}
