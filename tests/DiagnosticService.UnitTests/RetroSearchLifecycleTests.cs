using System.Collections.Concurrent;
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using Diagnostic.Service.Transport;
using DiagnosticExplorer;
using Microsoft.Extensions.Options;
using NSubstitute;
using Xunit;

namespace DiagnosticService.UnitTests;

/// <summary>
///     Retro search lifetime is subtle because searches are tracked per connection and complete on
///     background tasks. These tests pin the cleanup behavior so disconnects and overlapping searches
///     do not leak entries or remove the wrong active search.
/// </summary>
public class RetroSearchLifecycleTests
{
    /// <summary>
    ///     Verifies that finishing the current search removes its tracking entry. This is the core
    ///     leak-prevention behavior the manager needs once a search naturally completes.
    /// </summary>
    [Fact]
    public void HandleSearchFinished_WhenSearchIsCurrent_RemovesTrackedEntry()
    {
        var manager = CreateManager();
        var running = CreateSearch(manager, "conn-1", 1);
        SearchMap(manager)["conn-1"] = running;

        InvokeHandleSearchFinished(manager, running);

        SearchMap(manager).Should().NotContainKey("conn-1");
    }

    /// <summary>
    ///     Verifies that finishing an older search does not remove a newer replacement search for the
    ///     same connection. This protects the restart path from orphaning the active search.
    /// </summary>
    [Fact]
    public void HandleSearchFinished_WhenReplacementSearchExists_KeepsReplacementTracked()
    {
        var manager = CreateManager();
        var original = CreateSearch(manager, "conn-1", 1);
        var replacement = CreateSearch(manager, "conn-1", 2);
        SearchMap(manager)["conn-1"] = replacement;

        InvokeHandleSearchFinished(manager, original);

        SearchMap(manager).Should().ContainKey("conn-1");
        SearchMap(manager)["conn-1"].Should().BeSameAs(replacement);
    }

    /// <summary>
    ///     Verifies that connection-level cancellation removes the tracked search and trips its token.
    ///     This is the cleanup path WebHub disconnect handling needs to call.
    /// </summary>
    [Fact]
    public void CancelConnectionSearch_WhenSearchExists_RemovesItAndCancelsIt()
    {
        var manager = CreateManager();
        var running = CreateSearch(manager, "conn-1", 1);
        SearchMap(manager)["conn-1"] = running;

        manager.CancelConnectionSearch("conn-1");

        SearchMap(manager).Should().NotContainKey("conn-1");
        CancelToken(running).IsCancellationRequested.Should().BeTrue();
    }

    /// <summary>
    ///     Verifies that SendResults still raises Finished when the client error callback itself throws.
    ///     This prevents stuck search entries when a disconnected client cannot receive the error.
    /// </summary>
    [Fact]
    public async Task SendResults_WhenErrorCallbackThrows_StillRaisesFinished()
    {
        var manager = CreateManager();
        var client = Substitute.For<IWebHubClient>();
        client
            .ProcessSearchError(7, Arg.Any<string>(), Arg.Any<string>())
            .Returns(_ => throw new InvalidOperationException("client-gone"));
        RetroSearchProcess process = new(manager, "conn-1", client, new RetroQuery { SearchId = 7 });
        var finishedRaised = false;
        process.Finished += (_, _) => finishedRaised = true;

        var channel = Channel.CreateUnbounded<RetroSearchResult>();
        channel.Writer.Complete(new InvalidOperationException("search-failed"));

        var act = async () => await InvokeSendResults(process, channel);

        await act.Should().ThrowAsync<InvalidOperationException>();
        finishedRaised.Should().BeTrue();
    }

