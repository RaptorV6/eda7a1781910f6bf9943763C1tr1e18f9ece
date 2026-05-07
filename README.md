# CitrixComponent

Reusable ASP.NET Core 10 komponenta pro integraci Citrix StoreFrontu do libovolné hostitelské aplikace. Uživatel se přihlásí, vidí seznam jemu přidělených Citrix aplikací jako dlaždice a klikem některou spustí v Citrix Workspace App. Veškerá komunikace s Citrix StoreFrontem probíhá server-side; prohlížeč nikdy nedostane přímý přístup ke Citrix endpointům ani jeho session cookies.

> **Není to portál ani okleštěná verze Citrixu.** Je to komponenta — server-side proxy vrstva která hovoří protokolem StoreFront API a překládá ho do vlastního UI. Jde embedovat do jakékoli hostitelské .NET webové aplikace.

## Jak to funguje

### Kontext: co je StoreFront a proč to děláme takhle

Citrix StoreFront je webový server od Citrixu, který spravuje přihlášení a seznam virtualizovaných aplikací. Má vlastní UI na `citrixgw01.fis.acr` — ale to UI nelze embedovat do jiné aplikace (má `X-Frame-Options: deny`) ani přizpůsobit vizuálně.

StoreFront zároveň vystavuje **REST/XML API** — přesně to samé API co používá jeho vlastní webový frontend. Tato komponenta to API volá přímo ze serveru, bez jakéhokoli Citrix kódu. Vznikla reverzním inženýrstvím: zachytit HTTP provoz prohlížeče na officiálním Citrix UI (HAR capture), pochopit sekvenci volání, zreplikovat ji v C#.

### Přihlášení — server-side auth flow

Citrix StoreFront nevystavuje jednoduché `POST /login` s username/heslo. Přihlášení je 7-krokový HTTP protokol, který komponenta provádí celý na serveru:

```
1. GET  /Citrix/FISWeb/                 → NetScaler vrátí 302 přesměrování
2. GET  /cgi/setclient?wica             → 200 HTML obsahující <meta http-equiv="refresh">
                                          (není to HTTP redirect — je to HTML tag)
3. GET  /Citrix/FISWeb/                 → StoreFront vytvoří session (ASP.NET_SessionId + CsrfToken)
4. POST /Authentication/GetAuthMethods  → XML: jaké auth metody StoreFront podporuje
5. POST /ExplicitAuth/Login             → XML: formulář s ID políček a StateContext token
6. POST /ExplicitAuth/LoginAttempt      → přihlášení; odpověď: <Result>success</Result>
7. POST /Resources/List                 → JSON: seznam aplikací přiřazených tomuto uživateli
```

Proč `AllowAutoRedirect = false`: standardní HTTP klient by kroky 1 a 3 provedl sám, ale krok 2 je HTML `<meta refresh>` (HTTP 200, ne 3xx) — ten žádný klient nesleduje automaticky. Proto komponenta sleduje všechna přesměrování ručně a po každém kroku čte cookies.

Přiřazení aplikací uživateli řídí Citrix admin v Citrix Studio podle skupin v Active Directory — každý uživatel může vidět jiný seznam.

### Session a bezpečnost

Po úspěšném přihlášení komponenta uloží Citrix session cookies (`ASP.NET_SessionId`, `CsrfToken`, `NSC_*`, `CtxsAuthId`) do serverové paměti (`IMemoryCache`) pod náhodným GUID. Prohlížeč dostane jen tento GUID — nikdy ne samotné cookies.

```
Přihlášení:   browser → komponenta (HTTPS) → StoreFront (HTTPS)
                                           ↓
                              cookies → IMemoryCache[GUID]
                              GUID → browser

Další akce:   browser pošle GUID → komponenta najde cookies → zavolá StoreFront
```

Heslo cestuje po síti výhradně přes HTTPS (TLS). Server ho neloguje — do logů jde pouze username a doména. Po dokončení přihlášení heslo ze serverové paměti zmizí.

### Spuštění aplikace — receiver:// protokol

Citrix aplikace neběží v prohlížeči. Běží na vzdáleném Citrix serveru (XenApp/CVAD) a obraz se streamuje do **Citrix Workspace App** nainstalované na PC uživatele. Workspace App při instalaci zaregistruje `receiver://` jako OS-level URL handler — stejně jako `mailto:` otevře poštovního klienta.

Sekvence po kliku na dlaždici:

