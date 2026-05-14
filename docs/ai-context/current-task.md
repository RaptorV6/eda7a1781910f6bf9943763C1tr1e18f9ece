# Current task

Last updated: 2026-05-14

## Objective
Implement AD SSO — automatic Citrix login using the user's Windows domain identity, without manual password entry. PoC explicit-auth flow is fully functional. Next milestone: replicate CitrixAuth token-based flow.

## Current status — CitrixAuth probe added, awaiting test

mitmproxy capture confirmed Workspace App hits `CitrixAuth/Login` (NOT `DomainPassthroughAuth`):

```
POST /Citrix/FISWeb/CitrixAuth/Login HTTP/1.1
X-Citrix-Background-Request: True
X-Citrix-IsUsingHTTPS: Yes
Content-Length: 0
User-Agent: CitrixReceiver/26.3.0.95 Windows/10.0 SelfService/26.3.0.96
```

No `Authorization` header → NOT Kerberos/NTLM at this layer. CitrixAuth is token-based, not SPNEGO.

New endpoint added to `Program.cs:1524`:
- `POST /api/citrix-diagnostics/citrixauth-probe` — fires exact Workspace App headers at `CitrixAuth/Login`, returns `{status, headers, body}`

**Not yet deployed/tested.** Build clean (`dotnet build` 0 errors).

## Endpoints (current full list)
- `POST /api/citrix-diagnostics/explicit-login` — full auth + Resources/List + session token
- `GET /api/citrix-diagnostics/server-probe` — bootstrap chain probe
- `POST /api/citrix-diagnostics/citrixauth-probe` — **NEW** — probe CitrixAuth/Login with Workspace App headers
- `GET /api/citrix-proxy?session=<token>&path=<rel>` — authenticated proxy (anti-SSRF: path must start with `Resources/`)
- `GET /api/citrix-launch-status` — rewrites fileFetchUrl host
- `POST /api/client-log` — browser console relay

## Immediate next steps
1. `dotnet publish -c Release -o ./publish` → deploy to server
2. Call probe: `fetch('/api/citrix-diagnostics/citrixauth-probe', {method:'POST'}).then(r=>r.json()).then(console.log)`
3. Analyze response — expect XML describing CitrixAuth token protocol
4. Based on response, implement CitrixAuth-based SSO in `Program.cs`

## Architecture (locked-in)
```
Browser ──POST login─→ Portal ──server-side auth dance─→ StoreFront
                         │
                         ├─ IMemoryCache: GUID → CookieContainer + storeRootUri (20 min sliding TTL)
                         │
Browser ──klik─────────→ Portal /api/citrix-proxy?session=<GUID>&path=Resources/...
                         │
                         └─ Cached cookies → GET StoreFront → proxy bytes back
```

## Security
- Cookies (auth tokens) NEVER reach browser — server-side only
- Browser holds only opaque GUID
- Password never logged (only username + domain)
- Path whitelist on proxy blocks SSRF

## Commands
- `rm -rf ./publish && dotnet publish -c Release -o ./publish`
- `dotnet build`
- Probe call: `fetch('/api/citrix-diagnostics/citrixauth-probe', {method:'POST'}).then(r=>r.json()).then(console.log)`
