# Session handoff

Last updated: 2026-06-22

## Summary

8-krokový CitrixAuth + FISAuth + IntegratedWindows flow je plně zdokumentovaný a ověřený v PowerShellu jako uživatel ACR\VanD (výsledek: 9 aplikací). C# endpoint `/api/citrix-sso/login` flow technicky zvládá, ale přihlašuje se pod identitou app poolu (`process-fallback`), ne skutečného uživatele. Blokátor: IIS na VXXXX22FISXVI15 (FIS.ACR) dostává od uživatele vand@ACR pouze NTLM — Kerberos ticket nejde vydat cross-domain.

## Current state

**Branch:** `main`

**Last known commit:** `2f802e5 feat: add IntegratedWindows process fallback`

**Komponenta běží na:** `http://VXXXX22FISXVI15.fis.acr:89/`

## Ověřený 8-krokový FISAuth flow

PowerShell jako ACR\VanD (UseDefaultCredentials) → funguje, vrátí 9 aplikací:

1. POST `CitrixAuth/Login` (prázdné tělo, Workspace App hlavičky) → HTTP 401 + `WWW-Authenticate: CitrixAuth realm=... locations=<tokenUrl>`
2. POST `<tokenUrl>` s `requesttoken` XML (for-service=realm, for-service-url=loginUrl) → HTTP 401 + `WWW-Authenticate: CitrixAuth realm=... locations=<protocolsUrl> serviceroot-hint=<tokenServiceUrl>`
3. POST `<protocolsUrl>` s `requesttoken` XML → `requesttokenchoices` XML → vybrat `IntegratedWindows` choice → `<integratedUrl>`
4. POST `<integratedUrl>` s `requesttoken` XML + `UseDefaultCredentials=true` → `requesttokenresponse` XML s `<token>` (innerToken)
5. POST `<tokenUrl>` s `requesttoken` XML + `Authorization: CitrixAuth <innerToken>` → `requesttokenresponse` XML s `<token>` (outerToken / loginToken)
6. POST `CitrixAuth/Login` s `Authorization: CitrixAuth <loginToken>` → `<Result>success</Result>` HTTP 200
7. GET `https://pnagent.fis.acr/Citrix/FISWeb/` (page headers, browser UA) → cookies vč. `CsrfToken`
8. POST `Resources/List` (format=json, CSRF hlavička) → seznam aplikací uživatele

**Endpoints FISAuth:**
- `https://pnagent.fis.acr/Citrix/FISAuth/auth/v1/token`
- `https://pnagent.fis.acr/Citrix/FISAuth/auth/v1/protocols`
- `https://pnagent.fis.acr/Citrix/FISAuth/Integrated/Authenticate`

## Stav C# implementace

Endpoint `/api/citrix-sso/login` v `Program.cs`:
- Technicky projde flow → `loginSucceeded=True`, `resourcesStatusCode=200`
- ALE: `integratedAuthMode=process-fallback`, `resources=[]`
- Příčina: krok 4 (`IntegratedWindows/Authenticate`) se volá pod identitou app poolu, ne uživatele

Diagnostický endpoint `/api/whoami`:
```
authenticated=True
name=ACR\VanD
authType=Negotiate
isKerberos=False
impersonationLevel=None
```

## Blokátor — cross-domain Kerberos

- Uživatel: `vand@ACR` (doména ACR)
- Server: `VXXXX22FISXVI15.fis.acr` (doména FIS.ACR)
- SPN existují: `HTTP/vxxxx22fisxvi15`, `HTTP/vxxxx22fisxvi15.fis.acr`, `HTTP/vxxxx22fisxvi15.fis.acr:89`
- RBCD nastaveno: VXXXX22FISXVI15 → XVA07/XVA08/XVA09
- `klist get HTTP/vxxxx22fisxvi15.fis.acr` → chyba `0xc000018b` ("SAM databáze neobsahuje účet pro tento důvěryhodný vztah")
- BackConnectionHostNames test: nepomohl
- Secure channel serveru do FIS.ACR: v pořádku

**Závěr:** problém je v AD trustu ACR ↔ FIS.ACR nebo v cross-domain Kerberos referral. Není to problém kódu.

## Cílový stav

```
/api/whoami → isKerberos=True, impersonationLevel=Impersonation
/api/citrix-sso/login → integratedAuthMode=impersonated, resources=[...aplikace...]
```

## Otevřené otázky

1. Jaký je přesný typ AD trustu ACR ↔ FIS.ACR? (Forest trust, external trust, nebo žádný?)
2. Fungovalo by alternativní řešení: uložit service account credentials a volat FISAuth IntegratedWindows jako service account (process-level)?
3. Je možné nasadit CitrixComponent přímo na server v FIS.ACR doméně?

## Rules still active

- `AllowAutoRedirect = false` mandatory
- CSRF token re-read po každém hopu
- Czech strings preserved
- Page headers (bez `X-Requested-With`) pro navigaci; API headers pro `/Resources/*`, `/Authentication/*`
- Kerberos je nyní relevantní pro first hop (IIS auth), ale NESOUVISÍ s HTTP Auth layer vůči StoreFrontu

## Next session plan

Pokud chce uživatel pokračovat v kódu bez Kerberos:
- Alternativa A: `process-fallback` mode jako vědomá feature — SSO pod service accountem (ne uživatelsky) — pro scénáře kde všichni mají stejné aplikace
- Alternativa B: zachovat explicit-login flow (uživatel zadá heslo jednou) + implementovat SSO jako optional enhancement

Pokud AD tým vyřeší Kerberos:
1. Ověřit `klist get HTTP/vxxxx22fisxvi15.fis.acr` → musí projít
2. Ověřit `/api/whoami` → `isKerberos=True`
3. V C# přidat `WindowsIdentity.RunImpersonated` kolem kroku 4 (IntegratedWindows volání)
4. Ověřit `/api/citrix-sso/login` → `integratedAuthMode=impersonated`
