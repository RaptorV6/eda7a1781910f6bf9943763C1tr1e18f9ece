# Current task

Last updated: 2026-06-22

## Objective

Implementovat AD SSO — automatické přihlášení do Citrixu pomocí Windows doménové identity uživatele, bez ručního zadání hesla.

## Current status — flow hotový v C#, blokuje cross-domain Kerberos

### Co funguje
- 8-krokový CitrixAuth + FISAuth + IntegratedWindows flow je ověřen v PowerShellu jako ACR\VanD
- C# endpoint `/api/citrix-sso/login` flow technicky projde (`loginSucceeded=True`, `resourcesStatusCode=200`)
- Krok 4 (IntegratedWindows volání s `UseDefaultCredentials`) funguje jako process fallback

### Co nefunguje
- `integratedAuthMode=process-fallback` místo `impersonated` — volá se pod app poolem
- `klist get HTTP/vxxxx22fisxvi15.fis.acr` selhává pro uživatele vand@ACR (cross-domain)
- `/api/whoami` → `isKerberos=False, impersonationLevel=None`

### Blokátor
Cross-domain Kerberos ACR → FIS.ACR. Toto je AD/infrastruktura otázka, ne kódová.

## Dostupné alternativy (pokud Kerberos nejde vyřešit rychle)

**Alternativa A — process-fallback jako feature:**
Přijmout že SSO proběhne pod service accountem app poolu, ne uživatelsky. Vrátí aplikace přiřazené tomu service accountu v Citrixu. Použitelné pokud jsou všichni uživatelé homogenní.

**Alternativa B — hybrid flow:**
Zachovat explicit-login (uživatel zadá heslo jednou) + CitrixAuth SSO jako optional. Browser může cachovat session.

**Alternativa C — čekat na AD tým:**
AD tým musí vyřešit cross-domain trust / Kerberos referral. Pak přidat `WindowsIdentity.RunImpersonated` v C# kolem kroku 4.

## Endpoints (aktuální)

- `POST /api/citrix-sso/login` — **SSO endpoint** (process-fallback mode, čeká na Kerberos)
- `POST /api/citrix-diagnostics/explicit-login` — full explicit auth + Resources/List + session token
- `GET /api/citrix-diagnostics/server-probe` — bootstrap chain probe
- `POST /api/citrix-diagnostics/citrixauth-probe` — probe CitrixAuth/Login s Workspace App hlavičkami
- `GET /api/whoami` — diagnostika Windows identity (isKerberos, impersonationLevel)
- `GET /api/citrix-proxy?session=<token>&path=<rel>` — proxy (anti-SSRF whitelist)
- `GET /api/citrix-launch-status` — přepisuje fileFetchUrl host
- `POST /api/client-log` — browser console relay

## Cílový výstup po vyřešení Kerberos

```json
{
  "ok": true,
  "loginSucceeded": true,
  "integratedAuthMode": "impersonated",
  "resourcesStatusCode": 200,
  "resourcesPreview": "{...aplikace uživatele...}"
}
```
