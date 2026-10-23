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

public interface ITraceItem
{
    string Message { get; set; }
}

internal class TraceItem<TScope> : ITraceItem
{
    private string _message;
    private readonly TimeProvider _timeProvider;

    public TraceItem(string message, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        Message = message;
    }

    public TraceItem(TScope traceScope, TimeProvider timeProvider)
    {
        _timeProvider = timeProvider;
        TraceScope = traceScope;
        Created = UtcNow;
    }

    public DateTime Created { get; private set; }

    public TScope TraceScope { get; set; }

    public string Message
    {
        get => _message;
        set
        {
            _message = value;
            Created = UtcNow;
        }
    }

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;
}
