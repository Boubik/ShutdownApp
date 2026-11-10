#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$serviceName = 'IdleShutdown'
$taskName = 'Idle Shutdown Agent'
$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$guardPath = Join-Path $dataDir 'maintenance.lock'

function Remove-SoftwareInventoryEntry {
    $baseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
        [Microsoft.Win32.RegistryHive]::LocalMachine,
        [Microsoft.Win32.RegistryView]::Registry64)
    try {
        $baseKey.DeleteSubKeyTree(
            'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown', $false)
    }
    finally {
        $baseKey.Dispose()
    }
}

function Get-InstalledAgentProcesses {
    $agentPath = Join-Path $installDir 'IdleShutdown.Agent.exe'
    $processes = @(Get-CimInstance Win32_Process -Filter "Name = 'IdleShutdown.Agent.exe'" -ErrorAction Stop)
    foreach ($process in $processes) {
        if ($process.ExecutablePath -and
            [string]::Equals(
                [IO.Path]::GetFullPath([string]$process.ExecutablePath),
                [IO.Path]::GetFullPath($agentPath),
                [StringComparison]::OrdinalIgnoreCase)) {
            $process
        }
    }
}

New-Item $dataDir -ItemType Directory -Force | Out-Null
try {
    $guard = [System.IO.FileStream]::new(
        $guardPath, [System.IO.FileMode]::OpenOrCreate,
        [System.IO.FileAccess]::ReadWrite, [System.IO.FileShare]::None)
}
catch [System.IO.IOException] {
    throw 'Cannot uninstall IdleShutdown while automatic maintenance is running. Retry after updates finish.'
}

try {
    # Old beta compatibility: refuse uninstall if child updaters still run.
    $services = @(Get-CimInstance Win32_Process -Filter "Name = 'IdleShutdown.Service.exe'" -ErrorAction Stop)
    $ids = @($services | ForEach-Object { [int]$_.ProcessId })
    if ($ids.Count -gt 0) {
        $children = @(Get-CimInstance Win32_Process -ErrorAction Stop |
            Where-Object {
                $ids -contains [int]$_.ParentProcessId -and
                $_.Name -in @('powershell.exe', 'pwsh.exe', 'choco.exe')
            })
        if ($children.Count -gt 0) {
            throw 'IdleShutdown is still updating software. Uninstall is refused.'
        }
    }

    $task = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
    if ($null -ne $task) {
        Stop-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
        Unregister-ScheduledTask -TaskName $taskName -Confirm:$false -ErrorAction Stop
    }

    for ($attempt = 0; $attempt -lt 15; $attempt++) {
        $agents = @(Get-InstalledAgentProcesses)
        if ($agents.Count -eq 0) { break }
        foreach ($agent in $agents) {
            Stop-Process -Id $agent.ProcessId -Force -ErrorAction SilentlyContinue
        }
        Start-Sleep -Milliseconds 600
    }
    if (@(Get-InstalledAgentProcesses).Count -gt 0) {
        throw 'IdleShutdown.Agent.exe remains in use; cannot safely uninstall.'
    }

    $service = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
    if ($null -ne $service) {
        if ($service.Status -ne 'Stopped') {
            Stop-Service -Name $serviceName -Force -ErrorAction Stop
            (Get-Service -Name $serviceName).WaitForStatus(
                [System.ServiceProcess.ServiceControllerStatus]::Stopped,
                [TimeSpan]::FromSeconds(30))
        }
        sc.exe delete $serviceName | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'Could not delete IdleShutdown service.' }
    }

    if (Test-Path $installDir) {
        Remove-Item -LiteralPath $installDir -Recurse -Force -ErrorAction Stop
    }
    Remove-SoftwareInventoryEntry
    Uninstall-BinFile -Name 'idle-shutdown-test'
    Write-Host 'Idle Shutdown removed. Configuration and logs preserved in ProgramData.'
}
finally {
    $guard.Dispose()
}
