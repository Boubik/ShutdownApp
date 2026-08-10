#requires -Version 5.1
$ErrorActionPreference = 'Stop'

$installDir = Join-Path $env:ProgramFiles 'IdleShutdown'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$configPath = Join-Path $dataDir 'config.json'
$logPath = Join-Path $dataDir 'IdleShutdown.log'
$errors = [System.Collections.Generic.List[string]]::new()

foreach ($path in @(
    (Join-Path $installDir 'IdleShutdown.Service.exe'),
    (Join-Path $installDir 'IdleShutdown.Agent.exe'),
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

if ($null -ne $interactiveShell -and $interactiveAgent.Count -eq 0) {
    $errors.Add(
        'An interactive user is logged on, but IdleShutdown.Agent is not running. ' +
        'Start the scheduled task or sign out and sign in again.')
}

if (Test-Path $configPath) {
    try {
        $config = Get-Content $configPath -Raw | ConvertFrom-Json
        foreach ($name in @('IdleMinutes', 'WarningSeconds', 'LockedMinutes', 'NoUserMinutes', 'CheckIntervalSeconds')) {
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
