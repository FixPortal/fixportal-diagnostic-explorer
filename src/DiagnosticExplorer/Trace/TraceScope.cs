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

using System.CodeDom.Compiler;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using log4net;
using ATraceItem = DiagnosticExplorer.TraceItem<DiagnosticExplorer.TraceScope>;

namespace DiagnosticExplorer;

/// <summary>Enabled trace to a single source through method calls</summary>
public sealed class TraceScope : IDisposable
{
    private static readonly AsyncLocal<ScopeStack> _scopeStack = new();

    private readonly DateTime _created;
    private readonly TimeProvider _timeProvider;
    private DateTime? _disposed;
    private readonly List<ATraceItem> _traceItems = [];
    private readonly ReaderWriterLockSlim _traceItemsLock = new();
    private readonly object _autoTraceTimerLock = new();
    private bool _forceTrace;
    private bool _isRoot;
    private ITimer _autoTraceTimer;

    /// <summary>
    /// Sorted dictionary of time in milliseconds vs the trace method
    /// which should be used if the operation exceeds that time
    /// </summary>
    private SortedDictionary<int, Action<string>> _traceMethods;

    #region Constructors

    public TraceScope([CallerMemberName] string name = null)
        : this(TimeProvider.System, name, null) { }

    public TraceScope(Action<string> traceMethod, [CallerMemberName] string name = null)
        : this(TimeProvider.System, name, traceMethod) { }

    public TraceScope(string name, Action<string> traceMethod)
        : this(TimeProvider.System, name, traceMethod) { }

    public TraceScope(string name, Action<string> traceMethod, bool forceTrace)
        : this(TimeProvider.System, name, traceMethod, forceTrace) { }

    public TraceScope(Action<string> traceMethod, bool forceTrace, [CallerMemberName] string name = null)
        : this(TimeProvider.System, name, traceMethod, forceTrace) { }

