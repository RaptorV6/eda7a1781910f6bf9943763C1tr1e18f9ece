# CLAUDE.md — Project Context Router

This file is **always loaded**. Keep it short. Detailed knowledge lives in `docs/ai-context/`.

---

## Memory-first rule (read before acting)

Before asking the user to repeat anything, before making project-specific assumptions, and **before retrying any approach**, search project memory:

1. `docs/ai-context/current-task.md` — what's in flight
2. `docs/ai-context/session-handoff.md` — last session's exit state
3. `docs/ai-context/decisions.md` — durable decisions
4. `docs/ai-context/failed-approaches.md` — do not repeat these
5. `docs/ai-context/project-facts.md` — Citrix/StoreFront quirks
6. `docs/ai-context/environment.md` — runtime/host quirks
7. `docs/ai-context/commands.md` — verified commands
8. `docs/ai-context/project-map.md` — where things live
9. `docs/ai-context/conventions.md` — coding style

Public-facing docs:
- `README.md` — project overview, architecture, configuration, build, deployment-specific gotchas. Mirrors public repo `MO-FIS-DEV/citrix-poc`.
- `docs/code-audit.md` — DRY/OOP review + refactor priority for production hardening.
- `docs/file-map.md` — file-level mapa kde najít co.

If a user reminds you of something ("we already discussed", "I told you", "we tried that"), treat it as a memory-system bug: find or record the lesson immediately. Use the `lesson-capture` skill.

If a session is getting long, before `/compact`, or before ending work: run the `context-maintenance` skill.

---

## Project at a glance

**Purpose:** ASP.NET Core 10 portal that authenticates against Citrix StoreFront server-side, lists user-assigned applications, and launches them silently via `receiver://` protocol handoff to Citrix Workspace App.

**Layout:** project files at workspace root (no `PortalComponent/` folder, no `Citrix/` legacy reference — both removed). `Program.cs`, `PortalComponent.csproj`, `Models/`, `Pages/`, `Properties/`, `wwwroot/`, `appsettings.json` directly at root.

**Branches:**
- Local workspace: `citrix-poc` (development).
- Public mirror repo: `MO-FIS-DEV/citrix-poc` on GitHub. Workspace and public repo content are kept in sync (excluding `CLAUDE.md`, `docs/ai-context/`, `.claude/`, `.vscode/` which are workspace-only).

**Czech-language project:** API error messages, log messages, and the StoreFront submit button (`loginBtn=Přihlásit`) are in Czech. Do not "translate to English" without asking.

---

## Build / run / test

Verified commands live in [docs/ai-context/commands.md](docs/ai-context/commands.md). Quick reference:

```bash
dotnet build
dotnet run

# Production publish
rm -rf ./publish
dotnet publish -c Release -o ./publish
```

There is **no test project yet**. Do not invent test commands.

---

## Architecture rules (always-on)

- **`Program.cs` is the only HTTP entrypoint** for Citrix proxy logic. Five minimal-API endpoints (`client-log`, `server-probe`, `explicit-login`, `launch-status`, `proxy`) + Razor Pages. Helper code lives in `internal sealed class CitrixSessionEntry`, `CitrixSessionCache`, `CitrixAuthFormDefinition`, and `internal static class CitrixExplicitAuth` at the bottom of the same file.
- **`HttpClientHandler` settings are load-bearing:** `AllowAutoRedirect = false`, `UseCookies = true`, `CookieContainer` shared across the auth flow. Do not change to `AllowAutoRedirect = true` — the explicit-auth flow needs to inspect each redirect (HTTP 3xx **and** HTML `<meta http-equiv="refresh">`) to follow NetScaler's bootstrap.
- **CSRF cookie (`CsrfToken`) must be propagated** as `Csrf-Token` header on every StoreFront API call after bootstrap. Re-read it after each step — StoreFront rotates it.
- **`X-Citrix-IsUsingHTTPS`, `X-Citrix-AM-CredentialTypes`, `X-Citrix-AM-LabelTypes` headers are required** for StoreFront API calls. Do not strip them.
- **Page navigation requests must NOT send `X-Requested-With` / `X-Citrix-IsUsingHTTPS`** — those mark the request as an API call and StoreFront skips creating the ASP.NET session. Use `CitrixExplicitAuth.CreatePageHeaders` for navigation, `CreateBaseHeaders` for API.
- **`ExplicitAuth/Login` requires POST first, GET as fallback** on this StoreFront — IIS returns 404 on GET. See `docs/ai-context/failed-approaches.md`.
- **`fileFetchUrl` from `Resources/GetLaunchStatus` must be host-rewritten** from internal StoreFront host to public gateway host (`appsettings.json::CitrixDiagnostics:PublicGatewayHost`) before sending to browser — Workspace App on client cannot reach internal hostname.
- **Session cache (`CitrixSessionCache`) holds `CookieContainer` server-side** under random GUID. Browser holds opaque token only. 20-min sliding TTL.
- **Anti-SSRF whitelist on `/api/citrix-proxy`:** `path` must start with `Resources/`, no `..`, no `://`, no leading `/`.
- **Body previews are truncated to 1200 chars** (`CitrixExplicitAuth.Preview`). Do not raise this without checking `appsettings.json::CitrixDiagnostics:BodyPreviewLimit`.

---

## Safety rules

- Do not commit secrets. `appsettings.json::CitrixDiagnostics:BaseUrl` already contains an internal hostname (`citrixvpx01.fis.acr`) — that's fine, it's an internal target.
- Do not commit `bin/`, `obj/`, `publish/` content. `.gitignore` covers these — verify before staging.
- Never log credentials. The login endpoint takes `Password` — keep it out of logs (current code logs only username + domain — preserve that).
- HAR files / browser captures from the real StoreFront may contain real session cookies. Treat them as secrets if encountered.
- The `Citrix/` legacy reference folder was removed (2026-05-06) — was unused legacy material, no production dependency.

---

## Tool-output discipline

- Long tool outputs (logs, HAR dumps, full HTTP responses): keep command + status + relevant excerpt only. Full policy: `docs/ai-context/tool-output-policy.md`.
- Use subagents (`.claude/agents/codebase-scout.md`) for broad search. Don't fill main context with grep dumps.
- For long investigations or multi-file reviews, dispatch the `context-auditor` or `codebase-scout` subagent.

---

## Skills (project-local)

- `context-maintenance` — pre-compact / end-of-session memory checkpoint.
- `lesson-capture` — convert user corrections into durable memory entries.
- `memory-lookup` — search project memory before asking the user to repeat.

Located under `.claude/skills/`.

---

## Subagents

- `context-auditor` — checks ai-context completeness; recommends updates.
- `codebase-scout` — broad search without polluting main context.

Located under `.claude/agents/`.

---

## Hooks

Project-local, **reminder-only by default** (no silent file rewrites). Defined in `.claude/settings.json`. See `.claude/hooks/README.md` for what each hook does and how to disable.

---

## When in doubt

1. Check the relevant `docs/ai-context/*.md` file.
2. If still unclear, ask the smallest possible clarifying question.
3. After clarification, **record the answer** in the right memory file so it survives the next compaction.
