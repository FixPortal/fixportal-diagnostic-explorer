using System.Reflection;
using DiagnosticExplorer.Logging;
using Microsoft.Extensions.Logging;

namespace DiagnosticExplorer;

// The configuration MODEL: the data the getter pipeline reads when deciding how to present a
// property. Upstream keeps these in DiagnosticConfiguration.cs alongside the fluent builder that
// produces them. They are split out here because the getters need only this half, and the two
// halves are mutually dependent at file level - the builder reaches back into the getters through
// InlineCustomObject. Splitting at this seam lets the getter merge land wired up and testable
// instead of waiting on the whole 3,000-line configuration surface. Nothing below refers to the
// builder, which is what makes the seam real rather than convenient.
internal sealed class TypeConfiguration
{
    private readonly Dictionary<string, PropertyConfiguration> _properties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, PropertyConfiguration> _delegateProperties = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CustomPropertyConfiguration> _customProperties = new(StringComparer.Ordinal);
    private readonly List<DrillDownEventRouteTemplate> _eventRoutes = [];

    public TypeConfiguration(Type type)
    {
        Type = type;
    }

    public Type Type { get; }
    public bool? IncludeAll { get; set; }
    public IEnumerable<PropertyConfiguration> Properties => _properties.Values;
    public IEnumerable<PropertyConfiguration> DelegateProperties => _delegateProperties.Values;
    public IEnumerable<CustomPropertyConfiguration> CustomProperties => _customProperties.Values;
    public IEnumerable<DrillDownEventRouteTemplate> EventRoutes => _eventRoutes;

    public void AddEventRoute(DrillDownEventRouteTemplate route)
    {
        _eventRoutes.Add(route ?? throw new ArgumentNullException(nameof(route)));
    }

    public PropertyConfiguration GetOrAdd(PropertyInfo property)
    {
        string key = GetPropertyKey(property);
        if (!_properties.TryGetValue(key, out PropertyConfiguration configuration))
        {
            configuration = new PropertyConfiguration(property);
            _properties.Add(key, configuration);
        }
        return configuration;
    }

    public PropertyConfiguration Find(PropertyInfo property)
    {
        _properties.TryGetValue(GetPropertyKey(property), out PropertyConfiguration configuration);
        return configuration;
    }

    public CustomPropertyConfiguration AddCustomProperty(string name, Func<object, object> value)
    {
        CustomPropertyConfiguration configuration = new(name, value);
        _customProperties.Add(name, configuration);
        return configuration;
    }

    public PropertyConfiguration AddDelegateProperty(string name, Type valueType, Func<object, object> value)
    {
        PropertyConfiguration configuration = new(name, valueType, value);
        _delegateProperties.Add(name, configuration);
        return configuration;
    }

    public TypeConfiguration Clone()
    {
        TypeConfiguration clone = new(Type) { IncludeAll = IncludeAll };
        foreach (PropertyConfiguration property in _properties.Values)
        {
            clone._properties.Add(GetPropertyKey(property.Property), property.Clone());
        }

        foreach (PropertyConfiguration property in _delegateProperties.Values)
        {
            clone._delegateProperties.Add(property.Name.Value, property.Clone());
        }

        foreach (CustomPropertyConfiguration property in _customProperties.Values)
        {
            clone._customProperties.Add(property.Name, property.Clone());
        }

        clone._eventRoutes.AddRange(_eventRoutes.Select(route => route.Clone()));
        return clone;
    }

    public void Merge(TypeConfiguration source)
    {
        if (source.IncludeAll.HasValue)
        {
            IncludeAll = source.IncludeAll;
        }

        foreach (PropertyConfiguration sourceProperty in source.Properties)
        {
            PropertyConfiguration target = GetOrAdd(sourceProperty.Property);
            target.Merge(sourceProperty);
        }

        foreach (PropertyConfiguration sourceProperty in source.DelegateProperties)
        {
            _delegateProperties[sourceProperty.Name.Value] = sourceProperty.Clone();
        }

        foreach (CustomPropertyConfiguration sourceProperty in source.CustomProperties)
        {
            _customProperties[sourceProperty.Name] = sourceProperty.Clone();
        }

        _eventRoutes.AddRange(source.EventRoutes.Select(route => route.Clone()));
    }

    private static string GetPropertyKey(PropertyInfo property)
    {
        return property.Name;
    }
}

public sealed class DrillDownEventRoute
{
    internal string Category { get; private set; }
    internal string Name { get; private set; }
    internal LogLevel? MinLevel { get; private set; }
    internal LogLevel? MaxLevel { get; private set; }

