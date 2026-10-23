using log4net.Appender;
using log4net.Repository;
using log4net.Repository.Hierarchy;

namespace DiagnosticExplorer.Log4Net;

public static class LoggingRepositoryExtensions
{
    private const string AppenderName = "DiagnosticExplorer.Routing";

    public static void ConfigureDiagnosticExplorer(this ILoggerRepository repository)
    {
        if (repository == null)
        {
            throw new ArgumentNullException(nameof(repository));
        }

        if (repository is not Hierarchy hierarchy)
        {
            throw new NotSupportedException("DiagnosticExplorer requires a log4net hierarchy repository.");
        }

        RoutingDiagnosticAppender replacement = new()
        {
            Name = AppenderName,
            RoutingOptions = DiagnosticManager.CurrentConfiguration.RuntimeOptions.Routing,
        };
        replacement.ActivateOptions();

        IAppender previous = hierarchy.Root.Appenders.FirstOrDefault(appender =>
            appender.Name == AppenderName && appender is RoutingDiagnosticAppender
        );
        if (previous != null)
        {
            _ = hierarchy.Root.RemoveAppender(previous);
            previous.Close();
        }
        hierarchy.Root.AddAppender(replacement);
        hierarchy.Configured = true;
    }
}
