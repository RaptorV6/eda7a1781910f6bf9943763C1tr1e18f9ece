# File map — kde najít co

Přehled struktury repa pro budoucí úpravy. Pokud chceš změnit X, kouni do Y.

## Top-level

| Cesta | Co tam je | Kdy editovat |
|-------|-----------|--------------|
| `README.md` | High-level dokumentace projektu pro 3 publika (PM, junior dev, chatbot) | Při větších změnách scope nebo statusu |
| `.gitignore` | Standardní .NET ignore patterns + ochrana proti commitu HAR souborů a local config overrides | Pokud přidáš nový build artefakt který se nemá commitovat |
| `CitrixComponent.csproj` | .NET 10 SDK projekt, žádné NuGet závislosti (vše ze shared frameworku) | Při přidání NuGet balíčku |
| `Program.cs` | **Vše hlavní** — service registration, middleware pipeline, 5 endpoint handlerů, helper třídy | Většina backend změn |
| `appsettings.json` | Runtime konfigurace — Citrix hostnames, gateway, panel title | Při změně cílového Citrixu |
| `appsettings.Development.json` | Development override (verbose logging) | Lokální dev tweaks |

## Models/

DTOs pro request/response. POCOs, žádná logika.

| Soubor | Použití |
|--------|---------|
| `CitrixLoginRequest.cs` | Body pro `POST /api/citrix-diagnostics/explicit-login` |
| `CitrixLoginResponse.cs` | Response z login endpointu (sessionToken, status codes z každého kroku, body previews, resourcesPayload s apps) |
| `CitrixClientLogEntry.cs` | Body pro `POST /api/citrix-diagnostics/client-log` (browser → server log forwarding) |
| `CitrixProbeRequest.cs` + `Response.cs` | Generic probe endpoint pro debugging individuálních HTTP volání |

## Pages/

Razor Pages — server-rendered UI.

| Soubor | Účel |
|--------|------|
| `Index.cshtml` | Hlavní stránka. **Velký soubor (1060 řádků)** — obsahuje inline `<style>` (CSS) + `<script>` (JS) + HTML markup. Login formulář, dlaždice, status, diagnostic log toggle, info karty. |
| `Index.cshtml.cs` | Razor Pages model. Načítá `CitrixDiagnostics` sekci ze `appsettings.json`, předává do view jako `CitrixBaseUrl`, `PanelTitle`, atd. |
| `Error.cshtml` + `.cs` | Default ASP.NET error page (production) |
| `Shared/_Layout.cshtml` | Layout šablona — minimal, jen body container (header/footer odstraněn pro PoC) |
| `_ViewImports.cshtml` | TagHelpers + namespace imports |
| `_ViewStart.cshtml` | Default layout assignment |

## wwwroot/

Statické assety (CSS, JS, images).

| Cesta | Obsah |
|-------|-------|
| `css/site.css` | Default Bootstrap-derived stylesheet |
| `js/site.js` | Default ASP.NET site script (prázdný/scaffold) |
| `lib/bootstrap/` | Bootstrap 5 — UI framework |
| `lib/jquery*/` | jQuery + validation plugins |
| `favicon.ico` | Default favicon |

**Pozn.:** většina styling se dnes děje přes inline `<style>` v `Index.cshtml`. Při refactoru pro produkci přesunout do `wwwroot/css/citrix-portal.css`.

## Properties/

| Soubor | Účel |
|--------|------|
| `launchSettings.json` | URL bindings pro `dotnet run` (development only) |

## Endpoint cheatsheet (kde v Program.cs)

| Endpoint | Řádek (přibližně) | Účel |
|----------|-------------------|------|
| `POST /api/citrix-diagnostics/client-log` | ~34 | Browser → server log forwarding (audit trail) |
| `POST /api/citrix-diagnostics/server-probe` | ~59 | Generic HTTP probe na cílovou URL (debugging) |
| `POST /api/citrix-launch-status` | ~278 | GetLaunchStatus + rewrite fileFetchUrl na public host |
| `GET /api/citrix-proxy` | ~383 | Authenticated proxy pro icons + ICA download |
| `POST /api/citrix-diagnostics/explicit-login` | ~461 | **Hlavní login flow** — bootstrap → auth → Resources/List |

## Internal classes (na konci Program.cs)

| Class | Řádek | Co dělá |
|-------|-------|---------|
| `CitrixSessionEntry` | ~962 | Record-like třída pro hodnotu v session cache (CookieContainer + storeRootUri + CreatedAt) |
| `CitrixSessionCache` | ~971 | Wrapper nad `IMemoryCache`, vystavuje `Store(entry) → GUID`, `Get(guid)`, `Remove(guid)`. 20-min sliding TTL. |
| `CitrixAuthFormDefinition` | ~1004 | DTO pro výsledky parsování XML auth form (UsernameId, PasswordId, DomainId, SubmitButtonId, SubmitButtonValue, StateContext, PostBack) |
| `CitrixExplicitAuth` | ~1031 | **Hlavní helper class** (static). Konstanty (`FormCredentialTypes`, `FormLabelTypes`), header builders (`CreatePageHeaders`, `CreateBaseHeaders`), HTTP request factory (`CreateRequest`), parsery (`TryParseAuthForm`, `TryExtractMetaRefreshUrl`, `TryParseAuthMethodUris`, `TryParseJson`), utility funkce (`Preview`, `GetCookieValue`, `GetCookieNames`, `FindAuthMessage`, `FindElementValue`). |

## Časté změny — kde sahat

| Chci... | Sahej do... |
|---------|-------------|
| Změnit cílový StoreFront | `appsettings.json::CitrixDiagnostics:BaseUrl` |
| Změnit public gateway host | `appsettings.json::CitrixDiagnostics:PublicGatewayHost` |
| Přidat nový StoreFront API endpoint volání | `Program.cs` — přidat v rámci stávajícího login flow nebo nový endpoint, použij existing `CitrixExplicitAuth` helpers |
| Změnit UI / přidat tlačítko | `Pages/Index.cshtml` — HTML markup uprostřed, CSS na začátku v `<style>`, JS na konci v `<script>` |
| Změnit branding / titulek | `appsettings.json::CitrixDiagnostics:PanelTitle` + `Pages/Shared/_Layout.cshtml` |
| Přidat novou auth metodu | `Program.cs` — `CitrixExplicitAuth.TryParseAuthMethodUris` parsuje seznam, login flow zkouší `ExplicitForms` první. Pro novou metodu (např. SSO) přidat nový endpoint paralelně k `explicit-login`. |
| Změnit session TTL | `Program.cs::CitrixSessionCache.SessionTtl` |
| Přidat anti-SSRF whitelist path | `Program.cs::/api/citrix-proxy` — současný whitelist `Resources/`, přidat další prefixy |
| Disable diagnostic log toggle (production) | `Pages/Index.cshtml` — odstranit `<details>` blok s `id="citrix-log"` |

## Jak to spustit

```bash
# Vývoj
dotnet run

# Produkční publish
rm -rf ./publish
dotnet publish -c Release -o ./publish
```

Po publish zkopíruj obsah `./publish/` na deploy server, spusť `dotnet CitrixComponent.dll` nebo hostuj přes IIS.

Konfigurace přes `appsettings.json` ve výsledné `./publish/` složce (ne ze zdroje), nebo přes environment variables (`CitrixDiagnostics__BaseUrl=...`).
