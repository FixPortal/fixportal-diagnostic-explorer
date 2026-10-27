# DiagnosticExplorer — changes since the last upstream acceptance (→ v3.2.0)

> Historical snapshot, written at fork commit `353aecf`. Figures below (test counts, Angular 21,
> Angular Material/PrimeNG state) describe that commit. Since then the dashboard moved to Angular 22
> (`diagnostics-web/package.json`, `@angular/core` `^22.2.1`) and the fork released `v4.0.0`
> (see `docs/releases/4.0.0.md`). `upstream/main` has also moved on from `da97212` to `f8dbb59`
> (the baseline recorded in `docs/upstream-integration-status.md`).

**Baseline:** `da97212` (`Merge pull request #2 from DestructiveDude/main`) — the tip of
`upstream/main` (cell001nz/diagnostic-explorer) when this was written, and still the merge-base with this fork.
**Head:** `FixPortal/diagnostic-explorer` `main` at the time of this document (`353aecf`),
including the post-`3.2.1` `TreatWarningsAsErrors` follow-up, the Code Quality pass + CI action
currency (Part 3c), two further adversarial-review passes (Parts 3d–3e), the public-API surface
lock (Part 3f / v3.2.2), the diagnostics-web Angular Material → PrimeNG migration plus
GitHub dark re-skin (Parts 1.6–1.7), and a follow-on UI-polish pass that also adds a
trace-scope row filter and restores Docker config precedence (Part 1.8).
**Span:** ~130 commits · 234 files · +26,913 / −13,143 (plus this documentation commit).
**Package version:** `3.1.38` → **`3.2.0`** (NuGet `DiagnosticExplorer`). A minor bump: a new
backward-compatible opt-in feature (hub auth/CORS), a major framework upgrade (Angular 13 → 21),
and ~40 defect fixes. `3.1.38` was rebuilt during this work and is not reused. Pure defect fixes
that landed *after* the `v3.2.0` tag (CodeQL triage + a dogfood pass — Part 3b) are repackaged for
internal consumers as **`3.2.1`**; further fixes from the follow-on review passes (Parts 3d–3e) and
the API-lock tooling (Part 3f) are repackaged as **`3.2.2`**; for upstream they are simply part of
this body of work.

This document explains *what changed and why* for an upstream reviewer. The single most
important property of this whole body of work:

> **Runtime behaviour is unchanged by default.** Every behavioural change is either (a) a fix to
> a confirmed defect, or (b) gated behind opt-in configuration whose default reproduces the
> previous behaviour. A deployment that takes v3.2.0 without changing any configuration behaves
> exactly as v3.1.38 did. The new auth/CORS controls only engage when an operator turns them on.

History is preserved as a sequence of small, self-describing commits (the "test/build" series,
then `reviewer-findings-batch1..10`, the H1/H2 feature and its post-review hardening, and finally
the CodeQL-triage and dogfood-fix batches of Part 3b) so the large diff can be read one cohesive
step at a time.

---

## Part 1 — Tooling, build & test infrastructure

The repository had **zero automated tests** at the baseline. Everything here is additive (new
projects, CI, analyzers) or a toolchain upgrade; none of it changes the shipped library's
behaviour.

### 1.1 First .NET test project (0 → 76 tests)
`tests/DiagnosticExplorer.UnitTests` (xUnit v3 + NSubstitute + AwesomeAssertions, net8.0,
referencing only the netstandard2.0 core library so it runs cross-platform). Coverage targets the
pure, high-value logic: `PropertyBag`/`Property`/`Category`, `ProtobufUtil` compress/decompress,
`RateCounter.GetRates`, `ScopeStack`, `TypeUtil`, `WeakReferenceHash`, `EventSink`/`EventSinkRepo`,
the JSON converters, `AttributeUtil`, `TraceScope`, and (added during remediation) the
property-getter pipeline. Two internal helpers are reached via a single `InternalsVisibleTo` entry
naming only the test assembly.

### 1.2 Angular `diagnostics-web` upgraded 13 → 21
Stepped one major at a time (one commit per major) so each `ng update` migration is isolated and
reviewable: align Material/CDK to 13 first; migrate off the legacy-`*` Material modules to MDC
(required for v15+); 14 → 15 → … → 21, repairing the documented per-version fallout (BOM-corrupted
imports, snackbar/dialog module moves, an unreachable `?? 0` v19's compiler rejects, rxjs 7.8 /
tslib 2.8 peers). `.npmrc` pins `legacy-peer-deps` so `npm ci` resolves the graph the same way the
project was first built. Build green on Angular 21 / Node 22.

### 1.3 Karma/Jasmine → Jest, plus real behaviour tests (71 frontend tests)
Replaced the Karma runner with Jest + jest-preset-angular 16 and deleted the nine CLI
`should create` placeholder specs, replacing them with behaviour coverage: `FilterCriteria`,
`EventFilterComponent`, the pipes, `DiagHubService` SignalR lifecycle, `RetroModel` search/filter/
delete, and `RealtimeModel` ingestion + view-state (raised ~49% → ~98% lines). A genuine bug was
fixed along the way (`EventFilterComponent.loadCriteria` dropping level flags on inbound bind).

### 1.4 Static analysis
- **C#:** `SonarAnalyzer.CSharp` as a private-asset `PackageReference` in a new
  `Directory.Build.props` (S3776 cognitive-complexity gate pinned to warning). 0 errors.
- **Angular:** a minimal flat ESLint config with the SonarJS recommended set as warnings.
- **Warnings-as-errors, solution-wide** (post-`3.2.1` follow-up): every project now builds with
  `TreatWarningsAsErrors`, so no compiler (`CS`) warning can slip into a release. Sonar (`S####`)
  findings deliberately stay advisory — because `CodeAnalysisTreatWarningsAsErrors` governs only the
  built-in .NET SDK analyzers and **not** a third-party analyzer like Sonar (verified empirically),
  the full set of Sonar rule IDs the solution emits is listed once in a global `WarningsNotAsErrors`
  in `Directory.Build.props`. The setting is declared **once** in `Directory.Build.props` and
  inherited by all projects, rather than repeated per-`.csproj` — so any project added later is
  covered automatically, and the dead `CodeAnalysisTreatWarningsAsErrors=false` lines (a no-op
  against Sonar) are gone. Rollout happened in stages: it first surfaced and fixed three latent `CS`
  warnings in the core library — two `CS0108` (the static `RpcResult<T>.Fail` factories intentionally
  hide the same-signature base ones — marked `new`) and one `CS0414` (a dead `fixFlags` field
  shadowed by the real `Fix` property); then `DiagnosticService`'s ~62 mechanical nullable-reference
  warnings were cleared and TWAE switched on for it and the `WidgetSample`/`ConsoleApp` demos;
  finally the per-project declarations were collapsed into the single inherited one above. Solution
  builds with 0 errors. No runtime behaviour and no package version changes.

