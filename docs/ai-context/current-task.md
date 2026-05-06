# Current task

Last updated: 2026-05-20

## Objective
Citrix StoreFront / NetScaler explicit-auth proxy POC. Server-side .NET 10 endpoints replicate the browser login flow so the portal can drive Citrix end-to-end: auth → list apps → click → ICA download → Workspace App launch.

## Current status — PoC FUNCTIONAL END-TO-END
On `citrix-poc` branch. Verified against real StoreFront `https://citrixvpx01.fis.acr/Citrix/FISWeb/`:

- ✅ Server-side login → `loginSucceeded: true`, `result=success`
- ✅ `Resources/List` → 200, 9 apps returned
- ✅ Session token (GUID) returned to browser; cookies cached server-side under that token
- ✅ Tile rendering with click handler
- ✅ Icon proxy through `/api/citrix-proxy` (icons display)
- ✅ Click tile → `.ica` file downloads with correct STA + LogonTicket
- ✅ Confirmed by user: ICA opened on machine with Citrix Workspace App → app launched
- ✅ Cross-validated ICA against official StoreFront ICA — `SessionsharingKey` bit-identical, all per-launch tokens correctly unique

## Architecture (locked-in)

```
Browser ──POST login─→ Portal ──server-side auth dance─→ StoreFront
                         │
                         ├─ IMemoryCache: GUID → CookieContainer + storeRootUri (20 min sliding TTL)
                         │
Browser ──klik─────────→ Portal /api/citrix-proxy?session=<GUID>&path=Resources/...
                         │
                         └─ Cached cookies → GET StoreFront → proxy bytes back
                            (icons: pass-through Content-Type;
                             ICA: Content-Type=application/x-ica + Content-Disposition: attachment)
```

## Endpoints
- `POST /api/citrix-diagnostics/explicit-login` — auth + Resources/List + session token issuance
- `GET /api/citrix-proxy?session=<token>&path=<rel>` — generic authenticated proxy. Anti-SSRF: `path` must start with `Resources/`, no `..`, no absolute URI

## Security
- Cookies (auth tokens) NEVER reach browser — server-side only
- Browser holds only opaque GUID
- Password never logged (only username + domain logged)
- Path whitelist on proxy blocks SSRF

## What worked (final flow that succeeds)
1. Bootstrap GET `/Citrix/FISWeb/` with **page-like headers** (no `X-Requested-With`, browser Accept) → 302
2. Follow 302 to `/cgi/setclient?wica` → 200 HTML w/ meta-refresh
3. Parse meta-refresh → loop GET `/Citrix/FISWeb` → 301 → `/Citrix/FISWeb/` → 200 (StoreFront sets `ASP.NET_SessionId` + `CsrfToken`)
4. POST `/Authentication/GetAuthMethods` (no Csrf-Token header) → 200
5. POST `/ExplicitAuth/Login` (GET returns 404 on this IIS, POST works) → form XML
6. POST `/ExplicitAuth/LoginAttempt` with username/password/domain/`loginBtn=Přihlásit` → `<Result>success</Result>`
7. POST `/Resources/List` with `format=json&resourceDetails=Default` → 200 JSON

## Resource fields (from real `Resources/List`)
```json
{
  "id": "22Controller.GINIS PROVOZNI W202",
  "name": "GINIS CVICNA",
  "iconurl": "Resources/Icon/<base64>?size=128",
  "launchurl": "Resources/LaunchIca/<base64>.ica",
  "clienttypes": ["ica30", "rdp"],   // NO html5 → native Workspace App required
  "launchstatusurl": "Resources/GetLaunchStatus/<base64>",
  "cancellaunch": "Resources/CancelLaunch/<base64>",
  "subscriptionstatus": "subscribed"
}
```

## Open / next steps
- Production hardening:
  - Distributed cache (Redis) instead of IMemoryCache for multi-instance
  - Session token rotation
  - CSRF protection on portal endpoints
  - Refactor Citrix logic out of `Program.cs` into typed `CitrixStoreFrontClient`
- HTML5 fallback: not needed for this StoreFront (`clienttypes` lacks `html5`); native Workspace App required client-side
- `.gitignore` for `bin/`, `obj/` — currently tracked

## Commands
- `cd PortalComponent && rm -rf ./publish && dotnet publish -c Release -o ./publish`
- `dotnet build PortalComponent/PortalComponent.csproj`