```
1. Browser zavolá: POST /api/citrix-launch-status?session=<GUID>&resourceId=<id>
2. Komponenta zavolá StoreFront: POST /Resources/GetLaunchStatus/<id>
   → StoreFront vrátí JSON s fileFetchUrl + fileFetchTicket (platnost ~30 s)
3. Komponenta přepíše fileFetchUrl:
   citrixvpx01.fis.acr  →  pnagent.fis.acr
   (interní hostname → veřejná gateway; PC uživatele interní hostname nevidí)
4. Browser sestaví receiver:// URL a přesměruje na něj
5. OS předá URL Workspace App → ta fetchne ICA soubor z gateway → spustí HDX session
```

Klik → aplikace naběhne v okně Workspace App. Žádné viditelné stahování, žádné dialogy.

### Ověření správnosti implementace

Komponenta generuje ICA soubory (vstupenky) strukturálně identické s officiálním Citrix StoreFront UI. Konkrétně `SessionsharingKey` — deterministická hodnota odvozená z aplikace, serveru a konfigurace — je bit-identická s hodnotou z officiálního StoreFrontu pro stejný resource. Per-launch tokeny (`LogonTicket`, `STA*`) jsou unikátní v každém launch, jak má být.

### Co bylo zákeřné (pro budoucí integrátory)

Citrix pro tento scénář nemá dokumentaci. Vše vzniklo experimentem:

- **`<meta refresh>` není HTTP redirect.** NetScaler vrátí HTTP 200 s HTML stránkou obsahující `<META HTTP-EQUIV="REFRESH">`. Žádný HTTP klient toto nenásleduje automaticky — nutné parsovat regex extraktorem.
- **Hlavičky prozradí, kdo volá.** Pokud bootstrap requesty obsahují `X-Requested-With: XMLHttpRequest` (AJAX marker), StoreFront nevytvoří `ASP.NET_SessionId`. Session pak chybí a každý další krok vrátí `sessiontimeout`. Chyba nikam nenapsala proč — vypadalo to jako náhodné selhání.
- **Submit button musí být česky.** StoreFront validuje name=value párů formuláře včetně submit buttonu. `loginBtn=Log On` (dokumentace) zamítá. Správně je `loginBtn=Přihlásit` — tenhle StoreFront je česká instalace. Komponenta parsuje správnou hodnotu z XML formuláře, hard-coded fallback jen při selhání parseru.
- **CsrfToken se rotuje po každém hopu.** Token z kroku 3 je stale v kroku 6. Nutné re-číst cookie po každém API volání.
- **Internal vs public hostname v ICA.** StoreFront vrátí `fileFetchUrl` s interním hostname (`citrixvpx01.fis.acr`). Workspace App na klientovi ho nedosáhne — chodí přes `pnagent.fis.acr`. Bez přepisu Workspace App tiše selže bez chybové hlášky.
- **Workspace App musí mít přidaný store.** `receiver://` link selže tiše, pokud Workspace App nemá store přidaný (Add Account → URL discovery). Pro hromadné nasazení: MSI parametr `STORE0=...` nebo GPO.

## Architektura

```
[Browser]  ──┐
             │  HTTPS (jen na komponentu)
             ▼
    [CitrixComponent]  ───────────►  [Citrix StoreFront]
    ASP.NET Core 10                   (interní hostname)
    IMemoryCache session cache         │
    Anti-SSRF proxy                    │ HDX session
                                       ▼
                          [Workspace App na klientu]
                          (přes public gateway)
```

Tři interní HTTP endpointy:

| Endpoint | Účel |
|----------|------|
| `POST /api/citrix-diagnostics/explicit-login` | Bootstrap → ExplicitForms autentizace → Resources/List. Vrátí seznam aplikací a opaque session token. |
| `POST /api/citrix-launch-status` | Vyžádá launch ticket pro konkrétní aplikaci. Přepíše `fileFetchUrl` z interního StoreFront hostu na public gateway. |
| `GET /api/citrix-proxy?session=<token>&path=<rel>` | Authenticated proxy pro ikony aplikací a fallback ICA download. Anti-SSRF whitelist: `path` musí začínat `Resources/`. |

Session cache (`CitrixSessionCache`) drží `CookieContainer` s autentizovanými cookies (`ASP.NET_SessionId`, `CsrfToken`, `NSC_*`) pod náhodným GUID. TTL 20 min sliding (= StoreFront default). Browser drží jen GUID.

Spuštění aplikace probíhá protokolem `receiver://` (registrovaným Citrix Workspace App při instalaci jako OS-level URL handler). Komponenta sestaví URL ve formátu:

```
receiver://<public-gateway>/<store-path>/clientAssistant/getIcaFile/<base64-params>
```

