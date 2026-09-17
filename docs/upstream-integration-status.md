# Upstream integration status

This fork ports features from `cell001nz/diagnostic-explorer` onto its own
implementation. Upstream baseline: `f8dbb59`. The original phase numbering is
historical; use the implemented behavior below when resuming work.

## Already on main before this pass

- Logging core and MEL/NLog/Serilog adapters, with log4net retained in the core assembly.
- Property/getter model and fluent type configuration.
- MessagePack and SignalR client results on the agent channel.
- Realtime log stream with bounded payloads, framed replay and routing-change recovery.
- Drilldown protocol, fenced collection paths and deduplicated operator actions.
- First UI port: drilldowns, breadcrumbs, collection inspection, JSON/expanded previews,
  contextual actions, projected event views and event detail (PR #164).

## This completion pass

The implementation plan is [upstream integration completion](superpowers/plans/2026-09-08-upstream-integration-completion.md).
It closes legacy event retention, shared routing ownership and logging registration
helpers. It also activates configured object-registration callbacks, which the
first configuration port stored without invoking.
The remote host passes its service provider to each request's root collection,
so configured DI services can participate in diagnostics, drilldowns and actions.

The [logging executable](../samples/Logging/README.md) exercises all four adapters
without importing four copies of the upstream WinForms harness. The existing
`src/WidgetSample` remains the graphical diagnostics demonstration.

## UI completion pass

Completed in the `phase6-ui-completion` branch. Keep our `diagnostics-web`
application, Realtime/Retro switch and Trace Scope views.

| Status | Completed behavior |
| --- | --- |
| Complete | Per-process retained event stores honour stream/sequence identity and negotiated retention; sink views bind to reconciled destination projections. |
| Complete | Fixed configured sinks render when empty and routes reconcile without discarding surviving view state. |
| Complete | Visible event-bearing drilldowns own process subscriptions and retain their originating process after main selection changes. |
| Complete | Bag, group and property operations use contextual operation sets and complete fenced paths while preserving their originating process and refresh behavior. |
| Complete | Preview and JSON buttons provide live, read-only structured or verbatim server-formatted JSON overlays with five-second refresh, keyboard and pointer lifetime handling, and retained originating context. |

The source comparison points are our `Model/RealtimeModel.ts`, `Model/EventSinkModel.ts`,
`Model/PropGroup.ts`, `realtime-category/` and `drill-down-dialog/`, against upstream
`diag-web/src/app/diagnostics/model/ProcessEventStore.ts`, `ProcessModel.ts`,
`category-view/` and `property-hover/`.

Drilldown loading, manual refresh, stale-response suppression, errors, truncation,
breadcrumbs and full collection paths already work. Reuse them. Our Trace Scope
tab is valuable fork functionality and should survive the next UI pass.

## Deployment boundary

The project versions are `4.0.0`; this document does not establish a package release
or an EMS upgrade. Deploy the compatible DiagnosticService before new agents.
EMS must switch its realtime log4net configuration from `DiagnosticAppender` to
`RoutingDiagnosticAppender` and supply routing. There is deliberately no bridge
from the legacy appender to the new realtime stream.

## Decision: hosting parity is deliberately unported

Accepted 2026-09-08. Keep DiagnosticService and remote agents. Docker remains the
published collector image; the existing ASP.NET Core collector also contains a
Windows-service entry point. Reusing that collector on a Windows target does not
require upstream SelfHost or a new installer. Some EMS targets use native Windows
hosting, so Docker must not be assumed for every one.
The functional integration against upstream `f8dbb59` is complete for remote agents;
hosting parity is not unfinished merge work or a release prerequisite.

Do not port upstream `Hosts`/SelfHost startup, bundled standalone web assets, the
WiX Windows installer, or configurable system-environment presentation simply to
match upstream. These add deployment options beyond the existing collector host.
Revisit them only when a concrete standalone or embedded deployment requires them.

Some configuration types for these options exist in the fork, but the remote
hosting API does not apply them. They are unsupported configuration, not working
features. Clear rejection of unsupported options may be addressed separately;
this decision does not implement new hosting behavior.

Release packaging and the collector-first EMS rollout remain operational work.
See the [4.0.0 release notes](releases/4.0.0.md) for artifacts and migration order.
