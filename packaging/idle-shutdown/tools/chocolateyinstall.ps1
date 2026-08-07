#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$toolsDir = Split-Path -Parent $MyInvocation.MyCommand.Definition
$payloadDir = Join-Path $toolsDir 'dist'
$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$configPath = Join-Path $dataDir 'config.json'
$logPath = Join-Path $dataDir 'IdleShutdown.log'
$serviceName = 'IdleShutdown'
$taskName = 'Idle Shutdown Agent'
$parameters = Get-PackageParameters

function Get-PositiveIntegerParameter {
    param(
        [string] $Name,
        [int] $CurrentValue
    )

    if (-not $parameters.ContainsKey($Name)) {
        return $CurrentValue
    }

    $parsed = 0
    if (-not [int]::TryParse([string] $parameters[$Name], [ref] $parsed) -or $parsed -lt 1) {
        throw "Package parameter /$Name must be a positive integer."
    }

    return $parsed
}

function Get-BooleanParameter {
    param(
        [string] $Name,
        [bool] $CurrentValue
    )

    if (-not $parameters.ContainsKey($Name)) {
        return $CurrentValue
    }

    $parsed = $false
    if (-not [bool]::TryParse([string] $parameters[$Name], [ref] $parsed)) {
        throw "Package parameter /$Name must be true or false."
    }

    return $parsed
}

$serviceExe = Join-Path $payloadDir 'Service\IdleShutdown.Service.exe'
$agentExe = Join-Path $payloadDir 'Agent\IdleShutdown.Agent.exe'

if (-not (Test-Path $serviceExe) -or -not (Test-Path $agentExe)) {
    throw 'The package does not contain the published service and agent executables.'
}

$config = [ordered]@{
    IdleMinutes = 60
    WarningSeconds = 300
    LockedMinutes = 60
    NoUserMinutes = 60
    CheckIntervalSeconds = 5
    PauseWhenFullscreen = $true
    DryRun = $false
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
$config.LockedMinutes = Get-PositiveIntegerParameter 'LockedMinutes' $config.LockedMinutes
$config.NoUserMinutes = Get-PositiveIntegerParameter 'NoUserMinutes' $config.NoUserMinutes
$config.CheckIntervalSeconds = Get-PositiveIntegerParameter 'CheckIntervalSeconds' $config.CheckIntervalSeconds
$config.PauseWhenFullscreen = Get-BooleanParameter 'PauseWhenFullscreen' $config.PauseWhenFullscreen
$config.DryRun = Get-BooleanParameter 'DryRun' $config.DryRun

Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction SilentlyContinue
Stop-Service $serviceName -Force -ErrorAction SilentlyContinue

New-Item $installDir -ItemType Directory -Force | Out-Null
New-Item $dataDir -ItemType Directory -Force | Out-Null
Copy-Item (Join-Path $payloadDir 'Service\*') $installDir -Recurse -Force
Copy-Item (Join-Path $payloadDir 'Agent\*') $installDir -Recurse -Force

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

sc.exe description $serviceName "Automatické vypnutí počítače po nečinnosti nebo delším zamčení." | Out-Null
sc.exe failure $serviceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null

$installedAgentExe = Join-Path $installDir 'IdleShutdown.Agent.exe'
$agentExeXml = [System.Security.SecurityElement]::Escape($installedAgentExe)
$taskXml = @"
<?xml version="1.0" encoding="UTF-16"?>
<Task version="1.4" xmlns="http://schemas.microsoft.com/windows/2004/02/mit/task">
  <RegistrationInfo><Description>Agent pro automatické vypnutí po nečinnosti.</Description></RegistrationInfo>
  <Triggers><LogonTrigger><Enabled>true</Enabled><Delay>PT10S</Delay></LogonTrigger></Triggers>
  <Principals><Principal id="Users"><GroupId>S-1-5-32-545</GroupId><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
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
