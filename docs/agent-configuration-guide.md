# Configure an application for this fork

Reference `DiagnosticExplorer.Hosting` and the adapter for your logging framework:
`DiagnosticExplorer.Extensions.Logging`, `DiagnosticExplorer.NLog`, or
`DiagnosticExplorer.Serilog`. log4net's `RoutingDiagnosticAppender` stays in the
`DiagnosticExplorer` assembly; there is no separate log4net package in this fork.
The projects target `net10.0` and `net48`, with package version `4.0.0`.

## Configure before creating loggers

```csharp
using DiagnosticExplorer;
using DiagnosticExplorer.Logging;
using Microsoft.Extensions.Logging;

DiagnosticManager.Configure(diagnostics =>
{
    diagnostics.ConfigureEventRouting(routes => routes
        .UseMatchMode(EventSinkRouteMatchMode.AllMatches)
        .Route("MyApp.Workers", route => route
            .AtLeast(LogLevel.Information).To("Workers", "Events"))
        .Route("*", route => route.AtLeast(LogLevel.Error).To("System", "Errors")));
    diagnostics.ConfigureLogEventRetention(retention => retention
        .WithMaxEvents(5000).WithMaxAge(TimeSpan.FromMinutes(5)));
    diagnostics.ConfigureHosting(hosting => hosting.EventRetention(retention => retention
        .WithMaxEventsPerSink(1000).WithMaxAge(TimeSpan.FromMinutes(30))));
});
```

Log-stream retention governs the unified realtime feed. Legacy event retention
governs `EventSinkRepo` and its individual `EventSink` queues. They are separate
stores. Legacy retention is applied during publication, reconfiguration and
snapshot reads; a quiet process does not need a background purge timer to return
a fresh snapshot. Do not treat the public raw event queue as a retention-aware
snapshot API; use `GetEvents()` or `CreateSinkStream()`.

Configuration is a startup boundary. Configure routes before building adapters.
Explicit routes supplied to an adapter are a snapshot, so re-create that adapter
to change its filtering rules. Keep factories, sinks, targets and appenders alive
for the lifetime of the host, then dispose/close them with the host.

## Register logging

The parameterless convenience methods use
`DiagnosticManager.CurrentConfiguration.RuntimeOptions.Routing`:

```csharp
using DiagnosticExplorer.Extensions.Logging;
using Microsoft.Extensions.Logging;

using var factory = LoggerFactory.Create(builder => builder.AddDiagnosticExplorer());
factory.CreateLogger("MyApp.Workers.Job").LogInformation("Job started");
```

```csharp
using DiagnosticExplorer.NLog;
using NLog;
using NLog.Config;

using var factory = new LogFactory();
factory.Configuration = new LoggingConfiguration().AddDiagnosticExplorer();
factory.GetLogger("MyApp.Workers.Job").Info("Job started");
```

```csharp
using DiagnosticExplorer.Serilog;
using Serilog;

using var logger = new LoggerConfiguration().WriteTo.DiagnosticExplorer().CreateLogger();
logger.ForContext("SourceContext", "MyApp.Workers.Job").Information("Job started");
```

```csharp
using DiagnosticExplorer.Log4Net;
using log4net;

LogManager.GetRepository().ConfigureDiagnosticExplorer();
LogManager.GetLogger("MyApp.Workers.Job").Info("Job started");
```

The log4net helper retains the host's existing appenders and levels. Normal
log4net threshold/filter rules still apply; the helper does not switch the host
to `Level.All`. Shut down the repository with the application.

Explicit-options overloads remain available. Where an API accepts a `LogEventStore`,
it can target an isolated stream for a test or independent host component. The
[runnable sample](../samples/Logging/README.md) checks all four framework paths.

## Shared routing contract

`ConfigureEventRouting` defines the base table. Each live adapter contributes its
own routes to the store. Base routes come first, then adapters in registration
order, retaining each adapter's local ordering. The emitted snapshot has a
consistent global order. Disposing an adapter retracts its contribution and
preserves the base table and other adapters.

All nonempty contributions must use the same `MatchMode`. Conflicting modes are
rejected before changing the store or notifying live subscribers. Choose one
mode for the process; do not rely on whichever adapter happens to start last.
Routing changes notify subscribers through the existing stream restart/replay
mechanism. `FirstMatch`, `MostSpecific` and stop-processing apply to the combined
table when the viewer projects events into destinations. Each adapter first
filters incoming events using its own routes and publishes a matching event once.

