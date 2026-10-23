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

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;

namespace DiagnosticExplorer;

[Serializable]
internal class SystemStatus
{
    private readonly TimeProvider _timeProvider;

    public SystemStatus()
        : this(TimeProvider.System) { }

    [SuppressMessage(
        "Serialization",
        "S5766:Deserialization methods should be provided for optional fields",
        Justification = "This is a dependency-injection constructor; SystemStatus is never deserialized."
    )]
    internal SystemStatus(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        DiagnosticManager.Register(this, "Environment", "System");

        using (var p = Process.GetCurrentProcess())
        {
            Pid = p.Id;
        }

        User = $"{Environment.UserDomainName}\\{Environment.UserName}";
        HostMachine = Environment.MachineName;
        ProcessorCount = Environment.ProcessorCount;
        DiagnosticRequests = new RateCounter(5, timeProvider);
    }

    public static SystemStatus Instance { get; set; } = new();

    [Property(Category = "CPU")]
    public int ProcessorCount { get; private set; }

    [Property(Category = "CPU")]
    public int Threads
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.Threads.Count;
        }
    }

    [Property(Category = "Memory", FormatString = "{0:N2}")]
    public double VirtualMemory
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.PagedMemorySize64 / (1024F * 1024F);
        }
    }

    [Property(Category = "Memory", FormatString = "{0:N2}")]
    public double Memory
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return p.WorkingSet64 / (1024F * 1024F);
        }
    }

    public int Pid { get; set; }

    public string HostMachine { get; set; }

    [SuppressMessage(
        "Maintainability",
        "S2325:Methods and properties that do not access instance data should be static",
        Justification = "DiagnosticManager reflects instance properties; making this static would remove it from the diagnostic surface."
    )]
    public string BaseDirectory => AppDomain.CurrentDomain.BaseDirectory;

    public RateCounter DiagnosticRequests { get; }

    public string User { get; set; }

    [SuppressMessage(
        "Maintainability",
        "S2325:Methods and properties that do not access instance data should be static",
        Justification = "DiagnosticManager reflects instance properties; making this static would remove it from the diagnostic surface."
    )]
    public TimeSpan UpTime
    {
        get
        {
            using var p = Process.GetCurrentProcess();
            return _timeProvider.GetUtcNow().UtcDateTime - p.StartTime.ToUniversalTime();
        }
    }

    [Property(FormatString = "{0:d MMM yyyy HH:mm:ss}")]
    public DateTime SystemTime => _timeProvider.GetLocalNow().DateTime;

    public static void Register()
    {
        Instance ??= new SystemStatus();
    }

    public void RegisterDiagnosticRequest()
    {
        DiagnosticRequests?.Register(1);
    }
}
