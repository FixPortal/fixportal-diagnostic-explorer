using DiagnosticExplorer;
using DiagnosticExplorer.Logging;

namespace Diagnostic.Service.Common;

public interface IDiagnosticClient
{
    /// <summary>The snapshot an agent sends when its log stream starts, or restarts.</summary>
    IObservable<LogStreamInitialization> LogStreamInitialized { get; }

    /// <summary>Live log events, batched by the agent.</summary>
    IObservable<LogStreamEvent[]> LogStreamEvents { get; }
    Task<DiagnosticResponse> GetDiagnostics(CancellationToken cancel);
    Task<DrillDownResponse> GetDrillDown(DrillDownRequest request);

    /// <param name="objectPaths">
    ///     The drilldown the action was triggered from, empty for the main view.
    /// </param>
    /// <param name="requestId">
    ///     The operator action this is an attempt at. A retry carrying the same id joins the
    ///     first attempt on the agent rather than running the body a second time.
    /// </param>
    /// <param name="path">The property to set, as a path within the diagnostics tree.</param>
    /// <param name="value">The new value, as text, or null to clear it.</param>
    Task<OperationResponse> SetProperty(string requestId, string[] objectPaths, string path, string? value);
    Task<OperationResponse> ExecuteOperation(
        string requestId,
        string[] objectPaths,
        string path,
        string operation,
        string[] arguments
    );
    Task SubscribeEvents();
    Task UnsubscribeEvents();
}
