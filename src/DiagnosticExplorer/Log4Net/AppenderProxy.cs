using System.Diagnostics.CodeAnalysis;
using System.Net;
using System.Net.Mail;
using log4net.Appender;
using log4net.Core;

namespace DiagnosticExplorer.Log4Net;

public struct AppendResult
{
    public AppendResult(bool success)
        : this()
    {
        Success = success;
    }

    public AppendResult(bool success, string message)
    {
        Success = success;
        Message = message;
    }

    public bool Success { get; }
    public string Message { get; }
}

[DiagnosticClass(AttributedPropertiesOnly = true)]
public abstract class AppenderProxyBase
{
    // Guards the failover state (_isInError + the nullable _errorTime) and the diagnostic
    // timestamps (_lastError + _lastMessageSent). These are written by the append thread
    // (DoAppend) and Reactivate (a [DiagnosticMethod], i.e. the diagnostic-walk thread) and
    // read by StatusMessage / the [Property] getters on the walk thread; an unsynchronized
    // nullable DateTime read can tear. (M17a)
    private readonly object _stateLock = new();
    private readonly TimeProvider _timeProvider;
    private DateTime? _errorTime;
    private bool _isHalfOpen;
    private bool _isInError;
    private DateTime? _lastError;

    private string _lastErrorMessage;
    private DateTime? _lastMessageSent;
    protected TimeSpan _timeout;

    protected AppenderProxyBase(TimeSpan timeout)
        : this(timeout, TimeProvider.System) { }

    protected AppenderProxyBase(TimeSpan timeout, TimeProvider timeProvider)
    {
        _timeout = timeout;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public bool IsInError
    {
        get
        {
            lock (_stateLock)
            {
                return _isInError;
            }
        }
    }

    [Property]
    public DateTime? LastError
    {
        get
        {
            lock (_stateLock)
            {
                return _lastError;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _lastError = value;
            }
        }
    }

    [Property]
    public DateTime? LastMessageSent
    {
        get
        {
            lock (_stateLock)
            {
                return _lastMessageSent;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _lastMessageSent = value;
            }
        }
    }

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter MessagesSent { get; } = new(3);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter Errors { get; } = new(3);

    [Property]
    public string LastErrorMessage
    {
        get
        {
            lock (_stateLock)
            {
                return _lastErrorMessage;
            }
        }
        set
        {
            lock (_stateLock)
            {
                _lastErrorMessage = value;
            }
        }
    }

    [Property]
    public string StatusMessage
    {
        get
        {
            lock (_stateLock)
            {
                if (_isHalfOpen)
                {
                    return "PROBING";
                }

                var timeFailed = TimeUntilNextActive();
                if (timeFailed.HasValue)
                {
                    var remaining = FormatTimeSpan(_timeout - timeFailed.Value);
                    return $"FAILED, Ready in {remaining}";
                }

                return "READY";
            }
        }
    }

    [DiagnosticMethod]
    public void Reactivate()
    {
        lock (_stateLock)
        {
            _isInError = false;
            _isHalfOpen = false;
            _errorTime = null;
        }
    }

    private TimeSpan? TimeUntilNextActive()
    {
        DateTime? time;
        lock (_stateLock)
        {
            time = _errorTime;
        }

        if (!time.HasValue)
        {
            return null;
        }

        var elapsed = UtcNow - time.Value;
        if (elapsed > _timeout)
        {
            return null;
        }

        return elapsed;
    }

    private static string FormatTimeSpan(TimeSpan time)
    {
        if (time.TotalMinutes >= 60)
        {
            return string.Format("{0:D2}:{1:D2}:{2:D2}", (int)time.TotalHours, time.Minutes, time.Seconds);
        }

        if (time.TotalSeconds < 60)
        {
            return string.Format("{0} seconds", time.Seconds);
        }

        return string.Format("{0}m {1:D2}s", (int)time.TotalMinutes, time.Seconds);
    }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "The branches are the circuit-breaker state transitions and keeping them together makes the concurrency invariant reviewable."
    )]
    protected bool DoAppend(Func<AppendResult> appendAction)
    {
        var isProbe = false;

        lock (_stateLock)
        {
            if (_isInError)
            {
                if (ShouldResetErrorNoLock())
                {
                    // Timeout expired: transition from error to half-open, and this thread becomes the probe.
                    _isInError = false;
                    _isHalfOpen = true;
                    isProbe = true;
                }
                else
                {
                    // Still in error / quarantined
                    return false;
                }
            }
            else if (_isHalfOpen)
            {
                // Another thread is already probing; reject this thread to avoid thundering herd.
                return false;
            }
        }

        // Run append action outside the lock
        var result = appendAction();

        lock (_stateLock)
        {
            if (isProbe)
            {
                _isHalfOpen = false;
                if (result.Success)
                {
                    // Probe succeeded: clear error state
                    _errorTime = null;
                    _isInError = false;
                }
                else
                {
                    // Probe failed: go back to error state with a new timeout
                    _errorTime = UtcNow;
                    _isInError = true;
                }
            }
            else
            {
                // Normal append path (not a probe)
                if (!result.Success)
                {
                    _lastError = UtcNow;
                    _lastErrorMessage = result.Message;
                    if (_timeout > TimeSpan.Zero)
                    {
                        _errorTime = UtcNow;
                        _isInError = true;
                    }
                }
            }

            if (result.Success)
            {
                _lastMessageSent = UtcNow;
            }
        }

        if (result.Success)
        {
            MessagesSent.Register(1);
        }
        else
        {
            Errors.Register(1);
        }

        return result.Success;
    }

    // Caller must hold _stateLock.
    private bool ShouldResetErrorNoLock()
    {
        if (!_isInError)
        {
            return false;
        }

        if (_timeout <= TimeSpan.Zero)
        {
            return true;
        }

        // _isInError implies _errorTime was set; guard explicitly so the intent is clear
        // and a future invariant break can't silently rely on nullable arithmetic.
        if (!_errorTime.HasValue)
        {
            return false;
        }

        return UtcNow - _errorTime.Value >= _timeout;
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
}

