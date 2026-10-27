# DiagnosticService hub authentication & CORS — opt-in design

Status: **implemented** (opt-in, default `AuthMode: None` == prior behaviour). Raised by the
adversarial-review audit (2026-05-29) as findings **H1** (hubs expose mutating/reflective
operations with no authentication) and **H2** (CORS reflects any origin with credentials);
implemented in reviewer-findings batch 9.

## Operator guide — enabling auth

1. Generate one or more API keys (any opaque high-entropy string) and set them in
   `Config/settings.json` under `DiagServiceSettings:Security:ApiKeys`. Leave `AuthMode: None`
   for now — clients still connect without a key.
2. Configure each client to send a key while the server is still in `None` mode (they keep
   working): set `DiagnosticOptions.ApiKey` for `.NET` diagnostic clients, and the SPA's
   `environment.apiKey` (injected as `BASE_API_KEY`).
3. Once every client sends a key, flip `AuthMode: ApiKey` and set
   `AllowedCorsOrigins` to your real SPA origin(s). Now every hub connection (and the
   negotiate request) must present a valid key, and CORS reflects only the allowlist.
4. **Rotate** by adding the new key to `ApiKeys` (both old and new accepted), updating clients,
   then removing the old key.

Keys are matched with a fixed-time comparison and accepted from the `X-Diag-ApiKey` header, an
`Authorization: Bearer` header (SignalR negotiate), or the `access_token` query string (the WS
upgrade). When `AllowedCorsOrigins` is empty the service keeps the permissive any-origin policy
but logs a startup warning.

## Security note — the SPA API key is NOT a secret (threat model)

A cross-vendor adversarial review (2026-05-29) confirmed the limits of the browser side,
so be explicit about what ApiKey mode does and does not provide:

- **The SPA key is shipped in the JavaScript bundle.** `environment.apiKey` is a build-time
  constant baked into `main.js`; any user who can load the dashboard can read it from DevTools
  or the network tab. It is therefore **not confidential** — for browser clients the key only
  blocks *fully anonymous* connections, it does not authenticate a *user* and it cannot be kept
  from anyone able to load the app.
- **What the API key genuinely protects:** the **.NET diagnostic clients** (per-process keys
  held server-side, never shipped to a browser) and *machine-to-machine* callers. For those it
  is a real shared-secret gate.
- **For an internet-facing dashboard,** do not rely on the SPA key for real access control. Put
  the dashboard behind a reverse proxy / IdP (or real user/session auth) and have the server
  mint short-lived per-user tokens after an authenticated request, rather than handing every
  browser the same long-lived shared key.
- **The hardening shipped alongside this** narrows the blast radius: the .NET client refuses to
  send a key over a non-TLS (`http`/`ws`) URL; `AuthMode: ApiKey` fails startup unless both a
  non-empty `ApiKeys` list and an explicit CORS `AllowedCorsOrigins` allowlist are configured;
  and the hub paths validate the `Origin` header directly (CORS does not police the WebSocket
  upgrade). None of that makes the *browser-embedded* key secret — only real user auth does.

## Constraint

The fix **must not break existing trusted-network deployments** or the downstream consumer
flow. Diagnostic clients (`DiagnosticExplorer.Hosting.RegistrationHandler`) and the
Angular SPA currently connect with no credentials. So authentication is introduced as
**opt-in**: the default configuration preserves today's open behaviour, and an operator
turns it on once clients are ready.

## Current state (before implementation)

This section and the checklist below are the pre-implementation design record. The
implementation now exists: see `src/DiagnosticService/Program.cs:57-62` (authentication
scheme), `:166-176` (CORS), `:220-223` (hub `RequireAuthorization`) and
`src/DiagnosticService/Common/ApiKeyAuthenticationHandler.cs`.

- `Program.cs` maps `WebHub` (`/web-hub`) and `DiagnosticHub` (`/diagnostics`) with no
  `app.UseAuthentication()/UseAuthorization()` and no `[Authorize]`.
