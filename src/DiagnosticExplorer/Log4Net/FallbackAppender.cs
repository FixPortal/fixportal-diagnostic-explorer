using log4net.Core;
using log4net.Util;

namespace DiagnosticExplorer.Log4Net;

/// <summary>
///     This appender takes care of falling back to another appender if appending causes
///     an error
/// </summary>
[DiagnosticClass(AttributedPropertiesOnly = true, DeclaringTypeOnly = false)]
public class FallbackAppender : ForwardingAppenderBase
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

        if (proxies == null)
        {
            return;
        }

        var proxyQueue = new Queue<AppenderProxy>(proxies);

        while (proxyQueue.Count > 0)
        {
            var proxy = proxyQueue.Dequeue();

            if (proxy.TryAppend(loggingEvent))
            {
                EventsOut.Register(1);
                break;
            }

            EventsErrored.Register(1);
            RecordAppenderError(proxyQueue, proxy);
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

        if (proxies == null)
        {
            return;
        }

        EventsIn.Register(loggingEvents.Length);

        var appenderQueue = new Queue<AppenderProxy>(proxies);
        while (appenderQueue.Count > 0)
        {
            var appender = appenderQueue.Dequeue();

            if (appender.TryAppend(loggingEvents))
            {
                EventsOut.Register(loggingEvents.Length);
                break;
            }

            EventsErrored.Register(loggingEvents.Length);
            RecordAppenderError(appenderQueue, appender);
        }
    }

    private void RecordAppenderError(Queue<AppenderProxy> appenderQueue, AppenderProxy appender)
    {
        LogLogError(GetType(), $"appender [{appender.Name}] has an error.");
        if (appenderQueue.Count > 0)
        {
            var nextAppender = appenderQueue.Peek();
            LogLog.Debug(GetType(), $"Chaining through to appender [{nextAppender.Name}]");
        }
        else
        {
            LogLogError(GetType(), "No more appenders exist to chain through to");
        }
    }
}
