# CitrixComponent — SSO diagnostika
# Spustit jako ACR\VanD na serveru VXXXX22FISXVI15
# Výsledky zkopírovat a poslat zpět

$separator = "`n" + ("=" * 60) + "`n"

# ── 1. Kdo jsem ────────────────────────────────────────────────
Write-Host $separator
Write-Host "1. AKTUÁLNÍ UŽIVATEL"
Write-Host $separator
whoami
[System.Security.Principal.WindowsIdentity]::GetCurrent().Name

# ── 2. AD Trust — ACR strana ───────────────────────────────────
Write-Host $separator
Write-Host "2. AD TRUST — ACR strana (SIDFilteringQuarantined)"
Write-Host $separator
try {
    Get-ADTrust "fis.acr" -Server acr -Properties SIDFilteringQuarantined,TrustAttributes,TGTDelegation |
        Select-Object Name, SIDFilteringQuarantined, TrustAttributes, TGTDelegation |
        Format-List
} catch {
    Write-Host "CHYBA: $_"
}

# ── 3. Kerberos — staré endpointy ──────────────────────────────
Write-Host $separator
Write-Host "3. KERBEROS TICKET — citrixgw01 (stará konfigurace)"
Write-Host $separator
klist purge | Out-Null
Write-Host "--- HTTP/citrixgw01.fis.acr ---"
klist get HTTP/citrixgw01.fis.acr 2>&1
Write-Host "--- HTTP/vxxxx22fisxvi15.fis.acr ---"
klist get HTTP/vxxxx22fisxvi15.fis.acr 2>&1

# ── 4. DNS + konektivita — pnagent ─────────────────────────────
Write-Host $separator
Write-Host "4. DNS + KONEKTIVITA — pnagent.fis.acr"
Write-Host $separator
Write-Host "--- Resolve-DnsName ---"
Resolve-DnsName pnagent.fis.acr 2>&1
Write-Host "--- Test-NetConnection port 443 ---"
Test-NetConnection pnagent.fis.acr -Port 443 2>&1
Write-Host "--- Test-NetConnection port 80 ---"
Test-NetConnection pnagent.fis.acr -Port 80 2>&1

# ── 5. SPN — pnagent ───────────────────────────────────────────
Write-Host $separator
Write-Host "5. SPN — pnagent.fis.acr"
Write-Host $separator
setspn -Q HTTP/pnagent.fis.acr 2>&1
setspn -Q HTTP/pnagent 2>&1

# ── 6. Kerberos ticket — pnagent ───────────────────────────────
Write-Host $separator
Write-Host "6. KERBEROS TICKET — pnagent.fis.acr"
Write-Host $separator
klist purge | Out-Null
klist get HTTP/pnagent.fis.acr 2>&1
Write-Host "--- Aktuální tickety v cache ---"
klist 2>&1

# ── 7. HTTP test — pnagent StoreFront ──────────────────────────
Write-Host $separator
Write-Host "7. HTTP TEST — pnagent.fis.acr/Citrix/FISWeb/"
Write-Host $separator
try {
    $r = Invoke-WebRequest -Uri "https://pnagent.fis.acr/Citrix/FISWeb/" `
        -UseDefaultCredentials -UseBasicParsing -TimeoutSec 10
    Write-Host "StatusCode: $($r.StatusCode)"
    Write-Host "ContentType: $($r.Headers['Content-Type'])"
    Write-Host "Body (prvních 500 znaků):"
    Write-Host $r.Content.Substring(0, [Math]::Min(500, $r.Content.Length))
} catch {
    Write-Host "CHYBA: $_"
}

# ── 8. HTTP test — alternativní cesty ──────────────────────────
Write-Host $separator
Write-Host "8. HTTP TEST — alternativní cesty"
Write-Host $separator
foreach ($url in @(
    "https://pnagent.fis.acr/Citrix/FIS/",
    "https://pnagent.fis.acr/",
    "https://citrixgw01.fis.acr/Citrix/FISWeb/"
)) {
    try {
        $r = Invoke-WebRequest -Uri $url -UseDefaultCredentials -UseBasicParsing -TimeoutSec 10
        Write-Host "$url → $($r.StatusCode)"
    } catch {
        Write-Host "$url → CHYBA: $($_.Exception.Message)"
    }
}

Write-Host $separator
Write-Host "HOTOVO — zkopíruj celý výstup"
Write-Host $separator
