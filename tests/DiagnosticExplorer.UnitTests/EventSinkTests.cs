using AwesomeAssertions;
using Microsoft.Extensions.Time.Testing;

namespace DiagnosticExplorer.UnitTests;

/// <summary>
///     EventSink and EventSinkRepo are the in-process event store the diagnostic UI streams
///     from. Sinks are keyed by name+category and created on demand; logging stamps identity
///     and an incrementing id, and a multi-line message with no detail is split so the list
///     shows a one-line summary with the full text behind it. Tests drive a fresh repo (not the
///     shared Default) so they stay isolated, and assert through the public surface.
/// </summary>
public class EventSinkTests
{
    [Fact]
    public void SystemEvent_UsesInjectedTimeProvider()
    {
        DateTimeOffset now = new(2026, 8, 12, 9, 30, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);

        var systemEvent = new SystemEvent(timeProvider);

        systemEvent.Date.Should().Be(now.UtcDateTime);
    }

    /// <summary>
    ///     Event timestamps come from the repo's injected clock so callers and tests do not
    ///     depend on process wall time.
    /// </summary>
    [Fact]
    public void LogEvent_UsesInjectedTimeProvider()
    {
        DateTimeOffset now = new(2026, 8, 12, 9, 30, 0, TimeSpan.Zero);
        FakeTimeProvider timeProvider = new(now);
        var repo = new EventSinkRepo(timeProvider);

        repo.GetSink("svc", "cat").Info("event");

        repo.GetEvents().Should().ContainSingle().Which.Date.Should().Be(now.UtcDateTime);
    }

    /// <summary>
    ///     GetSink is get-or-create keyed on name+category: the same pair returns the same sink
    ///     (so events accumulate in one place), and a different category yields a distinct sink.
    /// </summary>
    [Fact]
    public void GetSink_SameKeyReturnsSameSink_DifferentCategoryReturnsDistinct()
    {
        var repo = new EventSinkRepo();

        var sink = repo.GetSink("svc", "cat");

        repo.GetSink("svc", "cat").Should().BeSameAs(sink);
        repo.GetSink("svc", "other").Should().NotBeSameAs(sink);
    }

    /// <summary>
    ///     Logging stamps the sink's name/category onto the event and assigns ids that increment
    ///     from one, so the UI can identify origin and order. Confirms the per-sink id counter.
    /// </summary>
    [Fact]
    public void LogEvent_StampsSinkIdentityAndIncrementsIds()
    {
        var repo = new EventSinkRepo();
        var sink = repo.GetSink("svc", "cat");

        sink.Info("first");
        sink.Info("second");

        var events = sink.Events.ToArray();
        events.Select(e => e.Id).Should().Equal(1, 2);
        events.Should().OnlyContain(e => e.SinkName == "svc" && e.SinkCategory == "cat");
    }

    /// <summary>
    ///     When a message has no separate detail but spans multiple lines, the first line becomes
    ///     the message and the whole text becomes the detail — the summary/detail split the event
    ///     list depends on. A single-line message is left untouched (detail stays empty).
    /// </summary>
    [Fact]
    public void LogEvent_MultilineMessageWithNoDetail_SplitsFirstLineIntoMessage()
    {
        var repo = new EventSinkRepo();
        var sink = repo.GetSink("svc", "cat");

        sink.Error("Connection failed\nstack frame 1\nstack frame 2");
        sink.Error("single line");

        var events = sink.Events.ToArray();
        events[0].Message.Should().Be("Connection failed");
        events[0].Detail.Should().Be("Connection failed\nstack frame 1\nstack frame 2");
        events[1].Message.Should().Be("single line");
        events[1].Detail.Should().BeNull();
    }

    /// <summary>
    ///     Repo-level LogEvent routes a pre-built event to the sink named by its own
    ///     SinkName/SinkCategory — the path used when events arrive already addressed (e.g. from
    ///     the log4net appender) rather than via a sink's typed Info/Warn helpers.
    /// </summary>
    [Fact]
    public void RepoLogEvent_RoutesEventToMatchingSink()
    {
        var repo = new EventSinkRepo();
        var evt = new SystemEvent
        {
            SinkName = "svc",
            SinkCategory = "cat",
            Message = "hi",
            Id = 5,
        };

        repo.LogEvent(evt);

        repo.GetSink("svc", "cat").Events.Should().ContainSingle().Which.Should().BeSameAs(evt);
    }