- `WebHub` exposes `RemoveProcess`, `SetProperty`, `ExecuteOperation`, `RetroDelete`;
  `DiagnosticHub` exposes `Register`, `LogEvents`. `SetProperty`/`ExecuteOperation`
  drive reflective property-set / method-invoke against live monitored objects
  (confirmed in the `lib-core` chunk), so an unauthenticated caller has broad control.
- CORS middleware uses `SetIsOriginAllowed(_ => true).AllowAnyHeader().AllowAnyMethod().AllowCredentials()`.

## Proposed design

### 1. Configuration (`DiagServiceSettings`)

```jsonc
"DiagServiceSettings": {
  "Security": {
    "AuthMode": "None",            // None (default, == today) | ApiKey
    "ApiKeys": [],                 // accepted keys when AuthMode == ApiKey
    "AllowedCorsOrigins": []       // when empty: today's permissive CORS (+ startup warning)
  }
}
```

- `AuthMode: None` (default) → no behavioural change. Existing trusted-network
  deployments and existing consumers keep working untouched.
- `AuthMode: ApiKey` → every hub connection (and the negotiate request) must present a
  valid key.

### 2. API-key authentication (when `AuthMode == ApiKey`)

- Add a minimal API-key authentication handler (header `X-Diag-ApiKey` for HTTP, and
  the SignalR `access_token` query-string for the WebSocket upgrade, since browsers
  can't set headers on the WS handshake).
- Apply an `[Authorize]` policy to both hubs **only when `AuthMode != None`** — register
  the policy conditionally so the attribute is a no-op gate in `None` mode (or guard the
  `MapHub` calls with `.RequireAuthorization()` applied conditionally).
- Keys are compared with a fixed-time comparison; rotate by listing multiple keys.

### 3. CORS (H2)

- When `AllowedCorsOrigins` is non-empty: use `WithOrigins(origins).AllowCredentials()`
  — a real allowlist, no wildcard-with-credentials.
- When empty: keep today's permissive policy **but log a startup warning** that the
  service is accepting credentialed requests from any origin. This preserves current
  behaviour while making the risk visible.

### 4. Client plumbing

- `RegistrationHandler.OpenHub` already accepts a `configureHttp` delegate — add an
  optional API-key option to `DiagnosticOptions` and set the `access_token`/header there
  when configured. (Ties in with finding **M23**: make `UseDefaultCredentials` opt-in at
  the same time, so the default connection neither forwards Windows creds nor assumes a
  key unless configured.)
- The SPA reads its key from runtime config (`environment` / injected token) and passes
  it via `accessTokenFactory` on the `HubConnectionBuilder`.

## Rollout (phased, zero-break)

1. Ship the code with `AuthMode: None` — no behavioural change; verify nothing regresses.
2. Configure `ApiKeys` + update each diagnostic client / the SPA to send a key (clients
   keep working because the server is still in `None` mode).
3. Flip `AuthMode: ApiKey` once all clients send keys; set `AllowedCorsOrigins`.

## Why opt-in rather than mandatory

Making auth mandatory now would break every existing diagnostic client and the downstream
package consumer flow at the moment of deploy. Opt-in lets a network-isolated deployment
stay as-is while an exposed one can be locked down without a flag-day migration.

## Implementation checklist (follow-up batch)

- [x] `DiagServiceSettings.Security` POCO + binding + validation.
- [x] API-key `AuthenticationHandler` (header + `access_token` query for WS).
- [x] Conditional `UseAuthentication/UseAuthorization` + hub `RequireAuthorization`.
- [x] CORS: allowlist when configured, warn-on-permissive otherwise.
- [x] `DiagnosticOptions` API-key option + `RegistrationHandler` plumbing; make
      `UseDefaultCredentials` opt-in (M23).
- [x] SPA `accessTokenFactory` + runtime config.
- [x] Docs: operator guide for enabling auth + rotating keys.