    public TraceScope(TimeProvider timeProvider, string name, Action<string> traceMethod, bool forceTrace = false)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _created = UtcNow;
        Setup(name, traceMethod, forceTrace);
    }

    private void Setup(string name, Action<string> traceMethod, bool forceTrace)
    {
        _traceMethods = [];
        SetTraceAction(0, traceMethod);

        Name = name;

        _forceTrace = forceTrace;

        ScopeStack scopeStack = _scopeStack.Value ?? ScopeStack.Empty;
        _isRoot = scopeStack.Current == null;

        TraceScope current = scopeStack.Current;
        current?.AddTraceItem(new ATraceItem(this, current._timeProvider));
        _scopeStack.Value = scopeStack.Push(this);
    }

    private void AddTraceItem(ATraceItem traceItem)
    {
        try
        {
            _traceItemsLock.EnterWriteLock();
            try
            {
                _traceItems.Add(traceItem);
            }
            finally
            {
                _traceItemsLock.ExitWriteLock();
            }
        }
        catch (ObjectDisposedException)
        {
            // Ignore if the TraceScope has been disposed
        }
    }

    #endregion


    public void StartAutoTraceTimer(TimeSpan time)
    {
        lock (_autoTraceTimerLock)
        {
            if (_disposed != null)
            {
                return;
            }

            if (_autoTraceTimer == null)
            {
                _autoTraceTimer = _timeProvider.CreateTimer(
                    AutoTraceAfterTimeout,
                    null,
                    time,
                    Timeout.InfiniteTimeSpan
                );
            }
            else
            {
                _autoTraceTimer.Change(time, Timeout.InfiniteTimeSpan);
            }
        }
    }

    internal bool HasAutoTraceTimer
    {
        get
        {
            lock (_autoTraceTimerLock)
            {
                return _autoTraceTimer != null;
            }
        }
    }

    private void AutoTraceAfterTimeout(object state)
    {
        try
        {
            AddTraceItem(new ATraceItem("FORCE TRACE AFTER TIMEOUT", _timeProvider));
            TraceMessage();
        }
        catch (ObjectDisposedException)
        {
            // Ignore if the TraceScope has been disposed concurrently
        }
    }

    public static TraceScope Current => _scopeStack.Value?.Current;

    public string Name { get; set; }
    public bool IsHidden { get; set; }

    public TimeSpan? SuppressDetailThreshold { get; set; }

    public void SetTraceAction(Action<string> traceMethod)
    {
        SetTraceAction(0, traceMethod);
    }

    public void SetTraceAction(int timeMillis, Action<string> traceMethod)
    {
        _traceMethods[timeMillis] = traceMethod;
    }

    public TimeSpan Age => UtcNow.Subtract(_created);

    private DateTime UtcNow => _timeProvider.GetUtcNow().UtcDateTime;

    public override string ToString()
    {
        StringBuilder sb = new StringBuilder();
        using StringWriter writer = new StringWriter(sb);
        using IndentedTextWriter indentedWriter = new IndentedTextWriter(writer);

        DateTime lastMessage = _created;
        ToString(indentedWriter, _created, ref lastMessage, 0);
        indentedWriter.Flush();
        writer.Flush();
        return sb.ToString();
    }

    public void ToString(IndentedTextWriter writer, DateTime scopeStart, ref DateTime lastMessage, int indent)
    {
        if (IsHidden)
        {
            return;
        }

        _traceItemsLock.EnterReadLock();
        try
        {
            if (_traceItems.Count == 0)
            {
                WriteBeginEndScope(this, writer, scopeStart, ref lastMessage, false);
            }
            else
            {
                WriteBeginScope(writer, scopeStart, ref lastMessage);

                writer.Indent += indent;
                foreach (ATraceItem item in _traceItems)
                {
                    if (item.TraceScope == null)
                    {
                        WriteString(writer, item.Message, item.Created, scopeStart, ref lastMessage);
                    }
                    else if (FullTraceRequired(item))
                    {
                        item.TraceScope.ToString(writer, scopeStart, ref lastMessage, 1);
                    }
                    else
                    {
                        WriteBeginEndScope(item.TraceScope, writer, scopeStart, ref lastMessage, true);
                    }
                }
                writer.Indent -= indent;

                WriteEndScope(writer, scopeStart, ref lastMessage);
            }
        }
        finally
        {
            _traceItemsLock.ExitReadLock();
        }
    }

    private static bool FullTraceRequired(ATraceItem item)
    {
        if (item.TraceScope == null)
        {
            return false;
        }

        if (item.TraceScope._forceTrace)
        {
            return true;
        }

        if (item.TraceScope.SuppressDetailThreshold == null)
        {
            return true;
        }

        TimeSpan thresh = item.TraceScope.SuppressDetailThreshold.Value;
        // Snapshot _disposed once: when a still-running child is rendered (the auto-trace timer
        // fires while the child is open), _disposed is null. Reading .Value threw a NullReference
        // that TraceMessage swallowed, so auto-trace silently produced no output. Fall back to
        // elapsed-so-far for an in-progress child.
        DateTime? disposed = item.TraceScope._disposed;
        TimeSpan duration = (disposed ?? item.TraceScope.UtcNow) - item.TraceScope._created;
        return duration >= thresh;
    }

    private void WriteBeginScope(IndentedTextWriter writer, DateTime scopeStart, ref DateTime lastMessage)
    {
        if (Name == null)
        {
            return;
        }

        string message = $"BEGIN {Name}";
        if (_disposed != null)
        {
            message = string.Format("{0} ({1:N3} seconds)", message, _disposed.Value.Subtract(_created).TotalSeconds);
        }

        WriteString(writer, message, _created, scopeStart, ref lastMessage);
    }

    private void WriteEndScope(IndentedTextWriter writer, DateTime scopeStart, ref DateTime lastMessage)
    {
        if (Name == null)
        {
            return;
        }

        if (_disposed == null)
        {
            return;
        }

        string message = string.Format(
            "END {0} ({1:N3} seconds)",
            Name,
            _disposed.Value.Subtract(_created).TotalSeconds
        );

        WriteString(writer, message, _disposed.Value, scopeStart, ref lastMessage);
    }

    private static void WriteBeginEndScope(
        TraceScope scope,
        IndentedTextWriter writer,
        DateTime scopeStart,
        ref DateTime lastMessage,
        bool suppressed
    )
    {
        if (scope.Name == null)
        {
            return;
        }

        if (scope._disposed == null)
        {
            return;
        }

        TimeSpan duration = scope._disposed.Value.Subtract(scope._created);

        string message = string.Format(
            "BEGIN/END{0} {1} ({2:N3} seconds)",
            suppressed ? "*" : "",
            scope.Name,
            duration.TotalSeconds
        );

        WriteString(writer, message, scope._disposed.Value, scopeStart, ref lastMessage);
    }

    private static void WriteString(
        IndentedTextWriter writer,
        string message,
        DateTime itemDate,
        DateTime scopeStart,
        ref DateTime lastMessage
    )
    {
        double age = itemDate.Subtract(scopeStart).TotalSeconds;
        double split = itemDate.Subtract(lastMessage).TotalSeconds;

        string[] parts = Regex.Split(message, @"\r?\n", RegexOptions.None, TimeSpan.FromSeconds(1));
        writer.Write("[{0:00.000}] [{1:00.000}] ", age, split);
        writer.WriteLine(parts[0].Trim());

        for (int i = 1; i < parts.Length; i++)
        {
            writer.Write("                  ");
            writer.WriteLine(parts[i]);
        }

        lastMessage = itemDate;
    }

    public void Dispose()
    {
        Dispose(true);
    }

    private void Dispose(bool disposing)
    {
        if (disposing)
        {
            lock (_autoTraceTimerLock)
            {
                _disposed = UtcNow;
                _autoTraceTimer?.Dispose();
                _autoTraceTimer = null;
            }

            ScopeStack currentStack = _scopeStack.Value;
            if (currentStack != null)
            {
                ScopeStack updatedStack = currentStack.Remove(this);
                _scopeStack.Value = updatedStack.IsEmpty ? null : updatedStack;
            }

            TraceMessage();

            // ReaderWriterLockSlim is IDisposable; a TraceScope is created and disposed per traced
            // operation, so not disposing the lock leaks it (and its lazily-allocated kernel handles)
            // on every scope. Dispose last — TraceMessage()/ToString() above still read under it. (B4)
            try
            {
                _traceItemsLock.Dispose();
            }
            catch (SynchronizationLockException)
            {
                // Suppress if timer callback is mid-flight holding the lock
            }
        }
    }

    private void TraceMessage()
    {
        try
        {
            Action<string> action = GetTraceMethod(Age.TotalMilliseconds);
            if (action == null)
            {
                return;
            }

            if (!_isRoot && !_forceTrace)
            {
                return;
            }

            action(ToString());
        }
        catch (ObjectDisposedException)
        {
            // Ignore silently if lock is disposed during concurrent TraceMessage call
        }
        catch (Exception ex)
        {
            LogManager.GetLogger(GetType()).Error(ex.Message, ex);
        }
    }

    private Action<string> GetTraceMethod(double millis)
    {
        return _traceMethods.Reverse().Where(pair => millis >= pair.Key).Select(pair => pair.Value).FirstOrDefault()
            ?? _traceMethods.FirstOrDefault().Value;
    }

    #region Write Methods

    public static ITraceItem Trace(StackTrace trace)
    {
        string s = trace.ToString();
        if (s.Length > 200)
        {
            s = s.Substring(0, 200);
        }

        return Trace(s);
    }

    public static ITraceItem Trace(object o)
    {
        return Trace(o?.ToString() ?? "");
    }

    public static ITraceItem Trace(string message)
    {
        var current = Current;
        if (current == null)
        {
            return null;
        }

        ATraceItem item = new ATraceItem(message, current._timeProvider);
        current.AddTraceItem(item);
        return item;
    }

    public static void TraceIf(bool condition, object o)
    {
        if (condition)
        {
            Trace(o?.ToString() ?? "");
        }
    }

    public static void TraceIf(bool condition, string message)
    {
        if (condition)
        {
            Trace(message);
        }
    }

    #endregion
}