### 1.5 CI / supply chain
- `dotnet-tests.yml` (xUnit on ubuntu, scoped to the test project), `mutation-web.yml` (StrykerJS
  over the Jest suite, informational), and a frontend `npm ci/test/build` gate on the Docker image
  publish.
- **All GitHub Actions pinned to commit SHAs** (mutable tags on the `packages:write` publish job
  were the supply-chain risk) and a `dependabot.yml` (nuget + npm + github-actions) to keep the
  SHA pins and dependencies maintained. The pins are kept current — the Docker publish job's
  actions were later re-pinned to their Node.js-24 releases (see Part 3c).

### 1.6 diagnostics-web: Angular Material → PrimeNG migration

The existing diagnostics-web UI was built on Angular Material with MDC components. After the
Angular 13 → 21 upgrade (§1.2) Material's visual quality degraded: broken theming, inconsistent
sizing, stale overlay behaviour. The entire component surface was migrated to **PrimeNG 21**
(Lara dark theme + PrimeIcons), the library used by Cameron's reference implementation
(`diag-azure-app`), giving the fork a consistent baseline with upstream.

- **App shell:** `p-splitter` replaces the custom flex layout; `p-selectButton` for the
  Realtime / Retro mode toggle.
- **Realtime:** `p-table` (sortable, resizable, `localStorage` column-width persistence) for the
  process list; `p-splitter` for the rail / content / detail split; `p-chip` severity rows.
- **Retro:** `p-inputText`, `p-calendar`, `p-select`, `p-button` on the search form;
  `p-table` with shared severity rows for results; `p-tabs` + `p-tabpanel` for event detail.
- **Dialogs:** `MatDialog` → PrimeNG `DynamicDialog` / `DialogService`.
- **Trace scope:** new `trace-scope` component replacing the bespoke `collapsible-region`;
  displayed as a collapsible inline tree inside the event-detail tab panel.
- **Category nav:** new `category-nav` component with severity-dot list.
- **Removed:** `@angular/material`, `@angular/cdk` and all related module registrations,
  `angular-split` (migrated to `p-splitter`), Angular `MessageService` toasts. Bundle budget
  restored to 2 MB (Material had exceeded it).
- **Preserved behaviours:** retro event-detail adapter is memoed so trace-scope expand state
  survives re-renders; process and results table column widths persist to `localStorage`.
- **Tests:** `app.component.spec.ts` updated for `p-splitter`; new `trace-scope.component.spec.ts`
  covering toggle behaviour; `RealtimeModel` and `RetroModel` specs adjusted for the updated
  component interface.

### 1.7 diagnostics-web: GitHub dark re-skin

A visual redesign aligned to the GitHub dark palette. No Angular or PrimeNG behaviour changes —
purely visual / CSS.

- **Palette:** global CSS custom properties in `styles.scss` (`--bg-*`, `--surface-*`, `--text-*`,
  `--border-*`) covering canvas (`#0d1117`), rail/header content (`#010409`), pure black header bar,
  box bodies, and interactive states; `--indigo-focus-ring` focus indicator.
- **Typography:** IBM Plex Mono for structural UI (header, process names, trace scope, metadata);
  IBM Plex Sans for Process messages and human-readable text. Served from `@fontsource` packages
  (no external CDN; bundled in the Docker image).
- **Tailwind 3.0.2 fix:** arbitrary `[var(--x)]` token utilities generated empty CSS rules under
  Tailwind 3.0.2 (the pinned version). Adding explicit property-hint prefixes (`[color:var(--x)]`,
  `[background-color:var(--x)]`, etc.) throughout `styles.scss` forces PurgeCSS to emit them
  correctly.
- **Realtime nav:** double-click a column header to auto-fit its width; fit-all on Online-only
  toggle; orange resize indicator strip; auto-fit uses `ElementRef` + `requestAnimationFrame`
  (not `ViewChild`, which resolves too late for dynamic column content).
- **Event filter:** severity-coded PrimeNG checkboxes; black filter input.
- **Category nav:** severity-dot decay extended from 2 s to 5 min.
- **Retro nav:** black input / select / datepicker; indigo primary; text-link Reset / Delete;
  white pill Search.
- **Event detail:** black tab header, grey content, white text; trace scope displayed inline in
  the retro event-detail view (with a stub fallback when no scope data is present).

### 1.8 diagnostics-web: UI-polish pass, trace-scope filter, Docker config fix

A follow-on round of UI refinements on top of the re-skin, plus one additive feature and one
config-precedence fix. Everything here is either visual/CSS or additive; no existing runtime
behaviour changes.

- **Type scale:** larger, more legible sizing for process names (IBM Plex Mono, green), Host/User
  columns, the category rail, property name/value grids, the severity-band tables, and the event
  detail body. Several rules had to move from component `:host ::ng-deep` / Tailwind `text-*`
  classes to globally-scoped selectors in `styles.scss` because the values were being lost across
  PrimeNG's `p-tabpanel` content-projection boundary or out-competed by Tailwind utilities.
- **Collapsible content panels:** every System-view sub-category and event-sink panel is now
  collapsible with a left-aligned chevron that rotates on expand; the Retro Results panel gained the
  same chevron + collapse.
- **Trace-scope row indicator:** rows whose detail carries a `BEGIN/END` trace scope show an inline
  `pi-info-circle` marker (realtime and retro), so a scope is discoverable without opening each row.
  Detection is centralised in a single `ScopeNode.hasTraceScope()` helper.
- **Trace-scope filter (new):** a 5th option on the shared event filter — an `pi-info-circle` toggle
  that keeps only rows carrying a trace scope. It is orthogonal to the four severity flags and the
  text matcher (all ANDed) and, because the filter lives in `FilterCriteria`, applies to both the
  realtime event-sink filters and the retro results filter. Covered by new `FilterCriteria` unit
  tests.
