# Project facts

Durable project-specific facts Claude must remember.

---

## 2026-05-07 — This is a COMPONENT, not a portal

Fact:
`CitrixComponent` is a **reusable Citrix component** meant to be embedded in a host application. It is NOT a standalone portal or "firemní portál". Never call it "portál".

Why it matters:
User explicitly corrected this. Calling it a portal misrepresents what it is and confuses stakeholders.

Do this:
Call it "Citrix komponenta", "CitrixComponent", or "the component".

Avoid:
"portál", "portal", "firemní portál", "Citrix portál" — all wrong.

---

## 2026-05-06 — Two stacks coexist: repo root (.NET 10) is active, `Citrix/` is reference

Fact:
The repo holds **two parallel codebases**: a modern ASP.NET Core 10.0 portal (repo root) and a snapshot of classic ASP.NET StoreFront artefacts (`Citrix/FIS`, `Citrix/FISAuth`, `Citrix/FISWeb`, `Citrix/PNAgent`, `Citrix/Roaming`, `Citrix/Configuration`).

Why it matters:
Confusion about which stack is "the project" leads to edits in the wrong tree. The classic ASP.NET tree contains `Global.asax`, `web.config`, `Views/`, `bin/` — it looks active but it isn't.

Applies to:
All work on this repo.

Do this:
- New code goes in repo root.
- Treat `Citrix/` as **read-only reference** for what the upstream StoreFront server expects (Views, web.config, FISAuth flows). Do not edit unless the user explicitly asks.

Avoid:
- Editing `Citrix/**` to "modernise" it.
- Treating `Citrix/FISWeb` as a deployment target — the deployment target is the **upstream** StoreFront at `citrixvpx01.fis.acr/Citrix/FISWeb/`, not this local copy.

---

## 2026-05-06 — Czech-language deployment

Fact:
The target StoreFront is a Czech-language deployment. The submit-button caption is `Přihlásit`. Diagnostic UI strings and API error messages are in Czech.

Why it matters:
- StoreFront's LoginAttempt validates the localised submit-button value. Posting `loginBtn=Log On` instead of `loginBtn=Přihlásit` fails on this deployment when the form parser doesn't run.
- Translating user-facing strings to English without asking breaks the operator workflow.

Applies to:
- `Program.cs` (login flow + API responses).
- `Pages/*.cshtml`.
- `appsettings.json::CitrixDiagnostics:PanelTitle`.

Do this:
Preserve Czech text in API responses, page text, and form values. Use the parsed-form `SubmitButtonValue` first; fall back to `Přihlásit`.

Avoid:
Auto-translating, "normalising", or replacing diacritics in any of these strings.

---

## 2026-05-06 — Citrix StoreFront bootstrap chain shape

Fact:
The bootstrap from `https://citrixvpx01.fis.acr/Citrix/FISWeb/` proceeds as:
1. `GET /Citrix/FISWeb/` → `/cgi/setclient?wica` (HTTP redirect).
2. `/cgi/setclient?wica` → 200 with `<META HTTP-EQUIV="REFRESH" CONTENT="0; URL=/Citrix/FISWeb">` (HTML meta-refresh, **not** an HTTP redirect).
3. `GET /Citrix/FISWeb` → 301 → `/Citrix/FISWeb/`.
4. `GET /Citrix/FISWeb/` → 200 with the auth shell HTML, ASP.NET session created, `CsrfToken` cookie set.

Why it matters:
Skipping any hop, or letting `HttpClient` auto-follow, breaks cookie capture or skips the meta-refresh. See [failed-approaches.md](failed-approaches.md).

Applies to:
`Program.cs::/api/citrix-diagnostics/explicit-login` and `::/api/citrix-diagnostics/server-probe`.

Do this:
Walk the chain manually with `AllowAutoRedirect = false`, hop limit 5, and the meta-refresh regex.

Avoid:
"Simplifying" the redirect-follow loop.

---

## 2026-05-06 — Page headers vs API headers are NOT interchangeable

Fact:
StoreFront branches on `X-Requested-With` and `X-Citrix-IsUsingHTTPS`. Sending these on the bootstrap GET makes StoreFront treat the request as an API call and **skip creating the ASP.NET session**.

Why it matters:
Without the ASP.NET session, downstream CSRF/cookie state never materialises, and every API call fails.

Applies to:
Every new StoreFront request added to `Program.cs`.

Do this:
- For navigation/bootstrap/meta-refresh hops → `CitrixExplicitAuth.CreatePageHeaders`.
- For StoreFront API calls (`Authentication/*`, `ExplicitAuth/*`, `Resources/*`) → `CitrixExplicitAuth.CreateBaseHeaders` (which adds `X-Requested-With`, `X-Citrix-IsUsingHTTPS`).

Avoid:
Using one helper for both, or copy-pasting headers by hand and forgetting which set applies.

---

## 2026-05-06 — `CsrfToken` rotates across hops and must be re-read

Fact:
StoreFront issues a fresh `CsrfToken` cookie at multiple points during the auth flow (bootstrap, meta-refresh landing, login form fetch). The cookie value at the start of the flow is **stale** by the time you POST the login.

