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
///     The former name of <see cref="DiagnosticPropertyAttribute" />, kept so existing call sites
///     keep compiling. Prefer <see cref="DiagnosticPropertyAttribute" /> in new code.
/// </summary>
/// <remarks>
///     This is a subclass rather than a type alias because attribute discovery runs through
///     <c>GetCustomAttributes(typeof(DiagnosticPropertyAttribute), false)</c>, which matches any
///     assignable attribute type. A property marked <c>[Property]</c> is therefore found by the same
///     query as one marked <c>[DiagnosticProperty]</c>, with no branch anywhere in the pipeline. The
///     consuming estate has upwards of 300 such call sites; the alternative was a mechanical rename
///     across all of them for no behavioural gain.
/// </remarks>
[AttributeUsage(AttributeTargets.Property)]
public class PropertyAttribute : DiagnosticPropertyAttribute
{
    public PropertyAttribute() { }

    public PropertyAttribute(string name)
        : base(name) { }

    public PropertyAttribute(string name, string category)
        : base(name, category) { }

    public PropertyAttribute(string name, string category, string description)
        : base(name, category, description) { }
}
