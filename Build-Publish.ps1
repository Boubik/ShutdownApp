#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root 'dist'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Není nainstalované .NET SDK. Nainstalujte .NET 8 SDK.'
}

Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Dist -ItemType Directory -Force | Out-Null

Write-Host '[INFO] Publikuji Windows service...'
dotnet publish (Join-Path $Root 'src\IdleShutdown.Service\IdleShutdown.Service.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $Dist 'Service')

Write-Host '[INFO] Publikuji uživatelského agenta...'
dotnet publish (Join-Path $Root 'src\IdleShutdown.Agent\IdleShutdown.Agent.csproj') `
    -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -o (Join-Path $Dist 'Agent')

Copy-Item (Join-Path $Root 'config.test.json') (Join-Path $Dist 'config.json') -Force
dotnet pack (Join-Path $Root 'packaging\idle-shutdown\IdleShutdown.Package.csproj') `
    -c Release --disable-build-servers
Write-Host "[OK] Výstup: $Dist"
