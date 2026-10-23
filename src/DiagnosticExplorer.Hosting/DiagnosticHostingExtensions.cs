#if NET5_0_OR_GREATER

using Microsoft.AspNetCore.Http.Connections.Client;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DiagnosticExplorer;

public static class DiagnosticHostingExtensions
{
    public static IServiceCollection ConfigureDiagnosticExplorer(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<IDiagConfigurator> configureDiagnostics,
        Action<HttpConnectionOptions>? configureHttp = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(configureDiagnostics);

        DiagnosticManager.Configure(configureDiagnostics);

        return services.AddDiagnosticExplorer(configuration, configureHttp);
    }

    public static IServiceCollection AddDiagnosticExplorer(
        this IServiceCollection services,
        IConfiguration config,
        Action<HttpConnectionOptions>? configureHttp = null
    )
    {
        services.Configure<DiagnosticOptions>(config.GetSection("DiagnosticExplorer"));
        services.AddHostedService(sp => new DiagnosticHostingService(
            sp.GetRequiredService<IOptions<DiagnosticOptions>>(),
            configureHttp,
            sp
        ));
        return services;
    }
}

#endif
