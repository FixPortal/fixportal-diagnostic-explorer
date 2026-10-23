#nullable enable annotations

using System.Threading.Channels;

namespace DiagnosticExplorer.Logging;

/// <summary>
///     Holds a bounded, time-limited window of log events for one stream, and fans new ones out to
///     live subscribers. A subscriber that attaches receives every retained event plus the sequence
///     number they run to, so it can distinguish replay from live traffic.
/// </summary>
/// <remarks>
///     The clock is injected rather than read from <c>DateTime.UtcNow</c> so retention is testable
///     without sleeping: pruning is entirely a function of the timestamps this store stamps.
/// </remarks>
public sealed class LogEventStore
{
    private readonly object _sync = new();
    private readonly List<LogStreamEvent> _events = [];
    private readonly HashSet<LogEventStoreSubscription> _subscriptions = [];
    private readonly TimeProvider _timeProvider;
    private readonly List<RoutingContribution> _routerContributions = [];
    private LogEventRetentionOptions _retention;
    private LogStreamRoutingConfiguration _baseRouting;
    private LogStreamRoutingConfiguration _routing;
    private long _sequence;

    /// <summary>Creates a store.</summary>
    /// <param name="retention">How much history to keep. Defaults to 5 000 events over 5 minutes.</param>
    /// <param name="streamId">
    ///     Identifies this stream to a consumer. Defaults to a fresh GUID per store, which is what
    ///     makes a process restart recognisable: the service treats a changed id as a new stream
    ///     and discards the history it held. Supplying a FIXED id gives that up — a restarted
    ///     process then looks like the same stream with its sequence counter back at the
    ///     beginning, and the service keeps the dead process's events in preference to the new
    ///     ones until the counter passes them. Supply one only when something outside the process
    ///     genuinely owns the stream's identity.
    /// </param>
    /// <param name="timeProvider">The clock retention is measured on. Defaults to the system clock.</param>
    public LogEventStore(
        LogEventRetentionOptions? retention = null,
        string? streamId = null,
        TimeProvider? timeProvider = null
    )
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _retention = (retention ?? new LogEventRetentionOptions()).CloneAndValidate();
        StreamId = string.IsNullOrWhiteSpace(streamId) ? Guid.NewGuid().ToString("N") : streamId;
        _baseRouting = new LogStreamRoutingConfiguration();
        _routing = _baseRouting.Clone();
    }

    /// <summary>The longest Detail an event may carry onto the wire.</summary>
    /// <remarks>
    ///     Detail is where an exception's stack trace lands, and nothing upstream of here bounds
    ///     it. A frame carries up to a hundred events against a 10 MB hub receive cap, so one
    ///     oversize event — or a hundred merely large ones — faults the invocation; that fault is
    ///     not cancellation, so it ends event delivery for the connection's life, and because the
    ///     event stays in the retained window every re-subscribe sends it again. Bounding it here
    ///     rather than at the frame keeps the poison out of the window in the first place.
    ///     32 KB is far above any real stack trace and leaves a full frame near 3 MB.
    /// </remarks>
    public const int MaxDetailLength = 32 * 1024;

    /// <summary>The longest Message an event may carry. Same reasoning as Detail, smaller bound.</summary>
    public const int MaxMessageLength = 4 * 1024;

    public string StreamId { get; }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public void Configure(LogEventRetentionOptions retention, LogStreamRoutingConfiguration routing)
    {
        if (retention == null)
        {
            throw new ArgumentNullException(nameof(retention));
        }

        if (routing == null)
        {
            throw new ArgumentNullException(nameof(routing));
        }

        LogEventRetentionOptions replacementRetention = retention.CloneAndValidate();
        LogStreamRoutingConfiguration replacementBaseRouting = routing.Clone();

        lock (_sync)
        {
            LogStreamRoutingConfiguration replacementRouting = BuildRouting(
                replacementBaseRouting,
                _routerContributions
            );
            _retention = replacementRetention;
            _baseRouting = replacementBaseRouting;
            _routing = replacementRouting;
            Prune(UtcNow);
            SupersedeSubscriptionsLocked();
        }
    }

    public void ConfigureRouting(LogStreamRoutingConfiguration routing)
    {
        if (routing == null)
        {
            throw new ArgumentNullException(nameof(routing));
        }

        LogStreamRoutingConfiguration replacementBaseRouting = routing.Clone();

        lock (_sync)
        {
            LogStreamRoutingConfiguration replacementRouting = BuildRouting(
                replacementBaseRouting,
                _routerContributions
            );
            _baseRouting = replacementBaseRouting;
            _routing = replacementRouting;
            SupersedeSubscriptionsLocked();
        }
    }

    internal RoutingContribution RegisterRoutingContribution(LogStreamRoutingConfiguration routing)
    {
        if (routing == null)
        {
            throw new ArgumentNullException(nameof(routing));
        }

        RoutingContribution contribution = new(routing.Clone());
        lock (_sync)
        {
            LogStreamRoutingConfiguration replacementRouting = BuildRouting(
                _baseRouting,
                [.. _routerContributions, contribution]
            );
            _routerContributions.Add(contribution);
            _routing = replacementRouting;
            SupersedeSubscriptionsLocked();
        }

        return contribution;
    }

    internal void RemoveRoutingContribution(RoutingContribution? contribution)
    {
        if (contribution == null)
        {
            return;
        }

        lock (_sync)
        {
            if (!_routerContributions.Remove(contribution))
            {
                return;
            }

            _routing = BuildRouting(_baseRouting, _routerContributions);
            SupersedeSubscriptionsLocked();
        }
    }

    internal RoutingContribution ReplaceRoutingContribution(
        RoutingContribution contribution,
        LogStreamRoutingConfiguration routing
    )
    {
        if (contribution == null)
        {
            throw new ArgumentNullException(nameof(contribution));
        }

        if (routing == null)
        {
            throw new ArgumentNullException(nameof(routing));
        }

        RoutingContribution replacement = new(routing.Clone());
        lock (_sync)
        {
            int index = _routerContributions.IndexOf(contribution);
            if (index < 0)
            {
                throw new ObjectDisposedException(nameof(RoutingContribution));
            }

            List<RoutingContribution> proposed = [.. _routerContributions];
            proposed[index] = replacement;
            LogStreamRoutingConfiguration replacementRouting = BuildRouting(_baseRouting, proposed);
            _routerContributions[index] = replacement;
            _routing = replacementRouting;
            SupersedeSubscriptionsLocked();
        }

        return replacement;
    }

    /// <summary>Records an event and pushes it to every live subscriber. Returns its sequence number.</summary>
    public long Publish(EventSinkLogEvent logEvent)
    {
        if (logEvent == null)
        {
            throw new ArgumentNullException(nameof(logEvent));
        }

        lock (_sync)
        {
            DateTime timestampUtc = UtcNow;

            LogStreamEvent streamEvent = new()
            {
                StreamId = StreamId,
                Sequence = ++_sequence,
                TimestampUtc = timestampUtc,
                LoggerCategory = logEvent.Category,
                Level = (int)logEvent.Level,
                Message = Truncate(logEvent.Message, MaxMessageLength),
                Detail = Truncate(logEvent.Detail, MaxDetailLength),
                EventId = logEvent.EventId.Id,
                EventName = logEvent.EventId.Name,
            };

            _events.Add(streamEvent);

            // One prune per publish. Pruning before the add as well would be pure O(n) waste on
            // the caller's logging thread: the timestamp does not change between the two, and the
            // event just added is the newest, so it can never be the one aged out.
            Prune(timestampUtc);

            // A subscriber whose bounded channel is full is dropped rather than allowed to stall the
            // publishing thread, which is the caller's logging path. ToArray so the set can be
            // mutated while iterating.
            foreach (LogEventStoreSubscription subscription in _subscriptions.ToArray())
            {
                if (subscription.TryWrite(streamEvent))
                {
                    continue;
                }

                _ = _subscriptions.Remove(subscription);
                subscription.Complete(SubscriptionEndReason.Overrun);
            }

            return streamEvent.Sequence;
        }
    }

    /// <summary>
    ///     Ends every live subscription so its reader re-subscribes and picks up the new routing.
    /// </summary>
    /// <remarks>
    ///     A subscriber resolves each event's destination from the routing snapshot it was handed
    ///     at subscribe time, so changing the routing without telling it leaves it filing events
    ///     under routes that no longer exist — silently, because an event that only a new route
    ///     admits simply resolves to no destination and is not shown. Ending the subscription is
    ///     the signal: the reader's loop already treats completion as "take a fresh subscription",
    ///     and a fresh one carries the new routing and replays the retained window, so nothing is
    ///     lost beyond what the window no longer holds.
    /// </remarks>
    private void SupersedeSubscriptionsLocked()
    {
        foreach (LogEventStoreSubscription subscription in _subscriptions.ToArray())
        {
            _ = _subscriptions.Remove(subscription);
            subscription.Complete(SubscriptionEndReason.Superseded);
        }
    }

    /// <summary>Opens a live subscription, with a snapshot of everything retained so far.</summary>
    /// <param name="liveSubscriptionCapacity">
    ///     How many events may queue for this subscriber before it is dropped. Defaults to this
    ///     store's own retention, so a subscriber can fall a whole window behind and still be
    ///     recoverable from the next snapshot; a fixed default smaller than the window would drop
    ///     subscribers a host had explicitly configured to keep more.
    /// </param>
    public LogEventStoreSubscription CreateSubscription(int? liveSubscriptionCapacity = null)
    {
        if (liveSubscriptionCapacity is <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(liveSubscriptionCapacity));
        }

        lock (_sync)
        {
            Prune(UtcNow);

            LogEventStoreSubscription subscription = new(this, liveSubscriptionCapacity ?? _retention.MaxEvents);
            _ = _subscriptions.Add(subscription);
            subscription.SetInitialization(CreateInitializationLocked());
            return subscription;
        }
    }

    /// <summary>
    ///     Bounds a field, marking it so a reader can tell truncation from a short value.
    /// </summary>
    private static string? Truncate(string? value, int maxLength)
    {
        const string marker = "... [truncated]";
        // Substring rather than a span overload: this file also targets net48, which has neither.
        return value is null || value.Length <= maxLength
            ? value
            : value.Substring(0, maxLength - marker.Length) + marker;
    }

    public LogStreamInitialization CreateInitialization()
    {
        lock (_sync)
        {
            Prune(UtcNow);
            return CreateInitializationLocked();
        }
    }

    private void RemoveSubscription(LogEventStoreSubscription subscription)
    {
        lock (_sync)
        {
            _ = _subscriptions.Remove(subscription);
        }
    }

    private LogStreamInitialization CreateInitializationLocked()
    {
        return new LogStreamInitialization
        {
            StreamId = StreamId,
            Routing = _routing.Clone(),
            ReplayEvents = [.. _events],
            HighWatermark = _sequence,
            MaxEvents = _retention.MaxEvents,
            MaxAgeMinutes = _retention.MaxAgeMinutes,
        };
    }

    private static LogStreamRoutingConfiguration BuildRouting(
        LogStreamRoutingConfiguration baseRouting,
        IEnumerable<RoutingContribution> contributions
    )
    {
        List<LogStreamRoutingConfiguration> sources =
        [
            baseRouting,
            .. contributions.Select(contribution => contribution.Routing),
        ];
        EventSinkRouteMatchMode? matchMode = null;
        foreach (
            EventSinkRouteMatchMode candidate in sources
                .Where(source =>
                    // ReSharper disable once ConstantConditionalAccessQualifier
                    // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
                    source.Routes?.Count > 0
                )
                .Select(source => source.MatchMode)
        )
        {
            if (matchMode.HasValue && matchMode.Value != candidate)
            {
                throw new InvalidOperationException("All nonempty routing contributions must use the same match mode.");
            }

            matchMode = candidate;
        }

        List<LogStreamRoute> routes = [];
        foreach (LogStreamRoutingConfiguration source in sources)
        {
            foreach (LogStreamRoute route in source.Clone().Routes)
            {
                route.Order = routes.Count;
                routes.Add(route);
            }
        }

        return new LogStreamRoutingConfiguration { MatchMode = matchMode ?? baseRouting.MatchMode, Routes = routes };
    }

    /// <summary>Drops events past the age limit, then past the count limit. Caller holds <see cref="_sync" />.</summary>
    private void Prune(DateTime timestampUtc)
    {
        DateTime minimumTimestamp = timestampUtc - TimeSpan.FromMinutes(_retention.MaxAgeMinutes);
        int firstCurrentIndex = _events.FindIndex(streamEvent => streamEvent.TimestampUtc >= minimumTimestamp);
        if (firstCurrentIndex < 0)
        {
            _events.Clear();
        }
        else if (firstCurrentIndex > 0)
        {
            _events.RemoveRange(0, firstCurrentIndex);
        }

        int excess = _events.Count - _retention.MaxEvents;
        if (excess > 0)
        {
            _events.RemoveRange(0, excess);
        }
    }

    public sealed class LogEventStoreSubscription : IDisposable
    {
        private readonly LogEventStore _owner;
        private readonly Channel<LogStreamEvent> _channel;
        private bool _disposed;

        internal LogEventStoreSubscription(LogEventStore owner, int capacity)
        {
            _owner = owner;
            _channel = Channel.CreateBounded<LogStreamEvent>(
                new BoundedChannelOptions(capacity)
                {
                    FullMode = BoundedChannelFullMode.Wait,
                    SingleReader = true,
                    SingleWriter = true,
                }
            );
        }

        public LogStreamInitialization? Initialization { get; private set; }

        /// <summary>
        ///     Why the subscription ended, once <see cref="Events" /> completes.
        /// </summary>
        /// <remarks>
        ///     A reader re-subscribes either way; the distinction is what it should say about it.
        ///     Overrun means this subscriber could not keep up and events were lost, which is worth
        ///     a warning. Superseded means the routing changed underneath it, which is routine.
        /// </remarks>
        public SubscriptionEndReason EndReason { get; private set; } = SubscriptionEndReason.Disposed;

        public ChannelReader<LogStreamEvent> Events => _channel.Reader;

        internal bool TryWrite(LogStreamEvent streamEvent)
        {
            return _channel.Writer.TryWrite(streamEvent);
        }

        internal void SetInitialization(LogStreamInitialization initialization)
        {
            Initialization = initialization;
        }

        internal void Complete(SubscriptionEndReason reason)
        {
            // First reason wins: whatever ended it first is the true cause.
            if (_channel.Writer.TryComplete())
            {
                EndReason = reason;
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _owner.RemoveSubscription(this);
            Complete(SubscriptionEndReason.Disposed);
        }
    }

    internal sealed class RoutingContribution
    {
        public RoutingContribution(LogStreamRoutingConfiguration routing)
        {
            Routing = routing;
        }

        public LogStreamRoutingConfiguration Routing { get; }
    }
}

