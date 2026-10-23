#nullable enable annotations

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


namespace DiagnosticExplorer.Util;

public class WeakReferenceHash<T>
    where T : class
{
    private readonly IDictionary<string, WeakReference> items = new SortedDictionary<string, WeakReference>(
        StringComparer.CurrentCultureIgnoreCase
    );

    public void Add(string name, T obj)
    {
        Guard.NotNull(name, nameof(name));
        Guard.NotNull(obj, nameof(obj));

        // Check-then-act under the same lock: SortedDictionary is not safe for a read racing a write,
        // and the duplicate check must be atomic with the insert. (B3)
        lock (items)
        {
            if (items.ContainsKey(name))
            {
                throw new ArgumentException(string.Format("There is already a {0} named '{1}'", typeof(T).Name, name));
            }

            items.Add(name, new WeakReference(obj));
        }
    }

    public bool ContainsName(string name)
    {
        // SortedDictionary reads must be locked against concurrent mutation in Add/Remove/GetItem. (B3)
        lock (items)
        {
            return items.ContainsKey(name);
        }
    }

    public void Remove(string name)
    {
        Guard.NotNull(name, nameof(name));

        lock (items)
        {
            items.Remove(name);
        }
    }

    public T? GetItem(string name, Func<T>? create = null)
    {
        lock (items)
        {
            T? target = null;
            if (items.TryGetValue(name, out var r))
            {
                target = r.Target as T;
                if (target == null)
                {
                    items.Remove(name);
                }
            }

            if (target == null && create != null)
            {
                target = create();
                items.Add(name, new WeakReference(target));
            }

            return target;
        }
    }

    public List<T> GetItems()
    {
        lock (items)
        {
            List<T> toList = new(items.Count);
            foreach (var pair in items.ToArray())
            {
                if (pair.Value.Target is not T target)
                {
                    items.Remove(pair.Key);
                }
                else
                {
                    toList.Add(target);
                }
            }

            return toList;
        }
    }
}
