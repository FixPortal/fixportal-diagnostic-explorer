using System.Net;
using System.Reactive.Subjects;
using System.Reflection;
using System.Threading.Channels;
using AwesomeAssertions;
using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.AspNetCore.SignalR.Protocol;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;

namespace DiagnosticExplorer.UnitTests;

public sealed class RegistrationHandlerLoggingTests
{
    [Fact]
    public async Task Stop_FlushesPartialBufferAndQueuedMessagesBeforeDeregistering()
    {
        using CancellationTokenSource ceiling = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        LoggingCase owner = new();
        Exception? primary = null;
        try
        {
            owner.Start();
            await owner.FirstSend.Task.WaitAsync(ceiling.Token);
            owner.Handler.LogEvent(new DiagnosticMsg { MsgId = "tail" });
            owner.StopTask = owner.Handler.Stop();
            await owner.StopCancelled.Task.WaitAsync(ceiling.Token);
            owner.ReleaseFirst.Task.IsCompleted.Should().BeFalse();
            owner.StopTask.IsCompleted.Should().BeFalse("Stop must join its positively held logging send");
            owner.ReleaseFirst.TrySetResult();
            await owner.StopTask.WaitAsync(ceiling.Token);
            Delivery[] deliveries = owner.Deliveries();
            deliveries.Should().HaveCountGreaterThanOrEqualTo(2, "Stop must flush the partial public LogEvent buffer");
            deliveries
                .SelectMany(x => x.Ids)
                .Should()
                .Equal(Enumerable.Range(0, 50).Select(i => "first-" + i).Append("tail"));
            deliveries.SelectMany(x => x.Ids).Should().HaveCount(51);
            deliveries.Skip(1).Should().OnlyContain(x => x.Token == CancellationToken.None);
            owner
                .Events()
                .Should()
                .Equal(
                    Enumerable.Range(1, deliveries.Length).Select(i => "send-" + i + "-completed").Append("deregister")
                );
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await owner.Finish(primary);
        }
    }

    [Fact]
    public async Task Start_CreatesTenThousandBatchChannelThatDropsTheNewestOverflow()
    {
        using CancellationTokenSource ceiling = CancellationTokenSource.CreateLinkedTokenSource(
            TestContext.Current.CancellationToken
        );
        ceiling.CancelAfter(TimeSpan.FromSeconds(30));
        LoggingCase owner = new();
        Exception? primary = null;
        try
        {
            owner.Start();
            await owner.FirstSend.Task.WaitAsync(ceiling.Token);
            // The actual logging reader is inside its positively held first RPC throughout
            // this synchronous interval, so this test is the only active channel reader.
            Channel<IList<DiagnosticMsg>> channel = GetField<Channel<IList<DiagnosticMsg>>>(
                owner.Handler,
                "_logChannel"
            );
            // Finish the unrelated public-message buffer synchronously, disposing its timer,
            // then remove its queued batches before measuring this actual channel alone.
            GetField<ISubject<DiagnosticMsg>>(owner.Handler, "_logSubject").OnCompleted();
            while (channel.Reader.TryRead(out _))
            {
                // Remove only the initial public-message batches before the capacity check.
            }
            for (int i = 0; i <= 10_000; i++)
            {
                channel.Writer.TryWrite(new DiagnosticMsg[] { new() { MsgId = "queued-" + i } });
            }
            List<string> retained = [];
            while (channel.Reader.TryRead(out IList<DiagnosticMsg>? batch))
            {
                retained.Add(batch.Single().MsgId);
            }
            retained.Should().HaveCount(10_000, "the actual Start-created channel must remain bounded");
            retained.Should().Equal(Enumerable.Range(0, 10_000).Select(i => "queued-" + i));
            retained.Should().NotContain("queued-10000", "DropWrite must discard the newest overflow batch");
            owner.ReleaseFirst.Task.IsCompleted.Should().BeFalse();
        }
        catch (Exception error)
        {
            primary = error;
            throw;
        }
        finally
        {
            await owner.Finish(primary);
        }
    }

    private static T GetField<T>(RegistrationHandler handler, string name) =>
        (T)(
            typeof(RegistrationHandler)
                .GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(handler)
            ?? throw new InvalidOperationException("Missing actual field " + name)
        );

    private sealed record Delivery(string[] Ids, CancellationToken Token);

