using log4net.Appender;
using log4net.Core;

namespace DiagnosticExplorer.Log4Net;

[DiagnosticClass(AttributedPropertiesOnly = true, DeclaringTypeOnly = false)]
public class AsyncForwardingAppender : ForwardingAppender, IDisposable
{
    private bool _disposed;
    private AsyncProcessor _processor;

    [Property]
    public int MaxQueueSize { get; set; } = 1000;

    [Property]
    public BufferOverflowMode Overflow { get; set; } = BufferOverflowMode.Block;

    public FixFlags Fix { get; set; } = FixFlags.Partial;

    [Property]
    public int? CurrentQueueSize => _processor?.QueueSize;

    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }

    public override void ActivateOptions()
    {
        base.ActivateOptions();

        InitializeAppenders();
        _processor = new AsyncProcessor(Overflow, MaxQueueSize, PerformAppend) { Fix = Fix };
        _processor.Start();
    }

    public override void AddAppender(IAppender appender)
    {
        base.AddAppender(appender);
        SetAppenderFixFlags(appender);
    }

    private void InitializeAppenders()
    {
        foreach (var appender in Appenders)
        {
            SetAppenderFixFlags(appender);
        }
    }

    private void SetAppenderFixFlags(IAppender appender)
    {
        if (appender is BufferingAppenderSkeleton bufferingAppender)
        {
            bufferingAppender.Fix = Fix;
        }
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        EventsIn.Register(1);
        var processor = _processor;
        processor?.Append(loggingEvent);
    }

    protected override void Append(LoggingEvent[] loggingEvents)
    {
        EventsIn.Register(loggingEvents.Length);
        var processor = _processor;
        processor?.Append(loggingEvents);
    }

    protected override void OnClose()
    {
        _processor?.Close();
        base.OnClose();
    }

    protected virtual void Dispose(bool disposing)
    {
        if (!_disposed)
        {
            if (disposing)
            {
                _processor?.Dispose();
                _processor = null;
            }

            _disposed = true;
        }
    }

    // Use C# destructor syntax for finalization code.
    ~AsyncForwardingAppender()
    {
        // Simply call Dispose(false).
        Dispose(false);
    }
}
