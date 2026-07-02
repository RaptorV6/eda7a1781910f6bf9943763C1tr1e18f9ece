# Failed approaches

Approaches that were tried and should not be repeated blindly.

---

## 2026-05-06 — `AllowAutoRedirect = true` for StoreFront flows

Context:
Initial probe / login implementation against NetScaler-fronted StoreFront.

Tried:
`HttpClientHandler { AllowAutoRedirect = true }` to let .NET follow 3xx automatically.

Observed failure:
- `CsrfToken` and `ASP.NET_SessionId` cookies not consistently captured per-hop.
- HTML `<meta http-equiv="refresh">` from `/cgi/setclient?wica` was **not** followed (it's a 200 response, not a 3xx) so the bootstrap never landed on `/Citrix/FISWeb/`.
- Subsequent API calls failed with 403 / "CsrfTokenMissing".

Root cause:
NetScaler's bootstrap chain mixes 3xx and HTML meta-refresh. Cookie state needs explicit per-hop inspection.

Do instead:
`AllowAutoRedirect = false`, manual loop with hop limit 5, manual `<meta refresh>` regex (`CitrixExplicitAuth.TryExtractMetaRefreshUrl`). Use `CreatePageHeaders` (no `X-Requested-With`) for navigation hops.

Do not repeat:
Setting `AllowAutoRedirect = true` "to simplify the code" — it breaks the bootstrap.

---

## 2026-05-06 — GET-only on `ExplicitAuth/Login`

Context:
Fetching the auth form definition (StateContext, field IDs, PostBack URL).

Tried:
`GET /Citrix/FISWeb/ExplicitAuth/Login`.

Observed failure:
HTTP 404 from IIS on this StoreFront deployment.

Root cause:
This deployment's IIS / StoreFront config rejects GET on the explicit-auth endpoint. POST is the supported method.

Do instead:
POST first (with empty `application/x-www-form-urlencoded; charset=UTF-8` body and the `X-Citrix-AM-CredentialTypes` / `X-Citrix-AM-LabelTypes` headers), GET as fallback for other deployments. See `Program.cs` login-form fetch loop.

Do not repeat:
Removing the POST branch "because GET works on the docs example" — it does not work here.

---

## 2026-05-06 — Sending API headers (`X-Requested-With`, `X-Citrix-IsUsingHTTPS`) on bootstrap navigation

Context:
Initial bootstrap GET to `/Citrix/FISWeb/`.

Tried:
Reusing `CreateBaseHeaders` (with `X-Requested-With: XMLHttpRequest` + `X-Citrix-IsUsingHTTPS`) for the bootstrap and meta-refresh hops.

Observed failure:
StoreFront treats the request as an API call and **does not create the ASP.NET session** in the response. Subsequent CSRF/cookie steps then have nothing to bind to.

Root cause:
StoreFront's auth shell branches on these headers. API headers → API code path → no session bootstrap.

Do instead:
Use `CitrixExplicitAuth.CreatePageHeaders` (which omits `X-Requested-With` and `X-Citrix-IsUsingHTTPS` and includes `Upgrade-Insecure-Requests: 1`) for every navigation hop. Switch to `CreateBaseHeaders` only for actual API calls (`Authentication/GetAuthMethods`, `ExplicitAuth/Login`, `ExplicitAuth/LoginAttempt`, `Resources/List`).

Do not repeat:
"Unifying" header construction across page navigation and API calls.

---

## 2026-05-06 — Hard-coding StoreFront field IDs without parsing the form

Context:
Building the LoginAttempt POST payload.

Tried:
Posting `username=...&password=...&domain=...&saveCredentials=false&StateContext=&loginBtn=Log On`.

Observed failure:
`Result` element returned non-success on customised StoreFront builds where field IDs differ, or where `loginBtn` value is the localised string.

Root cause:
Field IDs and submit button caption are configurable per StoreFront customisation. The HAR for this deployment shows `loginBtn=Přihlásit` (Czech).

Do instead:
Parse the auth form XML (`CitrixExplicitAuth.TryParseAuthForm`) and use the discovered `UsernameId`, `PasswordId`, `DomainId`, `SubmitButtonId`, `SubmitButtonValue`, `StateContext`, `PostBack`. Keep hard-coded defaults only as a last-resort fallback.

Do not repeat:
Removing the parsed-form path "to clean up the code" — the fallback alone is not enough.

---

## 2026-05-06 — Persisting CSRF token after only the first bootstrap hop

Context:
Forwarding `Csrf-Token` header on later API calls.

Tried:
Capturing the cookie once after the initial GET to the store root.

Observed failure:
StoreFront rotates `CsrfToken` after the meta-refresh / further navigation hops. Submitting the stale token caused the LoginAttempt to be rejected with `CsrfTokenMissing`-like errors.

Root cause:
`CsrfToken` is reissued by StoreFront across the bootstrap chain.

Do instead:
Re-read `CsrfToken` from the `CookieContainer` after every hop (bootstrap, meta-refresh, login form fetch) before composing the next request. See `Program.cs` `currentCsrfToken` re-reads.

Do not repeat:
Caching the CSRF token in a local variable and reusing it across all calls.

---

## 2026-05-06 — Sending `Content-Length` / `Host` from forwarded request headers

Context:
Generic server-probe endpoint forwards arbitrary client headers to StoreFront.

Tried:
Forwarding all keys from `probeRequest.Headers` verbatim.

Observed failure:
`HttpClient` rejects `Content-Length` and `Host` headers when set manually, or computes them itself; setting them via `TryAddWithoutValidation` corrupted the outgoing request.

Root cause:
These are managed by the transport layer.

Do instead:
Skip `Content-Length` and `Host` in the forwarding loop (already done in `Program.cs`).

Do not repeat:
"Forward every header for fidelity" — these two are off-limits.

---

## 2026-05-07 — Committing .sln alongside .csproj in repo root

Context:
During rename from PortalComponent → CitrixComponent, the untracked `.sln` file was accidentally staged and committed alongside `CitrixComponent.csproj`.

What broke:
`dotnet publish` (bare, no project argument) failed with `MSBUILD : error MSB1011: Specify which project or solution file to use because this folder contains more than one project or solution file.`

Fix applied:
`git rm eda7a1781910f6bf9943763C1tr1e18f9ece.sln` — removed from repo. `dotnet publish` works bare again.

Do not repeat:
Do not `git add` `.sln` files in this repo. If a `.sln` exists locally for IDE use, add it to `.gitignore`.

---

## 2026-05-13 — Navrhování Kerberos/delegace bez důkazu z traffic (původní chyba)

Context:
Implementace AD SSO pro automatické přihlášení přes Citrix StoreFront.

Tried:
Opakované navrhování Kerberos Constrained Delegation (RBCD, SPN, WindowsIdentity.RunImpersonated) bez znalosti co StoreFront reálně očekává.

Observed failure:
3+ dny bez výsledku. User explicitně řekl "žádný Kerberos dnes" a Claude přesto navracel Kerberos řešení.

Root cause:
Předpoklad že SSO = Kerberos, bez ověření reálného traffic z Workspace App.

**UPDATE 2026-06-22:**
Kerberos JE relevantní, ale jinak než se původně navrhoval:
- NENÍ potřeba pro HTTP auth vůči StoreFrontu (CitrixAuth je token-based, potvrzeno mitmproxy)
- JE potřeba pro first hop: uživatel ACR\VanD → IIS na VXXXX22FISXVI15 (FIS.ACR), aby IIS mohl impersonovat uživatele
- Bez Kerberos na first hopu nelze volat FISAuth IntegratedWindows endpoint jako skutečný uživatel

Do instead:
Pokud navrhovat Kerberos, vždy specifikovat kontext: first hop (IIS auth), ne StoreFront HTTP auth.
`WindowsIdentity.RunImpersonated` je správná technika pro krok 4 — ale funguje pouze pokud IIS dostane Kerberos token (ne NTLM).

Do not repeat:
- Nenavrhovat Kerberos jako `Authorization: Negotiate` header vůči StoreFrontu
- Nenavrhovat SPN/RBCD jako primární fix bez ověření cross-domain trust ACR ↔ FIS.ACR

---

## 2026-06-22/23 — Server-to-server backend SSO s UseDefaultCredentials (i15 → 07)

Status: superseded by 2026-07-02 — SSO řešeno přes sso.html popup bridge (viz `decisions.md`).

Context:
Implementace `/api/citrix-sso/login` v `Program.cs` — backend na i15 volá 8-krokový CitrixAuth+FISAuth+IntegratedWindows flow server-to-server proti StoreFrontu na serveru 07, s `HttpClientHandler.UseDefaultCredentials = true`.

Tried:
Backend endpoint technicky prošel celý flow (`loginSucceeded=True`, `resourcesStatusCode=200`).

Observed failure:
`integratedAuthMode=process-fallback` — autentizace proběhla jako identita IIS app poolu (doména ACR), ne jako přihlášený uživatel. `klist get HTTP/vxxxx22fisxvi15.fis.acr` selhává (`0xc000018b`), `/api/whoami` ukazuje `isKerberos=False`.

Root cause:
Cross-domain Kerberos ACR → FIS.ACR — uživatel v doméně ACR nemůže získat Kerberos ticket pro službu v doméně FIS.ACR bez fungujícího cross-forest trustu/delegace. Toto je AD/infrastrukturní otázka, ne kódová.

Do instead:
Použít `sso.html` na serveru 07 (`https://vxxxx22fisxva07.fis.acr/Citrix/FISWeb/custom/sso.html`) jako popup bridge — celý flow proběhne same-origin v prohlížeči uživatele, žádný cross-domain server-to-server hop není potřeba.

Do not repeat:
Znovu navrhovat nebo implementovat server-to-server backend endpoint s `UseDefaultCredentials=true` volající StoreFront na serveru 07 jako řešení user-level SSO — vždy skončí v `process-fallback` módu, dokud AD tým nevyřeší cross-domain Kerberos trust.

---

## 2026-06-22 — BackConnectionHostNames pro cross-domain Kerberos

Context:
`klist get HTTP/vxxxx22fisxvi15.fis.acr` selhává, `/api/whoami` ukazuje `isKerberos=False`.

Tried:
Nastavení `BackConnectionHostNames` registry klíče na serveru VXXXX22FISXVI15 + restart w3svc.

Observed failure:
`/api/whoami` stále ukazuje `isKerberos=False, impersonationLevel=None`.

Root cause:
BackConnectionHostNames řeší loopback auth (server volá sám sebe). Problém je cross-domain: uživatel vand@ACR nedokáže získat Kerberos ticket pro službu v FIS.ACR doméně. Chyba `0xc000018b` indikuje trust/referral problém na úrovni KDC, ne loopback.

Do not repeat:
BackConnectionHostNames jako fix pro cross-domain Kerberos — nesouvisí.
