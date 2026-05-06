# Decisions

Durable project decisions that future Claude sessions must remember.

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
- `PortalComponent/Program.cs` (`/api/citrix-diagnostics/explicit-login`, `/api/citrix-diagnostics/server-probe`)

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
- `PortalComponent/Program.cs::CitrixExplicitAuth.TryParseAuthForm`
- explicit-login endpoint: login-form fetch loop and authMethods loop.

---

## 2026-05-06 — Field IDs and submit button parsed from server XML, with hard fallbacks

Status: accepted

Decision:
Parse `UsernameId`, `PasswordId`, `DomainId`, `SubmitButtonId`/`SubmitButtonValue` and `StateContext` from the XML auth form definition. Fall back to hard-coded defaults (`username`, `password`, `domain`, `loginBtn=Přihlásit`) only when parsing fails.

Reason:
Field IDs vary by StoreFront version and customisation. Hard-coding alone fails on customised deployments; parsing alone fails when the form is missing.

Affected files/modules:
- `PortalComponent/Program.cs::CitrixAuthFormDefinition`, `CitrixExplicitAuth.TryParseAuthForm`

---

## 2026-05-06 — Body previews truncated to 1200 chars

Status: accepted

Decision:
StoreFront response bodies are truncated to 1200 chars in the API response (`CitrixExplicitAuth.Preview`). Configurable via `appsettings.json::CitrixDiagnostics:BodyPreviewLimit`.

Reason:
StoreFront returns full HTML pages (often >50 KB). Returning the entire body to the diagnostic UI would bloat the response. 1200 chars is enough for the redirect target / form structure / error message.

Affected files/modules:
- `PortalComponent/Program.cs`
- `PortalComponent/Pages/Index.cshtml.cs::BodyPreviewLimit`

---

## 2026-05-06 — Czech-language UI preserved in API surfaces

Status: accepted

Decision:
API error messages and the StoreFront submit-button value (`Přihlásit`) stay in Czech. Do not auto-translate to English.

Reason:
Target StoreFront is a Czech-language deployment; the localised submit button value is required for the form POST to be accepted. Diagnostic UI is consumed by Czech-speaking operators.

Affected files/modules:
- `PortalComponent/Program.cs`
- `PortalComponent/appsettings.json::CitrixDiagnostics:PanelTitle`

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
