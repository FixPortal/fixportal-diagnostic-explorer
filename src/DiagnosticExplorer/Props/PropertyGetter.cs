#region Copyright

// Diagnostic Explorer, a .Net diagnostic toolset
// Copyright (C) 2010 Cameron Elliot
//
// This file is part of Diagnostic Explorer.
//
// Diagnostic Explorer is free software: you can redistribute it and/or modify
// it under the terms of the GNU Lesser General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// Diagnostic Explorer is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU Lesser General Public License for more details.
//
// You should have received a copy of the GNU Lesser General Public License
// along with Diagnostic Explorer.  If not, see <http://www.gnu.org/licenses/>.
//
// http://diagexplorer.sourceforge.net/

#endregion

using System.Collections;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using DiagnosticExplorer.Util;

namespace DiagnosticExplorer;

internal class PropertyGetter
{
    // Upstream raised this to 100. Deliberately kept at 10: it is the default cap for EVERY
    // unconfigured collection property, so the change would multiply the inline payload of every
    // collection-valued property by ten on every poll. That is a presentation and bandwidth
    // decision to take on its own evidence, not a side effect of porting the getter layer. Three
    // tests pin the current cap.
    public const int MaxConcatItems = 10;
    private readonly Func<object, string> _nameFormatter;
    private readonly Func<object, string> _categoryFormatter;
    private ConfiguredValue<bool> _categoryInitiallyExpanded;
    private ConfiguredValue<string> _categoryExpansionScope;
    private readonly Func<object, string> _descriptionFormatter;
    private Func<object, string> _valueFormatter;
    private readonly Func<object, string> _textFormatter;
    private IReadOnlyList<PropertyAlertConfiguration> _alerts;
    private IReadOnlyList<PropertyStatusConfiguration> _statuses;
    protected bool DrillDownEnabled { get; private set; }
    protected int DrillDownMaxItems { get; private set; }
    protected bool DrillDownIconOnly { get; private set; }
    protected string DrillDownText { get; private set; }
    private Func<object, string> _drillDownTextFormatter;
    protected bool JsonHoverEnabled { get; private set; }
    protected bool ExpandedHoverEnabled { get; private set; }
    protected bool NoTruncate { get; private set; }
    protected StatusIconSize StatusIconSize { get; private set; }
    protected bool IsJson { get; private set; }

    protected PropertyGetter() { }

    public PropertyGetter(PropertyInfo propInfo, bool isStatic)
        : this(propInfo, AttributeUtil.GetAttribute<DiagnosticPropertyAttribute>(propInfo), null, isStatic) { }

