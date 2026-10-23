#nullable enable annotations

using System.ComponentModel;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer.Logging;

/// <summary>How many routes an event is allowed to match.</summary>
public enum EventSinkRouteMatchMode
{
    AllMatches,
    MostSpecific,
    FirstMatch,
}

/// <summary>Whether a destination value is fixed, or derived from the logger name at routing time.</summary>
public enum RouteValueSource
{
    Fixed,
    LoggerSuffix,
}

[TypeConverter(typeof(RouteValueConverter))]
public sealed class RouteValue
{
    public RouteValueSource Source { get; set; }

    public string? Value { get; set; }

    public static RouteValue LoggerSuffix => new() { Source = RouteValueSource.LoggerSuffix };

    public static RouteValue Fixed(string value) => new() { Value = value };

    public static implicit operator RouteValue(string value) => Fixed(value);

    /// <summary>Named alternative to the implicit conversion, for callers that avoid operators.</summary>
    public static RouteValue FromString(string value) => Fixed(value);
}

public sealed class RouteValueConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    // ReSharper disable NullnessAnnotationConflictWithJetBrainsAnnotations
    // ReSharper disable AssignNullToNotNullAttribute
    // ReSharper's bundled annotations for TypeConverter.ConvertFrom disagree with the nullable annotations on the BCL signature this override mirrors.
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        return value is string text ? RouteValue.Fixed(text) : base.ConvertFrom(context, culture, value);
    }
    // ReSharper restore AssignNullToNotNullAttribute
    // ReSharper restore NullnessAnnotationConflictWithJetBrainsAnnotations
}

/// <summary>
///     The routing table an adapter is configured with. Routes are evaluated in the order they were
///     added.
/// </summary>
public sealed class EventSinkRouteOptions
{
    public EventSinkRouteMatchMode MatchMode { get; set; } = EventSinkRouteMatchMode.AllMatches;

    public List<EventSinkRoute> Routes { get; set; } = [];

    public EventSinkRouteOptions UseMatchMode(EventSinkRouteMatchMode matchMode)
    {
        MatchMode = matchMode;
        return this;
    }

    public EventSinkRouteOptions Route(string categoryPattern, Action<EventSinkRoute> configure)
    {
        if (string.IsNullOrWhiteSpace(categoryPattern))
        {
            throw new ArgumentException("A category pattern is required.", nameof(categoryPattern));
        }

        if (configure == null)
        {
            throw new ArgumentNullException(nameof(configure));
        }

        EventSinkRoute route = new() { CategoryPattern = categoryPattern };
        configure(route);
        Routes.Add(route);
        return this;
    }
}

public sealed class EventSinkRoute
{
    public string? CategoryPattern { get; set; }

    public LogLevel? MinLevel { get; set; }

    public LogLevel? MaxLevel { get; set; }

    public List<EventSinkDestination> Destinations { get; set; } = [];

    public bool StopProcessing { get; set; }

    public EventSinkRoute AtLeast(LogLevel minLevel)
    {
        MinLevel = minLevel;
        return this;
    }

    public EventSinkRoute AtMost(LogLevel maxLevel)
    {
        MaxLevel = maxLevel;
        return this;
    }

    public EventSinkRoute To(string sinkCategory, string sinkName)
    {
        return To(RouteValue.Fixed(sinkCategory), RouteValue.Fixed(sinkName));
    }

    public EventSinkRoute To(RouteValue sinkCategory, RouteValue sinkName)
    {
        ValidateRouteValue(sinkCategory, nameof(sinkCategory));
        ValidateRouteValue(sinkName, nameof(sinkName));

        Destinations.Add(new EventSinkDestination { SinkCategory = sinkCategory, SinkName = sinkName });
        return this;
    }

    public EventSinkRoute StopAfterMatch(bool stopProcessing = true)
    {
        StopProcessing = stopProcessing;
        return this;
    }

    private static void ValidateRouteValue(RouteValue routeValue, string parameterName)
    {
        if (routeValue == null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (!Enum.IsDefined(typeof(RouteValueSource), routeValue.Source))
        {
            throw new ArgumentOutOfRangeException(parameterName, "The route value source is invalid.");
        }

        if (routeValue.Source == RouteValueSource.Fixed && string.IsNullOrWhiteSpace(routeValue.Value))
        {
            throw new ArgumentException("A fixed route value is required.", parameterName);
        }
    }
}

[TypeConverter(typeof(EventSinkDestinationConverter))]
public sealed class EventSinkDestination
{
    public RouteValue? SinkName { get; set; }

    public RouteValue? SinkCategory { get; set; }
}

public sealed class EventSinkDestinationConverter : TypeConverter
{
    public override bool CanConvertFrom(ITypeDescriptorContext? context, Type sourceType)
    {
        return sourceType == typeof(string) || base.CanConvertFrom(context, sourceType);
    }

    // ReSharper disable NullnessAnnotationConflictWithJetBrainsAnnotations
    // ReSharper disable AssignNullToNotNullAttribute
    // ReSharper's bundled annotations for TypeConverter.ConvertFrom disagree with the nullable annotations on the BCL signature this override mirrors.
    public override object? ConvertFrom(ITypeDescriptorContext? context, CultureInfo? culture, object value)
    {
        if (value is not string destination)
        {
            return base.ConvertFrom(context, culture, value);
        }

        string[] parts = destination.Split('/');
        if (parts.Length != 2 || string.IsNullOrWhiteSpace(parts[0]) || string.IsNullOrWhiteSpace(parts[1]))
        {
            throw new FormatException("A destination string must use the format 'SinkCategory/SinkName'.");
        }

        return new EventSinkDestination
        {
            SinkCategory = RouteValue.Fixed(parts[0].Trim()),
            SinkName = RouteValue.Fixed(parts[1].Trim()),
        };
    }
    // ReSharper restore AssignNullToNotNullAttribute
    // ReSharper restore NullnessAnnotationConflictWithJetBrainsAnnotations
}
