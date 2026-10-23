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

using System.Globalization;
using System.Reflection;
using System.Text.Json.Serialization;

namespace DiagnosticExplorer;

/// <summary>How prominently a property alert is rendered.</summary>
public enum PropertyAlertSeverity
{
    None = 0,
    Warning = 1,
    Error = 2,
}

/// <summary>
///     The status vocabulary a configurator can attach to a property. Values are explicit and must
///     stay stable: they cross the wire as integers and the web client maps them to icons.
/// </summary>
public enum StatusCode
{
    Active = 1,
    Inactive = 2,
    Pending = 3,
    Success = 4,
    Warning = 5,
    Error = 6,
    Alert = 7,
    Danger = 8,
    Running = 9,
    Stopped = 10,
    Disabled = 11,
    Paused = 12,
}

/// <summary>How large the client draws a property's status icon.</summary>
public enum StatusIconSize
{
    Small = 0,
    Medium = 1,
    Large = 2,
}

/// <summary>What kind of value a property holds, so the client can render it appropriately.</summary>
public enum PropertyValueKind
{
    Unspecified = 0,
    Null = 1,
    Text = 2,
    Boolean = 3,
    Number = 4,
    PositiveNumber = 5,
    ZeroNumber = 6,
    NegativeNumber = 7,
    DateTime = 8,
    Duration = 9,
    Enumeration = 10,
    Object = 11,
}

public class PropertyAlert
{
    public PropertyAlert() { }

    public PropertyAlert(PropertyAlertSeverity severity, string message)
        : this(severity, message, message) { }

    public PropertyAlert(PropertyAlertSeverity severity, string message, string category)
    {
        Severity = severity;
        Message = message;
        Category = category ?? message;
    }

    public PropertyAlertSeverity Severity { get; set; }

    public string Message { get; set; }

    public string Category { get; set; }
}

public class PropertyStatus
{
    public PropertyStatus() { }

    public PropertyStatus(StatusCode status, string text)
    {
        Status = status;
        Text = text ?? status.ToString();
    }

    public StatusCode Status { get; set; }

    public string Text { get; set; }
}

public class Property
{
    public Property() { }

    public Property(string name)
        : this(name, null, null) { }

    public Property(string name, string value)
        : this(name, value, null) { }

    public Property(string name, string value, string description)
    {
        Name = name;
        Value = value;
        Description = description;
    }

    public string Name { get; set; }

    public string Value { get; set; }

    public string Description { get; set; }

    public string OperationSet { get; set; }

    public bool CanSet { get; set; }

    // Members 6 onward are upstream's richer presentation model.
    public List<PropertyAlert> Alerts { get; set; }

    public bool CanDrillDown { get; set; }

    public bool DrillDownIconOnly { get; set; }

    public PropertyValueKind ValueKind { get; set; }

    public bool CanJsonHover { get; set; }

    public bool CanExpandedHover { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool IsJson { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int Width { get; set; }

    public string DrillDownText { get; set; }

    public bool NoTruncate { get; set; }

    public List<PropertyStatus> Statuses { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public StatusIconSize StatusIconSize { get; set; }

    internal object SourceObject { get; set; }

    internal object ValueObject { get; set; }

    internal object DrillDownObject { get; set; }
    internal int DrillDownMaxItems { get; set; }
    internal PropertyInfo SourceProperty { get; set; }

    /// <summary>
    ///     Classifies the value so the client can render it without reparsing the formatted string.
    ///     Only fills an unspecified kind, so an explicit configuration always wins.
    /// </summary>
    internal void InferValueKind()
    {
        if (ValueKind != PropertyValueKind.Unspecified)
        {
            return;
        }
        if (ValueObject == null)
        {
            ValueKind = Value == null ? PropertyValueKind.Null : PropertyValueKind.Text;
            return;
        }
        if (ValueObject is string or char or Guid or Uri)
        {
            ValueKind = PropertyValueKind.Text;
            return;
        }
        if (ValueObject is bool)
        {
            ValueKind = PropertyValueKind.Boolean;
            return;
        }
        if (ValueObject is DateTime or DateTimeOffset)
        {
            ValueKind = PropertyValueKind.DateTime;
            return;
        }
        if (ValueObject is TimeSpan)
        {
            ValueKind = PropertyValueKind.Duration;
            return;
        }
        Type valueType = ValueObject.GetType();
        if (valueType.IsEnum)
        {
            ValueKind = PropertyValueKind.Enumeration;
            return;
        }
        ValueKind = IsNumeric(valueType) ? GetNumericValueKind(ValueObject) : PropertyValueKind.Object;
    }

    private static bool IsNumeric(Type valueType)
    {
        return Type.GetTypeCode(valueType) switch
        {
            TypeCode.Byte
            or TypeCode.SByte
            or TypeCode.Int16
            or TypeCode.UInt16
            or TypeCode.Int32
            or TypeCode.UInt32
            or TypeCode.Int64
            or TypeCode.UInt64
            or TypeCode.Single
            or TypeCode.Double
            or TypeCode.Decimal => true,
            _ => false,
        };
    }

    private static PropertyValueKind GetNumericValueKind(object value)
    {
        try
        {
            decimal number = Convert.ToDecimal(value, CultureInfo.InvariantCulture);
            if (number > 0)
            {
                return PropertyValueKind.PositiveNumber;
            }
            return number < 0 ? PropertyValueKind.NegativeNumber : PropertyValueKind.ZeroNumber;
        }
        catch (OverflowException)
        {
            // A value outside decimal's range is still a number; it just cannot be signed here.
            return PropertyValueKind.Number;
        }
    }

    public override string ToString()
    {
        var descr = string.IsNullOrEmpty(Description) ? "" : string.Format(" ({0})", Description);

        var opset = OperationSet == null ? "" : string.Format(" (OperationSet={0})", OperationSet);

        var settable = CanSet ? " (SET)" : "";

        return $"{Name} = [{Value}]{settable}{descr}{opset}";
    }
}

public static class PropertyExtensions
{
    private static readonly StringComparer _ignoreCase = StringComparer.CurrentCultureIgnoreCase;

    public static Property FindByName(this IEnumerable<Property> list, string name)
    {
        Guard.NotNull(list, nameof(list));

        return list.FirstOrDefault(x => _ignoreCase.Equals(x.Name, name));
    }
}
