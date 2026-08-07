#requires -RunAsAdministrator
$ErrorActionPreference = 'SilentlyContinue'
$ServiceName = 'IdleShutdown'
$TaskName = 'Idle Shutdown Agent'

Stop-ScheduledTask -TaskName $TaskName
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Stop-Service $ServiceName -Force
sc.exe delete $ServiceName | Out-Null
Remove-Item 'C:\Program Files\IdleShutdown' -Recurse -Force
Write-Host '[OK] Služba a agent byly odinstalovány. Log/config v ProgramData zůstaly zachované.'
