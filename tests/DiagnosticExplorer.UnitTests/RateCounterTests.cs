using System.Diagnostics;
using System.Reflection;
using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     RateCounter samples through its injected TimeProvider, while its static GetRates
///     is the pure ring-buffer read behind per-second rate history.
/// </summary>
public class RateCounterTests
{
    /// <summary>
    ///     Subscriber failures are reported in Release builds without preventing other subscribers
    ///     from receiving the sample. The exception must be caught inside the dispatched task.
    /// </summary>
    [Fact]
    public async Task SampleCollected_WhenSubscriberThrows_ReportsFailureAndDispatchesOtherSubscribers()
    {
        FakeTimeProvider timeProvider = new();
        var counter = new RateCounter(5, timeProvider);
        TaskCompletionSource<RateSampleEventArgs> received = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using SampleFailureTraceListener listener = new();
        Trace.Listeners.Add(listener);
        counter.SampleCollected += (_, _) => throw new InvalidOperationException("sample subscriber failed");
        counter.SampleCollected += (_, args) => received.TrySetResult(args);

        try
        {
            counter.Register(2);
            timeProvider.Advance(TimeSpan.FromSeconds(1));

            await received.Task.WaitAsync(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken);
            string error = await listener.Error.Task.WaitAsync(
                TimeSpan.FromSeconds(5),
                TestContext.Current.CancellationToken
            );
            error.Should().Contain("InvalidOperationException").And.Contain("sample subscriber failed");
            counter.Rate.Should().Be(2);
        }
        finally
        {
            Trace.Listeners.Remove(listener);
        }
    }

    private sealed class SampleFailureTraceListener : TraceListener
    {
        public TaskCompletionSource<string> Error { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public override void Write(string? message) { }

        public override void WriteLine(string? message)
        {
            if (message?.Contains("sample subscriber failed", StringComparison.Ordinal) == true)
            {
                Error.TrySetResult(message);
            }
        }
    }

    [Fact]
    public void Increment_UsesInjectedTimeProviderForElapsedRate()
    {
        FakeTimeProvider timeProvider = new(new DateTimeOffset(2026, 8, 12, 9, 30, 0, TimeSpan.Zero));
        var counter = new RateCounter(5, timeProvider);
        counter.Register(2);
        timeProvider.Advance(TimeSpan.FromSeconds(2));

        counter.Rate.Should().Be(1);
    }

    /// <summary>
    ///     GetRates reads backwards from the current index, newest first, wrapping around
    ///     the ring buffer. Parameterized to cover a simple read and the request-clamped-to-
    ///     available case, which is the behaviour the rate graph depends on.
    /// </summary>
    [Theory]
    [InlineData(3, 5, new[] { 50, 40, 30 })] // newest (index 4) first, walking back
    [InlineData(10, 2, new[] { 20, 10 })] // seconds clamped to currentIndex
    public void GetRates_WithinFilledBuffer_ReturnsNewestFirst(int seconds, int currentIndex, int[] expected)
    {
        var values = new[] { 10, 20, 30, 40, 50 };

        var rates = RateCounter.GetRates(seconds, currentIndex, values);

        rates.Should().Equal(expected);
    }

    /// <summary>
    ///     Before any sample has been recorded (currentIndex 0) GetRates must return an
    ///     empty array rather than reading stale or out-of-range slots.
    /// </summary>
    [Fact]
    public void GetRates_WithNoSamplesYet_ReturnsEmpty()
    {
        var values = new[] { 10, 20, 30, 40, 50 };

        var rates = RateCounter.GetRates(3, 0, values);

        rates.Should().BeEmpty();
    }