The configured base table can deliberately retain a destination after an adapter
closes. That lets retained events still be projected. With explicit per-adapter
routes and no base entry, closing the last contributing adapter removes those
routes from the live snapshot.

## Register diagnostic roots

Use `RegisterObjects` to enumerate the objects currently owned by the application.
The callback runs on each collection, so additions and removals become visible.
Use stable category/name pairs, rather than positional names that change on each
refresh. Keep callbacks fast and side-effect free; polling, drilldown and actions
all need current roots.

```csharp
DiagnosticManager.Configure(diagnostics =>
{
    diagnostics.RegisterObjects(registrar =>
    {
        foreach (var worker in currentWorkers)
            registrar.Register(worker, "Workers", worker.Id);
    });

    diagnostics.Configure<Worker>(type =>
    {
        type.ExcludeAll();
        type.Property(worker => worker.Id).WithLabel("Worker ID");
        type.Property(worker => worker.State);
        type.Property(worker => worker.Connection).WithDrillDown();
    });
});
```

Here `currentWorkers` and `Worker` belong to the application. Combine root, type
and route setup in one configuration callback: `Configure` replaces the prior
configuration. Legacy explicit `DiagnosticManager.Register` roots still work.

The registrar also supports `RegisterService<TService>`. The DI-hosted agent
passes its service provider through polling, drilldowns and actions, resolving
the current roots on each request. Register long-lived diagnostic services in the
application's container. A direct core caller can pass a provider to
`GetRegisteredObjects(provider)` and use the returned roots with the existing
diagnostics/action methods. A missing service is an error, not an empty result.

## Detail and actions

Expose a small useful summary with `ExcludeAll()` and selected `Property(...)`
entries. Use `WithDrillDown()` for nested inspection and `AllowSet()` only for
properties the operator should edit. Collection output and JSON detail are
bounded; the viewer reports truncation or refusal rather than silently presenting
complete output. Full collection item paths include an identity fence: clients
must preserve it when sending an action, even though it is hidden in display text.

The UI supports drilldown navigation, contextual bag/group/property operations,
and live structured and JSON previews. Previews are read-only and refresh every
five seconds while visible; actions and nested navigation use the drilldown dialog.
JSON preserves the server's formatted text, including exact large numeric values.

## Connect to the remote service

This fork serves its browser UI from a separate DiagnosticService, normally in
Docker. Existing applications can keep the current configuration and host API:

```json
{
  "DiagnosticExplorer": {
    "Uri": "http://localhost:2803/diagnostics",
    "Enabled": true
  }
}
```

```csharp
services.AddDiagnosticExplorer(configuration);
```

Or combine fluent setup and hosting registration:

```csharp
using DiagnosticExplorer;

services.AddSingleton<WorkerService>();
services.ConfigureDiagnosticExplorer(configuration, diagnostics =>
{
    diagnostics.RegisterObjects(registrar =>
        registrar.RegisterService<WorkerService>("Workers", "Service"));
    diagnostics.Configure<WorkerService>(type =>
    {
        type.ExcludeAll();
        type.Property(worker => worker.State);
    });
});
```

Here `WorkerService` is an application service with a `State` property. Include
any route and retention setup in the same callback. This overload uses the same
remote-service configuration section and supports the existing HTTP-connection
options callback.

For a non-DI host, call `DiagnosticHostingService.Start(hubUrl)` and await
`DiagnosticHostingService.Stop()` during shutdown. Static `Start(url)` has no
`IServiceProvider`: `RegisterService<T>` requires DI
`ConfigureDiagnosticExplorer`/`AddDiagnosticExplorer` registration or a provider-aware
constructor. With static startup, use `RegisterObjects`/`Register` with actual roots.
The sample's optional hub URL demonstrates this path. Production authentication
configuration remains described in the main README and service security documentation.

Upstream's `Hosts`/SelfHost configuration and configurable system-environment
presentation are not wired by the existing remote hosting API. They are
[deliberately unported](upstream-integration-status.md#decision-hosting-parity-is-deliberately-unported);
do not copy upstream SelfHost examples into this fork and expect them to start a listener.

Deploy the compatible service before agents when moving to `4.0.0`. Realtime
logging in consuming applications must switch from `DiagnosticAppender` to `RoutingDiagnosticAppender` with
routing configured. The legacy appender is intentionally not bridged into the new
stream. Package publication and consumer rollout are separate from this code port.