[DiagnosticClass(AttributedPropertiesOnly = true)]
public class SmtpAppenderProxy : AppenderProxyBase
{
    private readonly SmtpAppender _appender;

    public SmtpAppenderProxy(SmtpAppender appender, string smtpHost, TimeSpan timeout)
        : base(timeout)
    {
        _appender = appender;
        SmtpHost = smtpHost;
    }

    public string SmtpHost { get; set; }

    public bool TrySend(MailMessage message)
    {
        return DoAppend(() => SendMessage(message));
    }

    [SuppressMessage(
        "Security",
        "S5332:Clear-text protocols should not be used",
        Justification = "Unauthenticated local SMTP relays may intentionally opt out of TLS; Basic authentication always requires it."
    )]
    private AppendResult SendMessage(MailMessage message)
    {
        try
        {
            using var smtpClient = new SmtpClient();
            if (
                !string.IsNullOrEmpty(SmtpHost)
                && !string.Equals(SmtpHost, SmtpAppender.DefaultHostName, StringComparison.CurrentCultureIgnoreCase)
            )
            {
                smtpClient.Host = SmtpHost;
            }

            if (_appender.Port > 0)
            {
                smtpClient.Port = _appender.Port;
            }

            smtpClient.DeliveryMethod = SmtpDeliveryMethod.Network;

            // Require TLS when sending Basic-auth credentials so username/password don't traverse
            // the wire in clear; otherwise honour the configured EnableSsl. (M13)
            smtpClient.EnableSsl =
                _appender.EnableSsl
                || _appender.Authentication == log4net.Appender.SmtpAppender.SmtpAuthentication.Basic;

            if (_appender.Authentication == log4net.Appender.SmtpAppender.SmtpAuthentication.Basic)
            {
                // Perform basic authentication
                smtpClient.Credentials = new NetworkCredential(_appender.Username, _appender.Password);
            }
            else if (_appender.Authentication == log4net.Appender.SmtpAppender.SmtpAuthentication.Ntlm)
            {
                // Perform integrated authentication (NTLM)
                smtpClient.Credentials = CredentialCache.DefaultNetworkCredentials;
            }

            smtpClient.Send(message);
            return new AppendResult(true);
        }
        catch (Exception ex)
        {
            return new AppendResult(false, ex.Message);
        }
    }
}