    /// <summary>
    ///     GetEvents aggregates across every sink, which is how the UI gets the full backlog on
    ///     connect. Logging to two distinct sinks must surface both events.
    /// </summary>
    [Fact]
    public void GetEvents_AggregatesAcrossAllSinks()
    {
        var repo = new EventSinkRepo();
        repo.GetSink("svc", "a").Info("one");
        repo.GetSink("svc", "b").Info("two");

        repo.GetEvents().Select(e => e.Message).Should().BeEquivalentTo("one", "two");
    }

    /// <summary>
    ///     AddSingleEvent bounds the queue at MaxMessages by dequeuing the oldest once the cap is
    ///     exceeded — a long-running sink must not grow without bound. Logging past the cap keeps
    ///     exactly MaxMessages events, and the survivors are the most recent: the oldest were
    ///     dropped. (DE-22)
    /// </summary>
    [Fact]
    public void LogEvent_BeyondMaxMessages_QueueStaysAtCapAndDropsOldest()
    {
        var repo = new EventSinkRepo();
        var sink = repo.GetSink("svc", "cat");

        for (var i = 0; i < EventSink.MaxMessages + 100; i++)
        {
            sink.Info($"event {i}");
        }

        var events = sink.Events.ToArray();
        events.Should().HaveCount(EventSink.MaxMessages);
        events[0].Message.Should().Be("event 100");
        events[^1].Message.Should().Be($"event {EventSink.MaxMessages + 99}");
    }

    [Fact]
    public void ConfigureEventRetention_PrunesEachSinkByCount()
    {
        var repo = new EventSinkRepo();
        var sink = repo.GetSink("svc", "cat");
        sink.Info("one");
        sink.Info("two");
        sink.Info("three");

        repo.ConfigureEventRetention(new EventRetentionOptions().WithMaxEventsPerSink(2));

        sink.Events.Select(e => e.Message).Should().Equal("two", "three");
    }

    [Fact]
    public void GetEvents_PrunesExpiredImportedEventsBehindFreshEvents()
    {
        DateTimeOffset now = new(2026, 9, 8, 9, 30, 0, TimeSpan.Zero);
        FakeTimeProvider clock = new(now);
        var repo = new EventSinkRepo(clock);
        repo.ConfigureEventRetention(new EventRetentionOptions().WithMaxAge(TimeSpan.FromMinutes(10)));
        var sink = repo.GetSink("svc", "cat");
        sink.LogEvent(
            new SystemEvent
            {
                Date = now.UtcDateTime,
                Message = "fresh",
                SinkName = "svc",
                SinkCategory = "cat",
            }
        );
        sink.LogEvent(
            new SystemEvent
            {
                Date = now.AddMinutes(-11).UtcDateTime,
                Message = "expired",
                SinkName = "svc",
                SinkCategory = "cat",
            }
        );

        repo.GetEvents().Select(e => e.Message).Should().Equal("fresh");
    }

    [Fact]
    public void CreateSinkStream_DoesNotReplayExpiredEventsFromAQuietSink()
    {
        DateTimeOffset now = new(2026, 9, 8, 9, 30, 0, TimeSpan.Zero);
        FakeTimeProvider clock = new(now);
        var repo = new EventSinkRepo(clock);
        repo.ConfigureEventRetention(new EventRetentionOptions().WithMaxAge(TimeSpan.FromMinutes(10)));
        repo.GetSink("svc", "cat").Info("old");
        clock.Advance(TimeSpan.FromMinutes(11));

        using EventSinkStream stream = repo.CreateSinkStream(TimeSpan.FromSeconds(1), 10);

        stream.InitialEvents.Should().BeEmpty();
    }

    /// <summary>
    ///     Clear() resets the sink set, so the aggregated backlog is empty afterwards and a sink
    ///     requested again is a fresh instance. (M34 — Clear now also takes the stream lock so it
    ///     can't race the CreateSinkStream/GetEvents snapshots; that's a concurrency property not
    ///     asserted here, but this pins the observable reset semantics.)
    /// </summary>
    [Fact]
    public void Clear_EmptiesTheBacklogAndResetsSinks()
    {
        var repo = new EventSinkRepo();
        var original = repo.GetSink("svc", "cat");
        original.Info("one");

        repo.Clear();

        repo.GetEvents().Should().BeEmpty();
        repo.GetSink("svc", "cat").Should().NotBeSameAs(original);
    }
}
