#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root 'dist'
$LogicTests = Join-Path $Root 'tests\IdleShutdown.LogicTests\IdleShutdown.LogicTests.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Není nainstalované .NET SDK. Nainstalujte .NET 8 SDK.'
}

Remove-Item $Dist -Recurse -Force -ErrorAction SilentlyContinue
New-Item $Dist -ItemType Directory -Force | Out-Null

Write-Host '[INFO] Spouštím testy časování a resetů aktivity...'
dotnet run --project $LogicTests -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Logic tests failed with exit code $LASTEXITCODE."
}

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

Remove-Item (Join-Path $Dist 'config.json') -Force -ErrorAction SilentlyContinue
dotnet pack (Join-Path $Root 'packaging\idle-shutdown\IdleShutdown.Package.csproj') `
    -c Release --disable-build-servers
Write-Host "[OK] Výstup: $Dist"
