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

using Timer = System.Threading.Timer;

namespace WidgetSample;

//Only public properties with a PropertyAttribute will be exposed
[DiagnosticClass(AttributedPropertiesOnly = true, DeclaringTypeOnly = true)]
public partial class Form1 : Form, INotifyPropertyChanged
{
    private static readonly ILog _gadgetLog = LogManager.GetLogger("Gadgets");
    private static readonly ILog _widgetLog = LogManager.GetLogger("Widgets");
    private static readonly ILog _formLog = LogManager.GetLogger(typeof(Form1));
    private static int _evtCount1;
    private readonly ComponentModelDemo _compModelDemo;
    private readonly Timer _counterTimer;
    private readonly Timer _evtTimer;
    private readonly BindingList<Gadget> _gadgets;
    private readonly Timer _listTestTimer;

    // Cancels the long-running RunScopeTask loop on shutdown so it doesn't outlive the form.
    private readonly CancellationTokenSource _scopeCts = new CancellationTokenSource();
    private readonly Task _scopeTask;
    private readonly Timer _scopeTimer;
    private readonly TimeProvider _timeProvider;
    private readonly BindingList<Widget> _widgets;

    private string _infoText = string.Empty;

    public Form1()
        : this(TimeProvider.System) { }

    internal Form1(TimeProvider timeProvider)
    {
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        WidgetEvents = new RateCounter(5, timeProvider);
        GadgetEvents = new RateCounter(5, timeProvider);
        InitializeComponent();

        string log4net = Path.GetFullPath("log4net.config");
        XmlConfigurator.ConfigureAndWatch(new FileInfo(log4net));

        Trace.Listeners.Add(new TextWriterTraceListener(Console.Error));
        Trace.Listeners.Add(new TextWriterTraceListener(Console.Out));

        /*            _autoLogTask = Task.Run(async () => {
                        while (true)
                        {
                            _formLog.Info($"Auto logging event {DateTime.Now:d MMM yyyy HH:ss:ss.ff}");
                            await Task.Delay(100);
                        }
                    });
        */
        StartDiagnostics();
        FormClosed += StopDiagnostics;

        //Expose the remoting interface
        _gadgets = [];
        _widgets = [];

        gadgetGrid.DataSource = _gadgets;
        widgetGrid.DataSource = _widgets;

        gadgetGrid.RowsRemoved += HandleGadgetRemoved;
        widgetGrid.RowsRemoved += HandleWidgetRemoved;

        UpdateList = [1, 2, 4, 5];

        //RegisterAsync this class with diagnostics
        DiagnosticManager.Register(this, "Main Form", "Form 1");
        _compModelDemo = new ComponentModelDemo();
        //			DiagnosticManager.RegisterAsync(_compModelDemo, "Simple Demo", "ComponentModel");
        //			SendInitial();
        _evtTimer = new Timer(SendEvents, null, 1000, 1000);
        _counterTimer = new Timer(IncrementCount, null, 400, 400);
        _listTestTimer = new Timer(MungeNumbersList, null, 100, 100);

        txtContent.DataBindings.Add("Text", this, "InfoText", false, DataSourceUpdateMode.OnPropertyChanged);

        _scopeTimer = new Timer(x => DoScopeTimerCode(), null, 500, 500);
        _scopeTask = RunScopeTask(_scopeCts.Token);
    }

    [ExtendedProperty]
    public Widget? NullWidget => null;

    [CollectionProperty(CollectionMode.List, Category = "Numbers")]
    public List<int> UpdateList { get; }

    [Property(Category = "Gadgets", Description = "Max Gadget Id")]
    public int GadgetIdCount { get; private set; }

    [Property(Category = "Widgets")]
    public int WidgetIdCount { get; private set; }

