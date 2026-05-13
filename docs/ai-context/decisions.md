# Decisions

Durable project decisions that future Claude sessions must remember.

---

## 2026-05-13 — Kerberos/RBCD/KCD přístup zamítnut, pivot na Gateway Pass-Through

Status: accepted

Decision:
Kerberos Constrained Delegation (KCD/RBCD) se pro SSO nepoužije. Místo toho zkoumáme **NetScaler Gateway Pass-Through** (`/GatewayAuth/Login`).

Reason:
- `DomainPassthroughAuth/Login` vyžaduje reálný Kerberos/NTLM handshake na úrovni HTTP spojení — server-side `HttpClient` to bez RBCD v AD nedokáže. Výsledek je vždy `fatalerror`.
- NTLM nejde delegovat (connection-oriented, single-hop).
- RBCD nebylo nakonfigurováno za týden pokusů, AD admin nezasahoval.
- Výzkum potvrdil: Gateway Pass-Through přes NetScaler (`/GatewayAuth/Login`) nevyžaduje žádné AD změny — StoreFront důvěřuje NetScaleru přímo.

Alternatives considered:
- KCD/RBCD — funguje ale vyžaduje AD změny které se nedaří prosadit.
- NTLM — nelze delegovat.
- FAS — netýká se StoreFront auth, pomáhá jen VDA launch.

Consequences:
Nenavrhovat Kerberos/RBCD/SPN cokoliv. Místo toho: zjistit konfiguraci Gateway Pass-Through na `pnagent.fis.acr` a implementovat `/GatewayAuth/Login` endpoint.

Affected files/modules:
- `Program.cs` (nový SSO endpoint)
- `appsettings.json` (případná gateway konfigurace)

---

## 2026-05-06 — Manual redirect handling for StoreFront bootstrap

Status: accepted

Decision:
`HttpClientHandler.AllowAutoRedirect = false`. Redirects (3xx + HTML `<meta http-equiv="refresh">`) are followed manually with a hop limit of 5.

Reason:
The NetScaler bootstrap path (`/Citrix/FISWeb/` → `/cgi/setclient?wica` → meta-refresh → `/Citrix/FISWeb/` → 301 → `/Citrix/FISWeb/`) needs cookie inspection and header customisation between hops. Auto-follow would skip cookie capture (`CsrfToken`, `ASP.NET_SessionId`) and would not handle the HTML meta-refresh that NetScaler sends as a 200.