    /// <summary>
    ///     With more increments than buffer slots the index keeps climbing and the read
    ///     must wrap modulo the buffer length, so the newest values still come back in order.
    /// </summary>
    [Fact]
    public void GetRates_WhenIndexHasWrapped_ReadsModuloBufferLength()
    {
        var values = new[] { 10, 20, 30, 40, 50 };

        // currentIndex 7 over a length-5 buffer: read slots 6%5=1, 5%5=0, 4%5=4.
        var rates = RateCounter.GetRates(3, 7, values);

        rates.Should().Equal(20, 10, 50);
    }

    /// <summary>
    ///     When both the requested seconds and the wrapped index exceed the buffer length, the result
    ///     is clamped to the buffer length — without this clamp GetRates would walk back past the ring
    ///     size and re-report the same slots as if they were distinct samples, fabricating history. (M21)
    /// </summary>
    [Fact]
    public void GetRates_WhenSecondsAndIndexExceedBufferLength_ClampsToBufferLength()
    {
        var values = new[] { 10, 20, 30, 40, 50 }; // length 5

        // seconds 8 and currentIndex 9 both exceed the 5-slot buffer; clamp to 5.
        var rates = RateCounter.GetRates(8, 9, values);

        // newest-first from index 9: slots 8%5=3, 7%5=2, 6%5=1, 5%5=0, 4%5=4.
        rates.Should().Equal(40, 30, 20, 10, 50);
    }

    /// <summary>
    ///     The ctor requires a positive averaging window. Zero would give zero-length buffers (a
    ///     swallowed DivideByZeroException on the timer thread, so the counter silently never advances)
    ///     and a negative value an OverflowException at allocation, so both fail fast instead. (M20)
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Ctor_WithNonPositiveSecondsAverage_Throws(int secondsAverage)
    {
        var act = () => new RateCounter(secondsAverage);

        act.Should().Throw<ArgumentOutOfRangeException>().WithParameterName("secondsAverage");
    }

    /// <summary>
    ///     Register must ignore non-positive counts: a negative would wrap the ulong Total into a
    ///     huge value (and diverge from the per-second bucket accounting, producing negative
    ///     rates); zero is a no-op. Only Total is asserted — the shared static timer legitimately
    ///     advances the rate buckets during the test, but it never touches Total. (DE-26)
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(-3)]
    [InlineData(int.MinValue)]
    public void Register_WithNonPositiveCount_DoesNotCorruptTotal(int count)
    {
        var counter = new RateCounter(60);
        counter.Register(5);

        counter.Register(count);

        counter.Total.Should().Be(5UL);
    }

    /// <summary>
    ///     CalcRate divides the summed counts by the summed sample windows; when every window is
    ///     zero (before the timer has moved the clock on) the guard must yield rate 0 rather than
    ///     0/0 = NaN, which would poison every downstream rate display. CalcRate is private and
    ///     reachable only from the timer-driven Increment, so the test drives it directly via
    ///     reflection with the sample windows forced to zero — holding the same _counts lock
    ///     Increment uses, so the shared timer cannot interleave a non-zero window. (DE-26)
    /// </summary>
    [Fact]
    public void CalcRate_WithAllZeroSampleWindows_YieldsZeroRateNotNaN()
    {
        var counter = new RateCounter(5);
        var counts = (int[])CountsField.GetValue(counter)!;
        var times = (TimeSpan[])TimesField.GetValue(counter)!;

        lock (counts)
        {
            Array.Clear(times);
            CalcRateMethod.Invoke(counter, null);
        }

        counter.Rate.Should().Be(0, "an all-zero averaging window must not divide by zero");
    }

    private static readonly FieldInfo CountsField = typeof(RateCounter).GetField(
        "_counts",
        BindingFlags.NonPublic | BindingFlags.Instance
    )!;

    private static readonly FieldInfo TimesField = typeof(RateCounter).GetField(
        "_times",
        BindingFlags.NonPublic | BindingFlags.Instance
    )!;

    private static readonly MethodInfo CalcRateMethod = typeof(RateCounter).GetMethod(
        "CalcRate",
        BindingFlags.NonPublic | BindingFlags.Instance
    )!;
}