public static class LogStreamRoutingConfigurationExtensions
{
    /// <summary>Projects the adapter-facing route options into the wire shape sent to clients.</summary>
    public static LogStreamRoutingConfiguration CreateSnapshot(this EventSinkRouteOptions options)
    {
        if (options == null)
        {
            throw new ArgumentNullException(nameof(options));
        }

        return new LogStreamRoutingConfiguration
        {
            MatchMode = options.MatchMode,
            Routes =
            [
                // ReSharper disable once ConstantNullCoalescingCondition
                // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
                .. (options.Routes ?? []).Select(
                    (route, index) =>
                        new LogStreamRoute
                        {
                            Order = index,
                            LoggerName = route.CategoryPattern ?? string.Empty,
                            LoggerNameMatchMode =
                                route.CategoryPattern == "*"
                                    ? LoggerNameMatchMode.Wildcard
                                    : LoggerNameMatchMode.Prefix,
                            MinLevel = route.MinLevel.HasValue ? (int)route.MinLevel.Value : null,
                            MaxLevel = route.MaxLevel.HasValue ? (int)route.MaxLevel.Value : null,
                            StopProcessing = route.StopProcessing,
                            Destinations =
                            [
                                // ReSharper disable once ConstantNullCoalescingCondition
                                // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
                                .. (route.Destinations ?? []).Select(destination => new LogStreamRouteDestination
                                {
                                    Category = destination.SinkCategory.ToSnapshot(),
                                    Name = destination.SinkName.ToSnapshot(),
                                }),
                            ],
                        }
                ),
            ],
        };
    }