[DiagnosticClass(AttributedPropertiesOnly = true)]
public class AppenderProxy : AppenderProxyBase
{
    /// <summary>
    ///     Wraps up an <see cref="IAppender" /> adding extra behaviour to how to handle
    ///     an error while appending
    /// </summary>
    /// <param name="appenderToWrap">The appender whose failures this proxy contains</param>
    /// <param name="timeout">Duration to wait before attempting to append again after an error</param>
    public AppenderProxy(IAppender appenderToWrap, TimeSpan timeout)
        : base(timeout)
    {
        RawAppender = appenderToWrap ?? throw new ArgumentNullException(nameof(appenderToWrap));

        if (
            appenderToWrap is AsyncFallbackAppender
            || appenderToWrap is AsyncForwardingAppender
            || appenderToWrap is AsyncSmtpAppender
        )
        {
            throw new ArgumentException(
                $"Cannot wrap async appender '{appenderToWrap.Name}' of type '{appenderToWrap.GetType().Name}' inside AppenderProxy. Failover and quarantine are not supported for asynchronous appenders."
            );
        }

        if (appenderToWrap is not AppenderSkeleton convertedAppender)
        {
            throw new ArgumentException(
                $"Appender '{appenderToWrap.Name}' of type '{appenderToWrap.GetType().Name}' does not inherit from AppenderSkeleton. AppenderProxy requires AppenderSkeleton targets to track errors."
            );
        }

        Appender = convertedAppender;
        ErrorHandler = new AppenderProxyErrorHandler();
        MultiErrorHandler.SetErrorHandler(Appender, ErrorHandler);
    }

    public IAppender RawAppender { get; }

    private AppenderProxyErrorHandler ErrorHandler { get; }

    /// <summary>
    ///     Appender being wrapped
    /// </summary>
    public AppenderSkeleton Appender { get; }

    public string Name => RawAppender.Name;

    /// <summary>
    ///     Attempts to append to wrapped appender
    /// </summary>
    /// <returns>Whether the append was successful</returns>
    public bool TryAppend(LoggingEvent loggingEvent)
    {
        return DoAppend(() => FireAppendAction(() => RawAppender.DoAppend(loggingEvent)));
    }

    /// <summary>
    ///     Attempts to append to wrapped appender
    /// </summary>
    /// <returns>Whether the append was successful</returns>
    public bool TryAppend(LoggingEvent[] loggingEvents)
    {
        return DoAppend(() =>
            FireAppendAction(() =>
            {
                if (Appender != null)
                {
                    Appender.DoAppend(loggingEvents);
                }
                else
                {
                    foreach (var loggingEvent in loggingEvents)
                    {
                        RawAppender.DoAppend(loggingEvent);
                    }
                }
            })
        );
    }

    private AppendResult FireAppendAction(Action appendAction)
    {
        if (ErrorHandler != null)
        {
            ErrorHandler.EnableForCurrentThread();
            ErrorHandler.ResetError();
        }

        try
        {
            appendAction();
        }
        catch (Exception ex)
        {
            return new AppendResult(false, ex.Message);
        }
        finally
        {
            ErrorHandler?.Disable();
        }

        return new AppendResult(ErrorHandler == null || !ErrorHandler.HasError, ErrorHandler?.Message);
    }
}