- **Salmon accent:** PrimeNG's Aura `primary` palette is rebranded to the app's salmon accent via
  `definePreset`, so theme highlight states (select selected-option, datepicker selected date) match
  the rest of the UI instead of Aura's default teal.
- **Process list affordances:** hover lift and an unmistakable salmon-tinted selected row with a
  salmon left marker in the realtime process list (previously a near-invisible 4%-white background
  and no hover state).
- **Resizable panels / tables:** property panels carry a vertical resize grip; event-sink panels are
  sized by the inner events table's own resize grip (drag to reveal more rows) — kept separate to
  avoid fighting PrimeNG's collapse animation, which drives panel height.
- **Nested-splitter fix:** a vertical `p-splitter` (and each `.p-splitterpanel`) is a grid/flex item
  with the default `min-height:auto`, so a tall panel — e.g. a full Retro results table — inflated
  the whole splitter past the viewport and pushed the detail/trace-scope pane off-screen. Forcing
  `min-height:0` on splitters/panels lets them honour their allotted track and clip+scroll
  internally.
- **Docker config precedence (fix):** `Program.cs` added `Config/settings.json` to configuration
  *after* `WebApplication.CreateBuilder` had already loaded environment variables, inverting ASP.NET
  Core's conventional precedence and causing container env overrides (e.g.
  `DiagServiceSettings__RetroConnection`) to be silently ignored — so the Mongo/Retro connection
  string fell back to `localhost`. Re-adding `AddEnvironmentVariables()` after the JSON file restores
  the expected "env wins" ordering.

---

## Part 2 — The adversarial audit and its remediation

The .NET + Angular surface was put through a **cross-vendor adversarial code audit** (Claude Opus
+ Claude Sonnet + GPT-5.4, blind review → cross-examination → adjudication) run as 11
functionally-cohesive chunks. It produced one ranked report (1 Critical, ~15 High, ~50 Medium,
~25 Low, plus refuted/already-fixed items). The full report and working materials live in the
team's audit archive. Remediation was applied in numbered batches, each its own commit with the
finding IDs it closes:

| Batch | Theme |
|------|-------|
| 1 | Critical + Highs — concurrency, DoS bounds, dead failover, broken ctor, prod build, Docker creds |
| 2 | Medium correctness / lifecycle / metric fixes |
| 3 | Lifecycle, dead-code & frontend-correctness Mediums |
| 4 | Lows — data-leak / privacy / hygiene |
| 5 | Finalise — test regression + async-trace assessment |
| 6 | Lows sweep — supply-chain, lifecycle, hygiene |
| 7 | Core-library logic Mediums |
| 8 | Hosting lifecycle & concurrency Mediums |
| 9 | Opt-in hub authentication & CORS (H1/H2) |
| 10 | Final cleanup — remaining log4net + WidgetSample Mediums |
| (11) | Post-review hardening of the H1/H2 feature |

---

## Part 3 — Defect fixes by severity (what & why)

### Critical
- **C1 — hosted-service managers never started.** `RealtimeManager`/`RetroManager` implemented
  `IHostedService` but were `AddSingleton`-only and self-wired their lifecycle in their ctors via
  `ApplicationStarted.Register` — which only fired if the singleton happened to be constructed
  before `ApplicationStarted`. A late first connection meant retro logging silently no-op'd and
  queue access NRE'd. **Fix:** register via `AddHostedService` so the host owns the lifecycle; drop
  the ctor self-wiring and a duplicate `AddSignalR()`.

### High (correctness / concurrency / DoS / security)
- **H3 — unauthenticated unbounded-payload DoS.** Finite SignalR `MaximumReceiveMessageSize`
  (10 MB, was `int.MaxValue`); `ProtobufUtil.Decompress` caps decompressed size (zip-bomb guard),
  guards empty input, disposes the `GZipStream`; `RetroDelete` batch length capped.
- **H4 — ReDoS via client filter strings.** Mongo retro queries now escape / bound / time-box the
  regex and cap `$in` length; `ObjectId.TryParse` instead of throwing `Parse`.
- **H5 — `SubCategory(PropertyBag)` ctor populated a discarded local** → always returned an empty
  object. Assigns `this`.
- **H6 — `RateCounter.SampleCollected` via `Delegate.BeginInvoke`** (throws
  `PlatformNotSupportedException` on .NET Core/5+, swallowed). Dispatch via `Task.Run`.
- **H7 / H8 — unsynchronised static caches** (`DiagnosticManager._typeHash`/`_operationLookup`,
  `GenericObjectCache._objectCache`) raced under the `Task.Run` dispatch model →
  `ConcurrentDictionary.GetOrAdd` (+ Ordinal comparer); `Clear()` locks.
- **H9 / H10 / H11 — log4net failover was dead.** `IsInError` was never set so the `FailTimeout`
  quarantine never engaged ("READY" forever); the error-handler gate keyed on
  `Thread.CurrentThread` dropped off-thread failures; the async appender's Discard mode used an
  unbounded queue and threw on the logging thread. All three fixed (engage quarantine; per-thread
  error context; bounded queue + `TryAdd`).
- **H13 — no working production build.** `ng build` inherited dev config and `build:prod` used the
  A13-removed `--prod` flag with a POSIX-only env prefix. Now `--configuration production`.
- **H14 — Docker default Mongo root creds + published 27017.** Require `MONGO_PASSWORD`; bind to
  loopback.
- **H15 — un-awaited `invoke()` fed to `plainToInstance`** → "Property set!" shown even on a hub
  error. Awaited.

### Medium (selected; ~50 total across batches 2–10)
- Lifecycle/teardown: orphaned subscriptions on process removal (M3); `RetroManager.StopAsync`
  draining/flush/dispose (M5); overlapping request loops fenced + CTS disposed (M9);
  `MailMessage`/`SmtpClient` disposed (M14); the hosting connection/adapter teardown made race-free
  and dispose-exactly-once across the loop / `Closed` event / `Stop` (M22, M24–M28).
- Correctness: 16.7-min write-lock typo → 10s (M2); double-counted write-queue metric (M4);
  `.Result` → `await` so the real exception surfaces (M6); null-guarded client handler (M7);
  per-`(name,category)`-tuple sink keys, not a colliding string (M30); single-pass collection
  enumeration (M19); `RateCounter` ctor validation + locked 64-bit reads + negative-count guard +
  ring-wrap clamp (M20/M21); guarded rate/date getters so one throwing property can't abort the
  whole walk (M18); `TraceScope` null-`_disposed` guard so auto-trace can't silently throw (M29);
  SMTP TLS, forced on Basic auth (M13); `EventSinkRepo.Clear` coherence (M34).
