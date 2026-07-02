# Environment notes

## Runtime / platform

- **Active app:** ASP.NET Core 10.0 (`net10.0`), nullable + implicit usings on (`CitrixComponent.csproj`).
- **Razor Pages + minimal API endpoints**, single `Program.cs` (top-level statements).
- **HTTPS redirection + HSTS** on in non-Development environments (`Program.cs::UseHsts`/`UseHttpsRedirection`).
- **Static assets** served via `MapStaticAssets` (.NET 10 feature) and `WithStaticAssets`.

## Reference (non-active) tooling

- `Citrix/**` is **classic ASP.NET / IIS** material (`Global.asax`, `web.config`, `Views/`, `App_Data/`, `bin/`). Treat as reference; do not assume any IIS or .NET Framework runtime is set up locally.

## Tooling quirks

- `dotnet build` is the only command pre-allowed by `.claude/settings.local.json`. Any other `dotnet` subcommand triggers a permission prompt.
- The dev container is a Codespaces-like Linux environment (`linux 6.8.0-1044-azure`). The Citrix StoreFront target is **internal** to the corporate network — DNS for `citrixvpx01.fis.acr` will not resolve from a generic Codespace.
- `bin/` and `obj/` artefacts are currently tracked in git. Build will dirty the working tree. Do not stage these as part of unrelated commits.
- `.codex` (zero-byte file at the repo root) is a marker — leave it alone.

## 2026-05-13 — App pool runs as ApplicationPoolIdentity, NOT app_zadosti

Lesson:
The IIS app pool for CitrixComponent runs as **ApplicationPoolIdentity**, not as the service account `app_zadosti`. The machine account is `VXXXX22FISXVI15$`.

Why it matters:
SPN and RBCD must be registered against `VXXXX22FISXVI15$`, not `app_zadosti`. Assuming `app_zadosti` leads to wrong SPN/delegation troubleshooting.

Do this:
When checking SPNs or RBCD, look for `VXXXX22FISXVI15$` (machine account).

Avoid:
Assuming app pool identity is `app_zadosti` or any named service account.

## 2026-06-23 — Kerberos first-hop diagnóza: tři větve

Lesson:
Problém SSO je v první části řetězce. Správný postup diagnózy (v tomto pořadí):

**Větev 1 — klist get selže → problém v AD/Kerberos/SPN/trustu:**
```powershell
klist purge
klist get HTTP/vxxxx22fisxvi15.fis.acr
```
Pokud selže, IIS nemůže dostat Kerberos token bez ohledu na jakoukoli konfiguraci IIS nebo Citrix.

**Větev 2 — klist get projde, ale /api/whoami ukazuje isKerberos=False → problém v browser/IIS/providers:**
```powershell
Invoke-WebRequest -Uri "http://VXXXX22FISXVI15.fis.acr:89/api/whoami" -Method GET -UseDefaultCredentials -UseBasicParsing |
  Select-Object -ExpandProperty Content | ConvertFrom-Json
```
Příčiny: prohlížeč URL není v Intranet Zone, IIS Windows Auth providers mají NTLM před Negotiate, kernel-mode auth s useAppPoolCredentials nesedí, nestandardní port 89 způsobuje SPN mismatch.

**Větev 3 — /api/whoami ukazuje isKerberos=True, ale Citrix SSO běží jako pool → problém v C# impersonaci:**
Přidat `WindowsIdentity.RunImpersonated` kolem kroku 4 FISAuth flow (IntegratedWindows volání).

Aktuální stav (2026-06-23): jsme ve větvi 1 nebo 2 — `klist get HTTP/vxxxx22fisxvi15.fis.acr` selhává.

Why it matters:
Bez správné diagnózy větve se řeší špatná věc. Citrix komponenta a StoreFront jsou správně — problém je výhradně v Kerberos first hop.

Do this:
Před jakýmkoli navrhováním řešení SSO ověřit ve které větvi se nacházíme pomocí výše uvedených příkazů.

Avoid:
Navrhovat změny v Citrix komponentě nebo StoreFront konfiguraci, dokud first hop nevydá isKerberos=True.

## Deployment constraints

- **Deployment workflow:** Codespaces (dev) → `dotnet publish` locally → kopie publish výstupu na server.
- **Deployment server:** domain-joined Windows, doména `fis.acr`. IIS nainstalovaný + Web-Windows-Auth modul přítomen. RDP přístup s admin právy.
- **Uživatelské domény:** acr (primární), fis, oeis a případně další — vyžaduje cross-domain trust v AD + všechny domény v StoreFront Trusted Domains.
- `appsettings.json::CitrixDiagnostics:BaseUrl` is hard-coded to `https://citrixvpx01.fis.acr/Citrix/FISWeb/`. Make it environment-configurable before deploying anywhere else.

## Known differences from defaults

- `HttpClientHandler` is intentionally configured with `AllowAutoRedirect = false` and `UseCookies = true` with a fresh `CookieContainer` per request — this is a load-bearing decision, not a default. See [decisions.md](decisions.md) and [failed-approaches.md](failed-approaches.md).
- `AutomaticDecompression = DecompressionMethods.All` is enabled so StoreFront's gzipped responses can be inspected as plain text.
- Body previews are clamped to 1200 chars by `CitrixExplicitAuth.Preview`. Override via `appsettings.json::CitrixDiagnostics:BodyPreviewLimit`.

## Network / DNS

- `citrixvpx01.fis.acr` resolves only on the corporate network. From a dev container without VPN, requests will fail with DNS errors — that is **not** a code bug.
- StoreFront uses `ASP.NET_SessionId`, `CsrfToken`, and frequently `CtxsAuthId` cookies. Some are HttpOnly; cookie inspection in browsers requires DevTools → Application → Cookies.

## 2026-05-14 — PowerShell Invoke-RestMethod TLS error on localhost

Lesson:
Calling `Invoke-RestMethod -Uri "https://localhost/..."` on the deployment server fails with "Nadřízené připojení bylo uzavřeno" (underlying connection closed) because ASP.NET Core uses a self-signed dev cert and PowerShell rejects it.

Fix options (in order of preference):
1. Add `-SkipCertificateCheck` flag to `Invoke-RestMethod`
2. Use the HTTP port instead (`http://localhost:<port>/...`)

Do this:
Always use `-SkipCertificateCheck` when calling localhost HTTPS from PowerShell on the deployment server, or find the HTTP port from `appsettings.json` / Kestrel config.

---

## Logging

- Default log level `Information`, `Microsoft.AspNetCore` clamped to `Warning` (`appsettings.json`).
- Citrix-specific loggers: `CitrixClientDiagnostics`, `CitrixServerProbe`, `CitrixExplicitLogin`. Use these names to grep logs.