    private sealed class LoggingCase
    {
        private readonly HubConnection _hub;
        private readonly List<Delivery> _deliveries = [];
        private readonly List<string> _events = [];
        private readonly object _sync = new();
        private Task? _registrationTask;
        private Task? _loggingTask;
        private CancellationTokenRegistration _stopRegistration;
        private bool _started;
        public RegistrationHandler Handler { get; } = new("https://unused.invalid/diagnostics", new Registration());
        public TaskCompletionSource FirstSend { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource StopCancelled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Task? StopTask { get; set; }

        public LoggingCase()
        {
            _hub = Substitute.For<HubConnection>(
                Substitute.For<IConnectionFactory>(),
                Substitute.For<IHubProtocol>(),
                new IPEndPoint(IPAddress.Loopback, 5000),
                Substitute.For<IServiceProvider>(),
                NullLoggerFactory.Instance
            );
            _hub.InvokeCoreAsync(Arg.Any<string>(), Arg.Any<Type>(), Arg.Any<object?[]>(), Arg.Any<CancellationToken>())
                .Returns(call => Respond(call.Arg<string>(), call.Arg<object?[]>(), call.Arg<CancellationToken>()));
            Type adapterType = typeof(RegistrationHandler).Assembly.GetType("DiagnosticExplorer.HubServerAdapter")!;
            object adapter = Activator.CreateInstance(adapterType, _hub, null)!;
            typeof(RegistrationHandler)
                .GetField("_hubAdapter", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Handler, adapter);
            typeof(RegistrationHandler)
                .GetField("_connection", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(Handler, _hub);
        }

        public void Start()
        {
            _started = true;
            Handler.Start();
            _registrationTask = GetField<Task>(Handler, "_registrationLoop");
            _loggingTask = GetField<Task>(Handler, "_loggingTask");
            CancellationTokenSource stop = GetField<CancellationTokenSource>(Handler, "_stopToken");
            _stopRegistration = stop.Token.Register(() => StopCancelled.TrySetResult());
            for (int i = 0; i < 50; i++)
            {
                Handler.LogEvent(new DiagnosticMsg { MsgId = "first-" + i });
            }
        }

        private async Task<object?> Respond(string method, object?[] args, CancellationToken token)
        {
            if (method == "Register")
            {
                return RpcResult<RegistrationResponse>.Success(new RegistrationResponse(TimeSpan.FromHours(1)));
            }
            if (method == "Deregister")
            {
                lock (_sync)
                {
                    _events.Add("deregister");
                }
                return RpcResult.Success("deregister");
            }
            if (method != "LogEvents")
            {
                throw new InvalidOperationException("Unexpected RPC " + method);
            }
            int number;
            lock (_sync)
            {
                _deliveries.Add(new Delivery(((DiagnosticMsg[])args[0]!).Select(x => x.MsgId).ToArray(), token));
                number = _deliveries.Count;
            }
            if (number == 1)
            {
                FirstSend.TrySetResult();
                // Deliberately hold the already-entered RPC independently of cancellation.
                await ReleaseFirst.Task;
            }
            lock (_sync)
            {
                _events.Add("send-" + number + "-completed");
            }
            return RpcResult.Success("logged");
        }

        public Delivery[] Deliveries()
        {
            lock (_sync)
            {
                return [.. _deliveries];
            }
        }

        public string[] Events()
        {
            lock (_sync)
            {
                return [.. _events];
            }
        }

        public async Task Finish(Exception? primary)
        {
            List<Exception> failures = [];
            ReleaseFirst.TrySetResult();
            using CancellationTokenSource cleanup = new(TimeSpan.FromSeconds(30));
            if (_started)
            {
                StopTask ??= Handler.Stop();
                await ObserveTasks(new[] { StopTask, _registrationTask, _loggingTask }, cleanup.Token, failures);
            }
            else
            {
                try
                {
                    await _hub.DisposeAsync();
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
            try
            {
                await _stopRegistration.DisposeAsync();
            }
            catch (Exception error)
            {
                failures.Add(error);
            }
            if (failures.Count != 0)
            {
                if (primary is not null)
                {
                    failures.Insert(0, primary);
                }
                throw new AggregateException("DE14CleanupFailure", failures);
            }
        }

        private static async Task ObserveTasks(
            IEnumerable<Task?> tasks,
            CancellationToken cancel,
            List<Exception> failures
        )
        {
            foreach (Task? task in tasks)
            {
                if (task is null)
                {
                    continue;
                }
                try
                {
                    await task.WaitAsync(cancel);
                }
                catch (OperationCanceledException) when (task.IsCanceled)
                {
                    // The actual registration loop may observe Stop's canceled delay.
                }
                catch (Exception error)
                {
                    failures.Add(error);
                }
            }
        }
    }
}
