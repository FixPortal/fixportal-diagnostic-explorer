# Spec / build prompt: Log Analytics backend for Retro

**Status:** Implemented on `feat/retro-log-analytics-backend` (write + read paths). Live integration test + Angular delete-gating still pending.
**Date:** 2026-06-07
**Companion doc:** `docs/retro-vs-log-analytics-decision.md` (the why)
**Branch:** `feat/retro-log-analytics-backend` (FixPortal fork). Lands as an **opt-in** backend — Mongo stays the default; validate the two open questions before relying on LA in production.

### Build status (what landed)

- `LogAnalyticsRetroLogger : IRetroLogger` — write via Logs Ingestion API, read via KQL, `Delete` throws `NotSupportedException`, `SupportsDelete => false`.
- `IRetroLogger.SupportsDelete` capability flag added; `MongoRetroLogger` returns `true`.
- `DiagServiceSettings`: `LogAnalytics` options + `"loganalytics"` factory case. `Config/settings.json` carries an empty `LogAnalytics` block (RetroType stays `mongo`).
- `infra/retro-loganalytics.bicep` — authored for the target tenant (eastus2); compiles clean.
- Packages: `Azure.Identity`, `Azure.Monitor.Ingestion` 1.2.0, `Azure.Monitor.Query` 1.7.1.
- Tests: `LogAnalyticsRetroLoggerTests` (12) green; full `DiagnosticService.UnitTests` suite 22/22 green; service builds.
- **Remaining:** (1) deploy the Bicep + dedicated SP and run the live round-trip / truncation test; (2) surface `SupportsDelete` to the Angular client to hide the delete button under LA (today LA `Delete` just fails safe with an error toast).

---

## 1. Goal

Add a **second, pluggable Retro backend** that writes and reads diagnostic log records against **Azure Log Analytics**, selectable by config, **without removing the existing MongoDB backend**.

This is an enlightenment / A-B experiment, not a migration. The MongoDB backend stays the default and remains the way Retro runs locally.

## 2. Non-goals

- **Not** replacing Mongo. Mongo stays the local default (see constraint below).
- **Not** touching the real-time diagnostic property-tree explorer (`RealtimeManager` / `GetDiagnostics`) — that is a separate subsystem and out of scope.
- **Not** changing the SignalR transport, the Angular UI data flow, or the `RetroMsg`/`DiagnosticMsg`/`RetroQuery` wire types (beyond what backend-capability gating requires — see Delete).
- **Not** a production cutover. No data migration from Mongo to LA.

## 3. Hard constraint: local must stay cheap

Until the entire EMS estate is in the cloud, running EMS locally against a *hosted* Retro would be cost-prohibitive (every local EMS process shipping logs to a billed LA workspace). Therefore:

- Local/dev/docker default **must remain `RetroType=mongo`** against the local container Mongo.
- The LA backend is **opt-in only**, activated by config, for the cloud deployment and for deliberate local experimentation.
- The two backends must coexist in one codebase with no build-time divergence — pure runtime selection.

This is exactly why the design is "add a backend," not "swap the backend."

---

## 4. The insertion point (already exists)

The abstraction is already in place. No new seam needs inventing.

- **Interface:** `DiagnosticService/Common/IRetroLogger.cs`
  ```csharp
  IAsyncEnumerable<RetroMsg[]> GetMessages(RetroQuery query, CancellationToken cancel);
  Task WriteMessages(ICollection<DiagnosticMsg> msg, CancellationToken cancel);
  Task<long> Delete(string[] idList);
  ```
- **Factory / switch:** `DiagnosticService/Common/DiagServiceSettings.cs:16` `CreateRetroLogger()` — currently a `RetroType` switch with a single `"mongo"` case. **Add a `"loganalytics"` case here.**
- **Consumer:** `DiagnosticService/Hubs/RetroManager.cs` resolves `IRetroLogger` via the factory and drives write (buffered channel → `WriteMessages`) and read (`GetMessages` → `RetroSearchProcess` → SignalR).

Implementation = one new class `LogAnalyticsRetroLogger : IRetroLogger` + one factory case + config + Azure resources.

---

## 5. Azure resources required

Stand these up via Bicep (preferred) so the experiment is reproducible. A dev/test-tier workspace keeps cost negligible at experiment volume.