    public PropertyGetter(PropertyInfo propInfo, DiagnosticPropertyAttribute propAttr, bool isStatic)
        : this(propInfo, propAttr, null, isStatic) { }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "A flat sequence of independent IsSet checks, one per configurable facet. "
            + "Splitting it would scatter the mapping it exists to make readable in one place."
    )]
    internal PropertyGetter(
        PropertyInfo propInfo,
        DiagnosticPropertyAttribute propAttr,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes = true,
        string defaultFormat = null,
        bool defaultDrillDown = false
    )
    {
        PropInfo = propInfo;

        // One of the two must supply a value function. Without either, GetFunc stays null and the
        // failure surfaces much later as a NullReferenceException from GetValue, on whichever poll
        // first reaches this property. Fail at construction instead, where the cause is visible.
        if (propInfo == null && configuration == null)
        {
            throw new ArgumentException(
                "A property getter needs either a PropertyInfo or a configuration to read its value from.",
                nameof(propInfo)
            );
        }

        if (propInfo != null)
        {
            GetFunc = PropertyToFunction(propInfo, isStatic);
            Name = propInfo.Name;

            if (applyAttributes)
            {
                DiagnosticClassAttribute classAttr = propInfo
                    .DeclaringType?.GetCustomAttributes(typeof(DiagnosticClassAttribute), true)
                    .Cast<DiagnosticClassAttribute>()
                    .FirstOrDefault();

                if (classAttr != null && classAttr.AllPropertiesSettable)
                {
                    CanSet = propInfo.CanWrite && classAttr.AllPropertiesSettable;
                }
            }

            if (propAttr != null)
            {
                Name = propAttr.Name ?? Name;
                Category = propAttr.Category;
                Description = propAttr.Description;
                FormatString = propAttr.FormatString;
                if (propInfo.CanWrite && propAttr.AllowSetSpecified)
                {
                    CanSet = propAttr.AllowSet;
                }
            }
        }
        else
        {
            GetFunc = configuration.Value;
            Name = configuration.Name.Value;
        }

        if (configuration != null)
        {
            if (configuration.Name.IsSet)
            {
                Name = configuration.Name.Value;
            }

            _nameFormatter = configuration.NameFormatter;
            if (configuration.Category.IsSet)
            {
                Category = configuration.Category.Value;
            }

            _categoryFormatter = configuration.CategoryFormatter;
            _categoryInitiallyExpanded = configuration.CategoryInitiallyExpanded;
            _categoryExpansionScope = configuration.CategoryExpansionScope;
            if (configuration.Description.IsSet)
            {
                Description = configuration.Description.Value;
            }

            _descriptionFormatter = configuration.DescriptionFormatter;
            if (configuration.FormatString.IsSet)
            {
                FormatString = configuration.FormatString.Value;
            }

            _valueFormatter = configuration.ValueFormatter;
            _textFormatter = configuration.TextFormatter;
            if (configuration.IsJson.IsSet)
            {
                IsJson = configuration.IsJson.Value;
            }

            _alerts = configuration.Alerts;
            _statuses = configuration.Statuses;
            if (configuration.StatusIconSize.IsSet)
            {
                StatusIconSize = configuration.StatusIconSize.Value;
            }

            if (configuration.AllowSet.IsSet)
            {
                CanSet = propInfo?.CanWrite == true && configuration.AllowSet.Value;
            }

            ConfigureDrillDown(
                configuration.DrillDown,
                configuration.DrillDownMaxItems,
                configuration.DrillDownIconOnly,
                configuration.DrillDownText,
                configuration.DrillDownTextFormatter
            );
            ConfigureHover(configuration.JsonHover, configuration.ExpandedHover);
            if (configuration.NoTruncate.IsSet)
            {
                NoTruncate = configuration.NoTruncate.Value;
            }
        }

        if (FormatString == null && defaultFormat != null)
        {
            FormatString = defaultFormat.Contains("{0") ? defaultFormat : "{0:" + defaultFormat + "}";
        }
        else if (FormatString == null && propAttr != null && propInfo != null)
        {
            // propInfo is null for a configured property that has no PropertyInfo behind it, which
            // this branch previously dereferenced unconditionally.
            FormatString = GetDefaultFormatString(propInfo.PropertyType);
        }

        if (defaultDrillDown)
        {
            DrillDownEnabled = true;
            DrillDownIconOnly = true;
        }
    }

    protected static Func<object, object> PropertyToFunction(PropertyInfo propInfo, bool isStatic)
    {
        if (propInfo == null)
        {
            return null;
        }

        try
        {
            // Compiled accessor rather than propInfo.GetValue, which costs about three times as
            // much per call and is on the hot path for every property of every poll.
            if (isStatic)
            {
                return obj => propInfo.GetValue(obj, null);
            }

            ParameterExpression objParam = Expression.Parameter(typeof(object), "obj");
            Type declaringType =
                propInfo.DeclaringType
                ?? throw new ArgumentException("Property must have a declaring type.", nameof(propInfo));
            UnaryExpression objToType = Expression.Convert(objParam, declaringType);
            Expression propExp = Expression.Property(objToType, propInfo);
            Expression resultToObj = Expression.Convert(propExp, typeof(object));
            return (Func<object, object>)Expression.Lambda(resultToObj, objParam).Compile();
        }
        catch (Exception ex)
        {
            string msg = string.Format(
                "Property {0}.{1}: {2}",
                propInfo.DeclaringType?.Name ?? "<unknown>",
                propInfo.Name,
                ex.Message
            );
            return _ => msg;
        }
    }

    protected Func<object, object> GetFunc { get; set; }

    protected static string MaxLengthString(string s, int maxLength)
    {
        if (s == null || s.Length <= maxLength)
        {
            return s;
        }

        return s.Substring(0, maxLength);
    }

    public virtual void GetProperties(object obj, PropertyBag bag, string catPrepend)
    {
        string value = GetValue(obj, out object objectValue);
        Property p = new Property
        {
            Name = GetName(obj),
            Description = GetDescription(obj),
            Value = MaxLengthString(GetText(obj, value), 8092),
            ValueObject = objectValue,
            CanSet = CanSet,
            Alerts = GetAlerts(obj),
            Statuses = GetStatuses(obj),
            StatusIconSize = StatusIconSize,
            IsJson = IsJson,
            SourceObject = obj,
            SourceProperty = PropInfo,
        };
        ApplyDrillDown(p, objectValue, obj);
        if (p.DrillDownIconOnly)
        {
            p.Value = null;
        }

        string prependToCategory = PrependToCategory(catPrepend, obj);
        bag.AddProperty(p, prependToCategory);
        if (
            _categoryInitiallyExpanded.IsSet
            && _categoryExpansionScope.IsSet
            && string.Equals(
                CategoryExtensions.NormalizeName(prependToCategory),
                CategoryExtensions.NormalizeName(_categoryExpansionScope.Value),
                StringComparison.OrdinalIgnoreCase
            )
        )
        {
            bag.FindOrCreateCategory(prependToCategory).IsExpanded = _categoryInitiallyExpanded.Value;
        }
    }

    protected string PrependToCategory(string prepend, object obj)
    {
        return CombineCategories(prepend, GetCategory(obj));
    }

    protected string PrependToCategory(string prepend)
    {
        return CombineCategories(prepend, Category);
    }

    protected virtual string GetName(object obj) => GetFormattedMetadata(_nameFormatter, obj, Name);

    protected virtual string GetDescription(object obj) =>
        GetFormattedMetadata(_descriptionFormatter, obj, Description);

    protected virtual string GetCategory(object obj) => GetFormattedMetadata(_categoryFormatter, obj, Category);

    private string GetText(object obj, string value)
    {
        if (_textFormatter == null)
        {
            return value;
        }

        try
        {
            return _textFormatter(obj);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }

    internal virtual bool IsDirectProperty => true;

    internal bool IsInGeneralCategory(object obj) => CategoryExtensions.NormalizeName(GetCategory(obj)) == null;

    /// <remarks>
    ///     Returns null, not an empty list, when nothing is configured. The result is assigned
    ///     straight to Property.Alerts and serialized, so an empty list would put "Alerts":[] on
    ///     every property of every poll across the estate.
    /// </remarks>
    [SuppressMessage(
        "Design",
        "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "Null omits the field from the serialized property; an empty list would not."
    )]
    protected List<PropertyAlert> GetAlerts(object obj)
    {
        if (_alerts == null || _alerts.Count == 0)
        {
            return null;
        }

        List<PropertyAlert> activeAlerts = [];
        Dictionary<string, int> alertIndexes = new(StringComparer.Ordinal);
        foreach (PropertyAlertConfiguration alert in _alerts)
        {
            try
            {
                if (alert.Condition(obj))
                {
                    string message = alert.Message(obj);
                    string category = alert.Category == null ? message : alert.Category(obj);
                    AddWorstAlert(activeAlerts, alertIndexes, new PropertyAlert(alert.Severity, message, category));
                }
            }
            catch (Exception ex)
            {
                string message = $"<{ex.Message}>";
                AddWorstAlert(activeAlerts, alertIndexes, new PropertyAlert(PropertyAlertSeverity.Error, message));
                break;
            }
        }

        return activeAlerts.Count == 0 ? null : activeAlerts;
    }

    /// <remarks>Null rather than empty, for the same serialization reason as GetAlerts.</remarks>
    [SuppressMessage(
        "Design",
        "S1168:Empty arrays and collections should be returned instead of null",
        Justification = "Null omits the field from the serialized property; an empty list would not."
    )]
    protected List<PropertyStatus> GetStatuses(object obj)
    {
        if (_statuses == null || _statuses.Count == 0)
        {
            return null;
        }

        List<PropertyStatus> activeStatuses = [];
        foreach (PropertyStatusConfiguration status in _statuses)
        {
            try
            {
                if (status.Condition(obj))
                {
                    activeStatuses.Add(new PropertyStatus(status.Status, status.Text(obj)));
                }
            }
            catch (Exception ex)
            {
                activeStatuses.Add(new PropertyStatus(StatusCode.Error, $"<{ex.Message}>"));
                break;
            }
        }

        return activeStatuses.Count == 0 ? null : activeStatuses;
    }

    private static void AddWorstAlert(List<PropertyAlert> alerts, IDictionary<string, int> indexes, PropertyAlert alert)
    {
        string category = alert.Category ?? string.Empty;
        if (indexes.TryGetValue(category, out int index))
        {
            if (alert.Severity > alerts[index].Severity)
            {
                alerts[index] = alert;
            }

            return;
        }

        indexes.Add(category, alerts.Count);
        alerts.Add(alert);
    }

    private static string GetFormattedMetadata(Func<object, string> formatter, object obj, string fallback)
    {
        if (formatter == null)
        {
            return fallback;
        }

        try
        {
            return formatter(obj);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }

    protected static string CombineCategories(string start, string end)
    {
        start = CategoryExtensions.NormalizeName(start);
        end = CategoryExtensions.NormalizeName(end);
        if (string.IsNullOrEmpty(start))
        {
            return end;
        }

        if (string.IsNullOrEmpty(end))
        {
            return start;
        }

        return start + "." + end;
    }

    private static string GetDefaultFormatString(Type type)
    {
        if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Nullable<>))
        {
            type = type.GetGenericArguments()[0];
        }

        if (type == typeof(float))
        {
            return "{0:N2}";
        }

        if (type == typeof(double))
        {
            return "{0:N2}";
        }

        if (type == typeof(decimal))
        {
            return "{0:N2}";
        }

        if (type == typeof(DateTime) || type == typeof(DateTime?))
        {
            return "{0:d MMM yyyy H:mm:ss}";
        }

        return null;
    }

    public PropertyInfo PropInfo { get; private set; }
    public string Name { get; protected set; }
    public string Description { get; protected set; }
    protected string FormatString { get; set; }
    public bool CanSet { get; private set; }
    public string Category { get; protected set; }

    /// <summary>Renders a bounded preview of a sequence.</summary>
    /// <remarks>
    ///     Deliberately does NOT call Count() and then Take() on the same sequence, which is what
    ///     upstream still does: that enumerates twice, is quadratic on a lazy source and never
    ///     returns on a streaming or unbounded one. A known count is used when the sequence can
    ///     supply one cheaply; otherwise one extra item is taken to detect an overflow without
    ///     draining the rest.
    /// </remarks>
    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "Bounded enumeration handles counted and streaming collections in one pass."
    )]
    protected string FormatEnumerable(IEnumerable col, string separator, int maxItems, bool includeCount = true)
    {
        if (maxItems <= 0)
        {
            maxItems = MaxConcatItems;
        }
        // ReSharper disable PossibleMultipleEnumeration
        // TryGetCount reads a Count and never enumerates, and the two Cast calls below sit in mutually exclusive branches.
        int count = TryGetCount(col);
        List<object> asObject;
        int remaining;
        int displayCount;
        if (count != -1)
        {
            asObject = [.. col.Cast<object>().Take(maxItems)];
            remaining = count - asObject.Count;
            displayCount = count;
        }
        else
        {
            asObject = [.. col.Cast<object>().Take(maxItems + 1)];
            // ReSharper restore PossibleMultipleEnumeration
            if (asObject.Count > maxItems)
            {
                remaining = 1;
                asObject.RemoveAt(maxItems);
                displayCount = maxItems;
            }
            else
            {
                remaining = 0;
                displayCount = asObject.Count;
            }
        }
        if (displayCount == 0)
        {
            return "0 items";
        }
        List<string> values = [];
        foreach (object o in asObject)
        {
            values.Add(FormatValue(o));
        }
        if (remaining > 0)
        {
            string remainingSuffix = remaining == 1 ? "" : "s";
            values.Add(
                count != -1 ? string.Format("... ({0} more item{1})", remaining, remainingSuffix) : "... (more items)"
            );
        }
        string formattedValues = string.Join(separator, [.. values]);
        if (!includeCount)
        {
            return formattedValues;
        }
        string countSuffix = count == 1 ? "" : "s";
        string countText = count != -1 ? string.Format("{0} item{1}", count, countSuffix) : "Many items";
        return countText + ": " + formattedValues;
    }

    /// <summary>A count if the sequence can give one without enumerating it, otherwise -1.</summary>
    private static int TryGetCount(IEnumerable col)
    {
        if (col is ICollection c)
        {
            return c.Count;
        }
        PropertyInfo countProp = col.GetType().GetProperty("Count", BindingFlags.Public | BindingFlags.Instance);
        if (countProp != null && countProp.PropertyType == typeof(int) && countProp.GetValue(col) is int propertyCount)
        {
            return propertyCount;
        }
        return -1;
    }

    public string GetValue(object obj, out object objectValue)
    {
        return GetValue(obj, GetFunc, out objectValue);
    }

    public string GetValue(object obj, Func<object, object> propInfo, out object propertyValue)
    {
        try
        {
            propertyValue = propInfo(obj);
            if (propertyValue == null)
            {
                return null;
            }

            return FormatValue(propertyValue);
        }
        catch (Exception ex)
        {
            propertyValue = null;
            return string.Format("<{0}>", ex.Message);
        }
    }

    protected void ConfigureCustomProperty(CustomPropertyConfiguration configuration)
    {
        Name = configuration.Name;
        Category = configuration.Category.IsSet ? configuration.Category.Value : null;
        Description = configuration.Description.IsSet ? configuration.Description.Value : null;
        _categoryInitiallyExpanded = configuration.CategoryInitiallyExpanded;
        _categoryExpansionScope = configuration.CategoryExpansionScope;
        _valueFormatter = configuration.ValueFormatter;
        _alerts = configuration.Alerts;
        _statuses = configuration.Statuses;
        if (configuration.IsJson.IsSet)
        {
            IsJson = configuration.IsJson.Value;
        }

        ConfigureDrillDown(
            configuration.DrillDown,
            configuration.DrillDownMaxItems,
            configuration.DrillDownIconOnly,
            configuration.DrillDownText,
            configuration.DrillDownTextFormatter
        );
        ConfigureHover(configuration.JsonHover, configuration.ExpandedHover);
    }

    protected void ApplyDrillDown(Property property, object value, object owner)
    {
        bool canDrillDown = DiagnosticManager.IsDrillDownValue(value);
        if (!canDrillDown && (!JsonHoverEnabled || value == null))
        {
            return;
        }

        property.CanDrillDown = DrillDownEnabled && canDrillDown;
        property.DrillDownIconOnly = property.CanDrillDown && DrillDownIconOnly;
        property.DrillDownText = property.CanDrillDown ? GetDrillDownText(owner) : null;
        property.CanJsonHover = JsonHoverEnabled && value != null;
        property.CanExpandedHover = ExpandedHoverEnabled && canDrillDown;
        property.DrillDownObject = value;
        property.DrillDownMaxItems = DrillDownMaxItems;
    }

    private void ConfigureDrillDown(
        ConfiguredValue<bool> enabled,
        ConfiguredValue<int> maxItems,
        ConfiguredValue<bool> iconOnly,
        ConfiguredValue<string> text,
        Func<object, string> textFormatter = null
    )
    {
        DrillDownEnabled = enabled.IsSet && enabled.Value;
        DrillDownMaxItems = maxItems.IsSet ? maxItems.Value : DiagnosticManager.DrillDownMaxItems;
        DrillDownIconOnly = iconOnly.IsSet && iconOnly.Value;
        DrillDownText = text.IsSet ? text.Value : null;
        _drillDownTextFormatter = textFormatter;
    }

    private string GetDrillDownText(object owner)
    {
        if (_drillDownTextFormatter == null)
        {
            return DrillDownText;
        }

        try
        {
            return _drillDownTextFormatter(owner);
        }
        catch (Exception ex)
        {
            return $"<{ex.Message}>";
        }
    }

    private void ConfigureHover(ConfiguredValue<bool> jsonHover, ConfiguredValue<bool> expandedHover)
    {
        JsonHoverEnabled = jsonHover.IsSet && jsonHover.Value;
        ExpandedHoverEnabled = expandedHover.IsSet && expandedHover.Value;
    }

    protected string FormatValue(object val)
    {
        if (val == null)
        {
            return null;
        }

        if (_valueFormatter != null)
        {
            return _valueFormatter(val);
        }

        if (val is TimeSpan timeSpan)
        {
            return FormatTimeSpan(timeSpan);
        }

        if (val is string text)
        {
            return text;
        }

        if (val is IEnumerable enumerable)
        {
            return FormatEnumerable(enumerable, Environment.NewLine, MaxConcatItems);
        }

        if (FormatString != null)
        {
            return string.Format(FormatString, val);
        }

        return val.ToString();
    }

    protected static string FormatTimeSpan(TimeSpan span)
    {
        // Built by interpolation rather than a switched format string. Upstream's version selects
        // between two format strings over one six-argument string.Format call, so the days argument
        // is silently unused whenever the span is under a day - which is what a supplied-but-ignored
        // format argument looks like to an analyzer, and one renumbering away from being a real bug.
        string sign = span < TimeSpan.Zero ? "-" : "";
        string value = $"{Math.Abs(span.Hours):D2}:{Math.Abs(span.Minutes):D2}:{Math.Abs(span.Seconds):D2}";
        if (span.Days != 0)
        {
            value = $"{Math.Abs(span.Days)}.{value}";
        }

        if (Math.Abs(span.TotalSeconds) < 1)
        {
            value += $".{Math.Abs(span.Milliseconds):D2}";
        }

        return sign + value;
    }
}
