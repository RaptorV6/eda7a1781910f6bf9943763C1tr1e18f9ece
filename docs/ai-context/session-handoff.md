# Session handoff

Last updated: 2026-05-07

## Summary
PoC end-to-end functional. Project renamed `PortalComponent` → `CitrixComponent` (2026-05-07). Both repos synced. `.sln` removed from repo root. Build + publish clean.

## Current state

**Public repo** `MO-FIS-DEV/citrix-poc` — synced. Latest commit `cfeac67` "Rename PortalComponent → CitrixComponent".

**Local workspace** — `citrix-poc` branch. Latest commit `8839aeb` "Remove .sln from repo".

**Private repo** `RaptorV6/eda7a1781910f6bf9943763C1tr1e18f9ece` — pushed, commit `8839aeb`.

## Architecture (final, see decisions.md)

- Cookies live server-side in `IMemoryCache` keyed by GUID; browser holds opaque token only
- Bootstrap chain uses page-like headers (no `X-Requested-With`); API calls keep AJAX headers
- `/api/citrix-launch-status` rewrites `fileFetchUrl` host from internal StoreFront to public gateway
- `receiver://<public-host>/<store-path>/clientAssistant/getIcaFile/<base64-params>` for silent app launch
- Anti-SSRF whitelist on `/api/citrix-proxy` (`path` MUST start with `Resources/`)

## Important files
- `Program.cs` — auth flow, session cache, proxy endpoint, launch-status endpoint
- `Pages/Index.cshtml` — login form, tile rendering, click handler with receiver:// + iframe ICA fallback
- `Pages/Index.cshtml.cs` — config binding (`PublicGatewayHost`, `PublicStorePath`)
- `Models/CitrixLoginResponse.cs` — includes `SessionToken`
- `appsettings.json` — `BaseUrl`, `PublicGatewayHost`, `PublicStorePath`
- `CitrixComponent.csproj` — project file (was `PortalComponent.csproj`)

## Naming (changed 2026-05-07)
- Project: `CitrixComponent` (was `PortalComponent`)
- Namespaces: `CitrixComponent.Models`, `CitrixComponent.Pages`
- DLL: `CitrixComponent.dll`
- No `.sln` in repo — `dotnet build/run/publish` uses `.csproj` standalone

## Verified end-to-end (against `citrixvpx01.fis.acr` / `pnagent.fis.acr`)
1. Login → `loginSucceeded: true`, sessionToken issued
2. Resources/List → 9 apps (GINIS sady, MS Edge, Visual Studio Pro 2022, RDP, Reporty FIS)
3. Icon proxy → tiles render correctly
4. Tile click → receiver:// invocation → Citrix Workspace App opens app silently
5. Cross-validation: bit-identical `SessionsharingKey` in our ICA vs official Citrix StoreFront ICA

## Next session — plan

1. User tests `publish` build on deployment server (smoke test against real Citrix).
2. After successful test → **AD SSO** implementation (variant B: Negotiate + Kerberos delegation).

## AD SSO prerequisites (need before code change)

**Infrastructure (user / AD admin):**
1. Service account in AD (e.g. `acr\svc-portal`)
2. SPN registration: `setspn -A HTTP/<portal-hostname>.<domain> <service-account>`
3. RBCD on Citrix StoreFront: `Set-ADComputer -Identity citrixvpx01 -PrincipalsAllowedToDelegateToAccount ...`
4. StoreFront: enable `Domain Pass-through` auth, configure Trusted Domains
5. Portal on domain-joined Windows, app pool runs as service account

**Code (Claude task):**
1. `AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate()` + `AddAuthorization()`
2. New endpoint `POST /api/citrix-diagnostics/sso-login` with `[Authorize]`
3. `WindowsIdentity.RunImpersonated` + `UseDefaultCredentials = true` for Kerberos delegation
4. Frontend: try SSO first on load, fallback to manual form on 401

## Open questions before AD SSO code
1. Confirm portal hostname (for SPN)
2. Confirm service account exact name/domain
3. Confirm RBCD set up on `citrixvpx01.fis.acr`
4. List of user domains for Citrix Trusted Domains

## End-goal architecture
1. ✅ PoC functional
2. ⏳ Publish test on deployment server
3. ⏳ AD SSO (variant B — Kerberos Constrained Delegation)
4. ⏳ Web component refactor — `<citrix-tiles>` custom element, JS bundle, backend endpoints unchanged
5. Final: merge `citrix-poc` → `main`

## For next Claude session
Read `CLAUDE.md` → `current-task.md` → this file. State after publish test: success → proceed to AD SSO open questions + code. Failure → debug regression.