    public static LogStreamRoutingConfiguration Clone(this LogStreamRoutingConfiguration routing)
    {
        if (routing == null)
        {
            throw new ArgumentNullException(nameof(routing));
        }

        return new LogStreamRoutingConfiguration
        {
            MatchMode = routing.MatchMode,
            Routes =
            [
                // ReSharper disable once ConstantNullCoalescingCondition
                // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
                .. (routing.Routes ?? []).Select(route => new LogStreamRoute
                {
                    Order = route.Order,
                    LoggerName = route.LoggerName,
                    LoggerNameMatchMode = route.LoggerNameMatchMode,
                    MinLevel = route.MinLevel,
                    MaxLevel = route.MaxLevel,
                    StopProcessing = route.StopProcessing,
                    Destinations =
                    [
                        // ReSharper disable once ConstantNullCoalescingCondition
                        // Routes and Destinations have public setters, so a caller can still assign null despite the annotation.
                        .. (route.Destinations ?? []).Select(destination => new LogStreamRouteDestination
                        {
                            Category = destination.Category.ToSnapshot(),
                            Name = destination.Name.ToSnapshot(),
                        }),
                    ],
                }),
            ],
        };
    }

    /// <summary>
    ///     Destinations are validated non-null when a route is compiled, but both projections are
    ///     also reachable from a hand-built configuration passed to the public
    ///     <see cref="LogEventStore.Configure" />, so an absent value projects as an empty fixed
    ///     value rather than throwing.
    /// </summary>
    private static LogStreamRouteValue ToSnapshot(this RouteValue? routeValue)
    {
        return routeValue == null
            ? new LogStreamRouteValue()
            : new LogStreamRouteValue { Source = routeValue.Source, Value = routeValue.Value };
    }

    /// <inheritdoc cref="ToSnapshot(RouteValue)" />
    private static LogStreamRouteValue ToSnapshot(this LogStreamRouteValue? routeValue)
    {
        return routeValue == null
            ? new LogStreamRouteValue()
            : new LogStreamRouteValue { Source = routeValue.Source, Value = routeValue.Value };
    }
}
