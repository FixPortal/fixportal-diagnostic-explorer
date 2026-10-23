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

using System.Collections.Concurrent;
using System.Diagnostics;
using log4net.Core;

namespace DiagnosticExplorer;

// Events are bounded by EventSinkRepo retention. The former static
// `sinks` WeakReferenceHash + 20s purge timer were dead code — nothing ever registered a sink
// into `sinks` (live sinks live in EventSinkRepo), so the 30-minute age purge never ran.
// Removed rather than half-wired (per-instance timers would leak; re-registering into the
// static hash reintroduces its collision/concurrency issues).
public class EventSink
{
    public const int MaxMessages = 1000;
    private const int MaxLength = 102400;
    private readonly EventSinkRepo _repo;
    private readonly TimeProvider _timeProvider;

    private long _idCount;
    private DateTime _nextExpiryUtc = DateTime.MaxValue;

    private bool _invalid;

    internal EventSink(EventSinkRepo repo, string name, string category, TimeProvider timeProvider)
    {
        _repo = repo;
        _timeProvider = timeProvider;
        Name = name;
        Category = category;
    }

    public string Name { get; }

    public string Category { get; }

    /// <summary>The legacy public event queue.</summary>
    /// <remarks>
    ///     Direct queue writes remain supported for compatibility, but bypass immediate retention;
    ///     the next snapshot or explicit retention configuration purges them.
    /// </remarks>
    public ConcurrentQueue<SystemEvent> Events { get; } = new();

    public void Info(string message, string detail = null)
    {
        LogEvent(Level.Info.Value, message, detail);
    }

    public void Notice(string message, string detail = null)
    {
        LogEvent(Level.Notice.Value, message, detail);
    }

    public void Warn(string message, string detail = null)
    {
        LogEvent(Level.Warn.Value, message, detail);
    }

    public void Error(string message, string detail = null)
    {
        LogEvent(Level.Error.Value, message, detail);
    }

    public void Fatal(string message, string detail = null)
    {
        LogEvent(Level.Fatal.Value, message, detail);
    }

    public void LogEvent(int level, string message, string detail)
    {
        try
        {
            CleanMessageAndDetail(ref message, ref detail);

            SystemEvent evt = new()
            {
                Id = Interlocked.Increment(ref _idCount),
                Date = _timeProvider.GetUtcNow().UtcDateTime,
                Level = level,
                SinkName = Name,
                SinkCategory = Category,
                Message = MaxLengthString(message, MaxLength),
                Detail = MaxLengthString(detail, MaxLength),
            };
            AddSingleEvent(evt);
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    public void LogEvent(SystemEvent evt)
    {
        try
        {
            // Imported events bypass the int overload, so apply the same length cap here —
            // otherwise an arbitrarily long Message/Detail flows unbounded into the queue and
            // protobuf serialization.
            evt.Message = MaxLengthString(evt.Message, MaxLength);
            evt.Detail = MaxLengthString(evt.Detail, MaxLength);

            AddSingleEvent(evt);

            // Atomically advance _idCount; a plain Math.Max RMW races the Interlocked.Increment
            // in the int overload and would lose updates / yield duplicate ids.
            var target = evt.Id + 1;
            long current;
            while ((current = Interlocked.Read(ref _idCount)) < target)
            {
                if (Interlocked.CompareExchange(ref _idCount, target, current) == current)
                {
                    break;
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine(ex);
        }
    }

    internal void Invalidate()
    {
        _invalid = true;
    }

    public void Clear()
    {
        while (Events.TryDequeue(out _))
        {
            // Drain the queue; ConcurrentQueue<T>.Clear is unavailable on net48.
        }

        Interlocked.Exchange(ref _idCount, 0);
    }

    private void AddSingleEvent(SystemEvent evt)
    {
        if (_invalid)
        {
            return;
        }

        // Events are enqueued inside RegisterEvent under the repo write lock to ensure
        // stream creation snapshots are atomic with respect to live broadcasts (DE03-DUP).
        _repo.RegisterEvent(this, evt);
    }

    internal bool AddAndPurge(SystemEvent evt, EventRetentionOptions retention, DateTime now)
    {
        Events.Enqueue(evt);
        _nextExpiryUtc = Min(_nextExpiryUtc, ExpiresAt(evt.Date, retention));
        if (now > _nextExpiryUtc)
        {
            return Purge(retention, now, evt);
        }

        TrimToCount(retention);
        return true;
    }

    internal void Purge(EventRetentionOptions retention, DateTime now)
    {
        _ = Purge(retention, now, null);
    }

    private bool Purge(EventRetentionOptions retention, DateTime now, SystemEvent added)
    {
        TimeSpan maxAge = TimeSpan.FromMinutes(retention.MaxAgeMinutes);
        DateTime minimumTimestamp = now.Ticks <= maxAge.Ticks ? DateTime.MinValue : now - maxAge;
        List<SystemEvent> retained = [];
        while (Events.TryDequeue(out SystemEvent current))
        {
            if (current.Date >= minimumTimestamp)
            {
                retained.Add(current);
            }
        }

        bool containsAdded = false;
        _nextExpiryUtc = DateTime.MaxValue;
        int firstRetained = Math.Max(0, retained.Count - retention.MaxEventsPerSink);
        for (int index = firstRetained; index < retained.Count; index++)
        {
            SystemEvent current = retained[index];
            Events.Enqueue(current);
            containsAdded |= ReferenceEquals(current, added);
            _nextExpiryUtc = Min(_nextExpiryUtc, ExpiresAt(current.Date, retention));
        }

        return containsAdded;
    }

    private void TrimToCount(EventRetentionOptions retention)
    {
        while (Events.Count > retention.MaxEventsPerSink)
        {
            _ = Events.TryDequeue(out _);
        }
    }

    private static DateTime ExpiresAt(DateTime timestamp, EventRetentionOptions retention)
    {
        TimeSpan maxAge = TimeSpan.FromMinutes(retention.MaxAgeMinutes);
        return timestamp.Ticks > DateTime.MaxValue.Ticks - maxAge.Ticks ? DateTime.MaxValue : timestamp + maxAge;
    }

    private static DateTime Min(DateTime left, DateTime right)
    {
        return left <= right ? left : right;
    }

    /// <summary>
    ///     If there is no detail but a massive message, put the whole message into detail
    ///     and leave only the first line in message
    /// </summary>
    private static void CleanMessageAndDetail(ref string message, ref string detail)
    {
        if (!string.IsNullOrEmpty(detail))
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            return;
        }

        var index = message.IndexOf('\n');
        if (index != -1)
        {
            detail = message;
            message = message.Substring(0, index);
        }
    }

    private static string MaxLengthString(string s, int maxLength)
    {
        if (s == null)
        {
            // ReSharper disable once ExpressionIsAlwaysNull -- legacy callers may supply null.
            return s;
        }

        if (s.Length <= maxLength)
        {
            return s;
        }

        return s.Substring(0, maxLength);
    }
}