    public DrillDownEventRoute To(string category, string name)
    {
        if (string.IsNullOrWhiteSpace(category))
        {
            throw new ArgumentException("An event-view category is required.", nameof(category));
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            throw new ArgumentException("An event-view name is required.", nameof(name));
        }

        Category = category;
        Name = name;
        return this;
    }

    public DrillDownEventRoute AtLeast(LogLevel minLevel)
    {
        MinLevel = minLevel;
        return this;
    }

    public DrillDownEventRoute AtMost(LogLevel maxLevel)
    {
        MaxLevel = maxLevel;
        return this;
    }

    internal void Validate()
    {
        if (string.IsNullOrWhiteSpace(Category) || string.IsNullOrWhiteSpace(Name))
        {
            throw new InvalidOperationException("A drilldown event route must define one destination.");
        }

        if (MinLevel > MaxLevel)
        {
            throw new InvalidOperationException(
                "A drilldown event route minimum level cannot exceed its maximum level."
            );
        }
    }
}

internal sealed class DrillDownEventRouteTemplate
{
    public DrillDownEventRouteTemplate(
        string staticLoggerName,
        LoggerNameMatchMode matchMode,
        DrillDownEventRoute route
    )
    {
        LoggerName = staticLoggerName;
        MatchMode = matchMode;
        Route = route;
    }

    public DrillDownEventRouteTemplate(
        Func<object, string> loggerName,
        LoggerNameMatchMode matchMode,
        DrillDownEventRoute route
    )
    {
        LoggerNameFactory = loggerName;
        MatchMode = matchMode;
        Route = route;
    }

    public string LoggerName { get; }
    public Func<object, string> LoggerNameFactory { get; }
    public LoggerNameMatchMode MatchMode { get; }
    public DrillDownEventRoute Route { get; }

    public string ResolveLoggerName(object target) => LoggerNameFactory?.Invoke(target) ?? LoggerName;

    public DrillDownEventRouteTemplate Clone()
    {
        DrillDownEventRoute route = new DrillDownEventRoute().To(Route.Category, Route.Name);
        if (Route.MinLevel.HasValue)
        {
            route.AtLeast(Route.MinLevel.Value);
        }

        if (Route.MaxLevel.HasValue)
        {
            route.AtMost(Route.MaxLevel.Value);
        }

        return LoggerNameFactory == null
            ? new DrillDownEventRouteTemplate(LoggerName, MatchMode, route)
            : new DrillDownEventRouteTemplate(LoggerNameFactory, MatchMode, route);
    }
}

internal sealed class PropertyConfiguration
{
    public PropertyConfiguration(PropertyInfo property)
    {
        Property = property;
    }

    public PropertyConfiguration(string name, Type valueType, Func<object, object> value)
    {
        Name = new ConfiguredValue<string>(name);
        ValueType = valueType;
        Value = value;
        UsesPropertyDefaults = true;
    }

    public PropertyInfo Property { get; }
    public Type ValueType { get; }
    public Func<object, object> Value { get; private set; }
    public bool? Included { get; set; }
    public bool UsesPropertyDefaults { get; set; }
    public PropertyStrategy? Strategy { get; set; }
    public ConfiguredValue<string> Name { get; set; }
    public Func<object, string> NameFormatter { get; set; }
    public ConfiguredValue<string> Category { get; set; }
    public Func<object, string> CategoryFormatter { get; set; }
    public ConfiguredValue<bool> CategoryInitiallyExpanded { get; set; }
    public ConfiguredValue<string> CategoryExpansionScope { get; set; }
    public ConfiguredValue<string> Description { get; set; }
    public Func<object, string> DescriptionFormatter { get; set; }
    public List<PropertyAlertConfiguration> Alerts { get; } = [];
    public List<PropertyStatusConfiguration> Statuses { get; } = [];
    public ConfiguredValue<string> FormatString { get; set; }
    public Func<object, string> ValueFormatter { get; set; }
    public Func<object, string> TextFormatter { get; set; }
    public ConfiguredValue<bool> IsJson { get; set; }
    public ConfiguredValue<StatusIconSize> StatusIconSize { get; set; }
    public ConfiguredValue<bool> AllowSet { get; set; }
    public ConfiguredValue<bool> ExposeRate { get; set; }
    public ConfiguredValue<bool> ExposeTotal { get; set; }
    public ConfiguredValue<bool> ExposeDate { get; set; }
    public ConfiguredValue<bool> ExposeElapsed { get; set; }
    public ConfiguredValue<bool> ExposeTimeUntil { get; set; }
    public ConfiguredValue<bool> InitiallyExpanded { get; set; }
    public ConfiguredValue<bool> PrimaryPropertiesOnly { get; set; }
    public ConfiguredValue<int> MaxItems { get; set; }
    public ConfiguredValue<bool> NoTruncate { get; set; }
    public ConfiguredValue<bool> DrillDown { get; set; }
    public ConfiguredValue<int> DrillDownMaxItems { get; set; }
    public ConfiguredValue<bool> DrillDownIconOnly { get; set; }
    public ConfiguredValue<string> DrillDownText { get; set; }
    public Func<object, string> DrillDownTextFormatter { get; set; }
    public ConfiguredValue<bool> JsonHover { get; set; }
    public ConfiguredValue<bool> ExpandedHover { get; set; }
    public List<CollectionOutputConfiguration> CollectionOutputs { get; } = [];