Why it matters:
Stale CSRF token → LoginAttempt rejected.

Applies to:
`Program.cs::/api/citrix-diagnostics/explicit-login`.

Do this:
After each hop, re-read the cookie from `handler.CookieContainer.GetCookies(storeRootUri)` and use the latest value as the `Csrf-Token` request header on the next call.

Avoid:
Caching the first-seen `CsrfToken` for the whole flow.

---

## 2026-05-06 — Auth form is XML; parse it before posting LoginAttempt

Fact:
`POST /Citrix/FISWeb/ExplicitAuth/Login` returns an XML auth form definition containing `Result`, `PostBack`, `StateContext`, and a list of `Credential` elements with `Type` (username/password/domain/...) and `ID`. The submit button is a `Credential` with `Type=savecredentials`-adjacent shape and a `Button` value (the localised label).

Why it matters:
Field IDs and the submit-button value are deployment-specific. Ignoring the form means the LoginAttempt POST uses wrong field names → server returns non-success.

Applies to:
`Program.cs::CitrixExplicitAuth.TryParseAuthForm`, `CitrixAuthFormDefinition`.

Do this:
Use parsed values; fall back to `username`/`password`/`domain`/`loginBtn=Přihlásit` only when parsing fails. The `PostBack` URL from the form, when present, supersedes the hard-coded `ExplicitAuth/LoginAttempt` URL.

Avoid:
Hard-coding only.

---

## 2026-05-06 — `BaseUrl` for the POC target

Fact:
`appsettings.json::CitrixDiagnostics:BaseUrl = https://citrixvpx01.fis.acr/Citrix/FISWeb/`. This is an **internal** hostname; it is unreachable outside the corporate network.

Why it matters:
- Anyone trying to run the POC from outside the network will see DNS failures, not a code bug.
- The trailing slash matters for relative URI composition (`new Uri(storeRootUri, "ExplicitAuth/Login")`).

Applies to:
Local dev runs, CI runs, anything that hits the StoreFront.

Do this:
Keep the trailing slash. Make the URL configurable per environment if a non-corporate target is added.

Avoid:
Stripping the trailing slash.

---

## 2026-05-06 — No test project exists

Fact:
There is no `*.Tests.csproj`, no xUnit/NUnit/MSTest setup, and no test runner configured. `CitrixComponent.sln` contains only the web project.

Why it matters:
Do not invent `dotnet test` commands or invent test files. Verification is currently UI-driven via the Razor Pages diagnostic page.

Applies to:
Any task that says "run the tests".

Do this:
Tell the user there is no test project. Offer to add one if they want.

Avoid:
Fake/aspirational test runs.

---

## 2026-05-14 — Workspace App domain pass-through uses `CitrixAuth/Login`, NOT DomainPassthroughAuth

Fact:
mitmproxy capture of real Citrix Workspace App domain pass-through login shows:
```
POST /Citrix/FISWeb/CitrixAuth/Login HTTP/1.1
X-Citrix-Background-Request: True
X-Citrix-IsUsingHTTPS: Yes
Content-Length: 0
User-Agent: CitrixReceiver/26.3.0.95 Windows/10.0 SelfService/26.3.0.96 (Release)
```
No `Authorization: Negotiate` header — this is NOT Kerberos/NTLM at the HTTP layer. CitrixAuth is a token-based mechanism specific to Citrix Workspace App.

Why it matters:
- 3+ days were spent on Kerberos/RBCD without this evidence. The correct endpoint is `CitrixAuth/Login`.
- Do NOT implement `DomainPassthroughAuth/*` or `WindowsIdentity.RunImpersonated` until `CitrixAuth/Login` response is analyzed.

Applies to:
`Program.cs` — SSO implementation. Any AD SSO proposal.

Do this:
Probe `CitrixAuth/Login` first (endpoint `POST /api/citrix-diagnostics/citrixauth-probe` already in `Program.cs:1524`). Read the response XML before writing SSO code.

Avoid:
- Proposing Kerberos, RBCD, SPN, `WindowsIdentity.RunImpersonated` without `Authorization: Negotiate` in captured traffic.
- Using `DomainPassthroughAuth/*` endpoint — not confirmed by traffic.

---

## 2026-05-14 — mitmproxy cert bypass: Workspace App has own cert store

Fact:
Citrix Workspace App rejects mitmproxy's CA certificate even after installing it to Windows Trusted Root Authorities. Workspace App ships its own certificate store and ignores the OS store.

Why it matters:
- `Certificate verify failed: self-signed certificate in certificate chain` will always appear in mitmproxy flows for Workspace App requests.
- The *response* is blocked, but mitmproxy still captures outgoing request headers — enough to identify endpoint and headers.

Applies to:
Any future mitmproxy traffic analysis of Workspace App behavior.

Do this:
Accept cert errors as expected. Use mitmproxy output for request headers only. Actual response analysis requires deploying the `citrixauth-probe` endpoint server-side.

Avoid:
Suggesting Workspace App cert import as a fix — it won't work.
