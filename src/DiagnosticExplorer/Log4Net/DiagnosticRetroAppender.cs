#nullable enable annotations

using System.Diagnostics;
using System.Reflection;
using log4net.Appender;
using log4net.Core;

namespace DiagnosticExplorer.Log4Net;

public class DiagnosticRetroAppender : AppenderSkeleton
{
    private static Action<DiagnosticMsg>? _loggingAction;
    private readonly string _process;
    private readonly TimeProvider _timeProvider;
    private readonly string? _version;

    public DiagnosticRetroAppender()
        : this(TimeProvider.System) { }

    public DiagnosticRetroAppender(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        // Version can be null for an assembly built without a version → guard with ?. (the
        // outer ?. only covered a null entry assembly, so this NRE'd the ctor and blocked
        // logging config init). Dispose the Process handle — only ProcessName is needed.
        _version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString();
        using var current = Process.GetCurrentProcess();
        _process = current.ProcessName;
        EventsIn = new RateCounter(3, timeProvider);
    }

    public string? Environment { get; set; }

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsIn { get; set; }

    public static void SetLoggingAction(Action<DiagnosticMsg>? action)
    {
        _loggingAction = action;
    }

    public override void ActivateOptions()
    {
        base.ActivateOptions();

        DiagnosticManager.Register(this, Name, "Log4Net");
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        DiagnosticMsg msg = new()
        {
            Level = loggingEvent.Level?.Value ?? Level.Info.Value,
            Date = _timeProvider.GetUtcNow().UtcDateTime,
            Machine = System.Environment.MachineName,
            User = System.Environment.UserName,
            Environment = Environment,
            Category = loggingEvent.LoggerName,
            Process = $"{_process} {_version}",
            Message = GetMessage(loggingEvent),
        };

        EventsIn.Register(1);
        _loggingAction?.Invoke(msg);
    }

    private string GetMessage(LoggingEvent loggingEvent)
    {
        var message = RenderLoggingEvent(loggingEvent);

        if (!ReferenceEquals(loggingEvent.MessageObject, loggingEvent.ExceptionObject))
        {
            message += System.Environment.NewLine + loggingEvent.ExceptionObject;
        }

        return message;
    }

    protected override void OnClose()
    {
        base.OnClose();
        DiagnosticManager.Unregister(this);
    }
}
