#requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$Root = Split-Path -Parent $MyInvocation.MyCommand.Path
$Dist = Join-Path $Root 'dist'
$InstallDir = 'C:\Program Files\IdleShutdown'
$DataDir = 'C:\ProgramData\IdleShutdown'
$ServiceName = 'IdleShutdown'
$TaskName = 'Idle Shutdown Agent'

if (-not (Test-Path (Join-Path $Dist 'Service\IdleShutdown.Service.exe'))) {
    throw 'Chybí sestavené soubory. Nejdřív spusťte Build-Publish.ps1.'
}

New-Item $InstallDir -ItemType Directory -Force | Out-Null
New-Item $DataDir -ItemType Directory -Force | Out-Null

Stop-Service $ServiceName -Force -ErrorAction SilentlyContinue
sc.exe delete $ServiceName 2>$null | Out-Null
Start-Sleep -Seconds 2

Copy-Item (Join-Path $Dist 'Service\*') $InstallDir -Recurse -Force
Copy-Item (Join-Path $Dist 'Agent\*') $InstallDir -Recurse -Force
if (-not (Test-Path (Join-Path $DataDir 'config.json'))) {
    Copy-Item (Join-Path $Dist 'config.json') (Join-Path $DataDir 'config.json') -Force
}

$logPath = Join-Path $DataDir 'IdleShutdown.log'
if (-not (Test-Path $logPath)) {
    New-Item $logPath -ItemType File -Force | Out-Null
}

# The interactive agent appends to the shared log as a standard user.
$logAcl = Get-Acl $logPath
$usersSid = [System.Security.Principal.SecurityIdentifier]::new('S-1-5-32-545')
$writeRule = [System.Security.AccessControl.FileSystemAccessRule]::new(
    $usersSid,
    [System.Security.AccessControl.FileSystemRights]::Write,
    [System.Security.AccessControl.AccessControlType]::Allow)
$logAcl.SetAccessRule($writeRule)
Set-Acl -Path $logPath -AclObject $logAcl

$serviceExe = Join-Path $InstallDir 'IdleShutdown.Service.exe'
sc.exe create $ServiceName binPath= "`"$serviceExe`"" start= auto DisplayName= "Idle Shutdown" | Out-Null
sc.exe description $ServiceName "Automatické vypnutí počítače po nečinnosti nebo delším zamčení." | Out-Null
sc.exe failure $ServiceName reset= 86400 actions= restart/60000/restart/60000/restart/60000 | Out-Null
Start-Service $ServiceName

$agentExe = Join-Path $InstallDir 'IdleShutdown.Agent.exe'
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
  <Actions Context="Users"><Exec><Command>$agentExe</Command></Exec></Actions>
</Task>
"@

$tempXml = Join-Path $env:TEMP 'IdleShutdown-Agent.xml'
Set-Content $tempXml $taskXml -Encoding Unicode
schtasks.exe /Create /TN $TaskName /XML $tempXml /F | Out-Null
Remove-Item $tempXml -Force

if (Get-Process 'explorer' -ErrorAction SilentlyContinue) {
    Start-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
}

Write-Host '[OK] Testovací instalace dokončena.'
Write-Host '[INFO] Výchozí konfigurace je DRY RUN – PC se zatím skutečně nevypne.'
Write-Host '[INFO] Odhlaste/přihlaste uživatele nebo spusťte:'
Write-Host "       Start-ScheduledTask -TaskName '$TaskName'"
Write-Host '[INFO] Log: C:\ProgramData\IdleShutdown\IdleShutdown.log'