    public PropertyConfiguration Clone()
    {
        PropertyConfiguration clone =
            Property == null
                ? new PropertyConfiguration(Name.Value, ValueType, Value)
                : new PropertyConfiguration(Property);
        clone.Merge(this);
        return clone;
    }

    public PropertyConfiguration Bind(object source)
    {
        PropertyConfiguration bound = Clone();
        bound.Value = _ => Value(source);
        bound.NameFormatter = NameFormatter == null ? null : _ => NameFormatter(source);
        bound.CategoryFormatter = CategoryFormatter == null ? null : _ => CategoryFormatter(source);
        bound.DescriptionFormatter = DescriptionFormatter == null ? null : _ => DescriptionFormatter(source);
        bound.TextFormatter = TextFormatter == null ? null : _ => TextFormatter(source);
        bound.DrillDownTextFormatter = DrillDownTextFormatter == null ? null : _ => DrillDownTextFormatter(source);
        bound.Alerts.Clear();
        bound.Alerts.AddRange(
            Alerts.Select(alert => new PropertyAlertConfiguration(
                alert.Severity,
                _ => alert.Condition(source),
                _ => alert.Message(source),
                alert.Category == null ? null : _ => alert.Category(source)
            ))
        );
        bound.Statuses.AddRange(
            Statuses.Select(status => new PropertyStatusConfiguration(
                status.Status,
                _ => status.Condition(source),
                _ => status.Text(source)
            ))
        );
        return bound;
    }

    public void Merge(PropertyConfiguration source)
    {
        Included = source.Included ?? Included;
        UsesPropertyDefaults |= source.UsesPropertyDefaults;
        Strategy = source.Strategy ?? Strategy;
        Name = source.Name.Or(Name);
        NameFormatter = source.NameFormatter ?? NameFormatter;
        Category = source.Category.Or(Category);
        CategoryFormatter = source.CategoryFormatter ?? CategoryFormatter;
        CategoryInitiallyExpanded = source.CategoryInitiallyExpanded.Or(CategoryInitiallyExpanded);
        CategoryExpansionScope = source.CategoryExpansionScope.Or(CategoryExpansionScope);
        Description = source.Description.Or(Description);
        DescriptionFormatter = source.DescriptionFormatter ?? DescriptionFormatter;
        FormatString = source.FormatString.Or(FormatString);
        ValueFormatter = source.ValueFormatter ?? ValueFormatter;
        TextFormatter = source.TextFormatter ?? TextFormatter;
        IsJson = source.IsJson.Or(IsJson);
        StatusIconSize = source.StatusIconSize.Or(StatusIconSize);
        AllowSet = source.AllowSet.Or(AllowSet);
        ExposeRate = source.ExposeRate.Or(ExposeRate);
        ExposeTotal = source.ExposeTotal.Or(ExposeTotal);
        ExposeDate = source.ExposeDate.Or(ExposeDate);
        ExposeElapsed = source.ExposeElapsed.Or(ExposeElapsed);
        ExposeTimeUntil = source.ExposeTimeUntil.Or(ExposeTimeUntil);
        InitiallyExpanded = source.InitiallyExpanded.Or(InitiallyExpanded);
        PrimaryPropertiesOnly = source.PrimaryPropertiesOnly.Or(PrimaryPropertiesOnly);
        MaxItems = source.MaxItems.Or(MaxItems);
        NoTruncate = source.NoTruncate.Or(NoTruncate);
        DrillDown = source.DrillDown.Or(DrillDown);
        DrillDownMaxItems = source.DrillDownMaxItems.Or(DrillDownMaxItems);
        DrillDownIconOnly = source.DrillDownIconOnly.Or(DrillDownIconOnly);
        DrillDownText = source.DrillDownText.Or(DrillDownText);
        DrillDownTextFormatter = source.DrillDownTextFormatter ?? DrillDownTextFormatter;
        JsonHover = source.JsonHover.Or(JsonHover);
        ExpandedHover = source.ExpandedHover.Or(ExpandedHover);
        Alerts.AddRange(source.Alerts.Select(alert => alert.Clone()));
        Statuses.AddRange(source.Statuses.Select(status => status.Clone()));
        if (source.CollectionOutputs.Count > 0)
        {
            CollectionOutputs.Clear();
            CollectionOutputs.AddRange(source.CollectionOutputs.Select(output => output.Clone()));
        }
    }
}

