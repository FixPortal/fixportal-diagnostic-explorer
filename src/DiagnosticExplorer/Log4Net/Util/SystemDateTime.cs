namespace DiagnosticExplorer.Log4Net;

/// <summary>
///     Retains the legacy public clock accessors for binary compatibility.
/// </summary>
public static class SystemDateTime
{
    /// <summary>
    ///     Production components inject <see cref="TimeProvider" />. These immutable delegates
    ///     remain only for callers compiled against the historical public surface.
    /// </summary>
    public static Func<DateTime> Now { get; } = () => TimeProvider.System.GetLocalNow().DateTime;

    public static Func<DateTime> UtcNow { get; } = () => TimeProvider.System.GetUtcNow().UtcDateTime;
}
