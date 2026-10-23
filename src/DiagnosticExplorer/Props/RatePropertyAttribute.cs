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

[AttributeUsage(AttributeTargets.Property)]
public class RatePropertyAttribute : DiagnosticPropertyAttribute
{
    public RatePropertyAttribute()
        : this(null) { }

    public RatePropertyAttribute(string name)
        : this(name, null) { }

    public RatePropertyAttribute(string name, string category)
        : base(name, category) { }

    public bool ExposeRate { get; set; } = true;

    public bool ExposeTotal { get; set; } = true;
}
