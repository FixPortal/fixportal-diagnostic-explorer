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

using System.Diagnostics;

namespace DiagnosticExplorer;

public class RateCounter
{
    private static readonly TimeSpan SampleInterval = TimeSpan.FromSeconds(1);
    private readonly int[] _counts;
    private readonly TimeProvider _timeProvider;
    private readonly TimeSpan[] _times;
    private int _index;

    // A monotonic timestamp, not a wall-clock reading. Elapsed time here drives the rate, so a
    // clock step - an NTP correction, a daylight-saving jump, an operator setting the clock -
    // would otherwise produce a wildly wrong or negative interval. Upstream fixed the same bug
    // with Stopwatch.GetTimestamp(); TimeProvider gives the same monotonic source while staying
    // injectable, so the counter is still testable without waiting on real time.
    private long _lastCheck;

    private double _rate;
    private ulong _total;

    public RateCounter(int secondsAverage)
        : this(secondsAverage, TimeProvider.System) { }

    public RateCounter(int secondsAverage, TimeProvider timeProvider)
    {
        // Must be > 0: zero gives zero-length buffers, so `_index % _counts.Length` throws
        // DivideByZeroException inside Run's swallowed try/catch and the counter silently never
        // advances; negative throws OverflowException at the array allocation below.
        if (secondsAverage <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(secondsAverage),
                secondsAverage,
                "secondsAverage must be greater than zero."
            );
        }

        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _lastCheck = _timeProvider.GetTimestamp();
        _counts = new int[secondsAverage];
        _times = new TimeSpan[secondsAverage];
        new CounterTimerState(this).Start(timeProvider);
    }

    // Read under the same lock the writers (CalcRate/Register) hold: Rate (double) and Total
    // (ulong) are 64-bit, so an unlocked read can tear on a 32-bit host.
    public double Rate
    {
        get
        {
            lock (_counts)
            {
                return _rate;
            }
        }
    }

    public ulong Total
    {
        get
        {
            lock (_counts)
            {
                return _total;
            }
        }
    }

    public event EventHandler<RateSampleEventArgs>? SampleCollected;

    private void Increment()
    {
        lock (_counts)
        {
            _times[_index % _counts.Length] = _timeProvider.GetElapsedTime(_lastCheck);
            CalcRate();
            _index++;
            _counts[_index % _counts.Length] = 0;
            _times[_index % _counts.Length] = TimeSpan.Zero;
            _lastCheck = _timeProvider.GetTimestamp();

            InvokeSampleCollected();
        }
    }

    private void InvokeSampleCollected()
    {
        try
        {
            var sampleCollectedHandler = SampleCollected;

            if (sampleCollectedHandler != null)
            {
                var args = new RateSampleEventArgs(Rate, GetRates(_times.Length));
                foreach (
                    var handler in sampleCollectedHandler
                        .GetInvocationList()
                        .OfType<EventHandler<RateSampleEventArgs>>()
                )
                {
                    // Delegate.BeginInvoke throws PlatformNotSupportedException on .NET Core/5+.
                    // Task.Run gives the same fire-and-forget async dispatch portably.
                    Task.Run(() =>
                    {
                        try
                        {
                            handler(this, args);
                        }
                        catch (Exception ex)
                        {
                            Trace.TraceError($"RateCounter.SampleCollected subscriber failed: {ex}");
                        }
                    });
                }
            }
        }
        catch (Exception ex)
        {
            Trace.TraceError($"RateCounter.SampleCollected dispatch failed: {ex}");
        }
    }

    private void CalcRate()
    {
        double r = _counts.Sum();
        var totalTime = _times.Aggregate((t1, t2) => t1 + t2);

        _rate = totalTime == TimeSpan.Zero ? 0 : r / totalTime.TotalSeconds;
    }

    public void Register(int count)
    {
        // Ignore non-positive counts: a negative would corrupt the bucket (and diverge from
        // Total, which only ever counted count > 0), producing negative rates. Zero is a no-op.
        if (count <= 0)
        {
            return;
        }

        lock (_counts)
        {
            _total += (ulong)count;
            _counts[_index % _counts.Length] += count;
        }
    }

    /// <summary>
    ///     Gets the last n seconds rates
    /// </summary>
    /// <param name="seconds">The number of seconds worth of data to fetch</param>
    /// <returns>A list of rates for the last n seconds, starting with the latest</returns>
    public int[] GetRates(int seconds)
    {
        lock (_counts)
        {
            return GetRates(seconds, _index, _counts);
        }
    }

    public static int[] GetRates(int seconds, int currentIndex, int[] values)
    {
        // Clamp to the samples actually recorded (currentIndex) AND to the ring-buffer capacity
        // (values.Length). Without the capacity clamp, once the index wraps we would walk back
        // past the buffer size and re-read ring slots as if they were distinct samples,
        // fabricating history the buffer never held.
        seconds = Math.Min(seconds, Math.Min(currentIndex, values.Length));
        var results = new int[seconds];

        for (var i = 0; i < results.Length; i++)
        {
            results[i] = values[(currentIndex - 1 - i) % values.Length];
        }

        return results;
    }

    private sealed class CounterTimerState
    {
        private readonly WeakReference<RateCounter> _counter;
        private ITimer? _timer;

        public CounterTimerState(RateCounter counter)
        {
            _counter = new WeakReference<RateCounter>(counter);
        }

        public void Start(TimeProvider timeProvider)
        {
            _timer = timeProvider.CreateTimer(
                static state => ((CounterTimerState?)state)?.Tick(),
                this,
                SampleInterval,
                SampleInterval
            );
        }

        private void Tick()
        {
            if (_counter.TryGetTarget(out RateCounter? counter))
            {
                try
                {
                    counter.Increment();
                }
                catch (Exception ex)
                {
                    Trace.TraceError($"RateCounter sampling failed: {ex}");
                }

                return;
            }

            _timer?.Dispose();
            _timer = null;
        }
    }
}
