#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'packaging/idle-shutdown/tools/self-update.ps1'
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw "Self-update helper has PowerShell syntax errors: $($parseErrors.Message -join ', ')"
}
$required = @('Test-WithinStartupUpgradeWindow', 'Test-SessionSafeForUpdate')
foreach ($functionName in $required) {
    $definitions = @($ast.FindAll({
        param($node)
        $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
            $node.Name -eq $functionName
    }, $true))
    if ($definitions.Count -ne 1) { throw "Expected one definition of $functionName" }
    . ([scriptblock]::Create($definitions[0].Extent.Text))
}

function Assert-Equal {
    param([bool]$Expected, [bool]$Actual, [string]$Case)
    if ($Expected -ne $Actual) {
        throw "Failed $($Case): expected $Expected, got $Actual"
    }
}

$boot = [datetime]'2026-11-12T11:00:00'
Assert-Equal $true (Test-WithinStartupUpgradeWindow $boot $boot) 'exact boot'
Assert-Equal $true (Test-WithinStartupUpgradeWindow $boot $boot.AddMinutes(2)) 'early interactive RDP'
Assert-Equal $true (Test-WithinStartupUpgradeWindow $boot $boot.AddMinutes(20)) 'end of post-boot window'
Assert-Equal $false (Test-WithinStartupUpgradeWindow $boot $boot.AddMinutes(20).AddSeconds(1)) 'outside window'
Assert-Equal $false (Test-WithinStartupUpgradeWindow $boot $boot.AddHours(-1)) 'clock skew'
Assert-Equal $false (Test-WithinStartupUpgradeWindow $boot $boot 0) 'disabled window'
Assert-Equal $false (Test-WithinStartupUpgradeWindow $boot $boot.AddMinutes(2) -1) 'invalid window'

# An actively logged-in RDP session is explicitly permitted only inside the
# post-boot window, while the daily unattended check must still defer.
function Test-NoLoggedOnUsers { return $false }
Assert-Equal $true (Test-SessionSafeForUpdate -BootStartupWindow $true) 'boot permits logged-on user'
Assert-Equal $false (Test-SessionSafeForUpdate -BootStartupWindow $false) 'daily rejects logged-on user'

# Also fail closed if the WTS probe says no users but explorer is still alive.
function Test-NoLoggedOnUsers { return $true }
function Get-Process {
    param([string]$Name, [string]$ErrorAction)
    return [pscustomobject]@{ Id = 1234; ProcessName = 'explorer' }
}
Assert-Equal $false (Test-SessionSafeForUpdate -BootStartupWindow $false) 'daily defers when explorer exists'
function Get-Process {
    param([string]$Name, [string]$ErrorAction)
    return
}
Assert-Equal $true (Test-SessionSafeForUpdate -BootStartupWindow $false) 'daily permits unattended machine'

$content = Get-Content $scriptPath -Raw
foreach ($requiredText in @(
    '$bootStartupWindow = Test-BootStartupUpgradeWindow',
    '$stillInBootWindow = $bootStartupWindow -and (Test-BootStartupUpgradeWindow)',
    'Test-SessionSafeForUpdate -BootStartupWindow $stillInBootWindow',
    'Test-RebootPending',
    '$maintenanceLockPath',
    '$selfLockPath',
    'Skipped non-stable upgrade target',
    '--version=$targetVersion'
)) {
    if (-not $content.Contains($requiredText)) {
        throw "Post-boot auto-upgrade missing safety guard: $requiredText"
    }
}
if ($content.Contains('$upgradeArgs += ''--pre''') -or
    $content.Contains('$queryArgs += ''--pre''') -or
    $content.Contains('SelfUpdateAllowPrerelease')) {
    throw 'Automatic self-updates must remain STABLE-ONLY.'
}
Write-Host 'PASS: boot auto-upgrade permits active RDP without relaxing daily user checks, maintenance locks or stable-only policy.'