Alternatives considered:
- `AllowAutoRedirect = true` — fails because (a) cookies not inspected per-hop, (b) HTML meta-refresh not followed (it's a 200, not a 3xx).

Consequences:
Every new StoreFront flow added must use `CitrixExplicitAuth.CreatePageHeaders` (for navigation hops) or `CreateBaseHeaders` (for API calls) and respect the hop limit.

Affected files/modules:
- `Program.cs` (`/api/citrix-diagnostics/explicit-login`, `/api/citrix-diagnostics/server-probe`)

---

## 2026-05-06 — POST-first, GET-fallback for ExplicitAuth/Login

Status: accepted

Decision:
Fetch `ExplicitAuth/Login` and `Authentication/GetAuthMethods` with POST first; fall back to GET on non-success.

Reason:
On this StoreFront deployment IIS rejects GET on `ExplicitAuth/Login` with 404. POST returns the auth form definition (StateContext, field IDs, PostBack URL). GET is kept as fallback for other deployments.

Alternatives considered:
- GET only — fails on this StoreFront (IIS 404).
- POST only — would break on stricter StoreFront builds.

Consequences:
Code must tolerate either method succeeding and parse the same XML form definition shape from the response.

Affected files/modules:
- `Program.cs::CitrixExplicitAuth.TryParseAuthForm`
- explicit-login endpoint: login-form fetch loop and authMethods loop.

---

## 2026-05-06 — Field IDs and submit button parsed from server XML, with hard fallbacks

Status: accepted

Decision:
Parse `UsernameId`, `PasswordId`, `DomainId`, `SubmitButtonId`/`SubmitButtonValue` and `StateContext` from the XML auth form definition. Fall back to hard-coded defaults (`username`, `password`, `domain`, `loginBtn=Přihlásit`) only when parsing fails.

Reason:
Field IDs vary by StoreFront version and customisation. Hard-coding alone fails on customised deployments; parsing alone fails when the form is missing.

Affected files/modules:
- `Program.cs::CitrixAuthFormDefinition`, `CitrixExplicitAuth.TryParseAuthForm`

---

## 2026-05-06 — Body previews truncated to 1200 chars

Status: accepted

Decision:
StoreFront response bodies are truncated to 1200 chars in the API response (`CitrixExplicitAuth.Preview`). Configurable via `appsettings.json::CitrixDiagnostics:BodyPreviewLimit`.

Reason:
StoreFront returns full HTML pages (often >50 KB). Returning the entire body to the diagnostic UI would bloat the response. 1200 chars is enough for the redirect target / form structure / error message.

Affected files/modules:
- `Program.cs`
- `Pages/Index.cshtml.cs::BodyPreviewLimit`

---

## 2026-05-06 — Czech-language UI preserved in API surfaces

Status: accepted

Decision:
API error messages and the StoreFront submit-button value (`Přihlásit`) stay in Czech. Do not auto-translate to English.

Reason:
Target StoreFront is a Czech-language deployment; the localised submit button value is required for the form POST to be accepted. Diagnostic UI is consumed by Czech-speaking operators.

Affected files/modules:
- `Program.cs`
- `appsettings.json::CitrixDiagnostics:PanelTitle`

---

## 2026-05-20 — Bootstrap navigation uses page-like headers (no X-Requested-With)

Status: accepted

Decision:
Bootstrap chain (initial GET, redirect hops, meta-refresh hops) uses `CitrixExplicitAuth.CreatePageHeaders` — `Accept: text/html,...`, `Upgrade-Insecure-Requests: 1`, NO `X-Requested-With`, NO `X-Citrix-IsUsingHTTPS`. API calls (GetAuthMethods, ExplicitAuth/Login, LoginAttempt, Resources/List) keep `CreateBaseHeaders` with AJAX markers.

Reason:
With `X-Requested-With: XMLHttpRequest` on bootstrap navigation, StoreFront treated the request as AJAX and did NOT create the ASP.NET session. Cookies appeared (`CsrfToken`, NSC) but `ASP.NET_SessionId` was missing. Subsequent `ExplicitAuth/Login` returned 404 from IIS and `LoginAttempt` returned `<LogMessage>sessiontimeout</LogMessage>`. Switching bootstrap to browser-style headers makes StoreFront treat it as a real page load and creates the session.

Confirmed by:
Cookie diagnostic added to `loginAttemptResults`. After fix, `[pre-login cookies:storeRoot]` includes `ASP.NET_SessionId`. Login succeeds.

Affected files/modules:
- `Program.cs::CitrixExplicitAuth.CreatePageHeaders`
- explicit-login endpoint: bootstrap GET, redirect-hop loop, meta-refresh loop

---

## 2026-05-20 — Server-side session cache (IMemoryCache)

Status: accepted

Decision:
After successful login, store the authenticated `CookieContainer` + `storeRootUri` in `IMemoryCache` keyed by a random GUID (sliding 20-min TTL = StoreFront default). Browser receives only the opaque GUID as `sessionToken` in the login response.

Reason:
Subsequent operations (icon fetch, ICA download for app launch) need authenticated requests to StoreFront but must not expose cookies to the browser. Cookies (`ASP.NET_SessionId`, `CsrfToken`, `NSC_*`) are bearer tokens — keeping them server-side is the security baseline.

Alternatives considered:
- Pass cookies to browser → rejected (security: any XSS or token leak compromises Citrix session).
- Re-login per click → rejected (latency, cred re-prompt, user lockout risk on retries).
- Distributed cache (Redis) → deferred; IMemoryCache fine for single-instance PoC. Production note in `current-task.md`.

Affected files/modules:
- `Program.cs::CitrixSessionCache`, `CitrixSessionEntry`
- `Models/CitrixLoginResponse.cs::SessionToken`

---

## 2026-05-20 — Generic authenticated proxy endpoint with path whitelist

Status: accepted

Decision:
Single endpoint `GET /api/citrix-proxy?session=<GUID>&path=<rel>` handles BOTH icon fetch and ICA download (and any future StoreFront pass-through). Anti-SSRF: `path` MUST start with `Resources/`, MUST NOT start with `/`, MUST NOT contain `..` or `://`. Resolved against cached `storeRootUri`.

For `.ica` paths (path contains `LaunchIca` or ends with `.ica`): forces `Content-Type: application/x-ica` + `Content-Disposition: attachment; filename="citrix-app.ica"` so browsers hand the file to the registered Workspace App MIME handler.

Reason:
- Icons need auth cookies → can't load directly from browser
- ICA download is just another authenticated GET with specific Content-Type
- One endpoint, one whitelist rule, less attack surface than multiple specific endpoints

Affected files/modules:
- `Program.cs::/api/citrix-proxy`
- `Pages/Index.cshtml` — `proxyUrl()` helper, click handler navigates to proxy URL

---

## 2026-05-06 — Context Engineering Level 3 architecture adopted

Status: accepted

Decision:
Project adopts a deliberate Claude Code context architecture: `CLAUDE.md` router, `docs/ai-context/` memory layer, project-local skills (`context-maintenance`, `lesson-capture`, `memory-lookup`), subagents (`context-auditor`, `codebase-scout`), and reminder-only hooks.

Reason:
Citrix POC has many environment-specific quirks that must survive `/compact` and new chats. Without durable memory the user repeatedly re-explains the same StoreFront edge cases.

Alternatives considered:
- Single bloated `CLAUDE.md` — rejected, becomes unreadable and loads on every prompt.
- Third-party MCP memory servers — deferred; project-local files are sufficient and have no external dependencies.

Consequences:
All future durable lessons must be captured via the `lesson-capture` skill into the appropriate file under `docs/ai-context/`.

Affected files/modules:
- `CLAUDE.md`, `docs/ai-context/`, `.claude/skills/`, `.claude/agents/`, `.claude/settings.json`, `.claude/hooks/`

---

## 2026-05-07 — No .sln file in repo root

Status: accepted

Decision:
Do not commit `.sln` to the repo root. Use `CitrixComponent.csproj` standalone. `.sln` may exist locally for VS IDE use but must not be git-tracked.

Reason:
When both a `.csproj` and `.sln` are present in the root, `dotnet publish` (and `dotnet build` without explicit target) fails with `MSB1011: Specify which project or solution file to use`. The `.sln` provides no value over the `.csproj` for this single-project repo.

Alternatives considered:
- Keep `.sln`, always specify `CitrixComponent.csproj` explicitly in commands — rejected, user expects bare `dotnet publish` to work.

Consequences:
All dotnet commands (`build`, `run`, `publish`) work bare without specifying the project file. If VS IDE needs a `.sln`, add it to `.gitignore`.

Affected files/modules:
- `CitrixComponent.csproj` (sole project file at root)
- `docs/ai-context/commands.md`

---

## 2026-05-07 — Project renamed CitrixComponent

Status: accepted

Decision:
Project name changed from `PortalComponent` to `CitrixComponent` across all files (.csproj, namespaces, docs, both repos).

Reason:
`PortalComponent` implied tight coupling to a specific host portal. The component is a reusable Citrix StoreFront integration, usable in any host application.

Affected files/modules:
- `CitrixComponent.csproj` (renamed)
- All `namespace PortalComponent.*` → `namespace CitrixComponent.*`
- Public repo `MO-FIS-DEV/citrix-poc` synced