- Frontend: reconnect no longer silently stops realtime (M1); date-picker no longer mutated by a
  search (M36); execute guarded (M41); event-detail textareas use `[value]`+`readonly` (M42);
  stale-frame guard on the active process (M37).
- WidgetSample (demo, mis-teaches consumers): `Notice` now attaches the exception (M46); invalid
  JSON comment removed (M48); unbounded demo recursion depth-capped (M49); removed widgets disposed
  → unregistered (M50).

### Low
A broad hygiene/supply-chain/privacy sweep (batches 4 & 6): the GitHub Actions SHA-pinning +
Dependabot above (L22); `RealtimeManager` subjects synchronised (L14); the round-trip-timeout made
configurable and disconnect frees the pending TCS promptly (L16); an unobserved fire-and-forget Rx
task replaced with a synchronous chunk loop (L13); debug `console.log`s and a shipped debug
`Info`/dead `Progress` field removed (L17/L24/L25); CS1998 async-no-await cleaned; the global
mutable `SystemDateTime` clock made non-public-mutable (L10); `Processes.xml` / log4net config
scrubbed of real internal hostnames, AD usernames and prior-employer addresses (L20/L21).

### Cross-cutting themes (the highest-value output of the audit)
Five recurring mistakes were fixed as *patterns*, not one-offs: (1) unsynchronised shared mutable
state across the `Task.Run` dispatch model; (2) an unauthenticated/over-permissive service surface
(→ Part 4); (3) lifecycle/teardown leaks (unawaited tasks, undisposed CTS/clients/connections);
(4) silent-failure patterns (swallowed exceptions, dead code masking intent); (5) left-in debug
logging.

### Part 3b — Fixes after the `v3.2.0` tag (CodeQL triage + dogfood pass)

Two further passes ran after the `v3.2.0` tag was cut. Both are pure defect/quality fixes behind
unchanged defaults, repackaged for internal consumers as `3.2.1`.

**CodeQL code-scanning triage (batches 12–14).** ~160 alerts were triaged; only genuine findings
were fixed, the remainder dismissed-with-reason as false-positive or by-design (after the audit the
codebase is clean, so few real alerts land). Genuine fixes:
- `LoggerNotFoundFilter` — the root logger has a null `Parent`, so the appender-name path could NRE
  on `hlog.Parent.Name`; guarded with `hlog.Parent?.Name` (a null parent then sorts `!= "ROOT"` →
  `Deny`, preserving intent).
- `DiagnosticSubscription` — removed a dead `isNull` local whose only consumer was a commented-out
  `Debug.WriteLine`.
- `diagnostics-web` — nine unused imports/locals removed and two missing semicolons inserted, each
  verified referenced only on its own import line.

**Dogfood pass (one High, one Medium, six Lows).** A hands-on pass over the running web UI against a
live store surfaced:
- **[High] Retro returned "No events" for every query at scale.** `Diagnostics.Log` had no `Date`
  index, so the Retro date-range filter + date-descending sort full-scanned the collection (~188M
  rows on the live store), tripped the 30 s `MaxTime`, and the timeout was rendered as an empty
  result — writes were never affected, only reads. `MongoRetroLogger` now ensures a `{ Date: -1 }`
  index on construction (idempotent, fire-and-forget so a long initial build on a large collection
  blocks neither startup nor queries). Verified: 841 rows in ~0.0 s on the live store once indexed.
- **[Medium] Operation exceptions showed the reflection wrapper text** ("Exception has been thrown
  by the target of an invocation") instead of the real cause. `DiagnosticManager.ExecuteOperation`
  now unwraps `TargetInvocationException` and surfaces the inner exception.
- **[Low] UI polish:** set-property dialog labels the friendly property name (not the internal pipe
  path); Process/Host/User cells get a title tooltip + truncate; the blank centre-toolbar button is
  hidden when no process is selected; the Trace Scope tab shows an explicit empty-state; the Detail
  exception textarea fills its panel; stray debug `console.log`s removed.

### Part 3c — Code Quality pass + CI action currency (post-`3.2.1` main-branch maintenance)

After `3.2.1`, two small maintenance passes landed directly on `main`. They are **not** packaged as
a new NuGet release — they live on the fork's mainline for inclusion in the upstream PR. As with
everything above, **runtime behaviour is unchanged**: the code changes are maintainability fixes or
behaviour-preserving thread-safety, and the CI change touches only the build pipeline.

**GitHub Code Quality (maintainability) triage.** A *separate* CodeQL surface from the
code-*scanning* (security) triage in Part 3b — these are the maintainability rules surfaced at
`/security/quality`. Most alerts were in the `WidgetSample` demo and are by-design (its purpose is
to demonstrate logging exceptions at each severity, force a GC to show gadget removal, etc.) or are
intentional operation-boundary `catch` handlers; those were dismissed-with-rationale in the UI. The
genuine fixes:
- `RetroManager.CancelRetroSearch` — collapse a nested `if` into one `&&` condition.
- `RetroManager.RunLoop` — document the intentionally-empty `catch (OperationCanceledException)`
  (expected on shutdown) so it no longer reads as a swallowed error.
- `AsyncResultBucket._results` — mark `readonly` (only assigned at declaration).
- `AppenderProxyBase.LastError` / `LastMessageSent` — back with fields guarded by the existing
  `_stateLock` instead of plain auto-properties. They are written off-lock in `DoAppend` and read on
  the diagnostic-walk thread, so the non-atomic nullable `DateTime` could tear; this extends the same
  M17a guard already applied to `_isInError` / `_errorTime`.
- `AppenderProxy` clarity (from a Copilot AI-findings review): an explicit `_errorTime.HasValue`
  guard + `.Value` in `ShouldResetErrorNoLock` (same result as the prior null-safe nullable
  subtraction, clearer intent), and a reworded ctor `InvalidOperationException`. Non-behavioural.

One Copilot suggestion was **declined pending a maintainer decision**: changing `LoggerNotFoundFilter`
to `Accept` when the logger is not found (`log == null`). The current logic only `Accept`s when the
logger *exists*, is appender-less, and parents to `ROOT`; flipping the not-found case is a
behavioural change whose intent isn't established, so it was left as-is.

**CI action currency.** The Docker publish job's four `docker/*` actions (`setup-buildx`, `login`,
`metadata`, `build-push`) were re-pinned from their Node.js-20 releases to the current Node.js-24
ones, keeping the SHA-pin + version-comment convention, ahead of GitHub forcing Node-24 on
2026-06-16 and removing Node-20 on 2026-09-16. No workflow inputs changed.