    /// <summary>
    ///     Verifies that SendResults raises Finished when the channel completes without error.
    ///     Complements the error-callback-throws test to confirm the normal completion path also fires Finished.
    /// </summary>
    [Fact]
    public async Task SendResults_WhenChannelCompletesSuccessfully_RaisesFinished()
    {
        var manager = CreateManager();
        var client = Substitute.For<IWebHubClient>();
        RetroSearchProcess process = new(manager, "conn-1", client, new RetroQuery { SearchId = 8 });
        var finishedRaised = false;
        process.Finished += (_, _) => finishedRaised = true;

        var channel = Channel.CreateUnbounded<RetroSearchResult>();
        channel.Writer.Complete();

        var act = async () => await InvokeSendResults(process, channel);

        await act.Should().NotThrowAsync();
        finishedRaised.Should().BeTrue();
    }

    [Fact]
    public async Task StartRetroSearch_WhenSearchCompletes_RemovesTrackedEntry()
    {
        var manager = CreateManager();
        TaskCompletionSource<bool> endEntered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> releaseEnd = new(TaskCreationOptions.RunContinuationsAsynchronously);
        TaskCompletionSource<bool> finished = new(TaskCreationOptions.RunContinuationsAsynchronously);
        RetroSearchProcess? search = null;
        void OnFinished(object? _, EventArgs __) => finished.TrySetResult(true);
        await manager.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            var mockLogger = Substitute.For<IRetroLogger>();
            mockLogger
                .GetMessages(Arg.Any<RetroQuery>(), Arg.Any<CancellationToken>())
                .Returns(GetEmptyAsyncEnumerable());
            SetPrivateField(manager, "_logger", mockLogger);

            var client = Substitute.For<IWebHubClient>();
            client
                .ProcessSearchEnd(123)
                .Returns(async _ =>
                {
                    endEntered.TrySetResult(true);
                    await releaseEnd.Task;
                });
            RetroQuery query = new() { SearchId = 123 };

            await manager.StartRetroSearch(query, "conn-123", client);

            // Hold the actual end callback before Finished, so subscribing cannot miss completion.
            // The manager registered its Finished handler first; our witness follows its cleanup.
            var map = SearchMap(manager);
            search = map["conn-123"];
            search.Finished += OnFinished;
            await endEntered.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
            releaseEnd.TrySetResult(true);
            await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);

