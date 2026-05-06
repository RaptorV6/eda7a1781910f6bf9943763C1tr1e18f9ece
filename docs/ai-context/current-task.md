# Current task

Last updated: 2026-05-06

## Objective
Citrix StoreFront / NetScaler explicit-auth proxy POC. Server-side .NET 10 endpoints replicate the browser login flow so the portal can drive Citrix without exposing the user to the StoreFront UI.

## Current status
POC functional end-to-end on `citrix-poc` branch. `/api/citrix-diagnostics/explicit-login` performs bootstrap → meta-refresh follow → AuthMethods discovery → Login form parse → LoginAttempt → Resources/List against the configured StoreFront. Recent commits are unlabelled progress markers (`9`–`13`); working tree dirty on `Program.cs` plus build artefacts.

## Files modified (working tree, uncommitted)
- `PortalComponent/Program.cs` — main proxy logic
- `PortalComponent/bin/`, `PortalComponent/obj/` — build artefacts (should not be tracked; out of scope for the POC fix)

## Files inspected (key)
- `PortalComponent/Program.cs` — three minimal-API endpoints + `CitrixExplicitAuth` helpers
- `PortalComponent/Models/Citrix*.cs` — request/response shapes
- `PortalComponent/Pages/Index.cshtml.cs` — Razor Pages diagnostic UI host
- `PortalComponent/appsettings.json` — `CitrixDiagnostics:BaseUrl = https://citrixvpx01.fis.acr/Citrix/FISWeb/`
- `Citrix/FIS`, `Citrix/FISAuth`, `Citrix/PNAgent` — legacy reference material

## Commands run
- `dotnet build PortalComponent/PortalComponent.csproj` — allowed in `.claude/settings.local.json`

## Tests / checks
- No test project exists. UI-driven verification only via the Razor Pages diagnostic page.

## Decisions made
See [decisions.md](decisions.md). Key ones:
- POST-first, GET-fallback for `ExplicitAuth/Login` (IIS 404 on GET in this deployment).
- Manual redirect handling (`AllowAutoRedirect = false`) + manual HTML meta-refresh follow.
- Czech UI strings preserved in API responses and submit button (`loginBtn=Přihlásit`).

## Failed approaches
See [failed-approaches.md](failed-approaches.md).

## User corrections / lessons learned
See [project-facts.md](project-facts.md) and [environment.md](environment.md).

## Open questions
- Should the POC be lifted out of `Program.cs` into a typed `CitrixStoreFrontClient` service before merging? (No decision yet.)
- Are the build artefacts under `PortalComponent/bin` / `obj` intentionally tracked? (Probably not — `.gitignore` may be missing/weak.)
- What is the target deployment story? IIS, Kestrel direct, container? Affects HSTS and HTTPS redirection.

## Next step
Either: (a) refactor `Program.cs` Citrix logic into a typed service for maintainability, or (b) capture more real-world StoreFront responses for additional auth paths (smartcard, RSA token) before refactoring. Confirm with user which.