| Resource | Purpose |
|----------|---------|
| Log Analytics workspace | Stores + serves the records (KQL). |
| Custom table `DiagRetro_CL` | Target table; columns mirror `RetroMsg` (see §8). |
| Data Collection Endpoint (DCE) | Ingestion endpoint URL for the Logs Ingestion API. |
| Data Collection Rule (DCR) | Declares incoming stream schema + transform + destination table. Gives the immutable ID used at write time. |
| Role: **Monitoring Metrics Publisher** on the DCR | For the writer identity (ingestion). |
| Role: **Log Analytics Reader** on the workspace | For the reader identity (query). |
| Managed identity (ACA) / `az login` (local) | Resolved via `DefaultAzureCredential`. |

> Read `~/.claude/notes/deploy-and-ci-traps.md` before authoring the Bicep, and `~/.claude/notes/dotnet-runtime-traps.md` for the SignalR-from-tests notes, per house rules.

---

## 6. Write path — Logs Ingestion API

The modern path. (The legacy HTTP Data Collector API is deprecated/retiring — do **not** use it.)

- **Package:** `Azure.Monitor.Ingestion`, `Azure.Identity`.
- **Client:** `new LogsIngestionClient(new Uri(dceEndpoint), new DefaultAzureCredential())`.
- **Call:** `await client.UploadAsync(dcrImmutableId, streamName, batch, cancel)` where `batch` is the mapped records. The SDK batches + gzips internally; respects the existing `RetroManager` write-channel buffering, so `WriteMessages(ICollection<DiagnosticMsg>)` maps the collection → anonymous/typed objects matching the DCR stream and uploads in one call (or chunks if over the SDK's per-call size cap).
- **Mapping:** `DiagnosticMsg` → stream object. `Date` → `TimeGenerated` (mandatory column, ISO-8601 UTC). Other fields → custom columns (§8).
- **Auth role:** Monitoring Metrics Publisher on the DCR.
- **Latency:** ingestion is eventual (typically minutes). Acceptable — Retro only ever queries *past* windows (see decision doc). But it means **read-after-write is not immediate** — important for tests (§11).

## 7. Read path — Log Analytics Query API

- **Package:** `Azure.Monitor.Query`.
- **Client:** `new LogsQueryClient(new DefaultAzureCredential())`.
- **Call:** `await client.QueryWorkspaceAsync(workspaceId, kql, new QueryTimeRange(start, end), cancel)`.
- **KQL generation** from `RetroQuery` (parity with `MongoRetroLogger.GetMessages`):
  ```kql
  DiagRetro_CL
  | where TimeGenerated between (datetime(START) .. datetime(END))
  | where Level >= MINLEVEL
  | where isempty('MACHINE') or Machine matches regex @"MACHINE"
  | where isempty('PROCESS') or Process matches regex @"PROCESS"
  | where isempty('USER')    or User    matches regex @"USER"
  | where isempty('MESSAGE') or Message matches regex @"MESSAGE"
  | sort by TimeGenerated desc
  | take MAXRECORDS
  ```
  - Build with **parameterised / properly-escaped values** — never string-concatenate raw user input into KQL. Use the SDK's parameter support or strict escaping to avoid KQL injection.
  - `matches regex` mirrors the Mongo regex filters. Watch query cost on high-cardinality regex; fine at experiment scale.
- **Map results back** to `RetroMsg[]` and honour the `IAsyncEnumerable<RetroMsg[]>` streaming contract (chunk the tabular result into batches — e.g. 250 to match the Mongo path — so `RetroSearchProcess`/SignalR streaming UX is unchanged).
- **Auth role:** Log Analytics Reader on the workspace.

## 8. Field mapping

| `RetroMsg` / `DiagnosticMsg` | `DiagRetro_CL` column | Type | Notes |
|------------------------------|------------------------|------|-------|
| `Date` | `TimeGenerated` | datetime | Mandatory LA column. |
| `Level` | `Level` | int | log4net severity. |
| `Machine` | `Machine` | string | |
| `Process` | `Process` | string | |
| `User` | `User` | string | |
| `Category` | `Category` | string | logger name. |
| `Message` | `Message` | string | **Truncation risk — see §10.** Carries the trace-scope text. |
| `Environment` | `Environment` | string | optional. |
| `RecordId` (ObjectId) | — | — | Mongo-specific; no LA column. **Implemented:** synthesised via `ObjectId.GenerateNewId()` per row on read, so `RetroMsg.MsgId` is unique within a result set for UI selection. There is no app-level id to persist (so no `MsgId` column). |

## 9. Delete path — the one real gap

LA is append-only. There is **no interactive selective delete** (only per-table retention/TTL, or the restricted async workspace **purge** API — GDPR-oriented, requires Data Purger role, not per-search).

**Decision (recommended):** `LogAnalyticsRetroLogger.Delete(...)` throws `NotSupportedException`, **and** the backend advertises a capability so the UI hides the Delete button when the active backend is LA.

- Add a lightweight capability signal (e.g. `bool SupportsDelete { get; }` on `IRetroLogger`, surfaced to the Angular client via an existing config/handshake message) so `RetroModel.canDelete` returns false under LA. This is the only UI-facing change.
- Rationale: per the decision doc, interactive delete is most likely a Mongo-era crutch for missing retention. Under LA, native per-table retention/TTL replaces it. **Open question O-2 (§13) must be answered before finalising** — if users genuinely curate logs by hand, this gap is a blocker and the answer might be "keep Mongo for that estate."

---

## 10. Known risks / things to verify

1. **Message truncation (highest risk).** LA imposes size limits on string/dynamic column values and rows. The trace-scope tree lives inside `Message`; if LA truncates large messages, the client-side tree parse degrades. **Verify against the largest real production messages.** Mitigations if it bites: split `Message`/`Detail` into separate columns, store oversized detail compressed, or cap+flag. Track this as the gating acceptance test.
2. **Table tier vs query features.** Analytics tier supports full KQL (`sort`, `matches regex`). Basic/Auxiliary tiers are cheaper for high-volume ingest but offer a *restricted* query experience — confirm `sort` + `matches regex` + `take` are all available on the chosen tier, or use Analytics tier (trivial cost at experiment volume). **Decision O-3.**
3. **Ingestion latency** (~minutes). Fine functionally; affects test design (§11).
4. **Cost shape.** LA bills per-GB ingested; a real EMS firehose is non-trivial — hence the local-stays-Mongo constraint. Keep the experiment workspace low-retention.
5. **KQL injection.** User-supplied filter/regex fields must be parameterised/escaped (§7).
6. **`RecordId` vestigial** under LA — ensure nothing downstream hard-depends on it when the backend is LA.

## 11. Testing approach

- **Unit:** KQL generation from `RetroQuery` (golden-string / parameter assertions); result-row → `RetroMsg[]` mapping; batching into the `IAsyncEnumerable` contract. Mock the Azure clients. (xUnit v3 + NSubstitute + AwesomeAssertions per house standard; match the existing test project's framework.)
- **Integration (manual / gated):** against a real dev workspace — write a batch, **wait out ingestion latency**, then query and assert round-trip + message-text fidelity (the truncation test). Do **not** assert read-after-write immediately.
- Reuse/extend the existing `RetroSearchLifecycleTests` patterns for the search lifecycle over the LA backend.

## 12. Config

Extend `DiagServiceSettings` and `appsettings`:

```jsonc
// Local default (docker / dev) — unchanged
"Diag": { "RetroType": "mongo", "RetroConnection": "mongodb://..." }

// LA profile (cloud / experiment) — appsettings.LogAnalytics.json or env
"Diag": {
  "RetroType": "loganalytics",
  "LogAnalytics": {
    "DceEndpoint": "https://<dce>.<region>.ingest.monitor.azure.com",
    "DcrImmutableId": "dcr-xxxxxxxx",
    "StreamName": "Custom-DiagRetro_CL",
    "WorkspaceId": "<guid>"
    // credential resolved via DefaultAzureCredential (managed identity in ACA, az login locally)
  }
}
```

`CreateRetroLogger()` adds:
```csharp
case "loganalytics":
    return new LogAnalyticsRetroLogger(LogAnalytics); // bind the section to a typed options object
```

---

## 13. Open decisions (answer before/while building)

- **O-1 — Build scope now:** write+read together, or read-first against a workspace that's already being fed by Azure Monitor agents? (Read-first is a faster path to "see it working" if logs already land in LA by other means.)
- **O-2 — Delete intent (from decision doc):** is interactive delete a real workflow or a retention crutch? Determines whether the `NotSupportedException` + hidden-button approach is acceptable, or whether this estate must stay on Mongo. **Blocking.**
- **O-3 — Table tier:** Analytics (full KQL, slightly costlier) vs Basic/Auxiliary (cheap ingest, restricted query). Verify `sort`/`matches regex` availability.
- **O-4 — Message truncation handling** if §10.1 bites: single column + accept caps, vs split `Message`/`Detail`, vs compress.

---

## 14. Implementation checklist (the returnable build prompt)

> On a `feat/retro-log-analytics-backend` branch in a **feature worktree** of the FixPortal fork (not the reviewer-passes worktree). Read the Azure/CI and .NET-runtime trap notes first.

1. [ ] Bicep: workspace + `DiagRetro_CL` custom table + DCE + DCR (stream schema per §8) + role assignments (Monitoring Metrics Publisher on DCR, Log Analytics Reader on workspace). **Full module in Appendix A — authored for the target tenant.**
2. [ ] Add NuGet: `Azure.Monitor.Ingestion`, `Azure.Monitor.Query`, `Azure.Identity` to `DiagnosticService`.
3. [ ] Typed options class for the `LogAnalytics` config section; bind in `DiagServiceSettings`.
4. [ ] `LogAnalyticsRetroLogger : IRetroLogger`:
   - [ ] `WriteMessages` → `LogsIngestionClient.UploadAsync` (map + chunk).
   - [ ] `GetMessages` → build KQL (parameterised), `LogsQueryClient.QueryWorkspaceAsync`, map rows → `RetroMsg[]`, yield in 250-batches.
   - [ ] `Delete` → `throw new NotSupportedException(...)`.
5. [ ] Capability flag (`SupportsDelete`) on `IRetroLogger`; surface to Angular handshake; gate `RetroModel.canDelete`.
6. [ ] `CreateRetroLogger()`: add `"loganalytics"` case.
7. [ ] `appsettings.LogAnalytics.json` (+ keep local default `mongo`).
8. [ ] Unit tests: KQL generation, result mapping, batching, delete-not-supported. (xUnit v3 / NSubstitute / AwesomeAssertions.)
9. [ ] Manual integration test against dev workspace: round-trip + **message-text fidelity (truncation)** + ingestion-latency awareness.
10. [ ] Verify local path still defaults to Mongo and is untouched (regression).
11. [ ] Document findings (truncation result, tier choice, cost observed) back into this doc and the decision doc.

---

## 15. My recommendation

Low-risk to land **because** the seam already exists and the local Mongo default is preserved — it ships off by default and disturbs nothing until someone sets `RetroType=loganalytics`. The load-bearing part is the **write path** (DCE/DCR/Logs Ingestion API + Entra auth) and confirming **message-text fidelity** end-to-end; the read path is routine KQL. Before LA is relied on in production, the truncation test must pass and O-2 (delete workflow) must resolve in LA's favour.

---

## Appendix A — Bicep for the target tenant

Authored to match the host platform's existing infrastructure convention: `targetScope = 'resourceGroup'`, `envName` (≤13, lowercase) + `location = resourceGroup().location`, workspace API `2023-09-01` / `PerGB2018` / 30-day retention, naming `log-<app>-<envName>`. This module uses `log-diag-<envName>` etc. for the diagnostics app.

**Authoritative file:** `infra/retro-loganalytics.bicep` (committed on this branch; compiles clean). The block below is a reference copy — if they drift, the file wins.

### Decisions baked in (override via params if needed)

- **Dedicated workspace** (`log-diag-<env>`), not the shared `log-qfservice-<env>`. Cleaner cost isolation + throwaway for an experiment. To reuse the qfservice workspace instead, delete the `la` resource and pass its existing resource ID in.
- **Table plan `Analytics`** (not Basic) so `sort` / `matches regex` / `take` all work (decision O-3). Trivial cost at experiment volume.
- **RBAC included** — Monitoring Metrics Publisher on the DCR (ingest) + Log Analytics Reader on the workspace (query), assigned to the DiagnosticService identity you pass in.

```bicep
targetScope = 'resourceGroup'

@description('Short environment suffix, lowercase alphanumeric (matches ems-win-app convention, e.g. "dev", "exp").')
@maxLength(13)
param envName string

param location string = resourceGroup().location

@description('Object (principal) ID of the identity the DiagnosticService runs as — the ACA system-assigned MI, or a user-assigned MI. Receives ingest + query rights.')
param diagServicePrincipalId string

@description('Retention (days) for both the workspace and the Retro custom table.')
param retentionInDays int = 30

var laName     = 'log-diag-${envName}'
var dceName    = 'dce-diag-${envName}'
var dcrName    = 'dcr-diag-${envName}'
var tableName  = 'DiagRetro_CL'
var streamName = 'Custom-DiagRetro_CL'

// Built-in role definition IDs
var monitoringMetricsPublisherId = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '3913510d-42f4-4e42-8a64-420c390055eb')
var logAnalyticsReaderId         = subscriptionResourceId('Microsoft.Authorization/roleDefinitions', '73c42c96-874c-492b-b04d-ab87d138a893')

// Column schema shared by the table and the DCR stream declaration (keep these in sync).
var retroColumns = [
  { name: 'TimeGenerated', type: 'datetime' } // mandatory LA column; maps from DiagnosticMsg.Date
  { name: 'Level',        type: 'int' }
  { name: 'Machine',      type: 'string' }
  { name: 'Process',      type: 'string' }
  { name: 'User',         type: 'string' }
  { name: 'Category',     type: 'string' }
  { name: 'Message',      type: 'string' } // carries trace-scope text — watch truncation (spec §10.1)
  { name: 'Environment',  type: 'string' }
]

resource la 'Microsoft.OperationalInsights/workspaces@2023-09-01' = {
  name: laName
  location: location
  properties: {
    sku: { name: 'PerGB2018' }
    retentionInDays: retentionInDays
  }
}

resource retroTable 'Microsoft.OperationalInsights/workspaces/tables@2023-09-01' = {
  parent: la
  name: tableName
  properties: {
    plan: 'Analytics'
    retentionInDays: retentionInDays
    schema: {
      name: tableName
      columns: retroColumns
    }
  }
}

resource dce 'Microsoft.Insights/dataCollectionEndpoints@2023-03-11' = {
  name: dceName
  location: location
  properties: {
    networkAcls: { publicNetworkAccess: 'Enabled' }
  }
}

resource dcr 'Microsoft.Insights/dataCollectionRules@2023-03-11' = {
  name: dcrName
  location: location
  // The custom table must exist before the DCR can target it as a destination output stream.
  dependsOn: [ retroTable ]
  properties: {
    dataCollectionEndpointId: dce.id
    streamDeclarations: {
      '${streamName}': {
        columns: retroColumns
      }
    }
    destinations: {
      logAnalytics: [
        {
          name: 'diagLa'
          workspaceResourceId: la.id
        }
      ]
    }
    dataFlows: [
      {
        streams: [ streamName ]
        destinations: [ 'diagLa' ]
        transformKql: 'source'      // identity transform; columns already match the table
        outputStream: streamName    // 'Custom-<TableName>' routes to the custom table
      }
    ]
  }
}

// --- RBAC for the DiagnosticService identity ---
// Ingest: Monitoring Metrics Publisher on the DCR.
resource publisherAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(dcr.id, diagServicePrincipalId, monitoringMetricsPublisherId)
  scope: dcr
  properties: {
    roleDefinitionId: monitoringMetricsPublisherId
    principalId: diagServicePrincipalId
    principalType: 'ServicePrincipal'
  }
}

// Query: Log Analytics Reader on the workspace.
resource readerAssign 'Microsoft.Authorization/roleAssignments@2022-04-01' = {
  name: guid(la.id, diagServicePrincipalId, logAnalyticsReaderId)
  scope: la
  properties: {
    roleDefinitionId: logAnalyticsReaderId
    principalId: diagServicePrincipalId
    principalType: 'ServicePrincipal'
  }
}

// --- Outputs map 1:1 to the app config block in §12 ---
output dceEndpoint        string = dce.properties.logsIngestion.endpoint // -> LogAnalytics:DceEndpoint
output dcrImmutableId     string = dcr.properties.immutableId            // -> LogAnalytics:DcrImmutableId
output streamName         string = streamName                            // -> LogAnalytics:StreamName
output workspaceId        string = la.properties.customerId              // -> LogAnalytics:WorkspaceId (query API)
output workspaceResourceId string = la.id
```

### Deploy

```powershell
az deployment group create -g <target-rg> -f infra/retro-loganalytics.bicep -p envName=exp diagServicePrincipalId=<diag-mi-object-id>
```

Read the outputs back into `appsettings.LogAnalytics.json` (or ACA env / Key Vault):

```powershell
az deployment group show -g <target-rg> -n retro-loganalytics --query properties.outputs
```

### Prerequisites / traps (per `~/.claude/notes/deploy-and-ci-traps.md`)

- **Deploying principal needs `Role Based Access Control Administrator`** (or User Access Administrator) on the RG — `Contributor` excludes `Microsoft.Authorization/roleAssignments/write`, so the two role assignments will 403 otherwise.
- **Resource providers** `Microsoft.OperationalInsights` and `Microsoft.Insights` must be registered on the subscription (fresh subs have none).
- **RBAC propagation lag ~30–60s** — the first ingest/query from the app may fail right after deploy; retry.
- **Confirm with the operator before deploy:** target **resource group**, **`envName`**, and the **DiagnosticService identity object ID** (`diagServicePrincipalId`). These are the only tenant-specific values; everything else follows convention. The live RG/subscription are deliberately not recorded here — fill them at deploy time.
- **Ingestion latency** (minutes) means the first round-trip test won't see records immediately (spec §6, §11).
