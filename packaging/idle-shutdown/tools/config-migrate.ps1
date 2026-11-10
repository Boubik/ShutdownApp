#requires -Version 5.1
# Pure migration function. No files, services, or network operations.
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