internal sealed class PropertyAlertConfiguration
{
    public PropertyAlertConfiguration(
        PropertyAlertSeverity severity,
        Func<object, bool> condition,
        Func<object, string> message,
        Func<object, string> category
    )
    {
        Severity = severity;
        Condition = condition;
        Message = message;
        Category = category;
    }

    public PropertyAlertSeverity Severity { get; }
    public Func<object, bool> Condition { get; }
    public Func<object, string> Message { get; }
    public Func<object, string> Category { get; }

    public PropertyAlertConfiguration Clone() => new(Severity, Condition, Message, Category);
}

internal sealed class PropertyStatusConfiguration
{
    public PropertyStatusConfiguration(StatusCode status, Func<object, bool> condition, Func<object, string> text)
    {
        Status = status;
        Condition = condition;
        Text = text;
    }

    public StatusCode Status { get; }
    public Func<object, bool> Condition { get; }
    public Func<object, string> Text { get; }

    public PropertyStatusConfiguration Clone() => new(Status, Condition, Text);
}

internal sealed class CustomPropertyConfiguration
{
    public CustomPropertyConfiguration(string name, Func<object, object> value)
    {
        Name = name;
        Value = value;
    }

    public string Name { get; }
    public Func<object, object> Value { get; }
    public ConfiguredValue<string> Category { get; set; }
    public Func<object, string> CategoryFormatter { get; set; }
    public ConfiguredValue<bool> CategoryInitiallyExpanded { get; set; }
    public ConfiguredValue<string> CategoryExpansionScope { get; set; }
    public ConfiguredValue<string> Description { get; set; }
    public Func<object, string> DescriptionFormatter { get; set; }
    public Func<object, string> ValueFormatter { get; set; }
    public ConfiguredValue<bool> IsJson { get; set; }
    public List<PropertyAlertConfiguration> Alerts { get; } = [];
    public List<PropertyStatusConfiguration> Statuses { get; } = [];
    public ConfiguredValue<bool> InitiallyExpanded { get; set; }
    public ConfiguredValue<bool> DrillDown { get; set; }
    public ConfiguredValue<int> DrillDownMaxItems { get; set; }
    public ConfiguredValue<bool> DrillDownIconOnly { get; set; }
    public ConfiguredValue<string> DrillDownText { get; set; }
    public Func<object, string> DrillDownTextFormatter { get; set; }
    public ConfiguredValue<bool> JsonHover { get; set; }
    public ConfiguredValue<bool> ExpandedHover { get; set; }

    public CustomPropertyConfiguration Clone()
    {
        CustomPropertyConfiguration clone = new(Name, Value)
        {
            Category = Category,
            CategoryFormatter = CategoryFormatter,
            CategoryInitiallyExpanded = CategoryInitiallyExpanded,
            CategoryExpansionScope = CategoryExpansionScope,
            Description = Description,
            DescriptionFormatter = DescriptionFormatter,
            ValueFormatter = ValueFormatter,
            IsJson = IsJson,
            InitiallyExpanded = InitiallyExpanded,
            DrillDown = DrillDown,
            DrillDownMaxItems = DrillDownMaxItems,
            DrillDownIconOnly = DrillDownIconOnly,
            DrillDownText = DrillDownText,
            DrillDownTextFormatter = DrillDownTextFormatter,
            JsonHover = JsonHover,
            ExpandedHover = ExpandedHover,
        };
        clone.Alerts.AddRange(Alerts.Select(alert => alert.Clone()));
        clone.Statuses.AddRange(Statuses.Select(status => status.Clone()));
        return clone;
    }

