# Citrix StoreFront PoC

Účelem je vytvořit komponentu, která dokáže nahradit oficiální Citrix StoreFront UI uvnitř firemního portálu — uživatel se přihlásí, vidí seznam jemu přidělených Citrix aplikací jako dlaždice a klikem některou spustí v Citrix Workspace App. Veškerá komunikace s Citrix StoreFrontem probíhá server-side; prohlížeč nikdy nedostane přímý přístup ke Citrix endpointům ani jeho session cookies.

## Jak to funguje

Citrix StoreFront je interní firemní server, na kterém jsou napojené virtualizované aplikace (GINIS, Edge, Visual Studio…). Má vlastní webovou stránku, která se ale nedá pěkně vložit do firemního portálu. Tato komponenta funguje jako tlumočník mezi portálem a Citrixem: uživatel mluví s portálem, portál to za něj přeloží do jazyka Citrixu a zase zpět.

### Při přihlášení

Uživatel zadá jméno, heslo a doménu do formuláře. Portál tyhle údaje vezme a sám projde celé Citrix přihlášení místo uživatele — odehrává roli "robotického prohlížeče". Nasbírá si přístupové sušenky (cookies), získá seznam aplikací, které má uživatel v Citrixu přidělené (Citrix admin to řídí v Citrix Studio podle skupin v Active Directory; každý uživatel může mít jiný seznam), a pošle ten seznam zpět do prohlížeče jako dlaždice s ikonami.

Přístupové sušenky zůstávají server-side. Prohlížeč dostane jen náhodný kód (token), kterým se identifikuje při dalších akcích. Pokud by někdo prohlížeč napadl, dostane kód, ne klíče od Citrixu.

### Při kliku na dlaždici

Klik dlaždice neznamená "spustit aplikaci v prohlížeči". Citrix aplikace běží na vzdáleném Citrix serveru a streamuje obraz do programu na uživatelově PC — Citrix Workspace App. Tu musí mít každý, kdo chce Citrix aplikace pouštět; v korporátu typicky předinstalovaná IT-čkem.

Sekvence po kliku:

1. Portál se zeptá Citrixu: "Dej mi vstupenku pro tuhle aplikaci."
2. Citrix vrátí jednorázovou vstupenku platnou 30 sekund.
3. Portál tu vstupenku zabalí do speciálního odkazu ve formátu `receiver://...`. Tohle URL schéma je registrované jako "rozumí mu Citrix Workspace App" — funguje stejně jako `mailto:` rozumí poštovní klient.
4. Prohlížeč ten odkaz předá Workspace App nainstalované na PC.
5. Workspace App si vstupenku vymění za otevřenou Citrix relaci a aplikace naběhne v jejím okně.

Žádný download `.ica` souboru, žádné okno se stahováním. Klik → aplikace. Stejný UX jako oficiální Citrix StoreFront.

### Ověření strukturální správnosti

Stáhnutý `.ica` soubor (vstupenka) z této komponenty obsahuje deterministický kontrolní součet `SessionsharingKey`, odvozený z aplikace, serveru a konfigurace. Pro stejný resource je tato hodnota bit-identická s `.ica` z oficiálního Citrix StoreFrontu. Per-launch tokeny (`LogonTicket`, `STA*`, `ClearPassword`) jsou unikátní v každém launch — což je správně, jednorázové bezpečnostní tokeny.

### Co bylo zákeřné

Citrix neudržuje pro tenhle scénář dokumentaci. Většina detailů byla odhalena experimentem (zachytit chování oficiálního klienta, porovnat s vlastní implementací, najít rozdíl, opakovat). Konkrétně:

