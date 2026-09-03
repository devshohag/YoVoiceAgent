$ErrorActionPreference = "Stop"

$projectRoot = Split-Path -Parent $PSScriptRoot
$pjsipPath = Join-Path $projectRoot "infra\asterisk\conf\pjsip.conf"

if (-not (Test-Path $pjsipPath)) {
    throw "pjsip.conf was not found at $pjsipPath"
}

$defaultRoute = Get-NetRoute -DestinationPrefix "0.0.0.0/0" -AddressFamily IPv4 |
    Where-Object { $_.NextHop -ne "0.0.0.0" } |
    Sort-Object RouteMetric, InterfaceMetric |
    Select-Object -First 1

if (-not $defaultRoute) {
    throw "No active IPv4 default route was found. Connect Windows to your network and retry."
}

$hostIp = Get-NetIPAddress -InterfaceIndex $defaultRoute.InterfaceIndex -AddressFamily IPv4 |
    Where-Object { $_.IPAddress -notlike "169.254.*" -and $_.IPAddress -ne "127.0.0.1" } |
    Select-Object -ExpandProperty IPAddress -First 1

if (-not $hostIp) {
    throw "No usable Windows LAN IPv4 address was found."
}

$backupPath = "$pjsipPath.before-local-network"
if (-not (Test-Path $backupPath)) {
    Copy-Item $pjsipPath $backupPath
}

$content = Get-Content $pjsipPath -Raw
$content = $content `
    -replace '(?m)^local_net=.*\r?\n?', '' `
    -replace '(?m)^external_signaling_address=.*\r?\n?', '' `
    -replace '(?m)^external_signaling_port=.*\r?\n?', '' `
    -replace '(?m)^external_media_address=.*\r?\n?', ''

$networkLines = "`$1`r`nexternal_signaling_address=$hostIp`r`nexternal_signaling_port=5060`r`nexternal_media_address=$hostIp"
$content = $content -replace '(?m)^(bind=0\.0\.0\.0:5060)\s*$', $networkLines
Set-Content -Path $pjsipPath -Value $content -Encoding utf8

Write-Host "Asterisk SIP and RTP address configured as $hostIp" -ForegroundColor Green
Write-Host "Set MicroSIP SIP Server and Domain to $hostIp (TCP), then recreate Asterisk." -ForegroundColor Cyan
Write-Host "docker compose up -d --build --force-recreate asterisk telephony-worker" -ForegroundColor Cyan
