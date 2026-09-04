#Requires -Version 5.1
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Start-Process powershell.exe -Verb RunAs -ArgumentList "-NoProfile -ExecutionPolicy Bypass -File `"$PSCommandPath`""
    exit 0
}

Stop-Service PosPrintAgent -Force -ErrorAction SilentlyContinue
sc.exe delete PosPrintAgent | Out-Null
Write-Host 'POS Print Agent desinstalado.' -ForegroundColor Green