    public CustomPropertyConfiguration Bind(object source)
    {
        CustomPropertyConfiguration bound = new(Name, _ => Value(source))
        {
            Category = Category,
            CategoryFormatter = CategoryFormatter == null ? null : _ => CategoryFormatter(source),
            CategoryInitiallyExpanded = CategoryInitiallyExpanded,
            CategoryExpansionScope = CategoryExpansionScope,
            Description = Description,
            DescriptionFormatter = DescriptionFormatter == null ? null : _ => DescriptionFormatter(source),
            ValueFormatter = ValueFormatter,
            IsJson = IsJson,
            InitiallyExpanded = InitiallyExpanded,
            DrillDown = DrillDown,
            DrillDownMaxItems = DrillDownMaxItems,
            DrillDownIconOnly = DrillDownIconOnly,
            DrillDownText = DrillDownText,
            DrillDownTextFormatter = DrillDownTextFormatter == null ? null : _ => DrillDownTextFormatter(source),
            JsonHover = JsonHover,
            ExpandedHover = ExpandedHover,
        };
        bound.Alerts.AddRange(
            Alerts.Select(alert => new PropertyAlertConfiguration(
                alert.Severity,
                _ => alert.Condition(source),
                _ => alert.Message(source),
                alert.Category == null ? null : _ => alert.Category(source)
            ))
        );
        bound.Statuses.AddRange(
            Statuses.Select(status => new PropertyStatusConfiguration(
                status.Status,
                _ => status.Condition(source),
                _ => status.Text(source)
            ))
        );
        return bound;
    }
}

internal readonly struct ConfiguredValue<T>
{
    public ConfiguredValue(T value)
    {
        IsSet = true;
        Value = value;
    }

    public bool IsSet { get; }
    public T Value { get; }

    public ConfiguredValue<T> Or(ConfiguredValue<T> fallback) => IsSet ? this : fallback;
}

internal sealed class CollectionOutputConfiguration
{
    public CollectionMode Mode { get; set; }
    public string Name { get; set; }
    public string Separator { get; set; }
    public string NameProperty { get; set; }
    public Func<object, string> NameFormatter { get; set; }
    public Func<object, int, string> IndexedNameFormatter { get; set; }
    public string ValueProperty { get; set; }
    public Func<object, string> ValueFormatter { get; set; }
    public bool ItemIsJson { get; set; }
    public string DescriptionProperty { get; set; }
    public Func<object, string> DescriptionFormatter { get; set; }
    public string CategoryProperty { get; set; }
    public Func<object, string> CategoryFormatter { get; set; }
    public ConfiguredValue<bool> NoTruncate { get; set; }
    public ConfiguredValue<bool> DrillDown { get; set; }
    public ConfiguredValue<int> DrillDownMaxItems { get; set; }
    public ConfiguredValue<bool> DrillDownIconOnly { get; set; }
    public ConfiguredValue<string> DrillDownText { get; set; }
    public Func<object, string> DrillDownTextFormatter { get; set; }
    public ConfiguredValue<bool> JsonHover { get; set; }
    public ConfiguredValue<bool> ExpandedHover { get; set; }
    public bool InitiallyExpanded { get; set; } = true;
    public bool PrimaryPropertiesOnly { get; set; }
    public List<PropertyStatusConfiguration> ItemStatuses { get; set; } = [];
    public ConfiguredValue<StatusIconSize> ItemStatusIconSize { get; set; }
    public int ItemWidth { get; set; }

    public CollectionOutputConfiguration Clone()
    {
        CollectionOutputConfiguration clone = (CollectionOutputConfiguration)MemberwiseClone();
        clone.ItemStatuses = [.. ItemStatuses.Select(status => status.Clone())];
        return clone;
    }
}

/// <summary>
///     Which getter a configured property is realised by. Upstream declares this alongside the
///     fluent builder; it lives with the model here because the getter pipeline dispatches on it.
/// </summary>
internal enum PropertyStrategy
{
    Default,
    Collection,
    Rate,
    Date,
    Extended,
}

/// <summary>
///     An object whose properties are projected inline into a parent bag rather than appearing as
///     a nested value.
/// </summary>
/// <remarks>
///     Only the contract lives here. The implementation is built by the fluent configuration
///     surface and arrives with it; the getter layer needs nothing more than this to call into it.
/// </remarks>
internal interface IInlineCustomObject
{
    void AddProperties(PropertyBag bag);

    void AddProperties(PropertyBag bag, string category);
}
