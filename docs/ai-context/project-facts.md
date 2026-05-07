# Project facts

Durable project-specific facts Claude must remember.

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
