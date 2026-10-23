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

/// <summary>
///     Marks a property for exposure through Diagnostic Explorer, and carries how it is presented.
/// </summary>
/// <remarks>
///     Named <c>PropertyAttribute</c> until 3.2.3. The old name survives as a subclass so existing
///     call sites keep compiling - see <see cref="PropertyAttribute" />. Discovery goes through
///     <c>GetCustomAttributes(typeof(DiagnosticPropertyAttribute), false)</c>, which matches any
///     assignable attribute, so both names and every specialised subclass are found by one query.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class DiagnosticPropertyAttribute : Attribute
{
    private bool _allowSet;

    public DiagnosticPropertyAttribute() { }

    public DiagnosticPropertyAttribute(string name)
        : this(name, null) { }

    public DiagnosticPropertyAttribute(string name, string category)
        : this(name, category, null) { }

    public DiagnosticPropertyAttribute(string name, string category, string description)
    {
        Ignore = false;
        Name = name;
        Category = category;
        Description = description;
    }

    public bool Ignore { get; set; }

    public string Name { get; set; }

    public string FormatString { get; set; }

    public string Category { get; set; }

    public string Description { get; set; }

    public bool AllowSet
    {
        get => _allowSet;
        set
        {
            _allowSet = value;
            AllowSetSpecified = true;
        }
    }

    internal bool AllowSetSpecified { get; private set; }
}
