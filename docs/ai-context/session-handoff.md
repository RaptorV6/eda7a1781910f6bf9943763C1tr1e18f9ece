# Session handoff

Last updated: 2026-05-14

## Summary
PoC end-to-end functional. mitmproxy confirmed Workspace App uses `CitrixAuth/Login` (NOT DomainPassthroughAuth, NOT Kerberos). New probe endpoint added to `Program.cs`. Build clean, not yet deployed.

## Current state

**Branch:** `main` (merged from `citrix-poc`)

**Last commit:** includes `citrixauth-probe` endpoint + Kerberos-without-evidence entry in failed-approaches.md

**Project:** `CitrixComponent` (renamed from `PortalComponent` 2026-05-07)

## Key finding from mitmproxy (2026-05-14)

Workspace App domain pass-through sends:
```
POST /Citrix/FISWeb/CitrixAuth/Login HTTP/1.1
X-Citrix-Background-Request: True
X-Citrix-IsUsingHTTPS: Yes
Content-Length: 0
User-Agent: CitrixReceiver/26.3.0.95 Windows/10.0 SelfService/26.3.0.96 (Release)
```

**No `Authorization: Negotiate` header.** CitrixAuth is token-based, not SPNEGO/Kerberos/NTLM at HTTP layer.

Mitmproxy cert blocker: Workspace App has own cert store, rejected mitmproxy CA. Still captured outgoing headers — enough to identify the endpoint.

## Architecture (final, see decisions.md)
- Cookies live server-side in `IMemoryCache` keyed by GUID; browser holds opaque token only
- Bootstrap chain uses page-like headers (no `X-Requested-With`); API calls keep AJAX headers
- `/api/citrix-launch-status` rewrites `fileFetchUrl` host from internal StoreFront to public gateway
- `receiver://<public-host>/<store-path>/clientAssistant/getIcaFile/<base64-params>` for silent app launch
- Anti-SSRF whitelist on `/api/citrix-proxy` (`path` MUST start with `Resources/`)

## Important files
- `Program.cs` — auth flow, session cache, proxy endpoint, launch-status endpoint, citrixauth-probe
- `Pages/Index.cshtml` — login form, tile rendering, click handler
- `Pages/Index.cshtml.cs` — config binding
- `Models/CitrixLoginResponse.cs` — includes `SessionToken`
- `appsettings.json` — `BaseUrl` = `https://pnagent.fis.acr/Citrix/FISWeb/`, `PublicGatewayHost` = `pnagent.fis.acr`
- `CitrixComponent.csproj` — project file

## Next session plan

1. **Deploy**: `rm -rf ./publish && dotnet publish -c Release -o ./publish` → copy to server
2. **Probe CitrixAuth**: call `POST /api/citrix-diagnostics/citrixauth-probe` from browser console:
   ```js
   fetch('/api/citrix-diagnostics/citrixauth-probe', {method:'POST'}).then(r=>r.json()).then(console.log)
   ```
3. **Analyze response** — expect XML describing CitrixAuth token challenge/protocol
4. **Implement**: based on response, add CitrixAuth-based SSO endpoint to `Program.cs`
5. **Do NOT** propose Kerberos/RBCD/SPN unless mitmproxy traffic contains `Authorization: Negotiate` header

## Rules still active
- No Kerberos proposals without mitmproxy evidence of `Authorization: Negotiate` (see failed-approaches.md)
- Czech strings preserved in API responses and form values
- `AllowAutoRedirect = false` mandatory
- CSRF token re-read after every hop

## End-goal architecture
1. ✅ PoC functional
2. ⏳ Publish + deploy test on deployment server
3. ⏳ CitrixAuth-based SSO (token-based, NOT Kerberos) — probe response needed first
4. ⏳ Web component refactor — `<citrix-tiles>` custom element
5. Final: production hardening (Redis cache, CSRF on portal endpoints)

## For next Claude session
Read `CLAUDE.md` → `current-task.md` → this file. First action: deploy and call the citrixauth-probe endpoint. Report the `{status, headers, body}` response. That response determines all SSO code.
