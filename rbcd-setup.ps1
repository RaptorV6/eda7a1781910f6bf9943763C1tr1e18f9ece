# CitrixComponent — RBCD setup + ověření
# Spustit jako Domain Admin v doméně fis.acr
# Nastavuje Resource-Based Constrained Delegation:
#   portálový server VXXXX22FISXVI15$ smí volat pnagent.fis.acr jménem uživatelů

param(
    [string]$PortalServer  = "VXXXX22FISXVI15",
    [string]$PnagentFqdn   = "pnagent.fis.acr",
    [string]$FisDomain     = "fis.acr"
)

$sep = "`n" + ("=" * 60) + "`n"

# ── 1. Zjisti machine accounty za pnagent ──────────────────────
Write-Host $sep
Write-Host "1. DNS — jaké IP adresy jsou za $PnagentFqdn"
Write-Host $sep

$ips = Resolve-DnsName $PnagentFqdn -ErrorAction Stop | Where-Object { $_.IPAddress } | Select-Object -ExpandProperty IPAddress
Write-Host "IP adresy: $($ips -join ', ')"

Write-Host ""
Write-Host "Machine accounty (PTR lookup):"
$citrixMachines = @()
foreach ($ip in $ips) {
    try {
        $ptr = Resolve-DnsName $ip -ErrorAction SilentlyContinue | Select-Object -First 1 -ExpandProperty NameHost
        $name = $ptr -replace "\..*", ""
        Write-Host "  $ip → $ptr → AD name: $name"
        $citrixMachines += $name
    } catch {
        Write-Host "  $ip → PTR lookup selhal"
    }
}

# ── 2. Načti AD objekty ────────────────────────────────────────
Write-Host $sep
Write-Host "2. AD objekty"
Write-Host $sep

try {
    $portal = Get-ADComputer $PortalServer -Server $FisDomain
    Write-Host "Portálový server: $($portal.DistinguishedName)"
} catch {
    Write-Host "CHYBA — portálový server $PortalServer nenalezen v $FisDomain"
    Write-Host $_
    Read-Host "`nStiskni Enter pro zavření"
    return
}

$citrixADObjects = @()
foreach ($machine in $citrixMachines) {
    try {
        $obj = Get-ADComputer $machine -Server $FisDomain
        Write-Host "Citrix server:    $($obj.DistinguishedName)"
        $citrixADObjects += $obj
    } catch {
        Write-Host "WARN — $machine nenalezen v AD (load balancer VIP?): $_"
    }
}

if ($citrixADObjects.Count -eq 0) {
    Write-Host ""
    Write-Host "Žádný citrix machine account nenalezen přes DNS."
    Write-Host "Zkus ručně: Get-ADComputer -Filter 'Name -like ""VXXXX22FISXVA*""' -Server $FisDomain"
    Write-Host "Pak spusť sekci 3 ručně pro každý nalezený stroj."
    Read-Host "`nStiskni Enter pro zavření"
    return
}

# ── 3. Nastav RBCD ─────────────────────────────────────────────
Write-Host $sep
Write-Host "3. Nastavuji RBCD"
Write-Host $sep
Write-Host "Říkám každému Citrix stroji: '$PortalServer smí volat mě jménem uživatelů'"
Write-Host ""

foreach ($citrix in $citrixADObjects) {
    try {
        $current = (Get-ADComputer $citrix.Name -Properties PrincipalsAllowedToDelegateToAccount -Server $FisDomain).PrincipalsAllowedToDelegateToAccount
        Write-Host "Před změnou na $($citrix.Name): $($current -join ', ')"

        Set-ADComputer $citrix.Name `
            -PrincipalsAllowedToDelegateToAccount $portal `
            -Server $FisDomain

        Write-Host "✓ RBCD nastaveno na $($citrix.Name)"
    } catch {
        Write-Host "CHYBA na $($citrix.Name): $_"
    }
}

# ── 4. Ověření ─────────────────────────────────────────────────
Write-Host $sep
Write-Host "4. Ověření — čteme PrincipalsAllowedToDelegateToAccount zpět"
Write-Host $sep

foreach ($citrix in $citrixADObjects) {
    $check = (Get-ADComputer $citrix.Name -Properties PrincipalsAllowedToDelegateToAccount -Server $FisDomain).PrincipalsAllowedToDelegateToAccount
    $ok = $check | Where-Object { $_ -like "*$PortalServer*" }
    if ($ok) {
        Write-Host "✓ $($citrix.Name) — $PortalServer je v PrincipalsAllowedToDelegateToAccount"
    } else {
        Write-Host "✗ $($citrix.Name) — $PortalServer NENÍ v PrincipalsAllowedToDelegateToAccount"
        Write-Host "  Aktuální hodnota: $($check -join ', ')"
    }
}

Write-Host $sep
Write-Host "HOTOVO"
Write-Host "Po nastavení RBCD:"
Write-Host "  1. Počkej 15 minut (AD replikace)"
Write-Host "  2. Spusť znovu sso-test.ps1 — sekce 3b by měla vrátit success"
Write-Host $sep

Read-Host "Stiskni Enter pro zavření"
