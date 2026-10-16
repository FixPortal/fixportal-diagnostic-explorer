using System.Reactive.Subjects;
using System.Reflection;
using AwesomeAssertions;
using Diagnostic.Service.Common;
using Diagnostic.Service.Hubs;
using DiagnosticExplorer;
using Microsoft.Extensions.Options;
using Xunit;

namespace DiagnosticService.UnitTests;

public sealed class RetroManagerConcurrentStopTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ConcurrentStops_DoNotCompleteAnOwnedSubjectAfterAnotherStopDisposesIt()
    {
        using Subject<IList<DiagnosticMsg>> owned = new();
        using var forwarding = new GatedCompletionSubject(owned);
        RetroManager manager = SeedManager(owned, forwarding);
        Task first = Task.Run(() => manager.StopAsync(CancellationToken.None), TestContext.Current.CancellationToken);
        Task? second = null;
        bool secondCompletedBeforeRelease = false;
        bool ownedDisposedBeforeRelease = false;
        bool subjectClearedBeforeRelease = false;
        Exception? setupFailure = null;
        try
        {
            await forwarding.FirstEntered.Task.WaitAsync(
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken
            );
            // Calling directly executes the actual StopAsync synchronous prefix. Do not await
            // its task until after releasing the first call: a safe shared-stop implementation
            // may return the in-flight first task instead of doing a second teardown.
            second = manager.StopAsync(CancellationToken.None);
            secondCompletedBeforeRelease = second.IsCompletedSuccessfully;
            ownedDisposedBeforeRelease = owned.IsDisposed;
            subjectClearedBeforeRelease = ReadField(manager, "_logSubject") is null;
        }
        catch (Exception exception)
        {
            setupFailure = exception;
        }
        finally
        {
            forwarding.ReleaseFirst();
        }

        Exception? secondFailure = second is null ? null : await CaptureAsync(second);
        Exception? firstFailure = await CaptureAsync(first);
        setupFailure.Should().BeNull("the controlled completion arrangement must be established and both tasks joined");
        output.WriteLine(
            $"Stop witness: secondCompletedBeforeRelease={secondCompletedBeforeRelease}; ownedDisposedBeforeRelease={ownedDisposedBeforeRelease}; subjectClearedBeforeRelease={subjectClearedBeforeRelease}; completionCalls={forwarding.CompletionCalls}; firstSawDisposed={forwarding.FirstSawDisposed}; firstFault={firstFailure?.GetType().FullName}; secondFault={secondFailure?.GetType().FullName}"
        );
        secondFailure.Should().BeNull("the independent second actual StopAsync completed normally");
        if (firstFailure is ObjectDisposedException)
        {
            // Prove the observed failure is the real owned Rx subject's lifetime race rather
            // than a gate timeout or an exception manufactured by the forwarding fixture.
            secondCompletedBeforeRelease.Should().BeTrue();
            ownedDisposedBeforeRelease.Should().BeTrue();
            subjectClearedBeforeRelease.Should().BeTrue();
            forwarding.CompletionCalls.Should().Be(2);
            forwarding.FirstSawDisposed.Should().BeTrue();
        }
        firstFailure
            .Should()
            .BeNull("overlapping actual StopAsync calls must not complete an already-disposed owned subject");
        first.IsCompletedSuccessfully.Should().BeTrue();
        second.Should().NotBeNull();
        second.IsCompletedSuccessfully.Should().BeTrue();
        forwarding.CompletionCalls.Should().Be(1, "only one caller owns completion and disposal");
        owned.IsDisposed.Should().BeTrue();
        ReadField(manager, "_logSubject").Should().BeNull();
        ReadField(manager, "_ownedLogSubject").Should().BeNull();
    }

    [Fact]
    public async Task SequentialStops_WithAnOwnedSubject_CompleteWithoutDisposalError()
    {
        using Subject<IList<DiagnosticMsg>> owned = new();
        RetroManager manager = SeedManager(owned, Subject.Synchronize(owned));
        Exception? first = await CaptureAsync(manager.StopAsync(CancellationToken.None));
        Exception? second = await CaptureAsync(manager.StopAsync(CancellationToken.None));
        first.Should().BeNull();
        second.Should().BeNull();
        owned.IsDisposed.Should().BeTrue();
        ReadField(manager, "_logSubject").Should().BeNull();
        ReadField(manager, "_ownedLogSubject").Should().BeNull();
    }

    [Fact]
    public async Task StopAsync_WithAnInFlightDrain_EachCallerAwaitsTheDrain()
    {
        using Subject<IList<DiagnosticMsg>> owned = new();
        RetroManager manager = SeedManager(owned, Subject.Synchronize(owned));
        TaskCompletionSource<bool> drain = new(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(RetroManager)
            .GetField("_loggingTask", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, drain.Task);
        Task first = manager.StopAsync(CancellationToken.None);
        Task second = manager.StopAsync(CancellationToken.None);
        try
        {
            first.IsCompleted.Should().BeFalse("the owned logging task has not completed");
            second.IsCompleted.Should().BeFalse("a second caller must await the same in-flight drain");
        }
        finally
        {
            drain.TrySetResult(true);
            await Task.WhenAll(first, second)
                .WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken);
        }
        first.IsCompletedSuccessfully.Should().BeTrue();
        second.IsCompletedSuccessfully.Should().BeTrue();
    }

    private static ValueTask<Exception?> CaptureAsync(Task task) =>
        Record.ExceptionAsync(async () =>
            await task.WaitAsync(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
        );

    private static RetroManager SeedManager(
        Subject<IList<DiagnosticMsg>> owned,
        ISubject<IList<DiagnosticMsg>> forwarding
    )
    {
        // Seed only the real post-Start subject ownership shape touched before Stop's first
        // await. Starting the logger/channel is unnecessary and would contact default Mongo.
        RetroManager manager = new(Options.Create(new DiagServiceSettings()));
        typeof(RetroManager)
            .GetField("_ownedLogSubject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, owned);
        typeof(RetroManager)
            .GetField("_logSubject", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(manager, forwarding);
        return manager;
    }

    private static object? ReadField(RetroManager manager, string name) =>
        typeof(RetroManager).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(manager);

    private sealed class GatedCompletionSubject(Subject<IList<DiagnosticMsg>> inner)
        : ISubject<IList<DiagnosticMsg>>,
            IDisposable
    {
        private readonly ManualResetEventSlim _release = new();
        private int _completionCalls;
        public TaskCompletionSource<bool> FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CompletionCalls => Volatile.Read(ref _completionCalls);
        public bool FirstSawDisposed { get; private set; }

        public void OnCompleted()
        {
            if (Interlocked.Increment(ref _completionCalls) == 1)
            {
                FirstEntered.TrySetResult(true);
                if (!_release.Wait(TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken))
                {
                    throw new TimeoutException("The owned first-completion gate was not released.");
                }
                FirstSawDisposed = inner.IsDisposed;
            }
            inner.OnCompleted();
        }

        public void ReleaseFirst() => _release.Set();

        public void OnNext(IList<DiagnosticMsg> value) => inner.OnNext(value);

        public void OnError(Exception error) => inner.OnError(error);

        public IDisposable Subscribe(IObserver<IList<DiagnosticMsg>> observer) => inner.Subscribe(observer);

        public void Dispose() => _release.Dispose();
    }
}
