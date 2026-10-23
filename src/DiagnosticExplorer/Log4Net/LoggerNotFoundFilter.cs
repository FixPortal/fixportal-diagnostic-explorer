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

using log4net.Core;
using log4net.Filter;
using log4net.Repository.Hierarchy;

namespace DiagnosticExplorer.Log4Net;

public class LoggerNotFoundFilter : FilterSkeleton
{
    public override FilterDecision Decide(LoggingEvent loggingEvent)
    {
        if (loggingEvent.Repository == null)
        {
            return FilterDecision.Deny;
        }

        if (loggingEvent.Repository is not Hierarchy hierarchy)
        {
            return FilterDecision.Deny;
        }

        if (
            // ReSharper disable once AssignNullToNotNullAttribute -- Exists returns null when absent.
            hierarchy.Exists(loggingEvent.LoggerName)
                is not Logger hlog
            || hlog.Appenders.Count != 0
        )
        {
            return FilterDecision.Deny;
        }

        if (string.Compare(hlog.Parent?.Name, "ROOT", StringComparison.OrdinalIgnoreCase) != 0)
        {
            return FilterDecision.Deny;
        }

        return FilterDecision.Accept;
    }
}
