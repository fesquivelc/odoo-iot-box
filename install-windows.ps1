#Requires -Version 5.1
[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\POS Print Agent",
    [int]$Port = 18181
)

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    $args = @('-NoProfile', '-ExecutionPolicy', 'Bypass', '-File', "`"$PSCommandPath`"",
        '-InstallDir', "`"$InstallDir`"", '-Port', $Port)
    Start-Process powershell.exe -Verb RunAs -ArgumentList ($args -join ' ')
    exit 0
}

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSCommandPath
$project = Join-Path $root 'src\PosPrintAgent\PosPrintAgent.csproj'
$publishDir = Join-Path $root '.publish\win-x64'
$serviceName = 'PosPrintAgent'

Write-Host 'Publicando POS Print Agent para Windows x64...'
dotnet publish $project -c Release -r win-x64 --self-contained true -o $publishDir

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item (Join-Path $publishDir '*') $InstallDir -Recurse -Force

$exe = Join-Path $InstallDir 'PosPrintAgent.exe'
$old = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($old) {
    Stop-Service $serviceName -Force -ErrorAction SilentlyContinue
    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

New-Service -Name $serviceName -DisplayName 'POS Print Agent' -Description 'Agente local de impresión para Odoo POS' `
    -BinaryPathName "`"$exe`" --urls http://127.0.0.1:$Port" -StartupType Automatic | Out-Null
Start-Service $serviceName

Write-Host ''
Write-Host 'Servicio instalado y arrancado.' -ForegroundColor Green
Write-Host "Configuración web: http://127.0.0.1:$Port"
Write-Host "Directorio: $InstallDir"
Write-Host 'Nota: el agente está limitado a localhost y no requiere una regla de firewall.'
