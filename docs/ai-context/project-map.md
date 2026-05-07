# Project map

## Project purpose
ASP.NET Core 10 portal that authenticates against Citrix StoreFront server-side, lists user-assigned applications as tiles, and launches them silently via `receiver://` protocol handoff to Citrix Workspace App.

## Layout (flat, project files at workspace root)

```
.
├── CitrixComponent.csproj      # net10.0, Razor Pages + minimal APIs
├── Program.cs                  # *** main entry point — all endpoints + helpers ***
├── appsettings.json            # CitrixDiagnostics:BaseUrl, PublicGatewayHost, BodyPreviewLimit, etc.
├── appsettings.Development.json
├── Models/
│   ├── CitrixLoginRequest.cs       # explicit-login input
│   ├── CitrixLoginResponse.cs      # explicit-login output (verbose, all hops)
│   ├── CitrixProbeRequest.cs       # generic server-probe input
│   ├── CitrixProbeResponse.cs      # generic server-probe output
│   └── CitrixClientLogEntry.cs     # browser-side log forwarder shape
├── Pages/
│   ├── Index.cshtml(.cs)           # main UI host (Razor Page)
│   ├── Error.cshtml(.cs)
│   └── Shared/_Layout.cshtml
├── Properties/launchSettings.json  # local run profile
├── wwwroot/                        # static assets (Bootstrap, jQuery)
├── README.md                       # public docs (mirrors MO-FIS-DEV/citrix-poc)
├── CLAUDE.md                       # workspace-only: Claude project router
├── .claude/                        # workspace-only: skills, agents, hooks, settings
└── docs/
    ├── code-audit.md               # public: refactor priority + DRY/OOP analysis
    ├── file-map.md                 # public: cheatsheet for common edits
    └── ai-context/                 # workspace-only: Claude memory files
        ├── current-task.md
        ├── session-handoff.md
        ├── decisions.md
        ├── failed-approaches.md
        ├── project-facts.md
        ├── environment.md
        ├── commands.md
        ├── project-map.md
        ├── conventions.md
        ├── tool-output-policy.md
        └── compact-template.md
```

## What's NOT in workspace (deleted 2026-05-06)

- `Citrix/` legacy reference folder — was 98 MB of classic ASP.NET artefacts (Global.asax, web.config, Views, bin) for `FIS`, `FISAuth`, `FISWeb`, `PNAgent`, `Roaming`, `Configuration`. Removed as unused legacy material.
- `PortalComponent/` wrapper folder — flattened, content moved to root.
- `PortalComponent.sln` — Visual Studio solution file removed; `.csproj` standalone is sufficient for `dotnet build`/`run`/`publish`.
- `bin/`, `obj/`, `publish/` — build artefacts excluded by `.gitignore`.

## Public mirror

Workspace content (excluding `CLAUDE.md`, `docs/ai-context/`, `.claude/`, `.vscode/`) is mirrored to public repo `MO-FIS-DEV/citrix-poc` on GitHub. Workspace-only files are Claude memory architecture and not public-relevant.

## Entry points

- **HTTP entry:** `Program.cs` (top-level statements).
- **API endpoints:**
  - `POST /api/citrix-diagnostics/client-log` — browser → server log forwarder.
  - `POST /api/citrix-diagnostics/server-probe` — generic StoreFront request proxy with bootstrap.
  - `POST /api/citrix-diagnostics/explicit-login` — full StoreFront ExplicitAuth flow → Resources/List. Issues opaque session token.
  - `POST /api/citrix-launch-status` — Resources/GetLaunchStatus + rewrites `fileFetchUrl` host from internal to public gateway.
  - `GET /api/citrix-proxy?session=<token>&path=<rel>` — authenticated proxy for icons + ICA (anti-SSRF whitelist on `path`).
- **Razor Pages:** `Pages/Index.cshtml` is the UI host; `Pages/Index.cshtml.cs::IndexModel` reads `CitrixDiagnostics` config and exposes endpoint URLs to the page.

## Important modules (in `Program.cs`)

- `CitrixSessionEntry` — DTO for session cache value (`CookieContainer` + `StoreRootUri` + `CreatedAt`).
- `CitrixSessionCache` — singleton wrapping `IMemoryCache`. `Store(entry) → GUID`, `Get(guid)`, `Remove(guid)`. 20-min sliding TTL.
- `CitrixAuthFormDefinition` — DTO for parsed XML auth form definition.
- `CitrixExplicitAuth` — static helper class. Key methods:
  - `CreatePageHeaders` — for navigation/bootstrap/meta-refresh hops (no `X-Requested-With`).
  - `CreateBaseHeaders` — for StoreFront API calls (with AJAX markers).
  - `CreateRequest` — composes `HttpRequestMessage` with body/headers.
  - `TryParseAuthForm` — XML → `CitrixAuthFormDefinition`.
  - `TryParseAuthMethodUris` — extracts auth-method candidates from response.
  - `TryExtractMetaRefreshUrl` — regex for `<meta http-equiv="refresh">`.
  - `Preview` — clamp body to 1200 chars (configurable).
  - `GetCookieNames`, `GetCookieValue` — cookie inspection.

## Important config files

- `appsettings.json` — `CitrixDiagnostics:BaseUrl`, `PublicGatewayHost`, `PublicStorePath`, `PanelTitle`, `BodyPreviewLimit`. Logging levels.
- `appsettings.Development.json` — dev overrides.
- `Properties/launchSettings.json` — local run profile.
- `.claude/settings.local.json` — pre-allowed Bash patterns (currently `dotnet build:*`).
- `.claude/settings.json` — hooks (reminder-only).

## Tests

None. See `commands.md`.

## Generated / build files (do not edit)

- `bin/`, `obj/`, `publish/` — generated by `dotnet build`/`publish`. Excluded by `.gitignore`.

## Files Claude should be careful with

- `Program.cs` — the whole Citrix flow lives here and the order of operations is load-bearing. Read [decisions.md](decisions.md) and [failed-approaches.md](failed-approaches.md) before refactoring.
- Anything under `bin/`, `obj/`, `publish/` — generated.
- `appsettings.json` — contains internal hostnames; safe to commit but think before exposing externally.

## Where to look for common tasks

| Task | File / location |
|---|---|
| Add a new StoreFront API call | `Program.cs::CitrixExplicitAuth.CreateBaseHeaders` + new endpoint |
| Tune the bootstrap redirect chain | `Program.cs` explicit-login endpoint, hop loops |
| Add a new field parsed from auth form | `CitrixAuthFormDefinition`, `CitrixExplicitAuth.TryParseAuthForm` |
| Change body-preview length | `appsettings.json::CitrixDiagnostics:BodyPreviewLimit` (consumed by `Index.cshtml.cs`); helper default in `CitrixExplicitAuth.Preview` |
| Change the diagnostic UI | `Pages/Index.cshtml`, `Pages/Index.cshtml.cs` |
| Permission-allow a new Bash command | `.claude/settings.local.json` |
