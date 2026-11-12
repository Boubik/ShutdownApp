#requires -Version 5.1
# Version-aware migration and safely tested atomic configuration writer.
function Merge-IdleShutdownConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [psobject] $Defaults,
        [AllowNull()] [psobject] $Existing,
        [Parameter(Mandatory = $true)] [string] $TargetVersion,
        [switch] $ResetConfig
    )
    if ($TargetVersion -notmatch '^[0-9]+[.][0-9]+[.][0-9]+$') {
        throw "Invalid stable application version '$TargetVersion'."
    }
    if ($Defaults.GetType().FullName -ne 'System.Management.Automation.PSCustomObject') {
        throw 'Packaged defaults must be a JSON object.'
    }
    $defaultVersion = $Defaults.PSObject.Properties['ConfigVersion']
    if ($null -eq $defaultVersion -or [string]$defaultVersion.Value -ne $TargetVersion) {
        throw "Packaged ConfigVersion must match application version '$TargetVersion'."
    }
    $names = @(
        'IdleMinutes', 'WarningSeconds', 'CheckIntervalSeconds',
        'PauseWhenFullscreen', 'DryRun',
        'MaintenanceEnabled', 'WindowsUpdateEnabled', 'DriverUpdatesEnabled',
        'OptionalDriversEnabled', 'ChocolateyEnabled', 'ChocolateySource',
        'MaintenanceMaxMinutes', 'SelfUpdateEnabled'
    )
    $config = [ordered]@{ ConfigVersion = $TargetVersion }
    foreach ($name in $names) {
        $property = $Defaults.PSObject.Properties[$name]
        if ($null -eq $property) {
            throw "Packaged default configuration is missing '$name'."
        }
        $config[$name] = $property.Value
    }
    if (-not $ResetConfig -and $null -ne $Existing) {
        if ($Existing.GetType().FullName -ne 'System.Management.Automation.PSCustomObject') {
            throw 'Existing config.json must contain a JSON object.'
        }
        $oldVersionProperty = $Existing.PSObject.Properties['ConfigVersion']
        if ($null -ne $oldVersionProperty) {
            $oldVersion = [string]$oldVersionProperty.Value
            if ($oldVersion -notmatch '^[0-9]+[.][0-9]+[.][0-9]+(?:-[0-9A-Za-z.-]+)?$') {
                throw "Existing ConfigVersion '$oldVersion' is invalid."
            }
            $oldBaseVersion = $oldVersion.Split('-')[0]
            if ([version]$oldBaseVersion -gt [version]$TargetVersion) {
                throw "Refusing to downgrade newer config '$oldVersion' to '$TargetVersion'."
            }
        }
        # Preserve all explicitly configured, recognized properties.
        # For versionless 1.3.x and 2.0.0/2.0.1, add missing 2.x defaults.
        foreach ($name in $names) {
            $property = $Existing.PSObject.Properties[$name]
            if ($null -ne $property) {
                $config[$name] = $property.Value
            }
        }
    }
    return ,$config
}

# Write an already-validated migrated configuration safely. File.Replace
# REQUIRES an actual backup filename on Windows PowerShell/.NET Framework;
# passing $null caused 2.0.2/2.0.3 upgrades to fail during config migration.
# Keep the caller's persistent versioned backup separate from this temporary
# atomic-swap backup, whose contents are no longer needed after a good swap.
function Save-IdleShutdownConfiguration {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)] [System.Collections.IDictionary] $Config,
        [Parameter(Mandatory = $true)] [string] $ConfigPath
    )

    $directory = Split-Path -Parent $ConfigPath
    if (-not (Test-Path -LiteralPath $directory -PathType Container)) {
        throw "Configuration directory does not exist: $directory"
    }
    $random = [guid]::NewGuid().ToString('N')
    $temporaryPath = Join-Path $directory ("config.json.new.$random")
    $swapBackupPath = Join-Path $directory ("config.json.swap.$random.bak")

    try {
        $json = $Config | ConvertTo-Json -Depth 5 -ErrorAction Stop
        [IO.File]::WriteAllText(
            $temporaryPath, $json, [Text.UTF8Encoding]::new($false))

        if ([IO.File]::Exists($ConfigPath)) {
            # Both paths and the backup live on the same NTFS volume.
            [IO.File]::Replace(
                $temporaryPath, $ConfigPath, $swapBackupPath)
        }
        else {
            [IO.File]::Move($temporaryPath, $ConfigPath)
        }
    }
    finally {
        # Leave no .new.* or .swap.* fragments after a normal attempt.
        # Interrupted upgrades retain their separate pre-VERSION backup.
        if (Test-Path -LiteralPath $temporaryPath) {
            Remove-Item -LiteralPath $temporaryPath -Force -ErrorAction SilentlyContinue
        }
        if (Test-Path -LiteralPath $swapBackupPath) {
            Remove-Item -LiteralPath $swapBackupPath -Force -ErrorAction SilentlyContinue
        }
    }
}
