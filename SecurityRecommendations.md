# Security Recommendations

These items were surfaced by a Semgrep Pro scan of the `diagnostic-explorer`
repository (rule packs: `p/csharp`, `p/javascript`, `p/typescript`,
`p/security-audit`, `p/secrets`, `p/default`).

The scan raised three issues. This file records what the source says about each
today. All three are addressed in the current source.

| Issue | Current state | Evidence |
|---|---|---|
| Unconditional `UseDeveloperExceptionPage()` | Fixed: the page is development-only, with `UseExceptionHandler` otherwise | `src/DiagnosticService/Program.cs:147` and `:151` |
| Missing Subresource Integrity on a CDN script | Fixed: the script tag carries an `integrity` hash and `crossorigin="anonymous"` | `Docker/Utility/test-signalr-connection.html:5` |
| Permissive CORS with credentials | Mitigated, opt-in: an explicit origin allow-list is used when configured; otherwise the permissive policy remains and logs a startup warning | `src/DiagnosticService/Program.cs:166-176` |

## 1. Subresource Integrity on the test utility script

`Docker/Utility/test-signalr-connection.html:5` loads
`https://cdnjs.cloudflare.com/ajax/libs/microsoft-signalr/6.0.1/signalr.min.js`
with a `sha256` `integrity` attribute and `crossorigin="anonymous"`. The URL still
pins an exact version, which SRI requires; bumping the version means regenerating
the hash, or the script stops loading.

## 2. CORS policy

The dead named `"CorsPolicy"` was removed (`Program.cs:42` comment). The policy is
now selected from configuration:

- `DiagServiceSettings:Security:AllowedCorsOrigins` non-empty: the service uses
  `WithOrigins(...)` with `AllowCredentials()` (`Program.cs:166-169`).
- Empty (the default in `src/DiagnosticService/Config/settings.json`): the any-origin
  credentialed policy remains for compatibility and a startup warning is logged
  (`Program.cs:174`).
- With `AuthMode` `ApiKey`, startup fails unless an allow-list and at least one key
  are configured (`Program.cs:127-140`).

So the default configuration is still permissive until an operator sets an allow-list.
Confirm the deployment topology and set `AllowedCorsOrigins` for any deployment that
is reachable from untrusted browsers. See
[`docs/security/hub-authentication-proposal.md`](docs/security/hub-authentication-proposal.md)
for the opt-in authentication and CORS design and its threat model.

## 3. Detailed hub errors

`Program.cs:78` sets `enableDetailedHubErrors` to `builder.Environment.IsDevelopment()`
and `Program.cs:105` applies it to `EnableDetailedErrors`, so detailed hub exception
text is development-only.
