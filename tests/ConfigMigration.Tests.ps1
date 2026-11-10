#requires -Version 5.1
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
. (Join-Path $root 'packaging/idle-shutdown/tools/config-migrate.ps1')
$defaults = Get-Content (Join-Path $root 'config.json') -Raw | ConvertFrom-Json
$target = [string]$defaults.ConfigVersion

function Assert-Equal {
    param($Expected, $Actual, [string]$Name)
    if ($Expected -cne $Actual) {
        throw ("FAILED {0}: expected '{1}', got '{2}'" -f $Name, $Expected, $Actual)
    }
}
function Assert-Throws {
    param([scriptblock]$Block, [string]$Name)
    $threw = $false
    try { & $Block | Out-Null }
    catch { $threw = $true }
    if (-not $threw) { throw "FAILED ${Name}: expected rejection" }
}

Assert-Equal $target $defaults.ConfigVersion 'packaged config version'

# 1.3.4: five settings, no version and no update options.
$legacy = @{
    IdleMinutes = 41
    WarningSeconds = 75
    CheckIntervalSeconds = 3
    PauseWhenFullscreen = $false
    DryRun = $true
} | ConvertTo-Json | ConvertFrom-Json
$migrated = Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $legacy -TargetVersion $target
Assert-Equal $target $migrated.ConfigVersion '1.3.4 marker'
Assert-Equal 41 $migrated.IdleMinutes '1.3.4 custom idle'
Assert-Equal 75 $migrated.WarningSeconds '1.3.4 custom warning'
Assert-Equal 3 $migrated.CheckIntervalSeconds '1.3.4 custom poll'
Assert-Equal $false $migrated.PauseWhenFullscreen '1.3.4 fullscreen'
Assert-Equal $true $migrated.DryRun '1.3.4 dry run'
foreach ($key in @('MaintenanceEnabled', 'OptionalDriversEnabled', 'SelfUpdateEnabled')) {
    Assert-Equal $true $migrated[$key] "1.3.4 default: $key"
}
Assert-Equal 120 $migrated.MaintenanceMaxMinutes '1.3.4 new maintenance limit'
Assert-Equal 14 $migrated.Count '1.3.4 migrated property count'

# 2.0.0: explicit administrator settings MUST NOT be silently overridden.
$old2 = $defaults | ConvertTo-Json | ConvertFrom-Json
$old2.PSObject.Properties.Remove('ConfigVersion')
$old2.MaintenanceEnabled = $false
$old2.OptionalDriversEnabled = $false
$old2.SelfUpdateEnabled = $false
$old2.IdleMinutes = 107
$migrated2 = Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $old2 -TargetVersion $target
Assert-Equal 107 $migrated2.IdleMinutes '2.0.0 custom idle'
foreach ($key in @('MaintenanceEnabled', 'OptionalDriversEnabled', 'SelfUpdateEnabled')) {
    Assert-Equal $false $migrated2[$key] "2.0.0 explicit false: $key"
}
Assert-Equal $target $migrated2.ConfigVersion '2.0.0 marker'

# 2.0.1: custom feed, explicit preferences and incomplete field.
$old21 = $defaults | ConvertTo-Json | ConvertFrom-Json
$old21.PSObject.Properties.Remove('ConfigVersion')
$old21.ChocolateySource = 'https://choco.cpto.ujep.cz/nuget/choco-internal/'
$old21.PSObject.Properties.Remove('OptionalDriversEnabled')
$old21.SelfUpdateEnabled = $false
$migrated21 = Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $old21 -TargetVersion $target
Assert-Equal 'https://choco.cpto.ujep.cz/nuget/choco-internal/' $migrated21.ChocolateySource '2.0.1 feed'
Assert-Equal $true $migrated21.OptionalDriversEnabled '2.0.1 missing option'
Assert-Equal $false $migrated21.SelfUpdateEnabled '2.0.1 explicit self-update choice'

# New installations and ResetConfig use package defaults.
$new = Merge-IdleShutdownConfiguration -Defaults $defaults -TargetVersion $target
Assert-Equal $target $new.ConfigVersion 'new install marker'
Assert-Equal 90 $new.IdleMinutes 'new install default'
$reset = Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $old2 -TargetVersion $target -ResetConfig
Assert-Equal $true $reset.MaintenanceEnabled 'explicit full config reset'
Assert-Equal 90 $reset.IdleMinutes 'full config reset idle'

# Re-running the migration is idempotent.
$again = Merge-IdleShutdownConfiguration -Defaults $defaults -Existing ($migrated | ConvertTo-Json | ConvertFrom-Json) -TargetVersion $target
Assert-Equal ($migrated | ConvertTo-Json -Compress) ($again | ConvertTo-Json -Compress) 'idempotence'

# Reject malformed/future configuration or mismatched package metadata.
$future = $defaults | ConvertTo-Json | ConvertFrom-Json
$future.ConfigVersion = '999.0.0'
Assert-Throws { Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $future -TargetVersion $target } 'future config'
$invalid = $defaults | ConvertTo-Json | ConvertFrom-Json
$invalid.ConfigVersion = 'unknown'
Assert-Throws { Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $invalid -TargetVersion $target } 'invalid version'
Assert-Throws { Merge-IdleShutdownConfiguration -Defaults $defaults -Existing @('bad') -TargetVersion $target } 'invalid JSON root'
Assert-Throws { Merge-IdleShutdownConfiguration -Defaults $defaults -Existing $legacy -TargetVersion '999.999.999' } 'default/package mismatch'
$partial = $defaults | ConvertTo-Json | ConvertFrom-Json
$partial.PSObject.Properties.Remove('SelfUpdateEnabled')
Assert-Throws { Merge-IdleShutdownConfiguration -Defaults $partial -Existing $legacy -TargetVersion $target } 'missing default'

Write-Host 'PASS: config migration from 1.3.4, 2.0.0, and 2.0.1; defaults, opt-outs, downgrade protection, idempotence.'
