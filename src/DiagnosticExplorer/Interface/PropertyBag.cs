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

using System.Collections.Generic;

namespace DiagnosticExplorer;

public class PropertyBag
{
    public PropertyBag()
    {
        Categories = [];
    }

    public PropertyBag(string name)
        : this()
    {
        Name = name;
    }

    public PropertyBag(string name, string category)
        : this(name)
    {
        Category = category;
    }

    public string Name { get; set; }

    public string Category { get; set; }

    public string OperationSet { get; set; }

    /// <summary>
    ///     Whether the registered object itself can be opened in a drilldown.
    /// </summary>
    /// <remarks>
    ///     True only where the host configured a drilldown for the object's type. A bag rendered
    ///     INSIDE a drilldown reports false: the operator is already looking at it, and offering to
    ///     open it again would nest a popup on itself.
    /// </remarks>
    public bool CanDrillDown { get; set; }

    public List<Category> Categories { get; set; }

    /// <summary>
    ///     The live registered object this bag was built from. Never on the wire.
    /// </summary>
    /// <remarks>
    ///     Internal, matching <see cref="Property.SourceObject" /> and
    ///     <see cref="Category.ValueObject" />. It was public until 4.0.0, which was harmless only
    ///     because protobuf's UseProtoMembersOnly contract was an allowlist and silently skipped
    ///     it. Contractless MessagePack has no allowlist, so a public member here puts the host's
    ///     own object graph on the agent channel: a host with an internal type, an interface-typed
    ///     property, a non-public constructor or a self-reference then fails to serialise, or
    ///     worse, ships.
    /// </remarks>
    internal object SourceObject { get; set; }

    public void AddProperty(Property property, string category)
    {
        Guard.NotNull(property, nameof(property));

        var cat = FindOrCreateCategory(category);
        cat.Properties.Add(property);
    }

    public Category FindOrCreateCategory(string category)
    {
        var cat = Categories.FindByName(category);
        if (cat == null)
        {
            // Store the same normalized form FindByName compares against ("General" and blank
            // both canonicalise to the unnamed default category) -- storing the raw name here
            // made a stored "General" unfindable by its own name and duplicated on every call.
            cat = new Category(CategoryExtensions.NormalizeName(category));
            Categories.Add(cat);
        }

        return cat;
    }

    public Property GetProperty(string name, string category = null)
    {
        var cat = Categories.FindByName(category);
        return cat?.Properties.FindByName(name);
    }
}
