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

using System.Runtime.Serialization;

namespace DiagnosticExplorer;

/// <summary>
///     Describes something that happened.
/// </summary>
[DataContract(Namespace = "http://diagnosticexplorer.com/2010")]
public class SystemEvent
{
    public SystemEvent()
        : this(TimeProvider.System) { }

    public SystemEvent(TimeProvider timeProvider)
    {
        Date = (timeProvider ?? throw new ArgumentNullException(nameof(timeProvider))).GetUtcNow().UtcDateTime;
    }

    [DataMember]
    public long Id { get; set; }

    [DataMember]
    public DateTime Date { get; set; }

    [DataMember]
    public string Message { get; set; }

    [DataMember]
    public string Detail { get; set; }

    // ProtoMember(5) was EventSeverity Severity — removed. The server never populated it
    // (write-dead) and the web no longer reads it; the tag is left as a gap so the remaining
    // members keep their wire numbers. The EventSeverity enum stays (SystemEventArgs uses it).

    [DataMember]
    public int Level { get; set; }

    [DataMember]
    public string SinkName { get; set; }

    [DataMember]
    public string SinkCategory { get; set; }

    public override string ToString()
    {
        return $"{Id} {Date:d MMM yyyy H:mm:ss} {Message}";
    }
}
