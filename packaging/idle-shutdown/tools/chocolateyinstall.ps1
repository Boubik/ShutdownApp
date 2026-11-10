#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$payloadDir = Join-Path $toolsDir 'dist'
$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$configPath = Join-Path $dataDir 'config.json'
$defaultConfigPath = Join-Path $payloadDir 'config.json'
$logPath = Join-Path $dataDir 'IdleShutdown.log'
$serviceName = 'IdleShutdown'
$taskName = 'Idle Shutdown Agent'
$parameters = Get-PackageParameters

function Set-SoftwareInventoryEntry {
    param(
        [string] $InstalledAgentExe
    )

    $packageVersion = [string] $env:ChocolateyPackageVersion
    if ([string]::IsNullOrWhiteSpace($packageVersion)) {
        $packageVersion = (Get-Item $InstalledAgentExe).VersionInfo.FileVersion
        $packageVersion = $packageVersion -replace '\.0$', ''
    }

    $chocoExe = Join-Path $env:ChocolateyInstall 'bin\choco.exe'
    $uninstallCommand = "`"$chocoExe`" uninstall idle-shutdown -y"
    $estimatedSizeBytes = (
        Get-ChildItem $installDir -File -Recurse |
            Measure-Object -Property Length -Sum
    ).Sum
    $estimatedSizeKb = [Math]::Max(
        1,
        [int][Math]::Ceiling($estimatedSizeBytes / 1KB))

    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)

    try {
        $inventoryKey = $baseKey.CreateSubKey(
            'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown')

        try {
            $inventoryKey.SetValue('DisplayName', 'Idle Shutdown', [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('DisplayVersion', $packageVersion, [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('Publisher', 'Boubik', [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('InstallDate', (Get-Date -Format 'yyyyMMdd'), [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('InstallLocation', $installDir, [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('DisplayIcon', "`"$InstalledAgentExe`",0", [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('UninstallString', $uninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('QuietUninstallString', $uninstallCommand, [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('URLInfoAbout', 'https://github.com/boubik/shutdownapp', [Microsoft.Win32.RegistryValueKind]::String)
            $inventoryKey.SetValue('EstimatedSize', $estimatedSizeKb, [Microsoft.Win32.RegistryValueKind]::DWord)
            $inventoryKey.SetValue('NoModify', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
            $inventoryKey.SetValue('NoRepair', 1, [Microsoft.Win32.RegistryValueKind]::DWord)
        }
        finally {
            $inventoryKey.Dispose()
        }
    }
    finally {
        $baseKey.Dispose()
    }
}

function Get-PositiveIntegerParameter {
    param(
        [string] $Name,
        [int] $CurrentValue
    )

    $value = if ($parameters.ContainsKey($Name)) {
        [string] $parameters[$Name]
    }
    else {
        [string] $CurrentValue
    }
    $parsed = 0
    if (-not [int]::TryParse($value, [ref] $parsed) -or $parsed -lt 1) {
        throw "Package parameter /$Name must be a positive integer."
    }

    return $parsed
}

function Get-BooleanParameter {
    param(
        [string] $Name,
        [bool] $CurrentValue
    )

    $value = if ($parameters.ContainsKey($Name)) {
        [string] $parameters[$Name]
    }
    else {
        [string] $CurrentValue
    }
    $parsed = $false
    if (-not [bool]::TryParse($value, [ref] $parsed)) {
        throw "Package parameter /$Name must be true or false."
    }

    return $parsed
}


