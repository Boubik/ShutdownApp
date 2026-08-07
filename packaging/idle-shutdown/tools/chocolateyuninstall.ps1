#requires -Version 5.1
$ErrorActionPreference = 'SilentlyContinue'

$serviceName = 'IdleShutdown'
$taskName = 'Idle Shutdown Agent'
$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'

Uninstall-BinFile -Name 'idle-shutdown-test'
Stop-ScheduledTask -TaskName $taskName
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Stop-Service $serviceName -Force
sc.exe delete $serviceName | Out-Null
Remove-Item $installDir -Recurse -Force

Write-Host 'Idle Shutdown was removed. Configuration and logs were preserved in ProgramData.'