            map.Should().NotContainKey("conn-123");
        }
        finally
        {
            releaseEnd.TrySetResult(true);
            try
            {
                if (search is not null)
                {
                    try
                    {
                        await finished.Task.WaitAsync(TimeSpan.FromSeconds(30), CancellationToken.None);
                    }
                    catch (TimeoutException exception)
                    {
                        throw new TimeoutException(
                            "Timed out joining the actual retro search Finished callback during cleanup.",
                            exception
                        );
                    }
                }
            }
            finally
            {
                search?.Finished -= OnFinished;
                await manager.StopAsync(CancellationToken.None);
            }
        }
    }

    [Fact]
    public async Task StopAsync_WhenCalledTwice_DoesNotThrow()
    {
        var manager = CreateManager();
        await manager.StartAsync(TestContext.Current.CancellationToken);

        var act = async () =>
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
            await manager.StopAsync(TestContext.Current.CancellationToken);
        };

        await act.Should().NotThrowAsync();
    }

    private static async IAsyncEnumerable<RetroMsg[]> GetEmptyAsyncEnumerable()
    {
        yield break;
    }

    [Fact]
    public async Task LogEvents_WhenPublishedConcurrently_DoesNotOverlapObserverCallbacks()
    {
        var manager = CreateManager();
        await manager.StartAsync(TestContext.Current.CancellationToken);
        try
        {
            using OverlapDetectingObserver<IList<DiagnosticMsg>> observer = new();
            var field = typeof(RetroManager).GetField("_logSubject", BindingFlags.Instance | BindingFlags.NonPublic)!;
            var subject = (IObservable<IList<DiagnosticMsg>>)field.GetValue(manager)!;

            using var subscription = subject.Subscribe(observer);

            var publishes = StartConcurrentPublishes(
                24,
                index => manager.LogEvents([new DiagnosticMsg { Message = $"msg-{index}" }])
            );

            try
            {
                observer.WaitUntilCallbackEntered(TestContext.Current.CancellationToken);
            }
            finally
            {
                observer.ReleaseCallbacks();
                await Task.WhenAll(publishes);
            }

            observer.OverlapDetected.Should().BeFalse();
            observer.SeenValues.Should().Be(24);
        }
        finally
        {
            await manager.StopAsync(TestContext.Current.CancellationToken);
        }
    }

    private static RetroManager CreateManager()
    {
        DiagServiceSettings settings = new() { RetroType = "mongo", RetroConnection = "mongodb://unused" };
        return new RetroManager(Options.Create(settings));
    }

    private static RetroSearchProcess CreateSearch(RetroManager manager, string connectionId, int searchId)
    {
        return new RetroSearchProcess(
            manager,
            connectionId,
            Substitute.For<IWebHubClient>(),
            new RetroQuery { SearchId = searchId }
        );
    }

    private static ConcurrentDictionary<string, RetroSearchProcess> SearchMap(RetroManager manager)
    {
        var field = typeof(RetroManager).GetField("_searches", BindingFlags.Instance | BindingFlags.NonPublic)!;
        return (ConcurrentDictionary<string, RetroSearchProcess>)field.GetValue(manager)!;
    }

    private static CancellationTokenSource CancelToken(RetroSearchProcess process)
    {
        var field = typeof(RetroSearchProcess).GetField(
            "_cancelToken",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        return (CancellationTokenSource)field.GetValue(process)!;
    }

    private static void InvokeHandleSearchFinished(RetroManager manager, RetroSearchProcess process)
    {
        var method = typeof(RetroManager).GetMethod(
            "HandleSearchFinished",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        method.Invoke(manager, [process, EventArgs.Empty]);
    }

    private static async Task InvokeSendResults(RetroSearchProcess process, Channel<RetroSearchResult> channel)
    {
        var method = typeof(RetroSearchProcess).GetMethod(
            "SendResults",
            BindingFlags.Instance | BindingFlags.NonPublic
        )!;
        var task = (Task)method.Invoke(process, [channel, CancellationToken.None])!;
        await task;
    }

    private static void SetPrivateField(object target, string fieldName, object? value)
    {
        var field = target.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.NonPublic)!;
        field.SetValue(target, value);
    }

    private static Task[] StartConcurrentPublishes(int count, Action<int> publish)
    {
        ManualResetEventSlim start = new(false);
        var tasks = Enumerable
            .Range(0, count)
            .Select(index =>
                Task.Run(() =>
                {
                    start.Wait();
                    publish(index);
                })
            )
            .ToArray();

        start.Set();
        _ = Task.WhenAll(tasks).ContinueWith(_ => start.Dispose(), TaskScheduler.Default);
        return tasks;
    }

    private sealed class OverlapDetectingObserver<T> : IObserver<T>, IDisposable
    {
        private readonly ManualResetEventSlim _callbackEntered = new(false);
        private readonly ManualResetEventSlim _releaseCallbacks = new(false);
        private int _activeNotifications;
        private int _seenValues;

        public bool OverlapDetected { get; private set; }
        public int SeenValues => _seenValues;

        public void Dispose()
        {
            _callbackEntered.Dispose();
            _releaseCallbacks.Dispose();
        }

        public void OnCompleted() { }

        public void OnError(Exception error) { }

        public void OnNext(T value)
        {
            if (Interlocked.Increment(ref _activeNotifications) > 1)
            {
                OverlapDetected = true;
            }

            try
            {
                _callbackEntered.Set();
                _releaseCallbacks.Wait(TimeSpan.FromSeconds(30));
                Interlocked.Increment(ref _seenValues);
            }
            finally
            {
                Interlocked.Decrement(ref _activeNotifications);
            }
        }

        public void WaitUntilCallbackEntered(CancellationToken cancellationToken)
        {
            _callbackEntered.Wait(cancellationToken);
        }

        public void ReleaseCallbacks()
        {
            _releaseCallbacks.Set();
        }
    }
}
