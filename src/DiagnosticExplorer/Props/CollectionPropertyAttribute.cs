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


namespace DiagnosticExplorer;

public enum CollectionMode
{
    /// <summary>The count of a collection property is exposed</summary>
    Count,

    /// <summary>The items in a collection property are concatenated together</summary>
    Concatenate,

    /// <summary>The items in a collection property are listed individually</summary>
    List,

    /// <summary>Each item in a collection property is exposed in its own category</summary>
    Categories,

    /// <summary>The collection is exposed as an expanded category containing one category per item</summary>
    ExpandedItems,
}

/// <summary>
///     Exposes a collection property, with <see cref="Mode" /> selecting how.
/// </summary>
/// <remarks>
///     Upstream made this class abstract and split the modes into four sealed attributes -
///     <see cref="CollectionCountAttribute" /> and friends, which are also available here. This form
///     is kept concrete so existing call sites keep compiling; the estate has some 90 files using
///     it, and migrating them is a decision to take on its own rather than as a side effect of a
///     getter port. Same trade as <see cref="PropertyAttribute" />: a compatibility surface bought
///     for decoupled releases, removable whenever the migration is actually done.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class CollectionPropertyAttribute : DiagnosticPropertyAttribute
{
    protected CollectionPropertyAttribute()
    {
        MaxItems = PropertyGetter.MaxConcatItems;
    }

    protected CollectionPropertyAttribute(string name, string category, string description)
        : base(name, category, description)
    {
        MaxItems = PropertyGetter.MaxConcatItems;
    }

    public CollectionPropertyAttribute(CollectionMode mode)
        : this(mode, null) { }

    public CollectionPropertyAttribute(CollectionMode mode, string displayName)
        : this(mode, displayName, null) { }

    public CollectionPropertyAttribute(CollectionMode mode, string displayName, string category)
        : base(displayName, category)
    {
        Mode = mode;
        MaxItems = PropertyGetter.MaxConcatItems;
    }

    public CollectionMode Mode { get; set; }
    public string NameProperty { get; set; }
    public string ValueProperty { get; set; }
    public string DescriptionProperty { get; set; }
    public string CategoryProperty { get; set; }
    public string Separator { get; set; }
    public int MaxItems { get; set; }

    /// <summary>Projects the attribute onto the options the getter actually reads.</summary>
    internal virtual CollectionOptions CreateOptions()
    {
        return new CollectionOptions(Mode)
        {
            NameProperty = NameProperty,
            ValueProperty = ValueProperty,
            DescriptionProperty = DescriptionProperty,
            CategoryProperty = CategoryProperty,
            Separator = Separator,
            MaxItems = MaxItems == 0 ? PropertyGetter.MaxConcatItems : MaxItems,
        };
    }
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CollectionCountAttribute : CollectionPropertyAttribute
{
    public CollectionCountAttribute() { }

    public CollectionCountAttribute(string name, string category = null, string description = null)
        : base(name, category, description) { }

    internal override CollectionOptions CreateOptions() => new(CollectionMode.Count);
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CollectionConcatenateAttribute : CollectionPropertyAttribute
{
    public CollectionConcatenateAttribute() { }

    public CollectionConcatenateAttribute(string name, string category = null, string description = null)
        : base(name, category, description) { }

    internal override CollectionOptions CreateOptions() =>
        new(CollectionMode.Concatenate)
        {
            ValueProperty = ValueProperty,
            Separator = Separator,
            MaxItems = MaxItems,
        };
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CollectionListAttribute : CollectionPropertyAttribute
{
    public CollectionListAttribute() { }

    public CollectionListAttribute(string name, string category = null, string description = null)
        : base(name, category, description) { }

    internal override CollectionOptions CreateOptions() =>
        new(CollectionMode.List)
        {
            NameProperty = NameProperty,
            ValueProperty = ValueProperty,
            DescriptionProperty = DescriptionProperty,
            CategoryProperty = CategoryProperty,
            MaxItems = MaxItems,
        };
}

[AttributeUsage(AttributeTargets.Property)]
public sealed class CollectionCategoriesAttribute : CollectionPropertyAttribute
{
    public CollectionCategoriesAttribute() { }

    public CollectionCategoriesAttribute(string name, string category = null, string description = null)
        : base(name, category, description) { }

    internal override CollectionOptions CreateOptions() =>
        new(CollectionMode.Categories) { CategoryProperty = CategoryProperty, MaxItems = MaxItems };
}

/// <summary>
///     The resolved collection settings the getter actually reads, produced either by an attribute
///     or by the fluent configuration. Keeping the getter on this rather than on the attribute is
///     what lets the two configuration routes share one code path.
/// </summary>
internal sealed class CollectionOptions
{
    public CollectionOptions(CollectionMode mode)
    {
        Mode = mode;
        MaxItems = PropertyGetter.MaxConcatItems;
    }

    public CollectionMode Mode { get; set; }
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
    public string Separator { get; set; }
    public int MaxItems { get; set; }
    public bool InitiallyExpanded { get; set; } = true;
    public bool PrimaryPropertiesOnly { get; set; }
    public List<PropertyStatusConfiguration> ItemStatuses { get; set; } = [];
    public ConfiguredValue<StatusIconSize> ItemStatusIconSize { get; set; }
    public int ItemWidth { get; set; }
}
