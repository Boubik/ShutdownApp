#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
$packageProject = Join-Path `
    $root `
    'packaging\idle-shutdown\IdleShutdown.Package.csproj'
$packagePath = Join-Path $root 'dist\package\idle-shutdown.nupkg'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw '.NET SDK není nainstalované nebo příkaz dotnet není v PATH.'
}

dotnet pack $packageProject -c Release --disable-build-servers

if ($LASTEXITCODE -ne 0) {
    throw "dotnet pack skončil s kódem $LASTEXITCODE."
}

if (-not (Test-Path $packagePath)) {
    throw "Chybí očekávaný balíček: $packagePath"
}

$version = (Get-Content (Join-Path $root 'VERSION') -Raw).Trim()
Write-Host "[OK] Chocolatey balíček verze $version byl vytvořen:"
Write-Host "     $packagePath"
