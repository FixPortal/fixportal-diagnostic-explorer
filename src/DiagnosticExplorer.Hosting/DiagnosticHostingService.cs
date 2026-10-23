using System.Diagnostics;
using System.Reflection;
using System.Text.RegularExpressions;
using DiagnosticExplorer.Log4Net;
using log4net;
using Microsoft.AspNetCore.Http.Connections.Client;
#if NET5_0_OR_GREATER
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
#endif

namespace DiagnosticExplorer;

public class DiagnosticHostingService
#if NET5_0_OR_GREATER
    : IHostedService
#endif
{
    private static readonly ILog _log = LogManager.GetLogger(typeof(DiagnosticHostingService));

    // Accessed via Interlocked/Volatile; only ever published after StartHosting succeeds so a
    // failed init can't leave a non-null, half-initialized instance behind.
    private static DiagnosticHostingService? _instance;

    private readonly Action<HttpConnectionOptions>? _configureHttp;
    private readonly DiagnosticOptions _options;
    private readonly IServiceProvider? _serviceProvider;

    private RegistrationHandler[]? _registrationHandlers;

    private DiagnosticHostingService(
        DiagnosticOptions options,
        Action<HttpConnectionOptions>? configureHttp = null,
        IServiceProvider? serviceProvider = null
    )
    {
        _options = options;
        _configureHttp = configureHttp;
        _serviceProvider = serviceProvider;
    }

    // Claim the singleton slot atomically, then start. Publish stays only if hosting actually
    // starts: on failure we roll the slot back to null so a later Start can retry and LogEvent
    // never sees a half-initialized instance.
    private static void TryStart(DiagnosticHostingService candidate)
    {
        if (Interlocked.CompareExchange(ref _instance, candidate, null) != null)
        {
            throw new InvalidOperationException(
                "An instance of DiagnosticHostingService is already running. Only one instance can run at a time."
            );
        }

        if (!candidate.StartHosting())
        {
            Interlocked.CompareExchange(ref _instance, null, candidate);
        }
    }

    private bool StartHosting()
    {
        try
        {
            string? uri = _options.Uri;
            if (string.IsNullOrWhiteSpace(uri))
            {
                _log.Warn("DiagnosticHostingService not started: no Uri configured");
                return false;
            }

            Registration registration = new()
            {
                ProcessId = Process.GetCurrentProcess().Id,
                InstanceId = Guid.NewGuid().ToString("N"),
                UserDomain = Environment.UserDomainName,
                UserName = Environment.UserName,
                MachineName = Environment.MachineName,
                ProcessName = ResolveProcessName(),
            };

            var handlers = Regex
                .Split(uri, @"\s|;|,", RegexOptions.None, TimeSpan.FromSeconds(1))
                .Select(hubUrl => hubUrl.Trim())
                .Where(hubUrl => !string.IsNullOrWhiteSpace(hubUrl))
                .Select(hubUrl => new RegistrationHandler(hubUrl, registration, _options.ApiKey, _serviceProvider))
                .ToArray();

            _registrationHandlers = handlers;
            DiagnosticRetroAppender.SetLoggingAction(LogEvent);
            SystemStatus.Register();

            foreach (var handler in handlers)
            {
                handler.Start(_configureHttp);
            }

            return true;
        }
        catch (Exception ex)
        {
            // Diagnostics setup must not crash the host, but the failure must be visible (logged,
            // not swallowed to Debug) and must not leave a half-initialized instance published.
            _log.Error("DiagnosticHostingService failed to start", ex);
            DiagnosticRetroAppender.SetLoggingAction(null);
            _registrationHandlers = null;
            return false;
        }
    }

    // The entry assembly name is a stabler identifier than the OS process
    // name for .NET apps: `dotnet MyApp.dll` reports "dotnet" as the
    // process, but the entry assembly is still "MyApp". Falls back to the
    // process name when there is no managed entry assembly (rare -- mostly
    // unmanaged hosts).
    private static string ResolveProcessName()
    {
        var entryAssemblyName = Assembly.GetEntryAssembly()?.GetName().Name;
        if (entryAssemblyName != null && entryAssemblyName.Length != 0)
        {
            return entryAssemblyName;
        }

        return Process.GetCurrentProcess().ProcessName?.Replace(".vshost", "") ?? "unknown";
    }

    public async Task StopHosting()
    {
        try
        {
            DiagnosticRetroAppender.SetLoggingAction(null);

            // Null-guard: StartHosting may have failed (or Stop been called without a successful
            // Start), leaving _registrationHandlers null.
            var handlers = _registrationHandlers;
            _registrationHandlers = null;
            if (handlers != null)
            {
                await Task.WhenAll(handlers.Select(handler => handler.Stop()).ToArray());
            }
        }
        catch (Exception ex)
        {
            _log.Error(ex);
        }
    }

    public static void Start(string? url, Action<HttpConnectionOptions>? configureHttp = null)
    {
        DiagnosticOptions options = new() { Uri = url };
        TryStart(new DiagnosticHostingService(options, configureHttp));
    }

    public static async Task Stop()
    {
        var instance = Interlocked.Exchange(ref _instance, null);
        if (instance != null)
        {
            await instance.StopHosting();
        }
    }

    public static void LogEvent(DiagnosticMsg evt)
    {
        var instance = Volatile.Read(ref _instance);
        if (instance != null)
        {
            foreach (var handler in instance._registrationHandlers ?? Array.Empty<RegistrationHandler>())
            {
                handler.LogEvent(evt);
            }
        }
    }

#if NET5_0_OR_GREATER

    public DiagnosticHostingService(
        IOptions<DiagnosticOptions> options,
        Action<HttpConnectionOptions>? configureHttp = null
    )
        : this(options.Value, configureHttp)
    {
        Debug.WriteLine($"DiagnosticHostingService constructed {_options.Enabled} Uri [{_options.Uri}");
    }

    public DiagnosticHostingService(
        IOptions<DiagnosticOptions> options,
        Action<HttpConnectionOptions>? configureHttp,
        IServiceProvider serviceProvider
    )
        : this(options.Value, configureHttp, serviceProvider)
    {
        Debug.WriteLine($"DiagnosticHostingService constructed {_options.Enabled} Uri [{_options.Uri}");
    }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        Debug.WriteLine($"DiagnosticHostingService starting {_options.Enabled} Uri [{_options.Uri}");
        if (_options.Enabled)
        {
            TryStart(this);
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Interlocked.CompareExchange(ref _instance, null, this);
        return StopHosting();
    }
#endif
}