### Part 3d — Adversarial review batches 15–17 (second multi-vendor pass)

A second cross-vendor adversarial code review (Claude Opus + GitHub Copilot) was run over the full
codebase after the auth-hardening work landed. Batches 15–17 carry the resulting remediation, with
a regression-fix commit (`ff74e21`) that repaired service-side cancellation and UI stale-state issues
surfaced by the review pass's verification run. Key changes by severity:

**High:**
- `EventSinkStream._eventSubject` wrapped with `Subject.Synchronize()` — the raw `Subject<T>` was
  not thread-safe and `OnNext` was called concurrently from the hub and the write loop.
- `DoScopeTimerCode` guarded with `IsHandleCreated` + `InvalidOperationException` catch — the scope
  timer could fire after form destruction and crash the process.
- `EventSinkStream` now throws `ArgumentOutOfRangeException` for `bufferSize ≤ 0` to prevent an
  infinite loop in `WriteEvents`.
- `WebClientHandler` per-process event stream: replaced the single-slot `EventStreamState` with a
  `ConcurrentDictionary<string, EventStreamState>` so multiple concurrently-subscribed processes
  each get a dedicated live stream.
- API key now delivered via a short-lived HTTP-only cookie (60 s TTL, Secure flag) rather than the
  query string; backend prefers cookie before header/query-string fallback.

**Medium (selected):** SMTP `FailTimeout` single-host detection fixed (`hosts.Length <= 1`);
`RetroSearchProcess` channel bounded (200 items, Wait mode) with query cancellation on delivery
failure; `RetroManager.RunLoop` drain fixed (pass `CancellationToken.None` so drain completes before
the `ApplicationStopping` token fires); `RegistrationHandler` log channel shrunk from 1 M to 10 k
batches with correct drain ordering; snapshot serialisation for `Gadgets`/`Widgets`; `ReDoS` guard
via `isSafeRegex()` in `FilterCriteria`; duplicate SignalR connect guard in `DiagHubService`;
`PropertyChanged` marshalled to UI thread in `Form1`.

**WidgetSample shutdown (batch 15):** timer/task cleanup in `OnFormClosed`; `StopDiagnostics`
off-thread to avoid UI-thread deadlock; `_evtTimer` callback marshalled via `BeginInvoke`; `Gadget`
made `IDisposable` for deterministic removal; shared `Random` replaced with `ThreadSafeRandom`.

**CodeQL follow-up (`2e45844`):** `EventSinkStream` holds the inner concrete `Subject<SystemEvent>`
so the wrapper's inner subject is disposed deterministically; two useless variable bindings in
`Form1` `using` statements removed; one false-positive dismissed with rationale.

---

### Part 3e — Adversarial review batches 19–20 (third multi-vendor pass)

A third adversarial review pass (batch 19, run 2026-06-02) covered the full codebase again,
generating 5 High and 10 Medium findings. Batches 19–20 apply the remediation plus the resulting
CodeQL and Sonar follow-up passes. Key changes:

**High:**
- `AppenderProxy` now rejects async appender targets at construction (previously async appenders
  would silently misbehave when registered via `AppenderProxy`).
- `FixFlags.All` applied before `Parallel.ForEach` in `ForwardingAppender` and `FallbackAppender` to
  materialise lazy `LoggingEvent` fields single-threaded.
- `_logChannel` no longer nulled until the drain task fully completes, closing a race where a late
  `LogEvent` after channel-null would NRE.
- `CancellationToken` threaded through `Register`/`LogEvents`/`Deregister` RPCs with a 5-second
  shutdown time-box.
- `_logSubject` wrapped with `Subject.Synchronize()` for thread safety in `RegistrationHandler`.

**Medium (selected):** type-backed registrations now reflect static methods (`DE01-F10`);
`DiagnosticClassAttribute` inheritance honoured in the property walk; `LoggerNotFoundFilter`
resolves against the event's own log4net repository (not the root); `AsyncSmtpAppender` gains an
`Append(LoggingEvent[])` override; `AppenderProxy` quarantine recovery uses a half-open probe
pattern; `EventSinkRepo.Dispose` completes streams under lock before disposing; `EventSinkRepo.Clear`
invalidates held `EventSink` references; `EventSink` enqueue and live broadcast made atomic under
read lock; `GenericObjectCache` keys on `AssemblyQualifiedName`; `Process.GetCurrentProcess()`
wrapped in `using` on the net48 path.

**CodeQL/Sonar follow-up (batches 19–20, `ef840f6` + `137349b` + `16c34a4`):** `TraceScope`
orphaned-timer disposal on concurrent `Dispose`; `RegistrationHandler` inner subject disposed in
`Stop()`; `DiagnosticHostingService.StopHosting` always clears the logging action; `DiagnosticManager`
query iteration conformed to CodeQL `.Where()`-before-foreach; seven Sonar readability findings
(`S3267`, `S3358`, `S3442`); 7 fields marked `readonly`; `RetroSearchLifecycleTests` inner subject
owned with `using`; `RetroModel` race-free cancel-before-search; `DiagHubService` `onreconnecting`
handler registered.

