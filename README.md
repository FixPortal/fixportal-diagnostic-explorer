# DiagnosticExplorer

> Start here: the repository ships a multi-targeted diagnostics library, its
> hosting integration, a standalone SignalR service, and the Angular dashboard.

DiagnosticExplorer is a .NET diagnostic instrumentation toolkit and an
accompanying web-based viewer service. Application code emits property
bags, operations, and events via the `DiagnosticExplorer` library;
those get pushed over SignalR to a central hosting service which fans
them out to a browser-based dashboard for live inspection across a
fleet of running processes.

For the current fluent configuration and logging adapters, see the
[agent configuration guide](docs/agent-configuration-guide.md) and the
[runnable logging examples](samples/Logging/README.md). The
[upstream integration status](docs/upstream-integration-status.md) records what
has landed and the deliberate hosting differences from upstream.

The project originated as Cameron Elliot's open-source diagnostic
toolset around 2010 (LGPL v3+) and has been carried forward as the
diagnostic backbone for a production EMS trading platform and its
surrounding services.

## Repository layout

```
src/DiagnosticExplorer/      net10.0 / net48 core library
                             - PropertyBag, TraceScope, OperationSet,
                               MessagePack transfer types, log4net forwarding
src/DiagnosticExplorer.Hosting/
                             net10.0 / net48 hosting integration
                             - AddDiagnosticExplorer DI extension,
                               DiagnosticHostingService, RegistrationHandler
src/DiagnosticService/       net10.0 standalone web service (Docker payload)
                             - ASP.NET Core + SignalR hubs + SPA host
diagnostics-web/             Angular 22 SPA (the dashboard UI)
Docker/                      Dockerfile and compose YAMLs for the service
src/WidgetSample/            net10.0-windows / net48 WinForms demo
src/ConsoleApp/              net10.0 CLI demo
```

## Using the library

Add the package reference:

```xml
<PackageReference Include="DiagnosticExplorer.Hosting" Version="4.0.0" />
```

Wire into a `Host.CreateDefaultBuilder` pipeline:

```csharp
services.AddDiagnosticExplorer(context.Configuration);
```

For SignalR connections that need custom configuration (e.g. an Azure
AD bearer token), pass an `Action<HttpConnectionOptions>`:

```csharp
services.AddDiagnosticExplorer(
    context.Configuration,
    options => options.AccessTokenProvider = GetCurrentAccessToken);
```

Static start (for non-DI hosts):

```csharp
DiagnosticHostingService.Start(
    "http://diagnostics:2803/diagnostics",
    options => options.AccessTokenProvider = GetCurrentAccessToken);
```

Required configuration:

```json
{
  "DiagnosticExplorer": {
    "Uri": "http://diagnostics:2803/diagnostics",
    "Enabled": true
  }
}
```

The `Uri` may be a comma-or-semicolon-separated list of hub URLs if you
want a single application to report to multiple diagnostic servers.

### Tracing scopes

```csharp
using (var scope = new TraceScope(Log.Info))
{
    TraceScope.Trace("Loaded {0} records", count);
    // ... work ...
    TraceScope.Trace("Completed in {0}ms", elapsed);
}
```

`TraceScope` flows through `AsyncLocal`, so nested async calls share
the same scope automatically.

## Running the service

Two compose files under `Docker/`:

Before using the local-build compose file, set `GITHUB_PACKAGES_TOKEN` in the
current shell to a GitHub token with `read:packages` access to the FixPortal
package feed. Compose mounts it only for the restore step; it is not stored in
the image.

```bash
# Build the image locally and bring up service + MongoDB:
docker compose -f Docker/compose-and-create-image.yaml up -d --build

# Or pull the published image from ghcr.io:
docker compose -f Docker/compose-with-existing-image.yaml up -d
```

Then open `http://localhost:2803/` for the dashboard.

Environment-variable overrides documented inline at the top of each
compose file. Most-useful:

| Variable | Default | Purpose |
|---|---|---|
| `GITHUB_PACKAGES_TOKEN` | required for local builds | Read the private FixPortal analyzer packages during image restore |
| `DIAGEXPLORER_HOST_PORT` | `2803` | Host-side port mapping if 2803 is in use locally |
| `MONGO_USERNAME` / `MONGO_PASSWORD` | `admin` / none (required) | Mongo root credentials |
| `MONGO_HOST_PORT` | `27017` | Host-side port mapping if a native `mongod` already holds 27017 |
| `DIAGEXPLORER_IMAGE_NAME` | `ghcr.io/fixportal/diagnostic-explorer` | Repoint at a fork's GHCR namespace |
| `DIAGEXPLORER_IMAGE_TAG` | `latest` | Pin to a specific GHCR tag (e.g. `3.1.38`) |

The service listens on port `2803` inside the container. Settings
default to a Mongo backend at `mongodb:27017` (the sidecar in compose);
override `DiagServiceSettings__RetroConnection` to point at a
different store.

## Building from source

