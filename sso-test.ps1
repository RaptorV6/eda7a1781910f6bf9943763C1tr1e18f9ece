# CitrixComponent — DomainPassthroughAuth test
# Spustit na serveru VXXXX22FISXVI15 jako uživatel, jehož SSO chceme ověřit
# Nepotřebuje nasazenou aplikaci — volá pnagent přímo
# Výsledky zkopírovat a poslat zpět

param(
    [string]$BaseUrl = "https://pnagent.fis.acr/Citrix/FISWeb/"
)

$sep = "`n" + ("=" * 60) + "`n"

# ── 0. Kdo jsem ────────────────────────────────────────────────
Write-Host $sep
Write-Host "0. AKTUÁLNÍ UŽIVATEL"
Write-Host $sep
$me = [System.Security.Principal.WindowsIdentity]::GetCurrent()
Write-Host "Name:             $($me.Name)"
Write-Host "AuthType:         $($me.AuthenticationType)"
Write-Host "ImpersonLevel:    $($me.ImpersonationLevel)"
Write-Host "IsAuthenticated:  $($me.IsAuthenticated)"

# ── 1. Bootstrap GET /Citrix/FISWeb/ ───────────────────────────
Write-Host $sep
Write-Host "1. BOOTSTRAP — GET $BaseUrl"
Write-Host $sep

$session = New-Object Microsoft.PowerShell.Commands.WebRequestSession

try {
    $r1 = Invoke-WebRequest -Uri $BaseUrl `
        -WebSession $session `
        -UseDefaultCredentials `
        -UseBasicParsing `
        -MaximumRedirection 0 `
        -ErrorAction SilentlyContinue

    Write-Host "StatusCode:   $($r1.StatusCode)"
    Write-Host "Cookies:      $(($session.Cookies.GetCookies($BaseUrl) | ForEach-Object { $_.Name }) -join ', ')"
} catch {
    # 302 hází výjimku při MaximumRedirection 0 — to je OK
    if ($_.Exception.Response) {
        $code = [int]$_.Exception.Response.StatusCode
        Write-Host "StatusCode:   $code (redirect — OK pro citrixgw01, chyba pro pnagent)"
        Write-Host "Location:     $($_.Exception.Response.Headers['Location'])"
    } else {
        Write-Host "CHYBA: $_"
    }
}

# Zkus znovu s redirecty povoleny (pnagent by měl vrátit 200 přímo)
try {
    $r1full = Invoke-WebRequest -Uri $BaseUrl `
        -WebSession $session `
        -UseDefaultCredentials `
        -UseBasicParsing `
        -TimeoutSec 15

    $csrfCookie = $session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }
    $sessionCookie = $session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "ASP.NET_SessionId" }
    $allCookies = ($session.Cookies.GetCookies($BaseUrl) | ForEach-Object { $_.Name }) -join ', '

    Write-Host "Final URL:    $($r1full.BaseResponse.ResponseUri)"
    Write-Host "StatusCode:   $($r1full.StatusCode)"
    Write-Host "Cookies:      $allCookies"
    Write-Host "CsrfToken:    $(if ($csrfCookie) { 'ANO ✓' } else { 'NE ✗' })"
    Write-Host "SessionId:    $(if ($sessionCookie) { 'ANO ✓' } else { 'NE ✗' })"
} catch {
    Write-Host "Bootstrap selhal: $_"
}

# ── 2. GetAuthMethods ──────────────────────────────────────────
Write-Host $sep
Write-Host "2. GetAuthMethods — POST Authentication/GetAuthMethods"
Write-Host $sep

$csrf = ($session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }).Value

try {
    $headers = @{
        "Accept"               = "application/xml, text/xml, */*"
        "X-Requested-With"     = "XMLHttpRequest"
        "X-Citrix-IsUsingHTTPS" = "Yes"
        "Referer"              = $BaseUrl
    }
    if ($csrf) { $headers["Csrf-Token"] = $csrf }

    $r2 = Invoke-WebRequest -Uri "${BaseUrl}Authentication/GetAuthMethods" `
        -Method POST `
        -WebSession $session `
        -UseDefaultCredentials `
        -Headers $headers `
        -ContentType "application/x-www-form-urlencoded; charset=UTF-8" `
        -Body "" `
        -UseBasicParsing `
        -TimeoutSec 15

    # Obnov CSRF (StoreFront ho může rotovat)
    $csrf = ($session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }).Value

    Write-Host "StatusCode:   $($r2.StatusCode)"
    Write-Host "CsrfToken po: $(if ($csrf) { 'ANO ✓' } else { 'NE ✗' })"
    Write-Host "Body preview: $($r2.Content.Substring(0, [Math]::Min(500, $r2.Content.Length)))"
} catch {
    Write-Host "CHYBA: $_"
}

# ── 3. DomainPassthroughAuth/Login ─────────────────────────────
Write-Host $sep
Write-Host "3. DomainPassthroughAuth — POST DomainPassthroughAuth/Login"
Write-Host $sep
Write-Host "Toto je klíčový krok — posíláme Windows identitu místo hesla."
Write-Host ""

try {
    $headers3 = @{
        "Accept"               = "application/xml, text/xml, */*"
        "X-Requested-With"     = "XMLHttpRequest"
        "X-Citrix-IsUsingHTTPS" = "Yes"
        "Referer"              = $BaseUrl
    }
    if ($csrf) { $headers3["Csrf-Token"] = $csrf }

    $r3 = Invoke-WebRequest -Uri "${BaseUrl}DomainPassthroughAuth/Login" `
        -Method POST `
        -WebSession $session `
        -UseDefaultCredentials `
        -Headers $headers3 `
        -ContentType "application/x-www-form-urlencoded; charset=UTF-8" `
        -Body "" `
        -UseBasicParsing `
        -TimeoutSec 15

    $csrf = ($session.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }).Value

    Write-Host "StatusCode:   $($r3.StatusCode)"
    Write-Host "WWW-Auth:     $($r3.Headers['WWW-Authenticate'])"
    Write-Host "Body:"
    Write-Host $r3.Content.Substring(0, [Math]::Min(800, $r3.Content.Length))

    $success = $r3.Content -match "<Result>success</Result>"
    Write-Host ""
    if ($success) {
        Write-Host ">>> SSO FUNGUJE ✓ — DomainPassthroughAuth vrátil success! <<<"
    } else {
        Write-Host ">>> SSO NEFUNGUJE — Result není success. Viz body výše. <<<"
    }
} catch {
    Write-Host "StatusCode:   $([int]$_.Exception.Response.StatusCode)"
    Write-Host "WWW-Auth:     $($_.Exception.Response.Headers['WWW-Authenticate'])"
    Write-Host "CHYBA: $_"
    Write-Host ""
    Write-Host ">>> SSO NEFUNGUJE — výjimka při volání DomainPassthroughAuth. <<<"
}

# ── 3b. Stejný test BEZ UseDefaultCredentials (simulace app pool) ──
Write-Host $sep
Write-Host "3b. DomainPassthroughAuth BEZ Windows credentials"
Write-Host "    (simuluje co dělá IIS app pool = VXXXX22FISXVI15`$)"
Write-Host $sep

