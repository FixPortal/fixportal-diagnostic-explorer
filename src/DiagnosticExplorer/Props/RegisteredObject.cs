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

public class RegisteredObject
{
    private readonly WeakReference _objectRef;
    private readonly object _strongRef;

    /// <summary>
    ///     Registers a host object, held weakly: registering must not be what keeps it alive.
    /// </summary>
    public RegisteredObject(object obj, string bagCategory, string bagName)
    {
        _objectRef = new WeakReference(obj);
        BagName = bagName;
        BagCategory = bagCategory;
    }

    private RegisteredObject(string bagCategory, string bagName, object strongRef)
    {
        _strongRef = strongRef;
        BagName = bagName;
        BagCategory = bagCategory;
    }

    /// <summary>
    ///     Wraps an object this library derived, held strongly for as long as the wrapper lives.
    /// </summary>
    /// <remarks>
    ///     A drilldown materialises objects that exist only to be rendered — a wrapper around a
    ///     scalar item, say. Nothing else references them, so held weakly they can be collected
    ///     between being registered and being rendered, and the item then silently does not appear.
    ///     Weak is right for a host's own objects and wrong for ours.
    /// </remarks>
    internal static RegisteredObject Derived(object obj, string bagCategory, string bagName)
    {
        return new RegisteredObject(bagCategory, bagName, obj);
    }

    public string BagName { get; set; }
    public string BagCategory { get; set; }

    public object Object => _strongRef ?? _objectRef.Target;
}

public static class RegisteredObjectExtensions
{
    private static readonly StringComparer _ignoreCase = StringComparer.CurrentCultureIgnoreCase;

    public static RegisteredObject FindByCategoryAndName(
        this IEnumerable<RegisteredObject> list,
        string category,
        string name
    )
    {
        Guard.NotNull(list, nameof(list));

        return list.FirstOrDefault(x =>
            _ignoreCase.Equals(x.BagCategory, category) && _ignoreCase.Equals(x.BagName, name)
        );
    }
}
