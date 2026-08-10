#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$configPath = Join-Path $dataDir 'config.json'
$logPath = Join-Path $dataDir 'IdleShutdown.log'
$agentPath = Join-Path $installDir 'IdleShutdown.Agent.exe'
$errors = [System.Collections.Generic.List[string]]::new()

foreach ($path in @(
    (Join-Path $installDir 'IdleShutdown.Service.exe'),
    $agentPath,
    $configPath,
    $logPath
)) {
    if (-not (Test-Path $path)) {
        $errors.Add("Missing: $path")
    }
}

$service = Get-Service 'IdleShutdown' -ErrorAction SilentlyContinue
if ($null -eq $service) {
    $errors.Add('Windows service IdleShutdown is not installed.')
}
elseif ($service.Status -ne 'Running') {
    $errors.Add("Windows service IdleShutdown is $($service.Status).")
}

$task = Get-ScheduledTask -TaskName 'Idle Shutdown Agent' -ErrorAction SilentlyContinue
if ($null -eq $task) {
    $errors.Add('Scheduled task Idle Shutdown Agent is not installed.')
}
elseif ($task.State -eq 'Disabled') {
    $errors.Add('Scheduled task Idle Shutdown Agent is disabled.')
}

$inventoryBaseKey = [Microsoft.Win32.RegistryKey]::OpenBaseKey(
    [Microsoft.Win32.RegistryHive]::LocalMachine,
    [Microsoft.Win32.RegistryView]::Registry64)

try {
    $inventoryKey = $inventoryBaseKey.OpenSubKey(
        'SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\IdleShutdown')

    if ($null -eq $inventoryKey) {
        $errors.Add('Windows installed-software inventory entry is missing.')
    }
    else {
        try {
            if ($inventoryKey.GetValue('DisplayName') -ne 'Idle Shutdown') {
                $errors.Add('Windows software inventory display name is invalid.')
            }

            if ([string]::IsNullOrWhiteSpace(
                    [string] $inventoryKey.GetValue('DisplayVersion'))) {
                $errors.Add('Windows software inventory version is missing.')
            }

            if ([string]::IsNullOrWhiteSpace(
                    [string] $inventoryKey.GetValue('QuietUninstallString'))) {
                $errors.Add('Windows software inventory uninstall command is missing.')
            }
        }
        finally {
            $inventoryKey.Dispose()
        }
    }
}
finally {
    $inventoryBaseKey.Dispose()
}

$interactiveShell = Get-Process 'explorer' -ErrorAction SilentlyContinue
$interactiveSessionIds = @(
    $interactiveShell |
        Select-Object -ExpandProperty SessionId -Unique)

$interactiveAgent = @(
    Get-CimInstance Win32_Process -Filter "Name = 'IdleShutdown.Agent.exe'" `
        -ErrorAction SilentlyContinue |
        Where-Object {
            $_.SessionId -in $interactiveSessionIds -and
            -not [string]::IsNullOrWhiteSpace($_.CommandLine) -and
            $_.CommandLine -notmatch '--machine-input-monitor'
        })

if ($null -ne $interactiveShell) {
    $agentSessionIds = @(
        $interactiveAgent |
            Select-Object -ExpandProperty SessionId -Unique)

    foreach ($sessionId in $interactiveSessionIds) {
        if ($sessionId -notin $agentSessionIds) {
            $errors.Add(
                "Interactive session $sessionId has no IdleShutdown.Agent. " +
                'Sign that user out and in again before production use.')
        }
    }
}

if (Test-Path $configPath) {
    try {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        foreach ($name in @('IdleMinutes', 'WarningSeconds', 'CheckIntervalSeconds')) {
            $value = $config.PSObject.Properties[$name].Value
            if ($null -eq $value -or [int] $value -lt 1) {
                $errors.Add("Configuration value $name must be a positive integer.")
            }
        }

        foreach ($name in @('PauseWhenFullscreen', 'DryRun')) {
            if ($null -eq $config.PSObject.Properties[$name]) {
                $errors.Add("Configuration value $name is missing.")
            }
        }
    }
    catch {
        $errors.Add("Configuration is invalid: $($_.Exception.Message)")
    }
}

if ($errors.Count -gt 0) {
    foreach ($message in $errors) {
        Write-Host "[ERROR] $message" -ForegroundColor Red
    }

    exit 1
}

Write-Host '[OK] Idle Shutdown installation is healthy.' -ForegroundColor Green
Write-Host "Configuration: $configPath"
Write-Host "Log: $logPath"