$session2 = New-Object Microsoft.PowerShell.Commands.WebRequestSession
try {
    # Bootstrap jako anonymous (bez UseDefaultCredentials)
    $null = Invoke-WebRequest -Uri $BaseUrl -WebSession $session2 -UseBasicParsing -TimeoutSec 15
    $csrf2 = ($session2.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }).Value

    $h2 = @{ "Accept" = "application/xml, text/xml, */*"; "X-Requested-With" = "XMLHttpRequest"; "X-Citrix-IsUsingHTTPS" = "Yes"; "Referer" = $BaseUrl }
    if ($csrf2) { $h2["Csrf-Token"] = $csrf2 }

    $null = Invoke-WebRequest -Uri "${BaseUrl}Authentication/GetAuthMethods" -Method POST -WebSession $session2 -Headers $h2 -ContentType "application/x-www-form-urlencoded; charset=UTF-8" -Body "" -UseBasicParsing -TimeoutSec 15
    $csrf2 = ($session2.Cookies.GetCookies($BaseUrl) | Where-Object { $_.Name -eq "CsrfToken" }).Value
    if ($csrf2) { $h2["Csrf-Token"] = $csrf2 }

    $r3b = Invoke-WebRequest -Uri "${BaseUrl}DomainPassthroughAuth/Login" -Method POST -WebSession $session2 -Headers $h2 -ContentType "application/x-www-form-urlencoded; charset=UTF-8" -Body "" -UseBasicParsing -TimeoutSec 15
    Write-Host "StatusCode:   $($r3b.StatusCode)"
    Write-Host "WWW-Auth:     $($r3b.Headers['WWW-Authenticate'])"
    Write-Host "Body: $($r3b.Content.Substring(0, [Math]::Min(400, $r3b.Content.Length)))"
    if ($r3b.Content -match "<Result>success</Result>") {
        Write-Host ">>> App pool SSO by fungoval i bez delegace! <<<"
    } else {
        Write-Host ">>> App pool potřebuje delegaci (očekáváno). <<<"
    }
} catch {
    $code = if ($_.Exception.Response) { [int]$_.Exception.Response.StatusCode } else { "N/A" }
    $wwwAuth = if ($_.Exception.Response) { $_.Exception.Response.Headers['WWW-Authenticate'] } else { "" }
    Write-Host "StatusCode:   $code"
    Write-Host "WWW-Auth:     $wwwAuth"
    Write-Host ">>> App pool potřebuje delegaci — bez credentials selže ($code). <<<"
}

# ── 4. Resources/List (jen pokud krok 3 prošel) ────────────────
Write-Host $sep
Write-Host "4. Resources/List — ověření že máme platnou session"
Write-Host $sep

try {
    $headers4 = @{
        "Accept"               = "application/json, text/javascript, */*"
        "X-Requested-With"     = "XMLHttpRequest"
        "X-Citrix-IsUsingHTTPS" = "Yes"
        "Referer"              = $BaseUrl
    }
    if ($csrf) { $headers4["Csrf-Token"] = $csrf }

    $r4 = Invoke-WebRequest -Uri "${BaseUrl}Resources/List" `
        -Method POST `
        -WebSession $session `
        -UseDefaultCredentials `
        -Headers $headers4 `
        -ContentType "application/x-www-form-urlencoded; charset=UTF-8" `
        -Body "format=json&resourceDetails=Default" `
        -UseBasicParsing `
        -TimeoutSec 15

    Write-Host "StatusCode:   $($r4.StatusCode)"

    try {
        $apps = ($r4.Content | ConvertFrom-Json).resources
        Write-Host "Počet aplikací: $($apps.Count)"
        $apps | ForEach-Object { Write-Host "  - $($_.name)" }
    } catch {
        Write-Host "Body preview: $($r4.Content.Substring(0, [Math]::Min(400, $r4.Content.Length)))"
    }
} catch {
    Write-Host "StatusCode:   $([int]$_.Exception.Response.StatusCode)"
    Write-Host "CHYBA: $_"
}

Write-Host $sep
Write-Host "HOTOVO — zkopíruj celý výstup"
Write-Host $sep