kde `<base64-params>` obsahuje `action=launch&serverProtocolVersion=1&transport=https&ticket=<fileFetchTicket>`. Browser URL předá Workspace App, ta si vyzvedne ICA a spustí HDX session — bez stahování souboru a bez viditelné UI.

## Požadavky

### Server
- .NET 10 SDK / runtime
- Síťový dosah na interní StoreFront (např. `citrixvpx01.fis.acr`)

### Klient
- Citrix Workspace App nebo Citrix Secure Access Client (registruje `receiver:` schéma a `application/x-ica` MIME)
- Workspace App musí mít přidaný target store (Add Account v UI nebo přes MSI parametr `STORE0=...` při GPO push)
- HTTPS dosah na public Citrix gateway (např. `pnagent.fis.acr`)

## Konfigurace

`appsettings.json`:

```json
{
  "CitrixDiagnostics": {
    "BaseUrl": "https://citrixvpx01.fis.acr/Citrix/FISWeb/",
    "PublicGatewayHost": "pnagent.fis.acr",
    "PublicStorePath": "/Citrix/FIS",
    "PanelTitle": "Citrix aplikace",
    "BodyPreviewLimit": 1200
  }
}
```

| Klíč | Význam |
|------|--------|
| `BaseUrl` | Interní URL StoreFrontu pro server-side autentizaci a Resources/List. Trailing slash povinný. |
| `PublicGatewayHost` | Public hostname kam Workspace App fetchuje ICA. Server-side host bývá interní → klient ho nedosáhne, proto rewrite v `/api/citrix-launch-status`. |
| `PublicStorePath` | Path prefix na public gateway pro `clientAssistant/getIcaFile`. Liší se od path v `BaseUrl` (gateway má jinou URL strukturu). |
| `PanelTitle` | UI hlavička. |
| `BodyPreviewLimit` | Max znaků logovaných z HTTP body (diagnostické). |

## Build & run

```bash
dotnet build
dotnet run
```

Produkční publish:

```bash
rm -rf ./publish
dotnet publish -c Release -o ./publish
```

Obsah `./publish/` zkopírovat na deployment server, spustit přes Kestrel (`dotnet CitrixComponent.dll`) nebo IIS.

Konfigurace lze přepsat environment proměnnými: `CitrixDiagnostics__BaseUrl=...`, `CitrixDiagnostics__PublicGatewayHost=...`, atd.

## Bezpečnostní model

- StoreFront cookies (`ASP.NET_SessionId`, `CsrfToken`, `NSC_TASS`, `NSC_AAAC`) zůstávají server-side. Browser drží jen opaque GUID session token.
- Anti-SSRF na `/api/citrix-proxy`: `path` MUSÍ začínat `Resources/`, NESMÍ obsahovat `..` ani `://`, NESMÍ začínat `/`.
- Heslo se neloguje. Loguje se jen username + doména pro audit.
- Session cache 20 min sliding TTL, automatický cleanup expired entries.

## Specifika cílového StoreFrontu

PoC byl validován proti `https://citrixvpx01.fis.acr/Citrix/FISWeb/` (Web API verze `2-6`, IIS-hosted, česká instalace). Pro replikaci proti jinému StoreFront deploymentu jsou relevantní následující detaily:

### Bootstrap chain

Initial GET na `/Citrix/FISWeb/` neprojde přímo. Sekvence:

1. `GET /Citrix/FISWeb/` → HTTP 302 → `/cgi/setclient?wica`
2. `GET /cgi/setclient?wica` → HTTP 200 s `<META HTTP-EQUIV="REFRESH" content="0; url=/Citrix/FISWeb">` (HTML meta-refresh, NE 3xx)
3. `GET /Citrix/FISWeb` → HTTP 301 → `/Citrix/FISWeb/`
4. `GET /Citrix/FISWeb/` → HTTP 200, StoreFront vytvoří `ASP.NET_SessionId` + `CsrfToken` cookies

`HttpClientHandler.AllowAutoRedirect = false` je nutný — auto-follow zvládne kroky 1 a 3, ale NE krok 2 (HTML meta-refresh není HTTP redirect). Implementace prochází chain manuálně s hop limitem 5 a regex-based meta-refresh extraktorem.

### Page vs API hlavičky

StoreFront se chová odlišně podle hlaviček `X-Requested-With` a `X-Citrix-IsUsingHTTPS`. Pokud bootstrap request obsahuje API marker `X-Requested-With: XMLHttpRequest`, StoreFront NEVYTVOŘÍ ASP.NET session.