function Get-InstalledAgentProcesses {
    $agentPath = Join-Path $installDir 'IdleShutdown.Agent.exe'
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'IdleShutdown.Agent.exe'" -ErrorAction Stop)
    foreach ($process in $processes) {
        if (-not $process.ExecutablePath) { continue }
        if ([string]::Equals(
                [IO.Path]::GetFullPath([string]$process.ExecutablePath),
                [IO.Path]::GetFullPath($agentPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            $process
        }
    }
}

function Stop-InstalledAgents {
    # The scheduled task must be removed before this function is called.
    for ($attempt = 1; $attempt -le 15; $attempt++) {
        $agents = @(Get-InstalledAgentProcesses)
        if ($agents.Count -eq 0) { return }
        foreach ($agent in $agents) {
            Write-Host ("Stopping IdleShutdown.Agent.exe PID={0}, session={1}" -f $agent.ProcessId, $agent.SessionId)
            Stop-Process -Id $agent.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 600
    }
    $remaining = @(Get-InstalledAgentProcesses)
    if ($remaining.Count -gt 0) {
        throw ("Cannot replace IdleShutdown.Agent.exe: {0} process(es) remain running." -f $remaining.Count)
    }
}

function Assert-NoActiveIdleMaintenance {
    # Compatibility check for old betas which do not hold maintenance.lock.
    # Their Windows Update and Chocolatey processes are direct service children.
    $services = @(Get-CimInstance Win32_Process -Filter "Name = 'IdleShutdown.Service.exe'" -ErrorAction Stop)
    if ($services.Count -eq 0) { return }
    $ids = @($services | ForEach-Object { [int]$_.ProcessId })
    $children = @(Get-CimInstance Win32_Process -ErrorAction Stop |
        Where-Object {
            $ids -contains [int]$_.ParentProcessId -and
            $_.Name -in @('powershell.exe', 'pwsh.exe', 'choco.exe')
        })
    if ($children.Count -gt 0) {
        $details = ($children | ForEach-Object { "$($_.Name) PID=$($_.ProcessId)" }) -join ', '
        throw ("IdleShutdown maintenance is still running: $details. Upgrade postponed to protect running installers.")
    }
}

function New-IdleUpdateGuard {
    # On current versions, service owns the same file exclusively during
    # maintenance. Keep this handle open until the install finishes.
    New-Item $dataDir -ItemType Directory -Force | Out-Null
    $guardPath = Join-Path $dataDir 'maintenance.lock'
    try {
        return [System.IO.FileStream]::new(
            $guardPath,
            [System.IO.FileMode]::OpenOrCreate,
            [System.IO.FileAccess]::ReadWrite,
            [System.IO.FileShare]::None)
    }
    catch [System.IO.IOException] {
        throw ('IdleShutdown is performing maintenance or another installation. ' +
            'Upgrade refused; retry after all updates finish. ' + $_.Exception.Message)
    }
}

$serviceExe = Join-Path $payloadDir 'Service\IdleShutdown.Service.exe'
$agentExe = Join-Path $payloadDir 'Agent\IdleShutdown.Agent.exe'

if (-not (Test-Path $serviceExe) -or -not (Test-Path $agentExe)) {
    throw 'The package does not contain the published service and agent executables.'
}

if (-not (Test-Path $defaultConfigPath)) {
    throw 'The package does not contain its default config.json.'
}

try {
    $defaults = Get-Content $defaultConfigPath -Raw | ConvertFrom-Json
}
catch {
    throw "Packaged default configuration is invalid: $($_.Exception.Message)"
}

$config = [ordered]@{}
foreach ($name in @(
    'IdleMinutes',
    'WarningSeconds',
    'CheckIntervalSeconds',
    'PauseWhenFullscreen',
    'DryRun',
    'MaintenanceEnabled',
    'WindowsUpdateEnabled',
    'DriverUpdatesEnabled',
    'OptionalDriversEnabled',
    'ChocolateyEnabled',
    'ChocolateySource',
    'MaintenanceMaxMinutes'
)) {
    $property = $defaults.PSObject.Properties[$name]
    if ($null -eq $property) {
        throw "Packaged default configuration is missing $name."
    }

    $config[$name] = $property.Value
}

$resetConfig = $parameters.ContainsKey('ResetConfig')
if ((Test-Path $configPath) -and -not $resetConfig) {
    try {
        $existingConfig = Get-Content $configPath -Raw | ConvertFrom-Json
        foreach ($name in @($config.Keys)) {
            $property = $existingConfig.PSObject.Properties[$name]
            if ($null -ne $property) {
                $config[$name] = $property.Value
            }
        }
    }
    catch {
        throw "Existing configuration is invalid: $($_.Exception.Message)"
    }
}

$config.IdleMinutes = Get-PositiveIntegerParameter 'IdleMinutes' $config.IdleMinutes
$config.WarningSeconds = Get-PositiveIntegerParameter 'WarningSeconds' $config.WarningSeconds
$config.CheckIntervalSeconds = Get-PositiveIntegerParameter 'CheckIntervalSeconds' $config.CheckIntervalSeconds
$config.PauseWhenFullscreen = Get-BooleanParameter 'PauseWhenFullscreen' $config.PauseWhenFullscreen
$config.DryRun = Get-BooleanParameter 'DryRun' $config.DryRun
$config.MaintenanceEnabled = Get-BooleanParameter 'MaintenanceEnabled' $config.MaintenanceEnabled
$config.WindowsUpdateEnabled = Get-BooleanParameter 'WindowsUpdateEnabled' $config.WindowsUpdateEnabled
$config.DriverUpdatesEnabled = Get-BooleanParameter 'DriverUpdatesEnabled' $config.DriverUpdatesEnabled
$config.OptionalDriversEnabled = Get-BooleanParameter 'OptionalDriversEnabled' $config.OptionalDriversEnabled
$config.ChocolateyEnabled = Get-BooleanParameter 'ChocolateyEnabled' $config.ChocolateyEnabled
$config.MaintenanceMaxMinutes = Get-PositiveIntegerParameter 'MaintenanceMaxMinutes' $config.MaintenanceMaxMinutes
if ($config.MaintenanceMaxMinutes -gt 1440) { throw 'MaintenanceMaxMinutes must be 1440 or less.' }
if ($parameters.ContainsKey('ChocolateySource')) {
    $source = [string] $parameters['ChocolateySource']
    $config.ChocolateySource = if ([string]::IsNullOrWhiteSpace($source)) { $null } else { $source.Trim() }
}
if (-not [string]::IsNullOrWhiteSpace([string] $config.ChocolateySource)) {
    $uri = $null
    if (-not [Uri]::TryCreate([string] $config.ChocolateySource, [UriKind]::Absolute, [ref] $uri) -or
        $uri.Scheme -notin @('https', 'http')) {
        throw 'ChocolateySource must be a valid HTTP(S) URL.'
    }
}

foreach ($removedParameter in @('LockedMinutes', 'NoUserMinutes')) {
    if ($parameters.ContainsKey($removedParameter)) {
        Write-Warning (
            "/$removedParameter was removed in version 1.3.0 and is ignored. " +
            '/IdleMinutes now controls unlocked, locked and no-user inactivity.')
    }
}

# Stage the complete new payload before stopping the working installation.
# This also prevents a half-upgraded directory if a copy later fails.
$stageDir = Join-Path $env:ProgramFiles ('IdleShutdown.stage.' + [guid]::NewGuid().ToString('N'))
$backupDir = Join-Path $env:ProgramFiles ('IdleShutdown.backup.' + [guid]::NewGuid().ToString('N'))
$hadExistingInstall = Test-Path $installDir
$previousConfig = if (Test-Path $configPath) { Get-Content $configPath -Raw } else { $null }
$previousInventoryVersion = (Get-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown' -ErrorAction SilentlyContinue).DisplayVersion
$previousTaskXml = $null
$oldService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
$wasServiceRunning = $null -ne $oldService -and $oldService.Status -ne 'Stopped'
$guard = $null
$servicesTouched = $false
$swapped = $false

New-Item $stageDir -ItemType Directory -Force | Out-Null
try {
    Copy-Item (Join-Path $payloadDir 'Service\*') $stageDir -Recurse -Force -ErrorAction Stop
    Copy-Item (Join-Path $payloadDir 'Agent\*') $stageDir -Recurse -Force -ErrorAction Stop

    if (-not (Test-Path (Join-Path $stageDir 'IdleShutdown.Service.exe')) -or
        -not (Test-Path (Join-Path $stageDir 'IdleShutdown.Agent.exe'))) {
        throw 'Incomplete staged payload; refusing to stop the installed service.'
    }

    $guard = New-IdleUpdateGuard
    Assert-NoActiveIdleMaintenance

    # Save the previously registered task so we can restore it on failure.
    $oldTask = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $oldTask) {
        $previousTaskXml = Export-ScheduledTask -TaskName $taskName -ErrorAction Stop
    }

    $servicesTouched = $true
    Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
    Stop-InstalledAgents

    $currentService = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $currentService -and $currentService.Status -ne 'Stopped') {
        Stop-Service -Name $serviceName -Force -ErrorAction Stop
        (Get-Service -Name $serviceName).WaitForStatus(
            [System.ServiceProcess.ServiceControllerStatus]::Stopped,
            [TimeSpan]::FromSeconds(30))
    }

    # Swap directories only after the agent and service have really exited.
    if ($hadExistingInstall) {
        Move-Item -LiteralPath $installDir -Destination $backupDir -ErrorAction Stop
    }
    Move-Item -LiteralPath $stageDir -Destination $installDir -ErrorAction Stop
    $swapped = $true

    $config | ConvertTo-Json | Set-Content $configPath -Encoding UTF8


if (-not (Test-Path $logPath)) {
    New-Item $logPath -ItemType File -Force | Out-Null
}

# Allow standard users to write agent events without granting write access to
# the machine-wide configuration file.
$logAcl = Get-Acl $logPath
$usersSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$writeRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $usersSid,
    [System.Security.AccessControl.FileSystemRights]::Write,
    [System.Security.AccessControl.AccessControlType]::Allow)
$logAcl.SetAccessRule($writeRule)
Set-Acl -Path $logPath -AclObject $logAcl

$installedServiceExe = Join-Path $installDir 'IdleShutdown.Service.exe'
$service = Get-Service $serviceName -ErrorAction SilentlyContinue

if ($null -eq $service) {
    sc.exe create $serviceName binPath= "`"$installedServiceExe`"" start= auto DisplayName= "Idle Shutdown" | Out-Null
}
else {
    sc.exe config $serviceName binPath= "`"$installedServiceExe`"" start= auto DisplayName= "Idle Shutdown" | Out-Null
}

sc.exe description $serviceName "Automatically shuts down the computer after a configurable period of machine inactivity." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

$installedAgentExe = Join-Path $installDir 'IdleShutdown.Agent.exe'
$agentExeXml = [System.Security.SecurityElement]::Escape($installedAgentExe)
$taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Interactive agent for automatic shutdown after inactivity.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT10S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Users"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <StartWhenAvailable>true</StartWhenAvailable>
    <Enabled>true</Enabled><Hidden>true</Hidden>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <RestartOnFailure><Interval>PT1M</Interval><Count>10</Count></RestartOnFailure>
  </Settings>
  <Actions Context="Users"><Exec><Command>$agentExeXml</Command></Exec></Actions>
</Task>
"@

$taskXmlPath = Join-Path $env:TEMP 'IdleShutdown-Agent.xml'
Set-Content $taskXmlPath $taskXml -Encoding Unicode
try {
    schtasks.exe /Create /TN $taskName /XML $taskXmlPath /F | Out-Null
}
finally {
    Remove-Item $taskXmlPath -Force -ErrorAction SilentlyContinue
}

Install-BinFile -Name 'idle-shutdown-test' -Path (Join-Path $toolsDir 'idle-shutdown-test.cmd')
Start-Service $serviceName
Set-SoftwareInventoryEntry -InstalledAgentExe $installedAgentExe

# A logon trigger created during an already active session does not fire
# retroactively. Start the agent immediately when an interactive user exists;
# otherwise the task will start normally at the next logon.
if (Get-Process 'explorer' -ErrorAction SilentlyContinue) {
    try {
        Start-ScheduledTask -TaskName $taskName -ErrorAction Stop
    }
    catch {
        Write-Warning (
            'The agent could not be started in the current session. ' +
            'It will start automatically at the next user logon. ' +
            $_.Exception.Message)
    }
}

Write-Host "Idle Shutdown installed. Configuration: $configPath"
Write-Host "Log: $logPath"

    # A successful upgrade can now discard the previous application binaries.
    if (Test-Path $backupDir) {
        Remove-Item -LiteralPath $backupDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}
catch {
    $failure = $_
    Write-Warning ('IdleShutdown upgrade failed: ' + $failure.Exception.Message)

    if ($servicesTouched) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
        Stop-Service -Name $serviceName -Force -ErrorAction SilentlyContinue
        try { Stop-InstalledAgents } catch { Write-Warning $_.Exception.Message }

        # Preserve the previous version if the directory swap has started.
        if ($swapped -or (Test-Path $backupDir)) {
            if (Test-Path $installDir) {
                try {
                    Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop
                }
                catch {
                    Write-Warning ('Could not remove failed installation: ' + $_.Exception.Message)
                }
            }
            if (Test-Path $backupDir) {
                Move-Item -LiteralPath $backupDir -Destination $installDir -ErrorAction Stop
                Write-Warning 'Restored previous IdleShutdown application binaries.'
            }
        }

        if ($null -ne $previousConfig) {
            $previousConfig | Set-Content -LiteralPath $configPath -Encoding UTF8
        }
        if ($previousTaskXml) {
            try {
                Register-ScheduledTask -TaskName $taskName -Xml $previousTaskXml -Force -ErrorAction Stop | Out-Null
            }
            catch { Write-Warning ('Could not restore agent task: ' + $_.Exception.Message) }
        }
        if ($hadExistingInstall -and $wasServiceRunning) {
            try { Start-Service -Name $serviceName -ErrorAction Stop }
            catch { Write-Warning ('Could not restart previous service: ' + $_.Exception.Message) }
        }
        if ($previousInventoryVersion) {
            try {
                Set-ItemProperty -Path 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown' -Name 'DisplayVersion' -Value $previousInventoryVersion -ErrorAction Stop
            }
            catch { Write-Warning ('Could not restore installed version registry value: ' + $_.Exception.Message) }
        }
    }
    throw $failure
}
finally {
    if ($guard) { $guard.Dispose() }
    if (Test-Path $stageDir) {
        Remove-Item -LiteralPath $stageDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

