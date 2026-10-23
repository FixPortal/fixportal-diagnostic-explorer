using System.Collections.Concurrent;
using System.Diagnostics;
using log4net.Core;
using log4net.Util;

namespace DiagnosticExplorer.Log4Net;

public sealed class AsyncProcessor : IDisposable
{
    private readonly Action<LoggingEvent> _forwardLoggingEvent;
    private readonly CancellationToken _loggingCancelationToken;
    private CancellationTokenSource _loggingCancelationTokenSource;
    private BlockingCollection<LoggingEventContext> _loggingEvents;
    private Task _loggingTask;
    private volatile bool _shutDownRequested;

    public AsyncProcessor(BufferOverflowMode overflow, int bufferSize, Action<LoggingEvent> forwardLoggingEvent)
    {
        Overflow = overflow;
        BufferSize = bufferSize;
        _forwardLoggingEvent = forwardLoggingEvent;

        // Bounded in both modes: Block uses Add (back-pressure), Discard uses TryAdd
        // (drop on full). Previously Discard used an UNBOUNDED collection with a racy
        // Count pre-check, so the cap was advisory and the queue could grow without limit.
        _loggingEvents = new BlockingCollection<LoggingEventContext>(BufferSize);

        _loggingCancelationTokenSource = new CancellationTokenSource();
        _loggingCancelationToken = _loggingCancelationTokenSource.Token;
        _loggingTask = new Task(SubscriberLoop, _loggingCancelationToken, TaskCreationOptions.LongRunning);
    }

    private BufferOverflowMode Overflow { get; }
    private int BufferSize { get; }

    public FixFlags Fix { get; set; }

    public int QueueSize
    {
        // Snapshot the field: Dispose nulls _loggingEvents on another thread and this is
        // exposed as a diagnostic property polled from the UI.
        get
        {
            var queue = _loggingEvents;
            return queue?.Count ?? 0;
        }
    }

    public void Dispose()
    {
        if (_loggingTask != null)
        {
            if (!(_loggingTask.IsCanceled || _loggingTask.IsCompleted || _loggingTask.IsFaulted))
            {
                try
                {
                    Close();
                }
                catch (Exception ex)
                {
                    ForwardingAppenderBase.LogLogError(
                        GetType(),
                        "Exception Completing Subscriber Task in Dispose Method",
                        ex
                    );
                }
            }

            try
            {
                _loggingTask.Dispose();
            }
            catch (Exception ex)
            {
                ForwardingAppenderBase.LogLogError(GetType(), "Exception Disposing Logging Task", ex);
            }
            finally
            {
                _loggingTask = null;
            }
        }

        if (_loggingEvents != null)
        {
            try
            {
                _loggingEvents.Dispose();
            }
            catch (Exception ex)
            {
                ForwardingAppenderBase.LogLogError(GetType(), "Exception Disposing BlockingCollection", ex);
            }
            finally
            {
                _loggingEvents = null;
            }
        }

        if (_loggingCancelationTokenSource != null)
        {
            try
            {
                _loggingCancelationTokenSource.Dispose();
            }
            catch (Exception ex)
            {
                ForwardingAppenderBase.LogLogError(GetType(), "Exception Disposing CancellationTokenSource", ex);
            }
            finally
            {
                _loggingCancelationTokenSource = null;
            }
        }
    }

    public void Start()
    {
        _loggingTask.Start();
    }

    private void SubscriberLoop()
    {
        //The task will continue in a blocking loop until
        //the queue is marked as adding completed, or the task is canceled.
        try
        {
            //This call blocks until an item is available or until adding is completed
            foreach (var entry in _loggingEvents.GetConsumingEnumerable(_loggingCancelationToken))
            {
                try
                {
                    _forwardLoggingEvent(entry.LoggingEvent);
                }
                catch (Exception ex)
                {
                    ForwardInternalError(ex.Message, ex);
                }
            }
        }
        catch (OperationCanceledException ex)
        {
            if (!ex.CancellationToken.IsCancellationRequested)
            {
                //The thread was canceled before all entries could be forwarded and the collection completed.
                ForwardInternalError("Subscriber task was canceled before completion.", ex);
            }
            //Cancellation is called in the CompleteSubscriberTask so don't call that again.
        }
        catch (ThreadAbortException ex)
        {
            //Thread abort may occur on domain unload.
            ForwardInternalError("Subscriber task was aborted.", ex);
            //Cannot recover from a thread abort so complete the task.
            CompleteSubscriberTaskAfterError();
            //The exception is swallowed because we don't want the client application
            //to halt due to a logging issue.
        }
        catch (Exception ex)
        {
            //On exception, try to log the exception
            ForwardInternalError("Subscriber task error in forwarding loop.", ex);
            //Any error in the loop is going to be some sort of extenuating circumstance from which we
            //probably cannot recover anyway.   Complete subscribing.
            CompleteSubscriberTaskAfterError();
        }
    }

    private void ForwardInternalError(string message, Exception exception)
    {
        try
        {
            Debug.WriteLine(exception);
            LogLog.Error(GetType(), message, exception);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    private void CompleteSubscriberTaskAfterError()
    {
        _shutDownRequested = true;
        if (_loggingEvents == null || _loggingEvents.IsAddingCompleted)
        {
            return;
        }

        //Don't allow more entries to be added.
        _loggingEvents.CompleteAdding();
    }

    public void Append(LoggingEvent loggingEvent)
    {
        var queue = _loggingEvents;
        if (_shutDownRequested || loggingEvent == null || queue == null)
        {
            return;
        }

        loggingEvent.Fix = Fix;
        var context = new LoggingEventContext(loggingEvent);

        try
        {
            if (Overflow == BufferOverflowMode.Discard)
            {
                // Bounded buffer + TryAdd: drop silently when full instead of throwing a
                // LogException back onto the caller's logging thread (which log4net would
                // route to the ErrorHandler and could fault the appender).
                queue.TryAdd(context);
            }
            else
            {
                // Block mode: Add blocks for back-pressure until space frees up.
                queue.Add(context, _loggingCancelationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down — drop.
        }
        catch (InvalidOperationException)
        {
            // Adding was completed concurrently (shutdown race) — drop.
        }
    }

    public void Append(LoggingEvent[] loggingEvents)
    {
        if (!_shutDownRequested && loggingEvents != null)
        {
            foreach (var loggingEvent in loggingEvents)
            {
                Append(loggingEvent);
            }
        }
    }

    public void Close()
    {
        _shutDownRequested = true;
        if (_loggingEvents == null || _loggingEvents.IsAddingCompleted)
        {
            return;
        }

        //Don't allow more entries to be added.
        _loggingEvents.CompleteAdding();

        //Wait 5 seconds for the events to flush
        var taskEnded = _loggingTask.Wait((int)TimeSpan.FromSeconds(5).TotalMilliseconds, CancellationToken.None);

        //If the task hasn't ended, cancel the task and record the error
        if (!taskEnded)
        {
            _loggingCancelationTokenSource.Cancel();

            // Give an in-flight forward a brief, bounded window to finish before the caller's
            // base.OnClose() closes the downstream appenders — otherwise the worker can be mid
            // PerformAppend into an appender being closed underneath it. Bounded so a genuinely
            // wedged downstream still can't hang shutdown indefinitely. (B5)
            _loggingTask.Wait((int)TimeSpan.FromSeconds(1).TotalMilliseconds, CancellationToken.None);

            ForwardInternalError("The buffer was not able to be flushed before timeout occurred.", null);
        }
    }
}