    [Property(AllowSet = true)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public string InfoText
    {
        get => _infoText;
        set
        {
            _infoText = value;
            if (PropertyChanged != null)
            {
                if (!IsDisposed && IsHandleCreated && InvokeRequired)
                {
                    try
                    {
                        BeginInvoke(
                            new Action(() =>
                            {
                                if (!IsDisposed)
                                {
                                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("InfoText"));
                                }
                            })
                        );
                    }
                    catch (InvalidOperationException)
                    {
                        // Ignore if handle is destroyed concurrently during shutdown
                    }
                }
                else
                {
                    PropertyChanged?.Invoke(this, new PropertyChangedEventArgs("InfoText"));
                }
            }
        }
    }

    [Property(AllowSet = true)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int SetMePlease { get; set; }

    [Property(AllowSet = false)]
    [DesignerSerializationVisibility(DesignerSerializationVisibility.Hidden)]
    public int Counter2 { get; set; }

    [RateProperty(Category = "Widgets", ExposeRate = false, ExposeTotal = true)]
    public RateCounter WidgetEvents { get; }

    [RateProperty(Category = "Gadgets", ExposeTotal = true, Description = "The rate of gadget events received")]
    public RateCounter GadgetEvents { get; }

    [CollectionProperty(CollectionMode.List, Category = "All Gadgets")]
    public IList<Gadget> Gadgets
    {
        get
        {
            if (!IsDisposed && IsHandleCreated && InvokeRequired)
            {
                try
                {
                    return (IList<Gadget>)Invoke((Delegate)new Func<IList<Gadget>>(() => _gadgets.ToList()));
                }
                catch
                {
                    return [];
                }
            }

            return _gadgets;
        }
    }

    [CollectionProperty(CollectionMode.Categories, CategoryProperty = nameof(Widget.FullName))]
    public IList<Widget> Widgets
    {
        get
        {
            if (!IsDisposed && IsHandleCreated && InvokeRequired)
            {
                try
                {
                    return (IList<Widget>)Invoke((Delegate)new Func<IList<Widget>>(() => _widgets.ToList()));
                }
                catch
                {
                    return [];
                }
            }

            return _widgets;
        }
    }

    private static void StartDiagnostics()
    {
        string? diagnosticExplorerUri = ConfigurationManager.AppSettings.Get("DiagnosticExplorerUri");
        Debug.WriteLine($"Starting diagnostics with DiagnosticExplorerUri='{diagnosticExplorerUri ?? "(null)"}'");
        DiagnosticHostingService.Start(diagnosticExplorerUri);
    }

    private void StopDiagnostics(object? sender, EventArgs e)
    {
        Debug.WriteLine("Calling StopDiagnostics");

        // DiagnosticHostingService.Stop() is async; firing it and forgetting let the form
        // (and possibly the process) tear down while connections were still being closed.
        // Block on completion, but run it on the thread pool so awaiting it can't deadlock
        // against the UI synchronization context during shutdown.
        Task.Run(() => DiagnosticHostingService.Stop()).GetAwaiter().GetResult();
    }

    protected override void OnFormClosed(FormClosedEventArgs e)
    {
        // Tear down the background timers and the scope loop so they don't keep firing
        // (and touching disposed WinForms controls) after the form is gone.
        _scopeCts.Cancel();
        _evtTimer?.Dispose();
        _counterTimer?.Dispose();
        _listTestTimer?.Dispose();
        _scopeTimer?.Dispose();

        base.OnFormClosed(e);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void MungeNumbersList(object? o)
    {
        try
        {
            for (var i = 0; i < 10; i++)
            {
                for (var j = 0; j < 10; j++)
                {
                    UpdateList.Add(ThreadSafeRandom.Next(100));
                }

                int removeCount = Math.Min(10, UpdateList.Count);
                for (var j = 0; j < removeCount; j++)
                {
                    UpdateList.RemoveAt(0);
                }
            }
        }
        catch (Exception ex)
        {
            _formLog.Error(ex);
        }
    }

    private void IncrementCount(object? o)
    {
        SetMePlease++;
        Counter2++;
    }

    [DiagnosticMethod]
    public void SayHelloAsync(string caption, string message)
    {
        if (message == "throw")
        {
            throw new ArgumentException("Ok, I'll throw");
        }

        void sayHello() => MessageBox.Show(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);

        BeginInvoke(sayHello);
    }

    [DiagnosticMethod]
    public string SayHelloSync(string caption, string message)
    {
        if (message == "throw")
        {
            throw new ArgumentException("Ok, I'll throw");
        }

        Stopwatch watch = Stopwatch.StartNew();

        void sayHello() => MessageBox.Show(this, message, caption, MessageBoxButtons.OK, MessageBoxIcon.Asterisk);

        Invoke(sayHello);
        return string.Format("User clicked Ok in {0:N1} seconds", watch.Elapsed.TotalSeconds);
    }

    [DiagnosticMethod]
    public string LogLotsOfStuff(
        string msg1,
        string msg2,
        string msg3,
        string msg4,
        string msg5,
        string msg6,
        string msg7
    )
    {
        string[] vals = { msg1, msg2, msg3, msg4, msg5, msg6, msg7 };
        string[] toLog = vals.Where(x => !string.IsNullOrEmpty(x)).ToArray();
        foreach (var msg in toLog)
        {
            _formLog.Info(msg);
        }

        return string.Format("Logged {0}/{1} messages", toLog.Length, vals.Length);
    }

    [DiagnosticMethod]
    public int GetRandomInt1()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt2()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt3()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt4()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt5()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt6()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt7()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public int GetRandomInt8()
    {
        return ThreadSafeRandom.Next();
    }

    [DiagnosticMethod]
    public string RandomText()
    {
        return string.Join(
            Environment.NewLine,
            Enumerable.Range(1, ThreadSafeRandom.Next(5, 100)).Select(_ => RandomLine()).ToArray()
        );
    }

    [DiagnosticMethod]
    public string RandomWord()
    {
        return new string(
            Enumerable
                .Range(1, ThreadSafeRandom.Next(1, 10))
                .Select(_ => ThreadSafeRandom.Next(0, 26))
                .Select(x => (char)('A' + (char)x))
                .ToArray()
        );
    }

    [DiagnosticMethod]
    public string RandomLine()
    {
        return string.Join(" ", Enumerable.Range(1, ThreadSafeRandom.Next(1, 50)).Select(_ => RandomWord()).ToArray());
    }

    private void SendEvents(object? o)
    {
        // Runs on a System.Threading.Timer thread. Reading control state (chk*.Checked)
        // off the UI thread is illegal, so marshal the read across. Guard against the
        // window handle having been destroyed during shutdown.
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            BeginInvoke(new Action(SendEventsCore));
        }
        catch (ObjectDisposedException)
        {
            // Handle destroyed and form disposed.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between the check above and the BeginInvoke - the form is
            // closing, so there is nothing to do.
        }
    }

    private void SendEventsCore()
    {
        if (chkSystem.Checked)
        {
            _ = RunScopedTraceExampleAsync(_formLog, $"Form Trace Scope {_evtCount1++}");
        }

        if (chkWidgets.Checked)
        {
            _ = RunScopedTraceExampleAsync(_widgetLog, $"Widget Trace Scope {_evtCount1++}");
        }

        if (chkGadgets.Checked)
        {
            _ = RunScopedTraceExampleAsync(_gadgetLog, $"Gadget Trace Scope {_evtCount1++}");
        }
    }

    private void SendInitial()
    {
        for (var i = 0; i < 10; i++)
        {
            _ = RunScopedTraceExampleAsync(_formLog, $"Form Trace Scope {_evtCount1++}");

            _ = RunScopedTraceExampleAsync(_widgetLog, $"Widget Trace Scope {_evtCount1++}");

            _ = RunScopedTraceExampleAsync(_gadgetLog, $"Gadget Trace Scope {_evtCount1++}");
        }
    }

    private Task RunScopedTraceExampleAsync(ILog log, string message)
    {
        return Task.Run(async () =>
        {
            try
            {
                using var scope = new TraceScope(_timeProvider, nameof(RunScopedTraceExampleAsync), log.Info);
                TraceScope.Trace(message);
                await TraceScopeExample.TestTraceScope1();
            }
            catch (Exception ex)
            {
                log.Error(ex);
            }
        });
    }

    private void HandleGadgetRemoved(object? sender, DataGridViewRowsRemovedEventArgs e)
    {
        _gadgetLog.Info("A gadget was removed");
        _formLog.Info("Form1 removed a gadget");
        GadgetEvents.Register(1);
    }

    private void HandleWidgetRemoved(object? sender, DataGridViewRowsRemovedEventArgs e)
    {
        _widgetLog.Info("A widget was removed");
        _formLog.Info("Form1 removed a widget");
        WidgetEvents.Register(1);
    }

    private void bAddGadget_Click(object sender, EventArgs e)
    {
        var gadget = new Gadget(GadgetIdCount++);
        _gadgets.Add(gadget);
        _gadgetLog.InfoFormat("Added gadget {0}", gadget.Id);
        _formLog.Info("Form1 added a gadget");
        GadgetEvents.Register(1);
    }

    private void bAddWidget_Click(object sender, EventArgs e)
    {
        var widget = new Widget(WidgetIdCount++, _timeProvider);
        _widgets.Add(widget);
        _widgetLog.InfoFormat("Added widget {0}", widget.Id);
        _formLog.Info("Form1 added a widget");
        WidgetEvents.Register(1);
    }

    private void bMinorProblem_Click(object sender, EventArgs e)
    {
        try
        {
            var hello = "Hello";
            var name = hello.Substring(6, 10);
            Debug.WriteLine("The name is " + name);
        }
        catch (Exception ex)
        {
            var msg = "Info something went a little wrong.";
            _formLog.Info(msg, ex);
            // MessageBox.Show(this, msg + "  Check diagnostics for a full stack trace.");
        }
    }

    private void bNotice_Click(object sender, EventArgs e)
    {
        try
        {
            var hello = "Hello";
            var name = hello.Substring(6, 10);
            Debug.WriteLine("The name is " + name);
        }
        catch (Exception ex)
        {
            var msg = "Notice something went a little wrong.";
            _formLog.Warn(msg, ex);
            // MessageBox.Show(this, msg + "  Check diagnostics for a full stack trace.");
        }
    }

    private void bWarn_Click(object sender, EventArgs e)
    {
        try
        {
            var hello = "Hello";
            var name = hello.Substring(6, 10);
            Debug.WriteLine("The name is " + name);
        }
        catch (Exception ex)
        {
            var msg = "Warn something went a little wrong.";
            _formLog.Warn(msg, ex);
            // MessageBox.Show(this, msg + "  Check diagnostics for a full stack trace.");
        }
    }

    private void bHorrificException_Click(object sender, EventArgs e)
    {
        try
        {
            decimal div = 0;
            var result = 12.345M / div;
            Debug.WriteLine("The result is " + result);
        }
        catch (Exception ex)
        {
            var msg = "OMFG the whole app just went BOOM";
            _formLog.Error(msg, ex);
            // MessageBox.Show(this, msg + ".  Check diagnostics for a full stack trace.");
        }
    }

    private void bRemoveGadget_Click(object sender, EventArgs e)
    {
        RemoveItem(_gadgets, _gadgetLog);
    }

    private void bRemoveWidget_Click(object sender, EventArgs e)
    {
        RemoveItem(_widgets, _widgetLog);
    }

    private void RemoveItem<T>(BindingList<T> items, ILog log)
    {
        try
        {
            if (items.Count == 0)
            {
                return;
            }

            var index = ThreadSafeRandom.Next(0, items.Count);
            T item = items[index];
            items.RemoveAt(index);

            // Dispose the removed item so it unregisters from diagnostics (Widget.Dispose calls
            // DiagnosticManager.Unregister). Without this a removed widget stays registered and
            // visible in the diagnostics view, mis-teaching the registration/disposal contract. (M50)
            (item as IDisposable)?.Dispose();
        }
        catch (Exception ex)
        {
            log.Error(ex);
            // MessageBox.Show(this, "Error removing item, check diagnostics for more details.");
        }
    }

    private async void btnTraceScope_Click(object sender, EventArgs e)
    {
        using (new TraceScope(_timeProvider, nameof(btnTraceScope_Click), _formLog.Info))
        {
            TraceScope.Trace($"In Trace Scope Button Click 1 InvokeRequired: {InvokeRequired}");

            Task task1 = Task.Run(async () =>
            {
                await Task.Delay(100);
                TraceScope.Trace("In the async bit A1");

                await TraceScopeExample.TestTraceScope1();

                await Task.Delay(100);
                TraceScope.Trace("In the async bit A2");
            });

            Task task2 = Task.Run(async () =>
            {
                await Task.Delay(100);
                TraceScope.Trace("In the async bit B1");
                await Task.Delay(100);
                TraceScope.Trace("In the async bit B2");
            });

            await task1;
            await task2;

            await Task.Delay(1000);
            // await TraceScopeExample.TestTraceScope1();
        }

        // MessageBox.Show("Just generated a trace scope.  Check diagnostics.");
    }

    private async void btnTestTraceScope2_Click(object sender, EventArgs e)
    {
        using var scope = new TraceScope(_timeProvider, "UI_ACTION_RoutingModel_SendAll", _formLog.Info, true);

        TraceScope.Trace($"In Trace Scope Button Click 2 InvokeRequired: {InvokeRequired}");
        // await TraceScopeExample.TestTraceScope1();

        Report("Starting");

        await Task.Delay(100);
        Report("In the async bit A");

        using SemaphoreSlim throttle = new SemaphoreSlim(3);
        IEnumerable<Task> parallelTasks = Enumerable
            .Range(1, 20)
            .Select(async x =>
            {
                await throttle.WaitAsync();
                try
                {
                    List<string> ids = [Task.CurrentId?.ToString() ?? "X"];
                    using var scope2 = new TraceScope(_timeProvider, "Doing the parallel bit", null);
                    Report($"Parallel...{x}...A");
                    ids.Add(Task.CurrentId?.ToString() ?? "X");
                    await Task.Delay(100);
                    ids.Add(Task.CurrentId?.ToString() ?? "X");

                    await Task.Run(async () =>
                    {
                        ids.Add(Task.CurrentId?.ToString() ?? "X");
                        Report($"Inner task {x} Q");
                        await Task.Delay(100);
                        ids.Add(Task.CurrentId?.ToString() ?? "X");
                        Report($"Inner task {x} W");
                    });

                    ids.Add(Task.CurrentId?.ToString() ?? "X");
                    Report($"Parallel...{x}...B [" + string.Join(", ", ids) + "]");
                }
                finally
                {
                    throttle.Release();
                }
            });

        await Task.WhenAll(parallelTasks);

        await Task.Delay(100);
        Report("In the async bit B");
        Report("Finished");

        await Task.Delay(1000);
    }

    private static void Report(string message)
    {
        TraceScope.Trace($"REPORT {Task.CurrentId} {message}");
        Trace.WriteLine($"REPORT {Task.CurrentId} {message}");
    }

    private async Task RunScopeTask(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            // using (var scope = new TraceScope("SYNC BLAH 1"))
            {
                var message =
                    $"###$%###$%###$%###$%###$%###$%###$%###$%###$%###$% SCOPE TASK {InvokeRequired} {_timeProvider.GetLocalNow():d MMM yyyy HH:mm:ss} ###$%###$%###$%###$%###$%###$%###$%###$%###$%###$%";
                TraceScope.Trace(message);
            }
            // using (var scope = new AsyncTraceScope("ASYNC BLAH 1"))
            {
                var message =
                    $"###$%###$%###$%###$%###$%###$%###$%###$%###$%###$% SCOPE TASK {InvokeRequired} {_timeProvider.GetLocalNow():d MMM yyyy HH:mm:ss} ###$%###$%###$%###$%###$%###$%###$%###$%###$%###$%";
                TraceScope.Trace(message);
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (TaskCanceledException)
            {
                break;
            }
        }
    }

    private void DoScopeTimerCode()
    {
        // Runs on a System.Threading.Timer thread. Timer.Dispose() is async and does not
        // wait for in-flight callbacks, so the callback can race form teardown.
        if (!IsHandleCreated)
        {
            return;
        }

        try
        {
            Invoke(() =>
            {
                using (new TraceScope(_timeProvider, "SYNC BLAH 2", null))
                {
                    var message = $"SCOPE TIMER {InvokeRequired} {_timeProvider.GetLocalNow():d MMM yyyy HH:mm:ss}";
                    TraceScope.Trace(message);
                }
            });
            Invoke(() =>
            {
                using (new TraceScope(_timeProvider, "ASYNC BLAH 2", null))
                {
                    var message =
                        $"<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$% SCOPE TIMER {InvokeRequired} {_timeProvider.GetLocalNow():d MMM yyyy HH:mm:ss} <EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$%<EFBFBD>$% ";
                    TraceScope.Trace(message);
                }
            });
        }
        catch (ObjectDisposedException)
        {
            // Handle destroyed and form disposed.
        }
        catch (InvalidOperationException)
        {
            // Handle destroyed between the IsHandleCreated check and Invoke - form is closing.
        }
    }

    private void btn10_Click(object sender, EventArgs e)
    {
        GenerateEvents(10);
    }

    private void btn100_Click(object sender, EventArgs e)
    {
        GenerateEvents(100);
    }

    private void btn1000_Click(object sender, EventArgs e)
    {
        GenerateEvents(1000);
    }

    private async void GenerateEvents(int count)
    {
        Cursor = Cursors.WaitCursor;
        try
        {
            Stopwatch watch = Stopwatch.StartNew();
            await Task.Run(() =>
            {
                for (var i = 0; i < count; i++)
                {
                    LoggingEventData data = new()
                    {
                        Message = $"Event #{i}",
                        Level = IntToLevel(ThreadSafeRandom.Next(1, 12) * 10000),
                        LoggerName = _formLog.Logger.Name,
                        TimeStampUtc = _timeProvider.GetUtcNow().UtcDateTime,
                    };

                    _formLog.Logger.Log(new LoggingEvent(data));
                    // await Task.Delay(TimeSpan.FromMilliseconds(5));
                }
            });

            Debug.WriteLine($"Send {count} messages took {watch.ElapsedMilliseconds}ms");
        }
        finally
        {
            Cursor = DefaultCursor;
        }
    }

    private void btnStartHosting_Click(object sender, EventArgs e)
    {
        try
        {
            StartDiagnostics();
        }
        catch (InvalidOperationException ex)
        {
            MessageBox.Show(ex.Message, "Hosting Service", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private static Level IntToLevel(int value)
    {
        if (value >= Level.Emergency.Value)
        {
            return Level.Emergency;
        }

        if (value >= Level.Fatal.Value)
        {
            return Level.Fatal;
        }

        if (value >= Level.Alert.Value)
        {
            return Level.Alert;
        }

        if (value >= Level.Critical.Value)
        {
            return Level.Critical;
        }

        if (value >= Level.Severe.Value)
        {
            return Level.Severe;
        }

        if (value >= Level.Error.Value)
        {
            return Level.Error;
        }

        if (value >= Level.Warn.Value)
        {
            return Level.Warn;
        }

        if (value >= Level.Notice.Value)
        {
            return Level.Notice;
        }

        if (value >= Level.Info.Value)
        {
            return Level.Info;
        }

        if (value >= Level.Debug.Value)
        {
            return Level.Debug;
        }

        if (value >= Level.Trace.Value)
        {
            return Level.Trace;
        }

        return Level.Verbose;
    }
}
