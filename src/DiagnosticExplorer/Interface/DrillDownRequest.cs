using DiagnosticExplorer.Logging;

namespace DiagnosticExplorer;

/// <summary>
///     Addresses a value inside a process's diagnostics so it can be inspected in its own right.
/// </summary>
/// <remarks>
///     <para>
///         The value is named by an ordered chain of ordinary diagnostic paths, not by an object
///         reference or an opaque handle the agent would have to keep alive between calls. Each
///         entry resolves against the diagnostics produced by the previous one, so a chain of two
///         means "render the process, take this property's value, render THAT, take this property".
///         Nothing is retained on the agent between requests, and a path that no longer resolves —
///         the object was replaced, the collection shrank — fails as a lookup rather than handing
///         back a stale object that is still rooted only because a drilldown was once opened on it.
///     </para>
///     <para>
///         The same chain travels with a nested property edit or operation, which is what makes an
///         action inside a drilldown run against the object the operator is looking at.
///     </para>
/// </remarks>
public sealed class DrillDownRequest
{
    /// <summary>The chain of diagnostic paths naming the value, outermost first.</summary>
    public List<string> ObjectPaths { get; set; } = [];

    /// <summary>Return the value serialised as JSON instead of as diagnostics.</summary>
    public bool JsonHover { get; set; }

    /// <summary>Skip resolving event views, for a caller that only wants the properties.</summary>
    public bool ExcludeEventViews { get; set; }
}

public sealed class DrillDownResponse
{
    public DiagnosticResponse Diagnostics { get; set; } = new();

    /// <summary>How many items the response carries, which is 1 for a single object.</summary>
    public int DisplayedCount { get; set; }

    /// <summary>The collection's own count where it has one, else null.</summary>
    public int? TotalCount { get; set; }

    public bool IsTruncated { get; set; }
    public string ErrorMessage { get; set; }
    public string ErrorDetail { get; set; }

    public List<DrillDownEventViewDefinition> EventViews { get; set; } = [];

    /// <summary>Set only for a <see cref="DrillDownRequest.JsonHover" /> request.</summary>
    public string Json { get; set; }
}

/// <summary>
///     One event table a drilldown offers, as a projection over the process's existing event
///     stream.
/// </summary>
/// <remarks>
///     A definition, not a subscription: it carries the matchers a client applies to events it is
///     already receiving. Opening a drilldown starts no new stream, retains nothing extra on the
///     agent, and cannot widen what the process captures — a category the process-wide routing
///     excludes stays excluded here.
/// </remarks>
public sealed class DrillDownEventViewDefinition
{
    public string Id { get; set; }
    public string Category { get; set; }
    public string Name { get; set; }
    public List<DrillDownEventMatcher> Matchers { get; set; } = [];
}

/// <summary>
///     Admits events by logger name and level. Levels are Microsoft.Extensions.Logging ordinals,
///     matching <see cref="LogStreamEvent.Level" /> on the wire.
/// </summary>
public sealed class DrillDownEventMatcher
{
    public string LoggerName { get; set; }
    public LoggerNameMatchMode LoggerNameMatchMode { get; set; }
    public int? MinLevel { get; set; }
    public int? MaxLevel { get; set; }
}
