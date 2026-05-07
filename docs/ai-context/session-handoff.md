# Session handoff

Last updated: 2026-05-06

## Summary
PoC end-to-end functional. Server-side auth + Resources/List + icons proxy + receiver:// silent launch verified against real StoreFront. Workspace cleanup done (Citrix/ legacy removed, flat layout, mirror of public repo `MO-FIS-DEV/citrix-poc`). Build clean.

## Current state

**Public repo** `MO-FIS-DEV/citrix-poc` — synced. Latest commit `cb3b2c3` "Add 'Jak to funguje' overview at top of README".

**Local workspace** — flat layout at root, build artefacts gitignored. Workspace-only files (`CLAUDE.md`, `docs/ai-context/`, `.claude/`, `.vscode/`) stay local. Latest local commit `0bf30c7` "Flatten layout, drop legacy material, mirror public repo".

## Architecture (final, see decisions.md)

- Cookies live server-side in `IMemoryCache` keyed by GUID; browser holds opaque token only
- Bootstrap chain uses page-like headers (no `X-Requested-With`); API calls keep AJAX headers
- `/api/citrix-launch-status` rewrites `fileFetchUrl` host from internal StoreFront to public gateway (so Workspace App on client can reach it)
- `receiver://<public-host>/<store-path>/clientAssistant/getIcaFile/<base64-params>` for silent app launch
- Anti-SSRF whitelist on `/api/citrix-proxy` (`path` MUST start with `Resources/`)

## Important files
- `Program.cs` — auth flow, session cache, proxy endpoint, launch-status endpoint
- `Pages/Index.cshtml` — login form, tile rendering, click handler with receiver:// + iframe ICA fallback
- `Pages/Index.cshtml.cs` — config binding (`PublicGatewayHost`, `PublicStorePath`)
- `Models/CitrixLoginResponse.cs` — includes `SessionToken`
- `appsettings.json` — `BaseUrl`, `PublicGatewayHost`, `PublicStorePath`

## Verified end-to-end (against `citrixvpx01.fis.acr` / `pnagent.fis.acr`)
1. Login → `loginSucceeded: true`, sessionToken issued
2. Resources/List → 9 apps (GINIS sady, MS Edge, Visual Studio Pro 2022, RDP, Reporty FIS)
3. Icon proxy → tiles render correctly
4. Tile click → receiver:// invocation → Citrix Workspace App opens app silently
5. Cross-validation: bit-identical `SessionsharingKey` in our ICA vs official Citrix StoreFront ICA for same resource

## Critical findings (all preserved in failed-approaches.md and decisions.md)
- `clienttypes: ["ica30", "rdp"]` — no html5 on this StoreFront
- ICA tickets (STA, LogonTicket, fileFetchTicket) expire ~30 sec
- Workspace App must have store added (Add Account or MSI parameter) — without it `nglauncher.exe` silently exits
- Internal vs public hostname rewrite is required (`fileFetchUrl` from StoreFront has internal host, client can't reach)

## Next session — user's stated plan

**Tomorrow (2026-05-07):**
1. User will test the latest `publish` build on deployment server. Verify cleanup didn't break anything end-to-end. Quick smoke test against real Citrix.
2. After successful test, move to **AD SSO** implementation (variant B from decisions: Negotiate auth + Kerberos delegation via DomainPassthroughAuth/Login).

## AD SSO prerequisites (need before code change)

**Infrastructure tasks (user / AD admin):**
1. Confirm service account in AD (e.g. `acr\svc-portal`) — user mentioned this exists.
2. Register SPNs:
   ```
   setspn -A HTTP/<portal-hostname>.<domain> <service-account>
   setspn -A HTTP/<portal-hostname> <service-account>
   ```
3. Configure **Resource-based Constrained Delegation (RBCD)** on Citrix StoreFront server (works cross-forest, modern):
   ```powershell
   Set-ADComputer -Identity citrixvpx01 -PrincipalsAllowedToDelegateToAccount (Get-ADUser <service-account>).SID
   ```
4. Citrix StoreFront: enable `Domain Pass-through` (`IntegratedWindows`) auth method, configure Trusted Domains for all relevant user domains.
5. Portal hosted on domain-joined Windows server, app pool / service runs as service account.

**Code change (Claude task):**
1. Add `Microsoft.AspNetCore.Authentication.Negotiate` (in shared framework, no NuGet ref needed).
2. Configure auth + authorization in `Program.cs`:
   ```csharp
   builder.Services.AddAuthentication(NegotiateDefaults.AuthenticationScheme).AddNegotiate();
   builder.Services.AddAuthorization();
   app.UseAuthentication();
   app.UseAuthorization();
   ```
3. Add new endpoint `POST /api/citrix-diagnostics/sso-login` with `[Authorize]`:
   - Extract `WindowsIdentity` from `HttpContext.User`
   - Run bootstrap chain (page headers) to get session cookies
   - Call `DomainPassthroughAuth/Login` with `WindowsIdentity.RunImpersonated(token, ...)` + `UseDefaultCredentials = true` to delegate user's Kerberos ticket
   - Cache cookies, return same shape as explicit-login (sessionToken + apps)
4. Frontend (`Index.cshtml`): try SSO endpoint first on page load. If 200 → render apps without form. If 401 → fallback to manual form (backwards compatibility for non-domain users).

## Open questions before AD SSO code

1. Confirm portal hostname (for SPN registration).
2. Confirm service account exact name/domain.
3. Confirm whether RBCD is set up on `citrixvpx01.fis.acr` (and `pnagent.fis.acr` if relevant).
4. List of user domains that should be allowed (will need to be in Citrix Trusted Domains).

## End-goal architecture (per user, 2026-05-06)

User's vision: "kolega hodí jen do divu" — Citrix tiles embedabable as a drop-in component in any host portal. Current PoC is standalone ASP.NET Core app, NOT a drop-in. Refactor required.

**Roadmap:**
1. ✅ PoC functional (auth + apps + receiver:// silent launch) — done
2. ⏳ Publish test (tomorrow) — verify cleanup didn't regress
3. ⏳ AD SSO (variant B — Kerberos Constrained Delegation) — see prerequisites above
4. ⏳ **Web component refactor** — extract frontend from Razor Pages into framework-agnostic JS bundle + custom element `<citrix-tiles session="..."></citrix-tiles>`. Backend stays ASP.NET API endpoints. Host portal includes JS + drops element wherever.
5. Final: merge `citrix-poc` branch into `main`, delete branch. Component ready for production.

**Web component refactor sketch (for future session):**
- Extract inline `<style>` from `Pages/Index.cshtml` → `wwwroot/css/citrix-tiles.css`
- Extract inline `<script>` → `wwwroot/js/citrix-tiles.js` (ES module with custom element registration)
- Razor Pages become optional preview / dev-only host
- Public asset bundles served from `/dist/citrix-tiles.js` + `/dist/citrix-tiles.css`
- Custom element `<citrix-tiles>` renders tiles, handles login form (or skips if SSO endpoint succeeds), invokes receiver:// on click
- Backend endpoints unchanged

## For next Claude session
Read CLAUDE.md → current-task.md → this file. State after publish test: either user reports failure (debug regression in cleanup) or success (proceed to AD SSO open questions above, then code). After SSO: web component refactor.
