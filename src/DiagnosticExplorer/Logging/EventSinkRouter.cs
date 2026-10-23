#nullable enable annotations

using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer.Logging;

/// <summary>
///     Decides whether a log event from a logging-framework adapter reaches the store, by matching
///     it against a compiled routing table. Routes are validated once at construction so the hot
///     path is comparison only.
/// </summary>
/// <remarks>
///     The router is a FILTER, not a fan-out. An event that matches any route is published exactly
///     once, to the single store this router holds; the number of routes it matched does not change
///     that. Route destinations and <see cref="EventSinkRouteOptions.MatchMode" /> are therefore not
///     consulted when publishing — they are snapshotted into
///     <see cref="LogStreamRoutingConfiguration" /> and carried to the client, which is what renders
///     the routing in force. Anything that needs per-destination delivery has to add fan-out here
///     first; narrowing the matched set alone would change nothing, because only whether the set is
///     empty is ever read.
/// </remarks>
public sealed class EventSinkRouter : IDisposable
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private readonly LogEventStore _eventStore;
    private readonly object _lifetimeSync = new();
    private CompiledRoute[] _routes;
    private LogEventStore.RoutingContribution _contribution;
    private bool _disposed;

    public EventSinkRouter(EventSinkRouteOptions options, LogEventStore? eventStore = null)
    {
        _eventStore = eventStore ?? DiagnosticManager.LogEventStore;
        _routes = CompileRoutes(options);

        _contribution = _eventStore.RegisterRoutingContribution(options.CreateSnapshot());
    }

    public LogEventStore EventStore => _eventStore;

    /// <summary>Lets an adapter skip building an event that no route would accept.</summary>
    public bool IsEnabled(string? category, LogLevel level)
    {
        return !_disposed && FindMatchingRoutes(category, level).Count != 0;
    }

    /// <summary>Publishes the event if any route matches. Returns the number of stores written to.</summary>
    public int Route(EventSinkLogEvent logEvent)
    {
        if (logEvent == null)
        {
            throw new ArgumentNullException(nameof(logEvent));
        }

        if (_disposed || !DiagnosticManager.Enabled)
        {
            return 0;
        }

        List<CompiledRoute> routes = FindMatchingRoutes(logEvent.Category, logEvent.Level);
        if (routes.Count == 0)
        {
            return 0;
        }

        _ = _eventStore.Publish(logEvent);
        return 1;
    }

    public void Dispose()
    {
        lock (_lifetimeSync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _eventStore.RemoveRoutingContribution(_contribution);
        }
    }

    public void Reconfigure(EventSinkRouteOptions options)
    {
        CompiledRoute[] routes = CompileRoutes(options);
        LogStreamRoutingConfiguration routing = options.CreateSnapshot();

        lock (_lifetimeSync)
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(EventSinkRouter));
            }

            LogEventStore.RoutingContribution contribution = _eventStore.ReplaceRoutingContribution(
                _contribution,
                routing
            );
            _routes = routes;
            _contribution = contribution;
        }
    }

    private static CompiledRoute[] CompileRoutes(EventSinkRouteOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        if (!Enum.IsDefined(typeof(EventSinkRouteMatchMode), options.MatchMode))
        {
            throw new ArgumentOutOfRangeException(nameof(options), "The configured match mode is invalid.");
        }

        // ReSharper disable once ConstantConditionalAccessQualifier
        // ReSharper disable once ConstantNullCoalescingCondition
        // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
        return options.Routes?.Select(route => new CompiledRoute(route)).ToArray() ?? [];
    }

    private List<CompiledRoute> FindMatchingRoutes(string? category, LogLevel level)
    {
        category ??= string.Empty;
        List<CompiledRoute> matches = [];
        foreach (CompiledRoute route in _routes)
        {
            if (!route.Matches(category, level))
            {
                continue;
            }

            matches.Add(route);
            if (route.StopProcessing)
            {
                break;
            }
        }

        return matches;
    }

    private sealed class CompiledRoute
    {
        public CompiledRoute(EventSinkRoute route)
        {
            if (route == null)
            {
                throw new ArgumentException("A route cannot be null.", nameof(route));
            }

            if (string.IsNullOrWhiteSpace(route.CategoryPattern))
            {
                throw new ArgumentException("A route category pattern is required.", nameof(route));
            }

            if (route.CategoryPattern != "*" && route.CategoryPattern.EndsWith(".", StringComparison.Ordinal))
            {
                throw new ArgumentException("A route category pattern cannot end with a period.", nameof(route));
            }

            if (route.MinLevel > route.MaxLevel)
            {
                throw new ArgumentException("A route minimum level cannot exceed its maximum level.", nameof(route));
            }

            if (route.Destinations == null || route.Destinations.Count == 0)
            {
                throw new ArgumentException("A route must define at least one destination.", nameof(route));
            }

            if (
                route.Destinations.Any(destination =>
                    // ReSharper disable once ConditionIsAlwaysTrueOrFalse
                    // A public list can hold a null element despite the annotation.
                    destination == null
                    || !IsValid(destination.SinkName)
                    || !IsValid(destination.SinkCategory)
                )
            )
            {
                throw new ArgumentException("Each route destination requires a sink name and category.", nameof(route));
            }

            CategoryPattern = route.CategoryPattern.Trim();
            MinLevel = route.MinLevel;
            MaxLevel = route.MaxLevel;
            StopProcessing = route.StopProcessing;
        }

        public string CategoryPattern { get; }

        public LogLevel? MinLevel { get; }

        public LogLevel? MaxLevel { get; }

        public bool StopProcessing { get; }

        private static bool IsValid(RouteValue? routeValue)
        {
            return routeValue != null
                && Enum.IsDefined(typeof(RouteValueSource), routeValue.Source)
                && (routeValue.Source != RouteValueSource.Fixed || !string.IsNullOrWhiteSpace(routeValue.Value));
        }

        /// <summary>
        ///     A pattern matches its own category exactly, or a dotted child of it. The explicit
        ///     '.' check stops "Foo" matching "Foobar".
        /// </summary>
        public bool Matches(string category, LogLevel level)
        {
            if (MinLevel.HasValue && level < MinLevel.Value)
            {
                return false;
            }

            if (MaxLevel.HasValue && level > MaxLevel.Value)
            {
                return false;
            }

            if (CategoryPattern == "*")
            {
                return true;
            }

            if (Comparer.Equals(category, CategoryPattern))
            {
                return true;
            }

            return category.Length > CategoryPattern.Length
                && category.StartsWith(CategoryPattern, StringComparison.OrdinalIgnoreCase)
                && category[CategoryPattern.Length] == '.';
        }
    }
}
