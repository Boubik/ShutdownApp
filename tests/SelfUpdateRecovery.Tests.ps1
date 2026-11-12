#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'packaging/idle-shutdown/tools/self-update.ps1'
$tokens = $null
$errorsFound = $null
$ast = [Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$errorsFound)
if ($errorsFound.Count -gt 0) {
    throw ('Self-update helper syntax errors: ' +
        (($errorsFound | ForEach-Object Message) -join ', '))
}
$functions = $ast.FindAll({
    param($node)
    $node -is [Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-UpgradeInconsistencies'
}, $true)
foreach ($fn in $functions) {
    . ([scriptblock]::Create($fn.Extent.Text))
}
if ($functions.Count -ne 1) { throw 'Recovery policy function missing.' }

# Substitute environment-dependent probes and exercise the production policy.
$script:agent = '2.0.1'
$script:service = '2.0.1'
$script:taskHelper = '2.0.0'
function Get-DeployedBinaryVersion {
    param([string]$Name)
    if ($Name -eq 'IdleShutdown.Agent.exe') { return $script:agent }
    return $script:service
}
function Get-SelfUpdateTaskVersion { return $script:taskHelper }
function Assert-Reasons {
    param([string]$Expected, [string]$ConfigVersion, [string[]]$ExpectedReasons)
    $config = [pscustomobject]@{ ConfigVersion = $ConfigVersion }
    $actual = @(Get-UpgradeInconsistencies -ExpectedVersion $Expected -CurrentConfig $config)
    foreach ($reason in $ExpectedReasons) {
        if ($reason -notin $actual) {
            throw "Expected missing inconsistency '$reason'; actual: $($actual -join ', ')"
        }
    }
    if ($actual.Count -ne $ExpectedReasons.Count) {
        throw "Unexpected inconsistency count for $($Expected): $($actual -join ', ')"
    }
}
# Reproduces real-world field report: choco 2.0.2, binaries 2.0.1,
# no config migration, task using 2.0.0 helper.
Assert-Reasons '2.0.2' '' @('agent=2.0.1','service=2.0.1','configVersion=','taskHelper=2.0.0')

# Legacy 2.0.1 genuinely lacked ConfigVersion and used task named 2.0.0.
Assert-Reasons '2.0.1' '' @()

# Fully installed stable release must not enter endless repair loop.
$script:agent = '2.0.3'; $script:service = '2.0.3'; $script:taskHelper = '2.0.3'
Assert-Reasons '2.0.3' '2.0.3' @()

# Each mismatch alone must trigger a repair.
$script:agent = '2.0.2'
Assert-Reasons '2.0.3' '2.0.3' @('agent=2.0.2')
$script:agent = '2.0.3'
$script:taskHelper = '2.0.0'
Assert-Reasons '2.0.3' '2.0.3' @('taskHelper=2.0.0')
$script:taskHelper = '2.0.3'
Assert-Reasons '2.0.3' '2.0.2' @('configVersion=2.0.2')

# Automatic updates remain stable-only.
$content = Get-Content $scriptPath -Raw
if ($content.Contains('SelfUpdateAllowPrerelease') -or
    $content.Contains('$upgradeArgs += ''--pre''') -or
    $content.Contains('$queryArgs += ''--pre''')) {
    throw 'Automatic updater must NEVER install prerelease packages.'
}
foreach ($phrase in @("'--force'",'Get-UpgradeInconsistencies',
                      'Get-DeployedBinaryVersion','Test-RebootPending',
                      'VERIFIED complete','INCOMPLETE previous installation')) {
    if (-not $content.Contains($phrase)) {
        throw "Missing recovery safeguard: $phrase"
    }
}
Write-Host 'PASS: interrupted 2.0.1->2.0.2 recovery, complete installation, mismatches, legacy configs and stable-only policy.'
