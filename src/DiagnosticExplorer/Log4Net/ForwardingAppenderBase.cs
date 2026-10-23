#nullable enable annotations

using System.Diagnostics;
using log4net.Appender;
using log4net.Util;

namespace DiagnosticExplorer.Log4Net;

[DiagnosticClass(AttributedPropertiesOnly = true, DeclaringTypeOnly = false)]
public abstract class ForwardingAppenderBase : log4net.Appender.ForwardingAppender
{
    protected readonly object _lock = new();

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsIn { get; set; } = new(3);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsOut { get; set; } = new(3);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsErrored { get; set; } = new(3);

    [Property]
    public string Type => GetType().Name;

    [CollectionProperty(CollectionMode.Categories, CategoryProperty = nameof(AppenderProxy.Name))]
    public List<AppenderProxy>? Proxies { get; private set; }

    /// <summary>
    ///     Used to specify the amount of minutes timeout to wait for before resetting that an error occurred on an
    ///     appender.
    /// </summary>
    [Property]
    public TimeSpan FailTimeout { get; set; } = TimeSpan.FromSeconds(-1);

    /// <summary>
    ///     Wraps the appenders in the corresponding <see cref="AppenderProxy" />
    /// </summary>
    public override void ActivateOptions()
    {
        base.ActivateOptions();

        if (FailTimeout < TimeSpan.Zero)
        {
            FailTimeout = Appenders.Count == 1 ? TimeSpan.Zero : TimeSpan.FromMinutes(5);
        }

        Proxies = Appenders.Select(a => new AppenderProxy(a, FailTimeout)).ToList();
        DiagnosticManager.Register(this, Name, "Log4Net");
    }

    public static void LogLogError(Type type, string msg, Exception? exception = null)
    {
        // Route to log4net's internal log so appender/forwarding/async failures are visible
        // in Release. Previously this ignored msg and only Debug.WriteLine'd a non-null
        // exception, so dropped logs and failover problems left no trace.
        Debug.WriteLine($"{msg} {exception}");
        LogLog.Error(type, msg, exception);
    }

    protected override void OnClose()
    {
        base.OnClose();
        DiagnosticManager.Unregister(this);
    }

    public override void AddAppender(IAppender appender)
    {
        base.AddAppender(appender);
        if (Proxies != null)
        {
            lock (_lock)
            {
                if (!Proxies.Any(p => ReferenceEquals(p.RawAppender, appender)))
                {
                    var newProxies = new List<AppenderProxy>(Proxies) { new(appender, FailTimeout) };
                    Proxies = newProxies;
                }
            }
        }
    }

    public override IAppender RemoveAppender(IAppender appender)
    {
        var removed = base.RemoveAppender(appender);
        if (Proxies != null && removed != null)
        {
            lock (_lock)
            {
                var newProxies = Proxies.Where(p => !ReferenceEquals(p.RawAppender, removed)).ToList();
                Proxies = newProxies;
            }
        }

        // ReSharper disable once AssignNullToNotNullAttribute -- log4net returns null when absent.
        return removed;
    }

    public override IAppender RemoveAppender(string name)
    {
        var removed = base.RemoveAppender(name);
        if (Proxies != null && removed != null)
        {
            lock (_lock)
            {
                var newProxies = Proxies.Where(p => !ReferenceEquals(p.RawAppender, removed)).ToList();
                Proxies = newProxies;
            }
        }

        // ReSharper disable once AssignNullToNotNullAttribute -- log4net returns null when absent.
        return removed;
    }

    public override void RemoveAllAppenders()
    {
        base.RemoveAllAppenders();
        if (Proxies != null)
        {
            lock (_lock)
            {
                Proxies = [];
            }
        }
    }
}