| Fáze | Hlavičky |
|------|----------|
| Bootstrap navigation (initial GET, redirect hops, meta-refresh hops) | `Accept: text/html,application/xhtml+xml,...`, `Upgrade-Insecure-Requests: 1`, BEZ `X-Requested-With`, BEZ `X-Citrix-IsUsingHTTPS` |
| StoreFront API calls | `Accept: application/xml, text/xml, */*; q=0.01`, `X-Requested-With: XMLHttpRequest`, `X-Citrix-IsUsingHTTPS: Yes`, `Csrf-Token: <value>`, `X-Citrix-AM-CredentialTypes: ...`, `X-Citrix-AM-LabelTypes: ...` |

Implementace má dvě helper metody: `CitrixExplicitAuth.CreatePageHeaders` a `CitrixExplicitAuth.CreateBaseHeaders`. Nemíchat.

### `ExplicitAuth/Login` POST-first

Na tomto deploymentu IIS odmítá `GET /ExplicitAuth/Login` s HTTP 404. POST s prázdným `application/x-www-form-urlencoded` body funguje a vrací form definition XML. Implementace zkouší POST first, GET jako fallback.

### CSRF token rotace

`CsrfToken` cookie se reissue-uje po několika hopech (po bootstrap chain, po `GetAuthMethods`). Token zachycený na začátku flow je stale v okamžiku `LoginAttempt`. Implementace re-readuje cookie po každém hopu.

### Lokalizovaný submit button

`LoginAttempt` POST body musí obsahovat name=value pár submit buttonu přesně jak ho deployment očekává. Na české instalaci `loginBtn=Přihlásit`. Implementace parsuje `Credential` elementy z form definition XML pro extraction `SubmitButtonValue`; hard-coded fallback `loginBtn=Přihlásit` jen když parser selže.

### Internal vs public hostname

Server-side komunikace se StoreFrontem probíhá na **interním** hostnamu (`citrixvpx01.fis.acr`). Response z `Resources/GetLaunchStatus` obsahuje `fileFetchUrl` se stejným interním hostem. Citrix Workspace App na klientovi obvykle nemá síťový dosah na interní hostname — chodí přes public gateway (`pnagent.fis.acr`).

Bez přepisu `fileFetchUrl` selže receiver:// invokace tiše (nglauncher.exe spustí, ale neumí fetchnout ICA z interního hostu). `/api/citrix-launch-status` proto přepíše host před vrácením JSON browseru. Konfigurace přes `PublicGatewayHost` v appsettings.

## Status

End-to-end funkční. Auth + Resources/List + ikony proxy + receiver:// silent launch ověřeno proti reálnému StoreFrontu s 9 aplikacemi (GINIS sady, MS Edge, Visual Studio Pro 2022, RDP, Reporty FIS).

## Plánovaný vývoj

### Automatická autentizace přes Active Directory

Cílem je odstranit přihlašovací formulář — uživatel otevře komponentu a vidí dlaždice okamžitě. Předpokládá použití Citrix StoreFront `IntegratedWindows` auth metody (`DomainPassthroughAuth/Login`) s Kerberos delegací identity uživatele přes service account, pod kterým komponenta běží.

Vyžaduje:
- Service account v AD
- SPN registrace pro hostname hostitelského serveru (`HTTP/<server>.<doména>`)
- Resource-based Constrained Delegation (RBCD) na Citrix StoreFront server (`Set-ADComputer VXXXX22FISXVA09 -PrincipalsAllowedToDelegateToAccount <hostitelský-server>`)
- Trust mezi uživatelskými doménami a FIS doménou bez SID filtering quarantine na obou stranách
- Citrix StoreFront: `Domain Pass-through` enabled, Trusted Domains konfigurované pro všechny relevantní domény uživatelů
- Code: `Microsoft.AspNetCore.Authentication.Negotiate` + endpoint s `[Authorize]` a `WindowsIdentity.RunImpersonated(HttpContext.User.AccessToken)` pro Kerberos delegaci přes RBCD

Frontend zkusí SSO automaticky při načtení stránky, fallback na manuální formulář při 401.

### Refactor pro produkci

Detaily v [`docs/code-audit.md`](docs/code-audit.md). Hlavní body:

- Split `Program.cs` (1435 řádků) na endpoint files + service layer (`ICitrixStoreFrontClient`)
- Typed `IOptions<CitrixOptions>` místo `configuration["..."]` magic strings
- `IHttpClientFactory` registrace (connection pooling, named clients)
- Distribuovaná cache (Redis) místo `IMemoryCache` pro multi-instance scaling
- CSS + JS extrakce z `Pages/Index.cshtml` (1060 řádků inline) do `wwwroot/`
- Unit testy s mocked `HttpClient`, integration testy proti reálnému StoreFrontu

