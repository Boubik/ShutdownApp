#requires -Version 5.1
$ErrorActionPreference = 'SilentlyContinue'

$serviceName = 'IdleShutdown'
$taskName = 'Idle Shutdown Agent'
$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'

function Remove-SoftwareInventoryEntry {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)

    try {
        $baseKey.DeleteSubKeyTree(
            'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown',
            $false)
    }
    finally {
        $baseKey.Dispose()
    }
}

Uninstall-BinFile -Name 'idle-shutdown-test'
Stop-ScheduledTask -TaskName $taskName
Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
Stop-Service $serviceName -Force
sc.exe delete $serviceName | Out-Null
Remove-Item $installDir -Recurse -Force
Remove-SoftwareInventoryEntry

Write-Host 'Idle Shutdown was removed. Configuration and logs were preserved in ProgramData.'