**Dead code removed:** `DiagnosticService.cs` and `Util/AsyncResultBucket.cs` (both were
`<Compile Remove>`'d in the csproj) deleted along with the csproj exclusion lines.

---

### Part 3f — v3.2.2: public-API surface lock (PublicApiAnalyzers) — added, then removed

v3.2.2 briefly added `Microsoft.CodeAnalysis.PublicApiAnalyzers` (v3.3.4, private analyzer asset)
to both published NuGet projects (`DiagnosticExplorer`, `DiagnosticExplorer.Hosting`), with
fully-bootstrapped `PublicAPI.Shipped.txt` files declaring every public symbol in Roslyn's
fully-qualified `ToDisplayString` format (commit `3166ea1`). While it was in place, any commit that
added, removed, or renamed a public member produced a **build error** (RS0016/RS0017) until the
author updated `PublicAPI.Shipped.txt` or `PublicAPI.Unshipped.txt`. RS0041 ("oblivious reference
types") was held at warning level via `WarningsNotAsErrors`, and the analyzer was conditioned to
`$(TargetFramework) != 'net48'` for `DiagnosticExplorer.Hosting`.

**The gate was subsequently removed and no longer protects either package.** The analyzer
references were dropped in `17f2a5b` ("chore: remove PublicApiAnalyzers and fix StartupObject
namespace"), and the then-orphaned `PublicAPI.Shipped.txt` / `PublicAPI.Unshipped.txt` files were
deleted in `a40fbf5` ("chore: delete orphaned PublicAPI.Shipped/Unshipped.txt files"). The
public-API surface is currently **not locked**: public members can be added, removed, or renamed
with no build-time signal. Restoring the gate is a separate, deliberate decision — it is not in
force today.

---

## Part 4 — Opt-in hub authentication & CORS (H1/H2), and its hardening

The audit's two service-security Highs were that the SignalR hubs expose mutating/reflective
operations with **no authentication** (H1) and that CORS **reflects any origin with credentials**
(H2). A mandatory fix would break every existing diagnostic client, the Angular SPA, and the downstream
package consumer flow on the day of deploy. So this ships as **opt-in**, with a phased zero-break
rollout (design recorded in `docs/security/hub-authentication-proposal.md`).

**Configuration** (`DiagServiceSettings.Security`): `AuthMode` (`None` default == today | `ApiKey`),
`ApiKeys[]`, `AllowedCorsOrigins[]`.

- **`None` (default):** no auth scheme is registered, no `RequireAuthorization`, no Origin check,
  CORS stays permissive (now with a startup warning). **Identical to v3.1.38.**
- **`ApiKey`:** an API-key `AuthenticationHandler` (key via `X-Diag-ApiKey`, `Authorization: Bearer`,
  or the `access_token` query for the WS upgrade; fixed-time comparison) gates both hubs; CORS uses
  `WithOrigins(AllowedCorsOrigins).AllowCredentials()`. The `.NET` hosting client and the SPA send a
  configured key via `AccessTokenProvider` / `accessTokenFactory`.

**The auth feature itself was then put through a second cross-vendor adversarial review**, and the
confirmed findings were hardened:
- **Fail closed on misconfiguration:** `AuthMode: ApiKey` refuses to start unless `ApiKeys` has a
  usable key (else every connection would 401 — a silent outage) **and** `AllowedCorsOrigins` is
  set (so credentialed any-origin CORS can never coexist with auth). `AuthMode` is read once from
  the bound settings and throws on an unparseable value rather than defaulting open.
- **TLS-or-nothing:** the `.NET` client refuses to send a key over a non-`https`/`wss` URL.
- **WebSocket Origin validation:** explicit middleware validates the `Origin` header on the hub
  paths, because CORS does not police the WS upgrade (native clients send no Origin and stay
  key-gated).
- Handler returns `Fail` (not `NoResult`) on a missing key; all key paths are trimmed; the client
  layers the key last so a caller callback can't silently drop it.
- **Honest threat model (documented):** the *SPA* key ships in the JS bundle and is therefore **not
  a secret** — for browsers it only blocks anonymous connections. Real protection for an
  internet-facing dashboard is a reverse proxy / IdP with server-minted short-lived per-user
  tokens. The API key is a genuine gate for the **server-side .NET clients**, which never expose it
  to a browser.

---

## Part 5 — Behavioural & contract notes for integrators / upstream

- **Default behaviour is unchanged.** See the banner at the top; all auth/CORS is opt-in.
- **One wire-contract change:** `RetroSearchResult.Progress` (server) was removed — it was dead on
  both sides (never set server-side; the Angular model has no `progress` field; the SPA computes
  its own progress). No client read it.
- **`AddDiagnosticExplorer` / `DiagnosticOptions`:** gains an optional `ApiKey` (null = no key =
  prior behaviour). Integrated Windows auth on the client is now **opt-in** via the `configureHttp`
  callback (previously the default forwarded `UseDefaultCredentials` to any configured URL — a
  credential-leak the hub never used).
- **Target frameworks unchanged:** core library `netstandard2.0`; hosting `net8.0;net6.0;net48`;
  service `net8.0`.
- **Package version:** the headline release is **3.2.0** (git tag + Docker image). The internal
  NuGet repackaged with the post-tag defect fixes (Part 3b) is **3.2.1**; the further review-pass
  fixes and the PublicApiAnalyzers tooling (Parts 3d–3f) are **3.2.2**. (The PublicApiAnalyzers
  gate of Part 3f was later removed — see Part 3f.) The downstream consumer picks up
  each version via the existing local-feed nupkg flow. Neither `3.2.0` nor `3.1.38` is reused.
- **Deferred, with rationale (not regressions):** Tailwind `important: true` (a visual-specificity
  change that needs a running-app pass, not a blind edit; the deprecated `~` SCSS import and the
  content-glob/darkMode issues *were* fixed); a small set of contested/by-design Low items
  (maintainability notes, already-dead code). Auth for the *browser* SPA beyond "block anonymous"
  is a product decision (real user/session auth), not a code fix.

---

## Part 6 — Verification

- Full solution builds **0 errors** (Debug & Release); the two published library projects build
  **warnings-as-errors** clean (Sonar findings remain advisory warnings).
- .NET unit suite: **76/76** green.
- Frontend: Jest suite extended post-PrimeNG migration (new `trace-scope.component.spec.ts`,
  updated app shell / model specs); all tests pass. `ng build` (production) succeeds.
- Docker image builds and serves the SPA + both SignalR hubs from a single container.
- The complete integrated tree (all batches together) was built and verified running before this
  document was written.

---

## Part 7 — How this is proposed for upstream (PR strategy)

The diff against `da97212` is **42 commits / 158 files / +21.8k / −11.3k**. That is far too large to
review hunk-by-hunk as a single pull request, and squashing it would destroy the very thing that
makes it reviewable — the curated sequence of small, self-describing commits aligned to the themes
above. So the proposal is **document-first, then PRs shaped to your appetite**:

1. **This document is the map.** Read it first; it is the review guide for the diff, not a
   substitute for it. Every behavioural change is either a fix to a confirmed defect or gated behind
   opt-in config whose default reproduces today's behaviour — so the large additive/upgrade parts
   can be accepted quickly and scrutiny concentrated on the small behavioural surface.

2. **Preferred shape — five thematic PRs along the seams the history already has,** stacked so each
   rebases on the one before. This lets the safe parts merge immediately and the behavioural parts
   be reviewed in isolation:
   - **PR 1 — Tooling, tests, CI, Angular 13 → 21 (Part 1, §1.1–1.5),** including
     `TreatWarningsAsErrors` on the published projects (§1.4). Purely additive / toolchain; no
     change to the shipped library's runtime behaviour. Safe to accept first. The Node.js-24
     Docker-action currency bump (Part 3c) folds in here.
   - **PR 2 — Audit remediation defect fixes (Parts 2–3; batches 1–8, 10).** The correctness /
     concurrency / DoS / lifecycle fixes, each commit carrying its finding IDs.
   - **PR 3 — Opt-in hub auth & CORS + hardening (Part 4; batch 9 + the hardening commit).** The one
     new feature; off by default.
   - **PR 4 — Post-tag fixes (Parts 3b–3f): CodeQL code-scanning triage, the dogfood
     Retro-index/exception/UI fixes, the Code Quality maintainability pass, the further adversarial
     review batches (15–20), and the PublicApiAnalyzers surface lock.**
   - **PR 5 — diagnostics-web visual overhaul (Parts 1.6–1.7): Angular Material → PrimeNG migration
     and GitHub dark re-skin.** Frontend only; no backend or library changes. Can be reviewed and
     merged independently of the behavioural PRs.

3. **Alternative — one umbrella PR** (`FixPortal:main` → `cell001nz:main`) whose description links
   this document, if you would rather have the whole thing in one place and review via the doc.
   Because the history is already themed, it can still be split into the five PRs above on request.

**Mechanics / cautions:**
- Base the PR(s) on `cell001nz/diagnostic-explorer@da97212` (current `upstream/main`); that is the
  merge-base, so there are no surprise conflicts from upstream drift.
- **Do not squash.** The reviewability of this work *is* the commit granularity; rebase-merge (or a
  plain merge) preserves it.
- `gh pr create` on this fork defaults its base to the `cell001nz` upstream remote — for an upstream
  PR that is what we want; just confirm the base shows `cell001nz/diagnostic-explorer:main`, not the
  FixPortal origin, before submitting.
- The internal NuGet repackage (`3.2.1`) and the consumer rollout are FixPortal-side distribution steps
  and are **not** part of any upstream PR.

---

## Appendix — commit inventory (newest first, since `da97212`)

```
fix(diagnostics-web): add fontsource packages missing from re-skin PR; fix Docker mongo port (Part 1.7)
feat(diagnostics-web): GitHub dark re-skin — event detail, trace scope, retro display; strip dummy data (Part 1.7)
dev(event-detail): dummy trace scope for styling — falls back to real region when present (Part 1.7)
feat(event-detail): black tab header, grey content, white text, drop indigo border (Part 1.7)
feat(styles): mixed typography — IBM Plex Mono for structure, Plex Sans for Process + Message (Part 1.7)
feat(styles): switch body font to IBM Plex Mono (Part 1.7)
fix(styles): set --p-font-family to override PrimeNG Aura's runtime cascade (Part 1.7)
chore(realtime-nav): remove redundant per-th dblclick handlers; HostListener covers it (Part 1.7)
feat(styles): add IBM Plex Sans as body font (Part 1.7)
feat(event-filter): severity-coded checkboxes; black filter input; fix chevron direction; bump detail font (Part 1.7)
feat(category-nav): extend severity dot decay from 2s to 5min (Part 1.7)
feat(retro-nav): text-link Reset/Delete + white pill Search; fixed-size, anchored left/right (Part 1.7)
feat(retro-nav): black input/select/datepicker boxes; indigo primary (Part 1.7)
feat(realtime-nav): position-based dblclick fit; overflow-gated toggle fit; orange resize indicator (Part 1.7)
fix(realtime-nav): use host ElementRef + rAF for column auto-fit (was ViewChild timing) (Part 1.7)
feat(realtime-nav): double-click column header to auto-fit; fit all columns on online-only toggle (Part 1.7)
style: black filter input + white text; orange splitter handles (Part 1.7)
style(realtime-nav): black table header, white process-table text (Part 1.7)
style(realtime-nav): white Online-only checkbox (checked state) (Part 1.7)
style(realtime-nav): orange Online-only checkbox + white label (Part 1.7)
fix(diagnostics-web): add color: type-hint to all [var(--*)] token utilities (Tailwind 3.0.2 fix) (Part 1.7)
tweak(diagnostics-web): black nav-tabs bar, process-table text green-500 (Part 1.7)
fix(diagnostics-web): grey canvas/rail vs black header+box body; splitter transparent (Part 1.7)
tweak(diagnostics-web): rail/canvas #010409, header+box body pure black (Part 1.7)
feat(diagnostics-web): GitHub re-skin — black header, underline nav tabs, GitHub palette (Part 1.7)
fix(diagnostics-web): drop border under top app-bar header (Part 1.7)
fix(diagnostics-web): remove PrimeNG default light borders on process-table header (Part 1.7)
fix(diagnostics-web): uniform 16px vertical spacing in rail (Part 1.7)
tweak(diagnostics-web): rail width range 250-500px (Part 1.7)
fix(diagnostics-web): process table fits container; rail px-clamped min/max (Part 1.7)
feat(diagnostics-web): rail controls — tools divider, bigger gaps/fonts, teal toggle (Part 1.7)
feat(diagnostics-web): bounded containers — rail borders, 9px padding+gap, 6px splitter gutters (Part 1.7)
fix(diagnostics-web): left rail — stack filter/Online, visible teal column grips (Part 1.7)
fix(diagnostics-web): solid indigo toggle fill (Part 1.7)
feat(diagnostics-web): panel + fieldset chrome (Part 1.7)
wip(diagnostics-web): visual-fidelity fixes — detail metadata, teal grips, indigo toggle (Part 1.7)
docs: add diagnostics-web UI redesign implementation plan (Part 1.6–1.7)
docs: add diagnostics-web UI redesign design spec (Part 1.6–1.7)
chore(diagnostics-web): drop unused angular-split dependency (migrated to p-splitter) (Part 1.6)
chore(diagnostics-web): remove Angular Material; restore 2MB bundle budget (Part 1.6)
feat(diagnostics-web): convert dialogs to PrimeNG DynamicDialog (Part 1.6)
feat(diagnostics-web): persist process + results table column widths to localStorage (Part 1.6)
fix(diagnostics-web): memoise retro event-detail adapter to preserve trace-scope expand state (Part 1.6)
feat(diagnostics-web): Retro results table with shared severity rows + event-detail (Part 1.6)
feat(diagnostics-web): Retro search form on PrimeNG controls + focus ring (Part 1.6)
feat(diagnostics-web): realtime display with category-nav + content/detail splitter (Part 1.6)
refactor(diagnostics-web): typed trace-scope toggle handler + clarify duration format (Part 1.6)
feat(diagnostics-web): restore Trace Scope as a proper collapsible tree + tabbed event-detail (Part 1.6)
feat(diagnostics-web): punchy severity event-row treatment (Part 1.6)
style(diagnostics-web): align category panels to palette tokens (Part 1.6)
feat(diagnostics-web): severity-dot category-nav list component (Part 1.6)
feat(diagnostics-web): resizable columns + palette on process list (Part 1.6)
test(diagnostics-web): update app shell spec for p-splitter (Part 1.6)
feat(diagnostics-web): shell on p-splitter + p-selectButton mode toggle (Part 1.6)
feat(diagnostics-web): add muted-slate palette tokens + indigo focus ring (Part 1.6)
feat(diagnostics-web): copy Cameron event table, row colours and detail panel verbatim (Part 1.6)
feat(diagnostics-web): add empty-state hint to realtime display (Part 1.6)
feat(diagnostics-web): adopt Cameron's realtime layout (stage 1) (Part 1.6)
feat(diagnostics-web): convert realtime-display tabs and app shell to PrimeNG (Part 1.6)
feat(diagnostics-web): convert realtime events, filter, category to PrimeNG (Part 1.6)
feat(diagnostics-web): convert realtime-nav to PrimeNG (Part 1.6)
feat(diagnostics-web): add PrimeNG 21 foundation alongside Material (Part 1.6)
chore(diagnostics-web): add ng serve proxy for local SignalR dev (Part 1.6)
v3.2.2 — lock public API surface via PublicApiAnalyzers; update upstream change doc (Part 3f)
fix: resolve batch20 CodeQL and Sonar findings (Part 3e — readonly fields, logic/dispose fixes)
fix: resolve Sonar warnings and failing frontend tests for PR #39 CI (Part 3e — S3267, test race fixes)
fix: resolve CodeQL findings on PR #39 batch19 diff (Part 3e — timer disposal, IDisposable subjects, .Where() in foreach)
fix: adversarial-review batch19 — remediate all High and Medium findings (Part 3e)
fix: resolve low-severity findings F-L03, F-L05, and F-L20 with unit tests (Part 3d — batch17 tail)
fix: resolve batch17 low-severity findings (part 3) (Part 3d — batch17)
Fix reviewer-findings-batch17: adversarial review remediation (part 2) (Part 3d — batch17)
fix: resolve 3 CodeQL findings from PR #35 review (Part 3d — EventSinkStream inner subject, useless bindings)
Fix reviewer-findings-batch16: adversarial review remediation (Part 3d — batch16)
Awaited .Stop correctly (Part 3d — trivial async fix)
Fix WidgetSample shutdown, threading and disposal findings (batch15) (Part 3d — batch15)
Address batch15 review findings in backend and web (Part 3d — batch15)
Fix realtime and retro lifecycle regressions (Part 3d — batch15 regression fixes)
Re-pin Docker build actions to Node.js-24 releases (Part 3c — CI action currency, no input changes)
AppenderProxy clarity tweaks from the AI-findings pass (Part 3c — non-behavioural)
Address CodeQL Code Quality findings (Part 3c — nested-if, empty-catch comment, readonly, AppenderProxy state locking)
Centralize TreatWarningsAsErrors in Directory.Build.props (inherited once, not per-project)
Clear DiagnosticService nullable warnings; enable TWAE on the host/demo projects
Document the TreatWarningsAsErrors change in the upstream change doc
Enable TreatWarningsAsErrors on the published library projects (CS warnings fail the build; Sonar stays advisory)
Repackage as 3.2.1 and finalise the upstream change document
Fix dogfood findings — Retro Date index (High), operation-exception unwrap (Medium), UI nits (Low)
CodeQL triage batches 12–14 — genuine fixes (LoggerNotFoundFilter null-guard, dead locals, unused frontend imports); FP/by-design dismissed with rationale
Add superpowers design + plan for the Angular 21 / test-modernization work (docs only)
Fix dotnet-tests CI — correct the setup-dotnet pin
Release v3.2.0 — bump version + this upstream change document
Reviewer-findings batch 10 — final cleanup (M13/M17a/M34, WidgetSample M46/M48/M49/M50)
Harden H1/H2 per adversarial review (fail-closed auth, TLS, hub Origin check)
Reviewer-findings batch 9 — opt-in hub authentication & CORS (H1/H2)
Reviewer-findings batch 8 — hosting lifecycle & concurrency (M22–M28)
Reviewer-findings batch 7 — core-library logic Mediums (M18–M21, M29)
Reviewer-findings batch 6 — Lows sweep (supply-chain, lifecycle, hygiene)
Reviewer-findings batch 5 — test regression + async-trace assessment (M40, M31)
Reviewer-findings batch 4 — Lows cleanup (data-leak, privacy, hygiene)
Reviewer-findings batch 3 — lifecycle, dead-code & frontend-correctness Mediums
Reviewer-findings batch 2 — Medium correctness, lifecycle & metric fixes
Add opt-in hub authentication & CORS design proposal (H1/H2)
Reviewer-findings batch 1 — Critical/High correctness, concurrency & DoS
Add SonarAnalyzer (C#) + eslint-plugin-sonarjs (Angular)
test: expand core-library unit tests (24 → 67) via InternalsVisibleTo
test: add first .NET unit test project for the core library
test: cover RealtimeModel SignalR ingestion and view-state
Fix EventFilterComponent.loadCriteria dropping level flags on inbound bind
ci: add Angular mutation testing and frontend validation
test: cover RealtimeModel process and property-set behaviour
test: cover RetroModel search, filter, select and delete flows
test: cover DiagHubService connection lifecycle and hub calls
test: behaviour coverage for filters and pipes
test: migrate diagnostics-web from Karma to Jest
build: upgrade diagnostics-web Angular 13 → 21 (one commit per major + MDC migration)
test: add frontend characterization coverage
build: pin legacy-peer-deps for the diagnostics-web upgrade
```