## Co je v kódu

### Program.cs — veškerá backend logika

Celý backend žije v jednom souboru (`Program.cs`, ~1400 řádků). Struktura:

**Horní část — setup:**
```csharp
builder.Services.AddRazorPages();
builder.Services.AddMemoryCache();
builder.Services.AddSingleton<CitrixSessionCache>();
builder.Services.AddAuthentication(IISDefaults.AuthenticationScheme);
```
Registrace služeb: Razor Pages pro frontend, IMemoryCache pro server-side session storage, Windows Auth přes IIS.

**Endpointy (minimal API):**

| Endpoint | Co dělá |
|---|---|
| `POST /api/citrix-diagnostics/client-log` | Přijme log zprávu z browseru, zaloguje ji server-side |
| `POST /api/citrix-diagnostics/server-probe` | Diagnostický: pošle libovolný HTTP request na StoreFront a vrátí odpověď (pro ladění) |
| `POST /api/citrix-diagnostics/explicit-login` | **Hlavní endpoint** — 7-krokový auth flow, vrátí seznam apps + session token |
| `POST /api/citrix-launch-status` | Vyžádá launch ticket pro konkrétní aplikaci, přepíše interní hostname na public gateway |
| `GET /api/citrix-proxy` | Authenticated proxy: ikony aplikací + ICA download |

**Dolní část — helper třídy:**

```
CitrixSessionCache        wrapper nad IMemoryCache; GUID → CookieContainer + storeRootUri
CitrixSessionEntry        jedna autentizovaná session (cookies + URL)
CitrixAuthFormDefinition  parsovaný login formulář ze StoreFrontu (field IDs ze XML)
CitrixExplicitAuth        statická třída: helper metody pro hlavičky, XML parsing, preview
```

### explicit-login: 7-krokový auth flow

Toto je srdce celé komponenty. Simuluje přesně co dělá prohlížeč na `citrixgw01.fis.acr`:

```
Krok 1  GET /Citrix/FISWeb/                     → 302 na NetScaler bootstrap
Krok 2  GET /cgi/setclient?wica                 → 200 HTML s <meta refresh> (ne 3xx!)
Krok 3  GET /Citrix/FISWeb → 301 → /Citrix/FISWeb/  → StoreFront vytvoří ASP.NET_SessionId
Krok 4  POST /Authentication/GetAuthMethods     → XML seznam auth metod
Krok 5  POST /ExplicitAuth/Login                → XML formulář (field IDs, StateContext)
Krok 6  POST /ExplicitAuth/LoginAttempt         → přihlášení; <Result>success</Result>
Krok 7  POST /Resources/List                    → JSON seznam aplikací uživatele
```

Po kroku 7: cookies uloženy do `IMemoryCache` pod GUID, GUID vrácen browseru. Heslo v paměti zanikne.

### Pages/Index.cshtml — frontend

Razor Page (~1000 řádků, HTML + inline CSS + JavaScript). Obsahuje:
- Login formulář (username, heslo, doména)
- JavaScript volající `/api/citrix-diagnostics/explicit-login` přes `fetch()`
- Grid dlaždic s ikonami načtenými přes `/api/citrix-proxy`
- Click handler: `fetch('/api/citrix-launch-status')` → sestaví `receiver://` URL → `window.location` předá Workspace App

### appsettings.json — konfigurace

Jediný konfigurační soubor. Při nasazení na jiný server stačí změnit hodnoty zde — bez překompilování.

## Struktura repozitáře

```
.
├── README.md                       Tato dokumentace
├── CitrixComponent.csproj          .NET 10 SDK projekt, žádné externí NuGet
├── Program.cs                      Veškerá runtime logika (endpoints + helpers)
├── appsettings.json                Runtime konfigurace
├── Models/                         Request/response DTOs
├── Pages/                          Razor Pages UI
│   ├── Index.cshtml                Hlavní stránka (login + dlaždice + diagnostic toggle)
│   └── Index.cshtml.cs             Razor Pages model
├── Properties/launchSettings.json  Development URL bindings
├── wwwroot/                        Bootstrap, jQuery, statické assety
└── docs/
    ├── code-audit.md               Refactor priority + DRY/OOP analýza
    └── file-map.md                 Mapa kde najít co + cheatsheet pro běžné edity
```
