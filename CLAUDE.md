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

If a user reminds you of something ("we already discussed", "I told you", "we tried that"), treat it as a memory-system bug: find or record the lesson immediately. Use the `lesson-capture` skill.

If a session is getting long, before `/compact`, or before ending work: run the `context-maintenance` skill.

---

## Project at a glance

**Purpose:** POC for diagnosing and proxying Citrix StoreFront / NetScaler explicit-auth login from a modern .NET portal.

**Two stacks coexist on disk:**

- `PortalComponent/` — **active**. ASP.NET Core 10.0 Razor Pages + minimal API endpoints. Server-side proxy that performs the StoreFront ExplicitAuth dance (bootstrap → meta-refresh → AuthMethods → Login form parse → LoginAttempt → Resources/List).
- `Citrix/` — **reference only, do not edit unless asked.** Classic ASP.NET artefacts (Global.asax, web.config, Views, bin) for `FIS`, `FISAuth`, `FISWeb`, `PNAgent`, `Roaming`, `Configuration`. Used as ground-truth for what the StoreFront server expects. Treat as captured legacy material.

**Branch:** `citrix-poc`.

**Czech-language project:** API error messages, log messages, and the StoreFront submit button (`loginBtn=Přihlásit`) are in Czech. Do not "translate to English" without asking.

---

## Build / run / test

Verified commands live in [docs/ai-context/commands.md](docs/ai-context/commands.md). Quick reference:

```bash
dotnet build PortalComponent/PortalComponent.csproj
dotnet run --project PortalComponent
```

There is **no test project yet**. Do not invent test commands.

---

## Architecture rules (always-on)

- **`PortalComponent/Program.cs` is the only HTTP entrypoint** for new Citrix proxy logic. Three minimal-API endpoints + Razor Pages. Helper code lives in `internal sealed class CitrixAuthFormDefinition` and `internal static class CitrixExplicitAuth` at the bottom of the same file.
- **`HttpClientHandler` settings are load-bearing:** `AllowAutoRedirect = false`, `UseCookies = true`, fresh `CookieContainer` per request. Do not change to `AllowAutoRedirect = true` — the explicit-auth flow needs to inspect each redirect (HTTP 3xx **and** HTML `<meta http-equiv="refresh">`) to follow NetScaler's bootstrap.
- **CSRF cookie (`CsrfToken`) must be propagated** as `Csrf-Token` header on every StoreFront API call after bootstrap. Re-read it after each step — StoreFront rotates it.
- **`X-Citrix-IsUsingHTTPS`, `X-Citrix-AM-CredentialTypes`, `X-Citrix-AM-LabelTypes` headers are required** for StoreFront API calls. Do not strip them.
- **Page navigation requests must NOT send `X-Requested-With` / `X-Citrix-IsUsingHTTPS`** — those mark the request as an API call and StoreFront skips creating the ASP.NET session. Use `CitrixExplicitAuth.CreatePageHeaders` for navigation, `CreateBaseHeaders` for API.
- **`ExplicitAuth/Login` requires POST first, GET as fallback** on this StoreFront — IIS returns 404 on GET. See `docs/ai-context/failed-approaches.md`.
- **Body previews are truncated to 1200 chars** (`CitrixExplicitAuth.Preview`). Do not raise this without checking `appsettings.json::CitrixDiagnostics:BodyPreviewLimit`.

---

## Safety rules

- Do not commit secrets. `appsettings.json::CitrixDiagnostics:BaseUrl` already contains an internal hostname (`citrixvpx01.fis.acr`) — that's fine, it's an internal target.
- Do not commit `bin/`, `obj/`, `publish/` content. The repo currently tracks some artefacts; do not add more.
- Never log credentials. The login endpoint takes `Password` — keep it out of logs (current code logs only username + domain — preserve that).
- Do not delete `Citrix/` legacy material — it's reference, not dead code.
- HAR files / browser captures from the real StoreFront may contain real session cookies. Treat them as secrets if encountered.

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
