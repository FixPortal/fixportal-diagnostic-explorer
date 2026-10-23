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

using log4net.Appender;
using log4net.Core;

namespace DiagnosticExplorer.Log4Net;

public class DiagnosticAppender : AppenderSkeleton
{
    private const int MaxMsgLength = 150;
    private const string appenderKey = "DiagnosticAppenderHandled";
    private EventSink? _sink;

    public DiagnosticAppender()
    {
        SinkName = "System";
        SinkCategory = "System";
        ExcludeAlreadyLogged = true;
    }

    public bool ExcludeAlreadyLogged { get; set; }

    public string SinkName { get; set; }

    public string SinkCategory { get; set; }

    protected override void Append(LoggingEvent loggingEvent)
    {
        var sink = _sink ??= EventSinkRepo.Default.GetSink(SinkName, SinkCategory);

        if (ExcludeAlreadyLogged)
        {
            if (loggingEvent.Properties.Contains(appenderKey))
            {
                return;
            }

            loggingEvent.Properties[appenderKey] = true;
        }

        var detail = RenderLoggingEvent(loggingEvent);
        if (!ReferenceEquals(loggingEvent.MessageObject, loggingEvent.ExceptionObject))
        {
            detail += Environment.NewLine + loggingEvent.ExceptionObject;
        }

        var message = GetMessage(loggingEvent);

        sink.LogEvent(loggingEvent.Level?.Value ?? Level.Info.Value, message, detail);
    }

    private static string GetMessage(LoggingEvent loggingEvent)
    {
        var message = loggingEvent.RenderedMessage ?? string.Empty;
        var index = message.IndexOf('\n');
        if (index != -1)
        {
            message = message.Substring(0, index);
        }

        if (message.Length > MaxMsgLength)
        {
            message = message.Substring(0, MaxMsgLength) + "...";
        }

        return message;
    }
}
