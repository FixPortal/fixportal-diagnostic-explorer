using System.Collections.Concurrent;
using AwesomeAssertions;
using DiagnosticExplorer.Log4Net;
using log4net.Core;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     <see cref="AsyncProcessor" /> runs a bounded queue in front of the forwarding
///     appenders. When the queue is full, Discard mode must drop the incoming event while
///     Block mode must apply back-pressure until space frees up — picking the wrong one
///     either loses logs silently or stalls the host app's logging threads. The tests are
///     event-driven (TaskCompletionSource-gated worker), no sleeps. The timeout-expiry
///     branch of Close is deliberately out of scope: its waits are hardcoded 5s/1s
///     literals, so covering them costs seconds for no signal. (DE-11)
/// </summary>
public class AsyncProcessorTests
{
    private static readonly TimeSpan GuardTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task Append_WhenQueueFullInDiscardMode_DropsIncomingEvent()
    {
        var forwardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<LoggingEvent>();

        using var processor = new AsyncProcessor(
            BufferOverflowMode.Discard,
            bufferSize: 1,
            e =>
            {
                forwardStarted.TrySetResult();
                releaseForward.Task.GetAwaiter().GetResult();
                received.Enqueue(e);
            }
        );
        processor.Start();

        var first = TestLoggingEvents.NewEvent("first");
        var second = TestLoggingEvents.NewEvent("second");
        var third = TestLoggingEvents.NewEvent("third");

        processor.Append(first);
        await forwardStarted.Task.WaitAsync(GuardTimeout, TestContext.Current.CancellationToken); // worker holds "first", queue empty

        processor.Append(second); // fills the size-1 queue
        processor.Append(third); // queue full: must be dropped in Discard mode

        releaseForward.SetResult();
        processor.Close();

        received.Should().HaveCount(2);
        received.Should().Contain(e => ReferenceEquals(e, first));
        received.Should().Contain(e => ReferenceEquals(e, second));
        received.Should().NotContain(e => ReferenceEquals(e, third));
    }

    [Fact]
    public async Task Append_WhenQueueFullInBlockMode_WaitsUntilSpaceFreesUp()
    {
        var forwardStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseForward = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var received = new ConcurrentQueue<LoggingEvent>();

        using var processor = new AsyncProcessor(
            BufferOverflowMode.Block,
            bufferSize: 1,
            e =>
            {
                forwardStarted.TrySetResult();
                releaseForward.Task.GetAwaiter().GetResult();
                received.Enqueue(e);
            }
        );
        processor.Start();

        var first = TestLoggingEvents.NewEvent("first");
        var second = TestLoggingEvents.NewEvent("second");
        var third = TestLoggingEvents.NewEvent("third");

        processor.Append(first);
        await forwardStarted.Task.WaitAsync(GuardTimeout, TestContext.Current.CancellationToken); // worker holds "first", queue empty

        processor.Append(second); // fills the size-1 queue
        Action<LoggingEvent> append = processor.Append;

        var appendStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var blockedAppend = Task.Run(
            () =>
            {
                appendStarted.TrySetResult();
                append(third);
            },
            TestContext.Current.CancellationToken
        );

        // Wait for the append call to have actually started on its own thread — an unscheduled
        // Task also reports IsCompleted == false, which proves nothing. Once appendStarted has
        // fired, the very next statement on that thread is the blocking Add() against a
        // capacity-one queue that is already full (second occupies its only slot), so the call
        // is now genuinely inside the blocked path rather than merely not-yet-scheduled.
        await appendStarted.Task.WaitAsync(GuardTimeout, TestContext.Current.CancellationToken);

        // Nothing can free queue space until the forward is released, so the Append
        // cannot have completed — Block mode is applying back-pressure.
        blockedAppend.IsCompleted.Should().BeFalse();

        releaseForward.SetResult();
        await blockedAppend.WaitAsync(GuardTimeout, TestContext.Current.CancellationToken);
        processor.Close();

        received.Should().HaveCount(3);
        received.Should().Contain(e => ReferenceEquals(e, first));
        received.Should().Contain(e => ReferenceEquals(e, second));
        received.Should().Contain(e => ReferenceEquals(e, third));
    }
}
