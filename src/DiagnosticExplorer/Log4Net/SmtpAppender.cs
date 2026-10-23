using System.Net.Mail;
using log4net.Appender;
using log4net.Core;
using log4net.Layout;

namespace DiagnosticExplorer.Log4Net;

public class SmtpAppender : AppenderSkeleton
{
    internal const string DefaultHostName = "Default Smtp Host";

    public SmtpAppender()
    {
        Authentication = log4net.Appender.SmtpAppender.SmtpAuthentication.None;
        Priority = MailPriority.Normal;
    }

    public string To { get; set; }

    public string From { get; set; }

    public ILayout Subject { get; set; }

    public string SmtpHost { get; set; }

    /// <summary>
    ///     Enable explicit TLS (STARTTLS) for the SMTP connection. Forced on when
    ///     <see cref="Authentication" /> is Basic so credentials never cross the wire in clear.
    /// </summary>
    public bool EnableSsl { get; set; }

    /// <summary>SMTP port; 0 (default) leaves the SmtpClient default (25, or 587 with TLS).</summary>
    public int Port { get; set; }

    public log4net.Appender.SmtpAppender.SmtpAuthentication Authentication { get; set; }

    public string Username { get; set; }

    public string Password { get; set; }

    public MailPriority Priority { get; set; }

    /// <summary>
    ///     Used to specify the amount of minutes timeout to wait for before resetting that an error occurred on an
    ///     appender.
    /// </summary>
    [Property]
    public TimeSpan FailTimeout { get; set; } = TimeSpan.FromSeconds(-1);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsIn { get; set; } = new(3);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsOut { get; set; } = new(3);

    [RateProperty(ExposeRate = false, ExposeTotal = true)]
    public RateCounter EventsErrored { get; set; } = new(3);

    protected override bool RequiresLayout => true;

    [CollectionProperty(CollectionMode.Categories, CategoryProperty = nameof(SmtpAppenderProxy.SmtpHost))]
    public List<SmtpAppenderProxy> Proxies { get; private set; }

    public override void ActivateOptions()
    {
        base.ActivateOptions();

        var hosts = (SmtpHost ?? "")
            .Split([',', ';'], StringSplitOptions.RemoveEmptyEntries)
            .Select(x => x.Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .ToArray();

        if (FailTimeout < TimeSpan.Zero)
        {
            FailTimeout = hosts.Length <= 1 ? TimeSpan.Zero : TimeSpan.FromMinutes(5);
        }

        Proxies = [];

        foreach (var host in hosts)
        {
            if (
                string.IsNullOrWhiteSpace(host)
                || string.Equals("Default", host.Trim(), StringComparison.InvariantCultureIgnoreCase)
            )
            {
                Proxies.Add(new SmtpAppenderProxy(this, DefaultHostName, FailTimeout));
            }
            else
            {
                Proxies.Add(new SmtpAppenderProxy(this, host, FailTimeout));
            }
        }

        if (!Proxies.Any())
        {
            Proxies.Add(new SmtpAppenderProxy(this, DefaultHostName, FailTimeout));
        }

        DiagnosticManager.Register(this, Name, "Log4Net");
    }

    protected override void Append(LoggingEvent loggingEvent)
    {
        EventsIn.Register(1);
        PerformSend(loggingEvent);
    }

    protected void PerformSend(LoggingEvent loggingEvent)
    {
        var layout = Layout;
        if (layout == null)
        {
            ErrorHandler.Error("No layout configured for SMTP appender.");
            return;
        }

        using var bodyWriter = new StringWriter();

        if (layout.Header != null)
        {
            bodyWriter.Write(layout.Header);
        }

        // Render the event and append the text to the buffer
        RenderLoggingEvent(bodyWriter, loggingEvent);

        if (layout.Footer != null)
        {
            bodyWriter.Write(layout.Footer);
        }

        var body = bodyWriter.ToString();
        var subject = RenderSubject(loggingEvent);

        SendToProxies(body, subject);
    }

    private string RenderSubject(LoggingEvent loggingEvent)
    {
        if (Subject == null)
        {
            return "No Subject";
        }

        try
        {
            using var subjectWriter = new StringWriter();
            //format the layout
            Subject.Format(subjectWriter, loggingEvent);

            return subjectWriter.ToString();
        }
        catch (Exception ex)
        {
            return $"Bad subject format: {ex.Message}";
        }
    }

    protected void SendToProxies(string body, string subject)
    {
        foreach (var proxy in Proxies)
        {
            using (var message = new MailMessage())
            {
                message.Body = body;
                if (!string.IsNullOrEmpty(From))
                {
                    message.From = new MailAddress(From);
                }

                if (!string.IsNullOrEmpty(To))
                {
                    message.To.Add(To);
                }

                message.Subject = subject;
                message.Priority = Priority;

                if (proxy.TrySend(message))
                {
                    EventsOut.Register(1);
                    break;
                }
            }

            EventsErrored.Register(1);
            RecordAppenderError(proxy);
        }
    }

    protected override void OnClose()
    {
        base.OnClose();
        DiagnosticManager.Unregister(this);
    }

    private void RecordAppenderError(SmtpAppenderProxy appender)
    {
        ForwardingAppenderBase.LogLogError(GetType(), $"appender [{appender.SmtpHost}] has an error.");
    }
}
