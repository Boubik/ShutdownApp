#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$scriptPath = Join-Path $root 'packaging/idle-shutdown/tools/self-update.ps1'
$tokens = $null
$parseErrors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile(
    $scriptPath, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count -gt 0) {
    throw 'Self-update helper PowerShell syntax error: ' +
        (($parseErrors | ForEach-Object Message) -join '; ')
}
$functions = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
        $node.Name -eq 'Get-IdleShutdownOutdatedResult'
}, $true))
if ($functions.Count -ne 1) {
    throw 'The production outdated results parser is missing.'
}
. ([scriptblock]::Create($functions[0].Extent.Text))

function Assert-Equal {
    param($Expected, $Actual, [string]$Context)
    if ([string]$Expected -cne [string]$Actual) {
        throw "$($Context): expected '$Expected', got '$Actual'"
    }
}
# Real output recorded by Windows Chocolatey 2.7.5 against ProGet:
$rows = @(
    'other-package|1.0.0|1.2.0|false',
    'idle-shutdown|2.0.6|2.0.7|false',
    'one-more|3.1.0|3.2.0|false'
)
$found = Get-IdleShutdownOutdatedResult -Lines $rows
Assert-Equal '2.0.6' $found.Current 'current version'
Assert-Equal '2.0.7' $found.Available 'available version'

$none = Get-IdleShutdownOutdatedResult -Lines @('other-package|1.0|2.0|false')
if ($null -ne $none) { throw 'Wrong package matched the outdated filter.' }
if ($null -ne (Get-IdleShutdownOutdatedResult -Lines @())) {
    throw 'Empty Chocolatey output must not generate an update.'
}
$spaces = Get-IdleShutdownOutdatedResult -Lines @('idle-shutdown| 2.0.6 | 2.0.7 |false')
Assert-Equal '2.0.7' $spaces.Available 'trimmed fields'

$content = Get-Content -LiteralPath $scriptPath -Raw
$expectedArgs = @('outdated', '--limit-output',
                  '--ignore-http-cache', '--ignore-unfound')
$actual = @($ast.FindAll({
    param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
        $node.Left.Extent.Text -eq '$queryArgs' -and
        $node.Operator -eq [System.Management.Automation.Language.TokenKind]::Equals
}, $true))
if ($actual.Count -ne 1) {
    throw 'Expected exactly one Chocolatey outdated argument assignment.'
}
foreach ($required in $expectedArgs) {
    if (-not $actual[0].Extent.Text.Contains("'$required'")) {
        throw "Chocolatey outdated is missing flag $required"
    }
}
if ($actual[0].Extent.Text.Contains("'idle-shutdown'")) {
    throw 'choco outdated does NOT accept a package name positional argument.'
}
if (-not $content.Contains('if ($source) { $queryArgs += @(''--source'', $source) }')) {
    throw 'Custom ChocolateySource must be honored.'
}
if ($content.Contains('$queryArgs += ''--pre''') -or
    $content.Contains('$upgradeArgs += ''--pre''')) {
    throw 'Automatic updater must never install prereleases.'
}
Write-Host 'PASS: Chocolatey outdated options, fresh ProGet metadata, package selection and stable-only policy.'
