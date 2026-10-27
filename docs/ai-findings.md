# AI Findings Ledger

GitHub's Copilot **AI Findings** set has no dismiss API; its only GitHub-side disposition is the PR
comment's Resolve action, which carries no reason and no comment. This file is the durable record of
the verdict that Resolve cannot hold — a finding present here with status `dismissed` or `fixed` does not need
re-investigation when it resurfaces.

Scope is the AI Findings set only. **Code Quality** findings and **code-scanning security alerts**
are both dismissable on GitHub, which records the verdict durably — do not add rows for those.

Rows first seen before **2026-08-01** are **legacy**: they were recorded under a wider scope and
include Code Quality, CodeQL and analyzer findings that would not be ledgered today. They are kept
as the durable record of a verdict already reached, not as precedent — do not cite them to justify a
new non-AI-Findings row.

Last triage: 2026-06-21 (batch25).

| Finding | Status | Reason | Rationale | First seen |
|---|---|---|---|---|
| `DiagnosticExplorer/Props/PropertyGetter.cs:356` — invalid-format-string | dismissed | false positive | Dynamic format string conditionally includes `{1}` (days) and `{5}` (ms); unused args silently ignored by `string.Format` — no `FormatException` possible | 2026-06-10 |
| `DiagnosticExplorer.Hosting/HubServerAdapter.cs:81` — empty-catch-block | dismissed | by-design | `ObjectDisposedException` on `CTS.Cancel()` in teardown is correct to swallow; comment explains disposal ordering | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:609` — redundant-ToString | fixed | — | Removed `.ToString()` on char; `string + char` works without it | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:619` — redundant-ToString | fixed | — | Same | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:392` — useless-assignment | fixed | — | Removed dead `context_enlargeIn` reassignment at end of outer loop; variable never read after loop closes | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:481` — useless-assignment | fixed | — | Removed dead `next = bits` assignment in `switch`; `next` variable also removed from declaration | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:16` — inefficient-ContainsKey | fixed | — | Refactored to `TryGetValue` + local dict reference; eliminates double-lookup | 2026-06-10 |
| `DiagnosticService/Common/LZString.cs:601` — inefficient-ContainsKey | fixed | — | Refactored to `TryGetValue`; also removed redundant `.ToString()` on `entry[0]` | 2026-06-10 |
| `DiagnosticExplorer/Util/ProtobufUtil.cs:13` — missing-Dispose | fixed | — | Added `using var` to `MemoryStream` | 2026-06-10 |
| `DiagnosticService/Program.cs:19` — path-combine-drops-args | dismissed | false positive | Both args are relative string literals — neither can be absolute | 2026-06-10 |
| `DiagnosticService/Program.cs:215` — path-combine-drops-args | dismissed | false positive | `Path.IsPathRooted` guard ensures `path` is relative before entering the `Combine` branch | 2026-06-10 |
| `DiagnosticService/Common/MongoRetroLogger.cs:138` — implicit-foreach-filter | dismissed | false positive | `TryParse` out-param pattern; equivalent LINQ chain is more complex and less readable | 2026-06-10 |
| `DiagnosticService/Hubs/RealtimeManager.cs:337` — implicit-foreach-filter | dismissed | by-design | State-mutation loop; `.Where().ForEach()` is not idiomatic for mutation | 2026-06-10 |
| `WidgetSample/Form1.cs:436` — gc-collect | dismissed | by-design | Sample code; intentional GC force to demonstrate gadget removal from diagnostics display | 2026-06-10 |
| `WidgetSample/Form1.cs:446` — gc-collect | dismissed | by-design | Same | 2026-06-10 |
| `DiagnosticExplorer.Hosting/DiagnosticClientHandler.cs:15` — missing-Dispose (Subject) | dismissed | won't fix | Rx `Subject<T>` holds no unmanaged resources; app-lifetime subject, disposal not required | 2026-06-10 |
| `DiagnosticExplorer.Hosting/DiagnosticClientHandler.cs:16` — missing-Dispose (Subject) | dismissed | won't fix | Same | 2026-06-10 |
| `DiagnosticService/Hubs/RealtimeManager.cs:32` — missing-Dispose (Subject) | dismissed | won't fix | Service-lifetime singleton Subject; no unmanaged resources | 2026-06-10 |
| `DiagnosticService/Hubs/RealtimeManager.cs:33` — missing-Dispose (Subject) | dismissed | won't fix | Same | 2026-06-10 |
| `DiagnosticExplorer/Trace/TraceScope.cs:121` — missing-Dispose (Timer) | dismissed | false positive | `newTimer` stored in `_autoTraceTimer` via `Interlocked.Exchange`; disposed inline if `_disposed != null`; class manages lifetime via `Dispose()` | 2026-06-10 |
| `WidgetSample/Form1.cs:384,389,394,404,406` — write-to-static-field | dismissed | by-design | Sample code; single-threaded WinForms UI event handlers | 2026-06-10 |
| `DiagnosticService/ClientHandlers/DiagnosticSubscription.cs:33` — write-to-static-field | dismissed | false positive | Uses `Interlocked.Increment` — thread-safe by design; CodeQL does not see through Interlocked | 2026-06-10 |
| `DiagnosticService/Transport/Operation.cs` — IndexOf-without-guard | fixed | — | Added `int i = IndexOf('(')` guard; `Name = i >= 0 ? sig[..i] : sig` | 2026-06-10 |
| `WidgetSample/Form1.cs:666,671` — encoding-artifact (SCOPE TASK strings) | fixed | — | Replaced U+FFFD garbled characters with `###$%` ASCII marker pattern | 2026-06-10 |
| `tests/DiagnosticService.UnitTests/LogAnalyticsRetroLoggerTests.cs:164` — Delete test missing message assertion | fixed | — | Added `.WithMessage("*not supported*")` to `ThrowAsync<NotSupportedException>` | 2026-06-10 |
| Generic catch — all `catch (Exception)` in diagnostic/logging infrastructure (~105 instances) | dismissed | by-design | Appenders must not throw (log4net contract); diagnostic walkers must degrade gracefully; `Dispose()` methods must not throw. Every catch logs or degrades to an error string. Files: DiagnosticHostingService, WebApiUtil, EventSink, DiagnosticManager, AppenderProxy, AsyncProcessor, SmtpAppender, DateGetter, CollectionGetter, ExtendedPropertyGetter, RateGetter, RateCounter, PropertyGetter, TraceScope, HubServerAdapter, RegistrationHandler, DiagnosticSubscription, WebClientHandler, MongoRetroLogger, RealtimeManager, RetroSearchProcess, LoggingExtensions, Form1 | 2026-06-10 |
| `DiagnosticService/ClientHandlers/WebClientHandler.cs:180` — manual-dispose-in-finally | fixed | — | Replaced `eventStreamCancel.Dispose()` in finally with `using (eventStreamCancel)` wrapper | 2026-06-15 |
| `DiagnosticExplorer/Events/EventSink.cs:149` — empty-while-body | dismissed | by-design | Queue drain pattern; `TryDequeue` is the side-effecting predicate — empty body is intentional | 2026-06-15 |
| `DiagnosticExplorer/Events/EventSinkStream.cs:74` — empty-catch-ObjectDisposedException | dismissed | by-design | Race between subject disposal and stream event delivery; swallowing is correct (same as HubServerAdapter.cs:81) | 2026-06-15 |
| `DiagnosticService/Hubs/RetroSearchProcess.cs:33` — empty-catch-ObjectDisposedException | dismissed | by-design | CTS may be disposed before Cancel() is called during teardown; swallowing is correct | 2026-06-15 |
| `DiagnosticExplorer/DiagnosticManager.cs:452` — implicit-foreach-filter | dismissed | false positive | `yieldedNames.Add()` is a side-effecting predicate; cannot lift to `.Where()` without breaking add-on-first-seen semantics; outer `.Where(p => ShouldIncludeProperty(...))` is already present | 2026-06-15 |
| `DiagnosticExplorer.Hosting/SystemStatus.cs:67,76` — wrong-category-label (AI Finding) | fixed | — | `VirtualMemory` and `Memory` had `Category = "CPU"`; corrected to `"Memory"` | 2026-06-15 |
| `tests/DiagnosticService.UnitTests/RetroSearchLifecycleTests.cs` — polling-loop + missing-cleanup (AI Finding) | fixed | — | Replaced 2s polling loop with `TaskCompletionSource` in `StartRetroSearch` test; added try-finally `StopAsync` guard to both async tests | 2026-06-15 |
| GitHub CI workflows (ci.yml, dotnet-tests.yml, mutation-web.yml) — missing-permissions (CodeQL) | fixed | — | Added `permissions: contents: read` at workflow level | 2026-06-15 |
| `DiagnosticExplorer/Props/CollectionGetter.cs:265` — useless-assignment | fixed | — | Removed `dummy1` variable; replaced `out dummy1` with `out _` discard — initial assignment to `obj` was dead | 2026-06-21 |
| `DiagnosticExplorer/Props/CollectionGetter.cs:268` — useless-assignment | fixed | — | Removed `dummy2` variable; replaced `out dummy2` with `out _` discard — same pattern | 2026-06-21 |