```bash
dotnet tool restore
dotnet restore DiagnosticExplorer.slnx --force --no-cache
dotnet csharpier check .
dotnet build DiagnosticExplorer.slnx --configuration Release --no-restore
dotnet test --solution DiagnosticExplorer.slnx --configuration Release --no-build
dotnet format DiagnosticExplorer.slnx --verify-no-changes --no-restore --exclude src/WidgetSample
jb inspectcode DiagnosticExplorer.slnx --no-build --output=.claude/scratch/inspectcode.xml

# Angular dashboard (Node 22.22.3, matching CI):
cd diagnostics-web
npm ci
npm test -- --runInBand
npm run lint
npm run build
```

`DiagnosticExplorer.slnx` includes the WinForms sample for orientation, but
excludes it from cross-platform build configurations because its intentional
`net10.0-windows` / `net48` targets require Windows; validate it separately with
`dotnet build src/WidgetSample/WidgetSample.csproj --configuration Release`.
The Roslyn formatter check also excludes that project: `dotnet format` cannot
construct its multi-target workspace even though both targets build. CSharpier
still checks the sample; remove this exception when `dotnet format` can load it.
The `net48` library and hosting targets are compatibility contracts for existing
consumers and remain supported alongside the current `net10.0` targets.

`diagnostics-web/.npmrc` pins `legacy-peer-deps=true`, so `npm ci` resolves the
dependency graph without an explicit flag (the Angular build tooling's peer
range for Tailwind lags the installed Tailwind 3).

### .NET unit tests

The core library and service are covered by xUnit v3 suites under `tests/`
(NSubstitute + AwesomeAssertions). Both are part of `DiagnosticExplorer.slnx`
and run on every push/PR via GitHub Actions.

Coverage spans the public surface — `PropertyBag`/`Property`/`Category`,
MessagePack wire round-tripping, the JSON converters, `AttributeUtil`,
`WeakReferenceHash`, `EventSink`/`EventSinkRepo`, and the `TraceScope` tracing
hierarchy — and two internal helpers, `ScopeStack` and `TypeUtil`. The library
grants the test project access to its internals via an `InternalsVisibleTo`
entry in `DiagnosticExplorer.csproj`; the generated attribute ships in the
assembly but only names the test project, so it exposes nothing else.

```bash
dotnet test --solution DiagnosticExplorer.slnx --configuration Release
```

Run the opt-in property-snapshot wire qualification separately; it reports the median of three full 10,000-item snapshot/compress/decompress iterations without enforcing a machine-specific timing threshold:

```powershell
dotnet run --project tests/DiagnosticExplorer.UnitTests/DiagnosticExplorer.UnitTests.csproj --configuration Release -- --explicit only --filter-method DiagnosticExplorer.UnitTests.PropertyGetterTests.PropertySnapshotWireQualificationProcessesCollectionLimit --show-live-output on --output Detailed
```

Both test projects target `net10.0` and run cross-platform. In Visual Studio,
open `DiagnosticExplorer.slnx`, build the solution, then use **Test Explorer >
Run All** to run both suites.

### Frontend tests and mutation analysis

The dashboard is tested with **Jest** (`jest-preset-angular`); Karma has been
removed.

```bash
cd diagnostics-web

# Unit tests with coverage:
npm test

# Mutation analysis (StrykerJS over the Jest suite):
npm run test:mutation
```

Stryker writes its report to `diagnostics-web/reports/mutation/`.
`scripts/summarize-stryker.ps1` condenses `mutation.json` into a compact
JSON/Markdown summary, which the `mutation-web` GitHub Actions workflow posts to
the run's job summary. The `publish-docker-image` workflow runs `npm ci`,
`npm test`, and `npm run build` as a frontend gate before building the image.

The shipped libraries have `<GeneratePackageOnBuild>true</GeneratePackageOnBuild>`,
so a Release build produces the NuGet packages under each project's
`bin/Release/`.

Vulnerability check:

```bash
dotnet list DiagnosticExplorer.slnx package --vulnerable --include-transitive
```

(Should report no vulnerable packages as of `3.2.2`.)

## Contributing

Create a branch from the current `main`, keep commits focused, and rebase before
opening a pull request. Format C# changes before running the checks above:

```bash
dotnet csharpier format .
```

## Container image

Published to `ghcr.io/fixportal/diagnostic-explorer` by the GitHub
Actions workflow at `.github/workflows/publish-docker-image.yml`.

Triggers:
- `push` to `main` → tags `latest`, `main`, `sha-<short>`
- `push` of a `v*.*.*` git tag → tags the matching semver + `major.minor`
- `workflow_dispatch` → manual publish from any ref
- `pull_request` against `main` → build-validate only, no push

The image is `linux/amd64`. The GHCR package's visibility inherits
the source repo on first publish (public repo → public package,
private repo → private package). Override at any time from the
package's settings page on GitHub.

## Releases

The current release is [**4.0.0**](https://github.com/FixPortal/fixportal-diagnostic-explorer/releases/tag/v4.0.0).
Download the packages and checksums from the release; EMS vendors the core and hosting
packages through its local folder feed. See the [release and migration notes](docs/releases/4.0.0.md)
before upgrading from **3.2.2**. Package publication does not establish consumer rollout.
The libraries preserve the intentional `net48`
compatibility targets while the current application and test scaffold use
`.NET 10`.

Versions are tagged `v{semver}` (e.g. `v3.2.2`); pushing the tag
triggers a container-image publish to GHCR.

## License

LGPL v3 or later — see `LICENSE` and the file headers in
`src/DiagnosticExplorer/`.
