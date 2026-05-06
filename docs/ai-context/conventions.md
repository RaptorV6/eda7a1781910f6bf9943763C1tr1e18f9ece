# Conventions

Project conventions Claude must follow.

## Coding style (C# / .NET 10)

- **Top-level statements** in `Program.cs`. Helpers (`CitrixAuthFormDefinition`, `CitrixExplicitAuth`) live at the bottom of the same file as `internal sealed` / `internal static` types. Keep them there for the POC; only split into separate files if the user asks for refactoring.
- **Nullable reference types: enabled.** `<Nullable>enable</Nullable>` in `.csproj`. Annotate `?` and use null-coalescing as needed. Do not turn it off.
- **Implicit usings: enabled.** Avoid adding redundant `using System;` etc.
- **`using var`** for `HttpClient`, `HttpClientHandler`, `HttpRequestMessage`, `HttpResponseMessage`. Already the pattern in `Program.cs` — preserve it.
- **DTOs are `sealed` with `init` setters and default-empty values** (`string Foo { get; init; } = string.Empty;`, `string[] Bar { get; init; } = [];`). Match this style for new request/response models.
- **String comparisons** use `StringComparison.OrdinalIgnoreCase` for headers/cookies/auth values. Do not switch to default Ordinal/Culture.
- **No `var` zealotry**: existing code mixes `var` and explicit types. Match the surrounding style.
- **Logging via `ILoggerFactory.CreateLogger("Name")`**, not `ILogger<T>`, in the minimal-API endpoints. Logger names: `CitrixClientDiagnostics`, `CitrixServerProbe`, `CitrixExplicitLogin`. Match this.
- **Never log passwords or full credentials.** Log username + domain only.
- **Comments are sparse and explain WHY**, not what. The Citrix flow has good examples (e.g. the meta-refresh comment block). Match that bar — only comment when the next reader would otherwise be confused by a non-obvious StoreFront quirk.

## Documentation style

- Project memory (`docs/ai-context/*.md`) uses ISO date headers (`## YYYY-MM-DD — Title`).
- `CLAUDE.md` is a router, not a knowledge base. Detailed material goes under `docs/ai-context/`.
- Czech is acceptable in user-facing strings and Citrix-specific values; English in code identifiers and durable docs.

## Testing style

- No test project yet. If/when added, prefer **xUnit** unless the user prefers otherwise. Place under `PortalComponent.Tests/` and add to the `.sln`.
- Do not use mocks for the StoreFront proxy logic — the value is in the real bootstrap dance. Prefer recorded HAR-driven integration tests against a stub server.

## Commit / change style

- Recent commits are unlabelled progress markers (`9`, `10`, `11`, `12`, `13`). The user has not asked for Conventional Commits or anything fancier — match the existing terse style unless the user requests otherwise.
- Do not commit `bin/`, `obj/`, `publish/` artefacts in new commits even though some are already tracked.
- Branch in flight: `citrix-poc`. Do not push or merge without explicit user instruction.

## User / project preferences

- Czech-language interaction is OK. The user's recent request was in Czech ("udělej všechny fáze"). Reply in the language the user uses.
- The user is using a "caveman mode" terse reply style (per the harness `SessionStart` hook). Keep technical content exact; drop filler. Do not drop caveman for normal requests; do drop it for security warnings, irreversible-action confirmations, and multi-step sequences where fragment ordering risks misreading.
- Do not destroy uncommitted work. The working tree on `citrix-poc` has uncommitted changes; respect them.
- Avoid third-party MCP servers / npm installs / global config changes without explicit user approval.
