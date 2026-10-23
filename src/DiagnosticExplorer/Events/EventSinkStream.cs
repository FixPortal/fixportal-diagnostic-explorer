#nullable enable annotations

using System.Reactive.Linq;
using System.Reactive.Subjects;
using System.Threading.Channels;

namespace DiagnosticExplorer;

public sealed class EventSinkStream : IDisposable
{
    private readonly IDisposable _eventSubscription;
    private readonly Subject<SystemEvent> _innerSubject;
    private readonly int _bufferSize;
    private readonly ISubject<SystemEvent> _eventSubject;

    public EventSinkStream(SystemEvent[] initialEvents, TimeSpan buffer, int bufferSize)
    {
        if (bufferSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(bufferSize), "bufferSize must be positive");
        }

        InitialEvents = initialEvents;
        _bufferSize = bufferSize;

        _innerSubject = new Subject<SystemEvent>();
        _eventSubject = Subject.Synchronize(_innerSubject);
        _eventSubscription = _eventSubject
            .Publish(sp => sp.GroupByUntil(_ => true, _ => Observable.Timer(buffer)).SelectMany(i => i.ToList()))
            .Subscribe(WriteEvents, () => EventChannel.Writer.TryComplete());

        EventChannel = Channel.CreateBounded<IList<SystemEvent>>(
            new BoundedChannelOptions(10000) { SingleReader = true, FullMode = BoundedChannelFullMode.DropWrite }
        );
    }

    public SystemEvent[] InitialEvents { get; }

    public Channel<IList<SystemEvent>> EventChannel { get; }

    public void Dispose()
    {
        Disposed?.Invoke(this, EventArgs.Empty);
        Disposed = null;

        _eventSubscription.Dispose();
        _innerSubject.Dispose();
    }

    public event EventHandler? Disposed;

    private void WriteEvents(IList<SystemEvent> evts)
    {
        if (evts.Count <= _bufferSize)
        {
            EventChannel.Writer.TryWrite(evts);
            return;
        }

        // Split into bufferSize-sized chunks synchronously. The previous
        // evts.ToObservable().Buffer(bufferSize).ForEachAsync(...) returned a Task that was never
        // awaited or observed (fire-and-forget) — any fault was swallowed and completion ordering
        // was undefined. A plain loop has the same chunking effect with none of that.
        for (var i = 0; i < evts.Count; i += _bufferSize)
        {
            var chunk = new List<SystemEvent>(Math.Min(_bufferSize, evts.Count - i));
            for (var j = i; j < evts.Count && j < i + _bufferSize; j++)
            {
                chunk.Add(evts[j]);
            }

            EventChannel.Writer.TryWrite(chunk);
        }
    }

    public void StreamEvent(SystemEvent evt)
    {
        try
        {
            _eventSubject.OnNext(evt);
        }
        catch (ObjectDisposedException)
        {
            // Events racing disposal are intentionally dropped.
        }
    }
}
