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

using System.Reflection;

namespace DiagnosticExplorer;

internal class ExtendedPropertyGetter : PropertyGetter
{
    private readonly string _name;

    public ExtendedPropertyGetter(PropertyInfo info, ExtendedPropertyAttribute attr, bool isStatic)
        : this(info, attr, attr, null, isStatic) { }

    /// <summary>
    ///     False: an extended property renders its target's properties rather than a value of its
    ///     own, which is what <see cref="NestedPropertyRenderMode.PrimaryOnly" /> filters on.
    /// </summary>
    internal override bool IsDirectProperty => false;

    internal ExtendedPropertyGetter(
        PropertyInfo info,
        ExtendedPropertyAttribute attr,
        DiagnosticPropertyAttribute metadata,
        PropertyConfiguration configuration,
        bool isStatic,
        bool applyAttributes = true,
        string defaultFormat = null
    )
        : base(info, metadata, configuration, isStatic, applyAttributes, defaultFormat)
    {
        _name = attr.Name ?? info.Name;
    }

    public override void GetProperties(object obj, PropertyBag bag, string catPrepend)
    {
        var newPrepend = CombineCategories(catPrepend, _name);

        object val;
        try
        {
            val = GetFunc(obj);
        }
        catch (Exception ex)
        {
            var p = new Property
            {
                Name = "Error",
                Value = $"<{ex.InnerException?.Message ?? ex.Message}>",
                CanSet = false,
                SourceObject = obj,
                SourceProperty = PropInfo,
            };
            var prependToCategory = PrependToCategory(newPrepend);
            bag.AddProperty(p, prependToCategory);
            return;
        }

        if (val == null)
        {
            var p = new Property
            {
                Name = "null",
                CanSet = CanSet,
                SourceObject = obj,
                SourceProperty = PropInfo,
            };

            var prependToCategory = PrependToCategory(newPrepend);
            bag.AddProperty(p, prependToCategory);
        }
        else
        {
            var visited = DiagnosticManager.VisitedObjects;
            if (visited.Contains(val))
            {
                var p = new Property
                {
                    Name = "<cycle>",
                    CanSet = false,
                    SourceObject = obj,
                    SourceProperty = PropInfo,
                };
                var prependToCategory = PrependToCategory(newPrepend);
                bag.AddProperty(p, prependToCategory);
                return;
            }

            if (visited.Count > 50)
            {
                var p = new Property
                {
                    Name = "<max depth>",
                    CanSet = false,
                    SourceObject = obj,
                    SourceProperty = PropInfo,
                };
                var prependToCategory = PrependToCategory(newPrepend);
                bag.AddProperty(p, prependToCategory);
                return;
            }

            visited.Add(val);
            try
            {
                var getters = DiagnosticManager.GetPropertyGetters(val);
                foreach (var getter in getters)
                {
                    getter.GetProperties(val, bag, newPrepend);
                }

                if (bag.Categories.FindByName(newPrepend) is { } cat)
                {
                    cat.ValueObject = val;
                }
            }
            finally
            {
                visited.Remove(val);
            }
        }
    }
}