- **Bootstrap přesměrování v cyklech.** Mezi přihlášením je míchané HTTP přesměrování (rozumí mu prohlížeč) a HTML značka `<meta refresh>` (vypadá jako obyčejná stránka, ale ve skutečnosti přesměrovává). Žádný HTTP klient toto nedělá automaticky — nutné odsledovat manuálně s hop limitem a regex extraktorem.
- **Citrix podle hlaviček pozná, kdo se ho ptá.** Když požadavky vypadaly jako "z aplikace" (`X-Requested-With: XMLHttpRequest`), Citrix odmítl vytvořit přihlášenou relaci. Když vypadaly jako "z prohlížeče" (jiné hlavičky), všechno prošlo. Chybová hláška to nikam nenapsala — vypadalo to, že náhodně padá.
- **Tlačítko Přihlásit musí být v češtině.** Citrix validuje pole formuláře proti přesnému textu na tlačítku. `loginBtn=Log On` (jak je to v dokumentaci) zamítá. Stačilo to změnit na `loginBtn=Přihlásit`. Tenhle StoreFront je česká instalace.
- **Bezpečnostní token (CSRF) se po každém kroku mění.** Cache-ování tokenu na začátku flow všechny další kroky rozbilo, protože server očekával nový token, ne starý.
- **Internal vs public hostname.** Server-side komunikace probíhá na interním hostnamu (`citrixvpx01.fis.acr`). Citrix vrací vstupenku, která říká "stáhni si ICA z `citrixvpx01.fis.acr`". Jenže uživatelův počítač na interní adresu nedosáhne — chodí přes veřejnou bránu (`pnagent.fis.acr`). Bez přepisu té adresy v odpovědi se vstupenka nedá použít. Nejvíc zdržující detail, protože všechno fungovalo až do okamžiku spuštění, pak Workspace App tiše selhal bez chybové hlášky.
- **Workspace App musí mít přidaný store.** Když uživatel `receiver://` link kliknul a Workspace App neměla Citrix přidaný jako "známý účet", taky tiše selhala. Po přidání store v UI Workspace App (Add Account → URL discovery) všechno fungovalo. Pro celofiremní nasazení jde pushnout přes GPO s parametrem MSI instalace.

## Architektura

```
[Browser]  ──┐
             │  HTTPS (jen na portál)
             ▼
        [Portal]  ─────────────────►  [Citrix StoreFront]
        ASP.NET Core 10                 (interní hostname)
        IMemoryCache session cache       │
        Anti-SSRF proxy                  │ HDX session
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

Spuštění aplikace probíhá protokolem `receiver://` (registrovaným Citrix Workspace App při instalaci jako OS-level URL handler). Portál sestaví URL ve formátu:

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

Cílem je odstranit přihlašovací formulář — uživatel otevře portál a vidí dlaždice okamžitě. Předpokládá použití Citrix StoreFront `IntegratedWindows` auth metody (`DomainPassthroughAuth/Login`) s Kerberos delegací identity uživatele přes service account, pod kterým běží portál.

Vyžaduje:
- Service account v AD
- SPN registrace pro hostname portálu (`HTTP/portal.<doména>`)
- Resource-based Constrained Delegation (RBCD) na Citrix StoreFront server (`Set-ADComputer ... -PrincipalsAllowedToDelegateToAccount`) — funguje napříč doménami v rámci forest trust
- Citrix StoreFront: `Domain Pass-through` enabled, Trusted Domains konfigurované pro všechny relevantní domény uživatelů
- Code: `Microsoft.AspNetCore.Authentication.Negotiate` + nový endpoint `/api/citrix-diagnostics/sso-login` s `[Authorize]` a `WindowsIdentity.RunImpersonated` pro Kerberos delegaci

Frontend pokus SSO endpoint first, fallback na manuální form při 401 (zachová backwards compatibility pro mimo-doménové scénáře).

### Refactor pro produkci

Detaily v [`docs/code-audit.md`](docs/code-audit.md). Hlavní body:

- Split `Program.cs` (1435 řádků) na endpoint files + service layer (`ICitrixStoreFrontClient`)
- Typed `IOptions<CitrixOptions>` místo `configuration["..."]` magic strings
- `IHttpClientFactory` registrace (connection pooling, named clients)
- Distribuovaná cache (Redis) místo `IMemoryCache` pro multi-instance scaling
- CSS + JS extrakce z `Pages/Index.cshtml` (1060 řádků inline) do `wwwroot/`
- Unit testy s mocked `HttpClient`, integration testy proti reálnému StoreFrontu

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
