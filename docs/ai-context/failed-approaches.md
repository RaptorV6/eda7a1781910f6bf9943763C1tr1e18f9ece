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

## 2026-05-13 — DomainPassthroughAuth/Login s Windows identitou uživatele

Context:
SSO — automatické přihlášení bez formuláře. Uživatel `ACR\VanD` je správně identifikován přes Windows Auth na portálu.

Tried:
POST `/DomainPassthroughAuth/Login` s Windows identitou přihlášeného uživatele (Negotiate/Kerberos).

Observed failure:
`<Result>fail</Result><LogMessage>fatalerror</LogMessage>` — StoreFront požadavek odmítl.

Root cause:
Double-hop problém: komponenta má Kerberos ticket pro sebe, ale bez RBCD (Resource-Based Constrained Delegation) v AD na `citrixvpx01` nemůže delegovat identitu uživatele dál na StoreFront. StoreFront dostane impersonation token bez práva delegace → fatalerror.

Do instead:
Čekat na mitmproxy traffic z Workspace App — zjistit jestli existuje alternativa bez RBCD. Alternativně: AD admin musí nastavit RBCD (`Set-ADComputer citrixvpx01 -PrincipalsAllowedToDelegateToAccount (Get-ADComputer VXXXX22FISXVI15)`).

Do not repeat:
Navrhovat DomainPassthroughAuth bez potvrzení že RBCD je nastaveno v AD. Navrhovat SPN/RBCD obecně dokud není mitmproxy traffic — bylo zkoušeno opakovaně bez výsledku.

---

## 2026-05-13 — SPN registrace pro `fis\app_zadosti`

Context:
Pokus o registraci SPN pro service account který by komponenta používala.

Tried:
`setspn -A HTTP/<portal-hostname> fis\app_zadosti`

Observed failure:
Error 8647 — SPN již existuje jinde v AD forest (duplikát).

Root cause:
SPN pro daný hostname je už přiřazen jinému účtu v doméně. AD neumožňuje duplicitní SPN.

Do instead:
AD admin musí nejdřív najít kde SPN existuje (`setspn -F -Q HTTP/<hostname>`) a buď SPN přesunout, nebo použít jiný hostname/service account.

Do not repeat:
Pokoušet se registrovat SPN bez ověření duplikátů. Předpokládat že SPN registrace bude čistá.

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
