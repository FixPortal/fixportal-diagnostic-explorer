using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace DiagnosticExplorer;

public sealed class EventSinkRepo : IDisposable
{
    private readonly ReaderWriterLockSlim _eventStreamLock = new(LockRecursionPolicy.NoRecursion);
    private readonly List<EventSinkStream> _sinkStreams = [];

    // Keyed by the (name, category) tuple, not a "{name}.{category}" string: the latter collided
    // distinct sinks, e.g. ("a.b","c") and ("a","b.c") both mapped to "a.b.c".
    private readonly ConcurrentDictionary<(string Name, string Category), EventSink> _sinks = new();
    private readonly TimeProvider _timeProvider;
    private EventRetentionOptions _eventRetention;

    private bool _disposed;

    public static EventSinkRepo Default { get; } = new();

    public EventSinkRepo()
        : this(TimeProvider.System) { }

    public EventSinkRepo(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _eventRetention = new EventRetentionOptions().CloneAndValidate();
    }

    public EventRetentionOptions EventRetention => Volatile.Read(ref _eventRetention).CloneAndValidate();

    public void ConfigureEventRetention(EventRetentionOptions eventRetention)
    {
        if (eventRetention == null)
        {
            throw new ArgumentNullException(nameof(eventRetention));
        }

        EventRetentionOptions replacement = eventRetention.CloneAndValidate();

        _eventStreamLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            Volatile.Write(ref _eventRetention, replacement);
            PurgeSinks(replacement);
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }
    }

    public void Dispose()
    {
        _eventStreamLock.EnterWriteLock();
        try
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var stream in _sinkStreams.ToArray())
            {
                stream.Disposed -= HandleEventStreamDisposed;
                stream.EventChannel.Writer.TryComplete();
                stream.Dispose();
            }

            _sinkStreams.Clear();
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }

        // ponytail: deliberately not disposing _eventStreamLock. Disposing it here, outside the
        // lock it was just released from, raced RegisterEvent/CreateSinkStream: a thread that
        // acquired the write lock the instant it was released could still be holding it when this
        // ran, and ReaderWriterLockSlim.Dispose() throws SynchronizationLockException on a lock
        // that is held or waited on. This type never uses the upgradeable-lock path, so the
        // instance allocates no kernel wait handle to leak; every entry point already checks
        // ThrowIfDisposed() under the same lock, so disposal is fully synchronized without ever
        // tearing down the primitive itself.
    }

    public EventSink GetSink(string name, string category)
    {
        return _sinks.GetOrAdd((name, category), key => new EventSink(this, key.Name, key.Category, _timeProvider));
    }

    public void LogEvent(SystemEvent evt)
    {
        GetSink(evt.SinkName, evt.SinkCategory).LogEvent(evt);
    }

    public void LogEvents(SystemEvent[] evts)
    {
        foreach (var evt in evts)
        {
            LogEvent(evt);
        }
    }

    public EventSinkStream CreateSinkStream(TimeSpan buffer, int bufferSize)
    {
        ThrowIfDisposed();

        // ponytail: this serializes snapshot purge and live handoff; use per-sink locking only if
        // measured contention warrants it, since delaying it breaks expiry and out-of-order imports.
        _eventStreamLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            PurgeSinks(Volatile.Read(ref _eventRetention));

            EventSinkStream stream = new(_sinks.Values.SelectMany(sink => sink.Events).ToArray(), buffer, bufferSize);
            _sinkStreams.Add(stream);
            stream.Disposed += HandleEventStreamDisposed;
            return stream;
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }
    }

    private void ThrowIfDisposed()
    {
        if (_disposed)
        {
            throw new ObjectDisposedException(nameof(EventSinkRepo));
        }
    }

    public SystemEvent[] GetEvents()
    {
        _eventStreamLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            PurgeSinks(Volatile.Read(ref _eventRetention));
            return _sinks.Values.SelectMany(sink => sink.Events).ToArray();
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }
    }

    private void HandleEventStreamDisposed(object sender, EventArgs e)
    {
        var stream = (EventSinkStream)sender;
        UnregisterStream(stream);
    }

    private void UnregisterStream(EventSinkStream stream)
    {
        if (_disposed)
        {
            return;
        }

        try
        {
            _eventStreamLock.EnterWriteLock();
        }
        catch (ObjectDisposedException)
        {
            return;
        }

        try
        {
            _sinkStreams.Remove(stream);
            stream.EventChannel.Writer.TryComplete();
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }

        stream.Disposed -= HandleEventStreamDisposed;
    }

    internal void RegisterEvent(EventSink sink, SystemEvent evt)
    {
        _eventStreamLock.EnterWriteLock();
        try
        {
            ThrowIfDisposed();
            if (!sink.AddAndPurge(evt, Volatile.Read(ref _eventRetention), _timeProvider.GetUtcNow().UtcDateTime))
            {
                return;
            }

            foreach (var stream in _sinkStreams)
            {
                stream.StreamEvent(evt);
            }
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }
    }

    private void PurgeSinks(EventRetentionOptions retention)
    {
        DateTime now = _timeProvider.GetUtcNow().UtcDateTime;
        foreach (EventSink sink in _sinks.Values)
        {
            sink.Purge(retention, now);
        }
    }

    public void Clear()
    {
        // Take the write lock so the clear is coherent with the _sinks.Values snapshots in
        // CreateSinkStream/GetEvents (which run under this lock) rather than racing them mid-
        // enumeration. Active _sinkStreams are intentionally left running — they belong to live
        // subscriptions; this only resets the sink set. (M34)
        _eventStreamLock.EnterWriteLock();
        try
        {
            foreach (var sink in _sinks.Values)
            {
                sink.Invalidate();
            }

            _sinks.Clear();
        }
        finally
        {
            _eventStreamLock.ExitWriteLock();
        }
    }
}
