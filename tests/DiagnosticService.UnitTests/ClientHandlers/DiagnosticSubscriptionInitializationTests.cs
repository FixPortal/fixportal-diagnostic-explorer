using System.Collections.Concurrent;
using AwesomeAssertions;
using Diagnostic.Service.ClientHandlers;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using Microsoft.Extensions.Time.Testing;
using NSubstitute;
using Xunit;

namespace DiagnosticService.UnitTests.ClientHandlers;

/// <summary>
///     A browser's stream snapshot is sent in frames, not as one message.
/// </summary>
/// <remarks>
///     The snapshot is the whole retained window — up to five thousand events by default. Sending
///     it as a single InitializeLogStream is the same shape that broke the agent leg against its
///     receive cap, moved one hop on and multiplied by the number of browsers watching. So the
///     initialization carries the routing and the watermark, and the events follow as
///     StreamLogEvents frames.
/// </remarks>
public sealed class DiagnosticSubscriptionInitializationTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(10);

    [Fact]
    public async Task AddWebClient_WithARetainedWindow_SendsTheSnapshotInFrames()
    {
        RecordingWebHubClient client = new();
        DiagnosticSubscription subscription = new(
            new DiagProcess { Id = "process-1", ProcessName = "Test Process" },
            new FakeTimeProvider()
        );
        WebClientHandler webClient = new("connection-1", client);

        // More than one frame's worth, arriving the way an agent delivers them.
        const int retained = 250;
        var agent = Substitute.For<IDiagnosticClient>();
        subscription.SetDiagnosticClient(agent);
        await subscription.AddWebClient(webClient);

        await client.WaitForSends(1, SignalTimeout);
        client.Reset();

        PublishFromAgent(subscription, retained);
        await subscription.AddWebClient(new WebClientHandler("connection-2", client));

        // 1 initialization + ceil(250/100) frames.
        await client.WaitForSends(4, SignalTimeout);

        var sends = client.Sends;
        sends[0]
            .Should()
            .StartWith(
                "init:",
                "the initialization comes first, or a browser applying it would discard its own events"
            );
        sends.Skip(1).Should().OnlyContain(send => send.StartsWith("events:"));
        client.InitializationReplayCounts.Should().OnlyContain(count => count == 0, "the replay travels as frames");
        client.FrameSizes.Should().OnlyContain(size => size <= 100);
        client.FrameSizes.Sum().Should().Be(retained);

        subscription.RemoveWebClient(webClient);
    }

    /// <summary>Pushes events through the relay the way an agent's frames do.</summary>
    private static void PublishFromAgent(DiagnosticSubscription subscription, int count)
    {
        var relay = typeof(DiagnosticSubscription)
            .GetField(
                "_eventStore",
                System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic
            )!
            .GetValue(subscription)!;
        var merge = relay.GetType().GetMethod("MergeInitialization")!;
        var append = relay.GetType().GetMethod("Append")!;

        merge.Invoke(
            relay,
            [
                new LogStreamInitialization
                {
                    StreamId = "stream-1",
                    Routing = new LogStreamRoutingConfiguration(),
                    ReplayEvents = [],
                    MaxEvents = 10_000,
                    MaxAgeMinutes = 60,
                },
            ]
        );

        _ = append.Invoke(
            relay,
            [
                Enumerable
                    .Range(1, count)
                    .Select(sequence => new LogStreamEvent
                    {
                        StreamId = "stream-1",
                        Sequence = sequence,
                        TimestampUtc = DateTime.UtcNow,
                        LoggerCategory = "App",
                        Level = 2,
                        Message = $"event-{sequence}",
                    })
                    .ToArray(),
            ]
        );
    }

    private sealed class RecordingWebHubClient : IWebHubClient
    {
        private readonly ConcurrentQueue<string> _sends = new();
        private readonly ConcurrentQueue<int> _initializationReplayCounts = new();
        private readonly ConcurrentQueue<int> _frameSizes = new();

        public string[] Sends => [.. _sends];
        public int[] InitializationReplayCounts => [.. _initializationReplayCounts];
        public int[] FrameSizes => [.. _frameSizes];

        public void Reset()
        {
            _sends.Clear();
            _initializationReplayCounts.Clear();
            _frameSizes.Clear();
        }

        public async Task WaitForSends(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow.Add(timeout);
            while (_sends.Count < count)
            {
                DateTime.UtcNow.Should().BeBefore(deadline, "expected {0} sends, saw {1}", count, _sends.Count);
                await Task.Delay(TimeSpan.FromMilliseconds(10), TestContext.Current.CancellationToken);
            }
        }

        public Task InitializeLogStream(string id, LogStreamInitialization initialization)
        {
            // ReSharper disable once ConditionalAccessQualifierIsNonNullableAccordingToAPIContract
            // ReSharper disable once NullCoalescingConditionIsAlwaysNotNullAccordingToAPIContract
            // ReplayEvents has a public setter, and the point of this stub is to record what actually arrived on the wire.
            _initializationReplayCounts.Enqueue(initialization.ReplayEvents?.Length ?? 0);
            _sends.Enqueue($"init:{id}");
            return Task.CompletedTask;
        }

        public Task StreamLogEvents(string id, LogStreamEvent[] events)
        {
            _frameSizes.Enqueue(events.Length);
            _sends.Enqueue($"events:{id}");
            return Task.CompletedTask;
        }

        public Task SetProcesses(DiagProcess[] processes) => Task.CompletedTask;

        public Task UpdateProcess(DiagProcess processes) => Task.CompletedTask;

        public Task RemoveProcess(string id) => Task.CompletedTask;

        public Task ShowDiagnostics(string id, DiagnosticResponse response) => Task.CompletedTask;

        public Task ShowDiagnosticsError(string id, string message) => Task.CompletedTask;

        public Task ProcessSearchResults(RetroSearchResult result) => Task.CompletedTask;

        public Task ProcessSearchEnd(int searchId) => Task.CompletedTask;

        public Task ProcessSearchError(int searchId, string message, string detail) => Task.CompletedTask;
    }
}
