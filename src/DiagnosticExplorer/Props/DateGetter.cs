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

using System.Diagnostics.CodeAnalysis;
using System.Reflection;

namespace DiagnosticExplorer;

internal class DateGetter : PropertyGetter
{
    private readonly bool _exposeDate = true;
    private readonly bool _exposeElapsed;
    private readonly bool _exposeTimeUntil;
    private readonly bool _isUtc;

    public DateGetter(PropertyInfo prop, DatePropertyAttribute attr, bool isStatic)
        : this(prop, attr, attr, null, isStatic) { }

    internal DateGetter(
        PropertyInfo prop,
        DatePropertyAttribute attr,
        DiagnosticPropertyAttribute metadata,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes = true,
        string defaultFormat = null
    )
        : base(prop, metadata, configuration, isStatic, applyAttributes, defaultFormat)
    {
        if (attr != null)
        {
            _exposeDate = attr.ExposeDate;
            _exposeElapsed = attr.ExposeElapsed;
            _exposeTimeUntil = attr.ExposeTimeUntil;
            _isUtc = attr.IsUTC;
        }
    }

    [SuppressMessage(
        "Maintainability",
        "S3776:Cognitive Complexity of methods should not be too high",
        Justification = "The branches are the independent date, elapsed and time-until presentation options."
    )]
    public override void GetProperties(object obj, PropertyBag bag, string catPrepend)
    {
        if (_exposeDate)
        {
            base.GetProperties(obj, bag, catPrepend);
        }

        if (!_exposeElapsed && !_exposeTimeUntil)
        {
            return;
        }

        DateTime? dateVal;
        try
        {
            var value = GetFunc(obj);
            // Normalised to UTC, with the elapsed arithmetic below running against UtcNow. Done in
            // local time, "time since" jumps by an hour at each daylight-saving transition, twice a
            // year, for every date property in the estate. Only the arithmetic is affected: the
            // displayed date itself goes through base.GetProperties above and is untouched.
            dateVal = value is DateTimeOffset off ? off.UtcDateTime : (DateTime?)value;
            if (dateVal != null)
            {
                dateVal = dateVal.Value.Kind switch
                {
                    // An Unspecified value means UTC only when the property says so; otherwise it
                    // is a local wall-clock reading and has to be converted, or the elapsed value
                    // comes out wrong by the UTC offset.
                    DateTimeKind.Unspecified when _isUtc => DateTime.SpecifyKind(dateVal.Value, DateTimeKind.Utc),
                    DateTimeKind.Unspecified => DateTime
                        .SpecifyKind(dateVal.Value, DateTimeKind.Local)
                        .ToUniversalTime(),
                    DateTimeKind.Local => dateVal.Value.ToUniversalTime(),
                    _ => dateVal.Value,
                };
            }
        }
        catch (Exception ex)
        {
            // A throwing date property must degrade to an error string rather than abort the
            // whole diagnostic walk; this raw getter call bypassed the guarded GetValue path.
            var error = $"<{ex.Message}>";
            if (_exposeElapsed)
            {
                bag.AddProperty(new Property("Time since " + GetName(obj), error), PrependToCategory(catPrepend, obj));
            }

            if (_exposeTimeUntil)
            {
                bag.AddProperty(new Property("Time until " + GetName(obj), error), PrependToCategory(catPrepend, obj));
            }

            return;
        }

        if (_exposeElapsed)
        {
            var val = dateVal == null ? "" : FormatTimeSpan(DateTime.UtcNow.Subtract(dateVal.Value));
            var property = new Property("Time since " + GetName(obj), val);
            bag.AddProperty(property, PrependToCategory(catPrepend, obj));
        }

        if (_exposeTimeUntil)
        {
            var val = dateVal == null ? "" : FormatTimeSpan(dateVal.Value.Subtract(DateTime.UtcNow));
            var property = new Property("Time until " + GetName(obj), val);
            bag.AddProperty(property, PrependToCategory(catPrepend, obj));
        }
    }
}
