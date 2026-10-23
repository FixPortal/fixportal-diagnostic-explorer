using log4net.Appender;
using log4net.Core;
using log4net.Util;

namespace DiagnosticExplorer.Log4Net;

public class MultiErrorHandler : IErrorHandler
{
    private readonly object _sync = new();

    // Copy-on-write: AddHandler runs at appender-configuration time, Error iterates at
    // runtime on logging threads. A plain List mutated while iterated throws
    // InvalidOperationException; swapping an immutable snapshot under a lock avoids it.
    private volatile IErrorHandler[] _handlers;

    public MultiErrorHandler()
    {
        _handlers = new IErrorHandler[] { new OnlyOnceErrorHandler() };
    }

    public void Error(string message)
    {
        foreach (var handler in _handlers)
        {
            handler.Error(message);
        }
    }

    public void Error(string message, Exception e)
    {
        foreach (var handler in _handlers)
        {
            handler.Error(message, e);
        }
    }

    public void Error(string message, Exception e, ErrorCode errorCode)
    {
        foreach (var handler in _handlers)
        {
            handler.Error(message, e, errorCode);
        }
    }

    public void AddHandler(IErrorHandler handler)
    {
        Guard.NotNull(handler, nameof(handler));

        lock (_sync)
        {
            var updated = new IErrorHandler[_handlers.Length + 1];
            Array.Copy(_handlers, updated, _handlers.Length);
            updated[_handlers.Length] = handler;
            _handlers = updated;
        }
    }

    public static void SetErrorHandler(AppenderSkeleton appender, AppenderProxyErrorHandler handler)
    {
        var existingHandler = appender.ErrorHandler;

        if (existingHandler is not MultiErrorHandler multiHandler)
        {
            multiHandler = new MultiErrorHandler();
            appender.ErrorHandler = multiHandler;
            multiHandler.AddHandler(existingHandler);
        }

        multiHandler.AddHandler(handler);
    }
}

/// <summary>
///     This object records whether an error has been recorded
/// </summary>
public class AppenderProxyErrorHandler : IErrorHandler
{
    private readonly ThreadLocal<ErrorState> _state = new(() => new ErrorState());

    public bool HasError => _state.Value.HasError;

    public string Message => _state.Value.Message;

    public void Error(string message)
    {
        Record(message, null);
    }

    public void Error(string message, Exception e)
    {
        Record(message, e);
    }

    public void Error(string message, Exception e, ErrorCode errorCode)
    {
        Record(message, e);
    }

    public void EnableForCurrentThread()
    {
        _state.Value.Enabled = true;
    }

    public void Disable()
    {
        _state.Value.Enabled = false;
    }

    private void Record(string message, Exception exception)
    {
        var state = _state.Value;
        if (!state.Enabled)
        {
            return;
        }

        state.Message = exception?.Message ?? message;
        state.HasError = true;
    }

    internal void ResetError()
    {
        var state = _state.Value;
        state.HasError = false;
        state.Message = null;
    }

    // Per-thread error state. FireAppendAction enables on the appending thread, the wrapped
    // appender raises Error synchronously on that same thread, then we read it back. Holding
    // the enabled flag + captured error per-thread means concurrent appends through the same
    // proxy (e.g. ForwardingAppender's Parallel.ForEach) can't stomp each other's tracking,
    // and the previous Thread.CurrentThread == _enabledThread gate (which dropped any error
    // raised on a different thread) is gone.
    private sealed class ErrorState
    {
        public bool Enabled;
        public bool HasError;
        public string Message;
    }
}
