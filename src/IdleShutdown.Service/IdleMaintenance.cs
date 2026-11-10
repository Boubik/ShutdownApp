using System.Diagnostics;
using System.Text;

namespace IdleShutdown.ServiceApp;

// Coordinates one maintenance cycle at a time. Never kills an in-flight installer.
// Cancellation means "do not start another stage", not "terminate this installation".
internal sealed class IdleMaintenance
{
    private readonly object _sync = new();
    private Task? _task;
    private bool _cancelRequested;
    private bool _completed;
    private bool _warnedOverrun;
    private bool _failed;
    private bool _timedOut;
    private DateTime _startedUtc;
    private AppConfig? _settings;

    public bool Running
    {
        get { lock (_sync) return _task is { IsCompleted: false }; }
    }

    public bool Completed
    {
        get { lock (_sync) return _completed && _task?.IsCompleted == true; }
    }

    public bool Failed
    {
        get { lock (_sync) return _failed && _task?.IsCompleted == true; }
    }

    public void BeginIfNeeded(AppConfig config)
    {
        lock (_sync)
        {
            if (_task is not null)
                return;
            _cancelRequested = false;
            _warnedOverrun = false;
            _completed = false;
            _failed = false;
            _timedOut = false;
            _startedUtc = DateTime.UtcNow;
            _settings = config;
            _task = Task.Run(RunAsync);
            Log.Write("Maintenance: cycle started.");
        }
    }

    public void Cancel(string reason)
    {
        lock (_sync)
        {
            if (_task is null) return;
            if (!_cancelRequested)
                Log.Write($"Maintenance: cancellation requested ({reason}); active installer will finish.");
            _cancelRequested = true;
            _timedOut = false;
            _completed = false;
            // Keep the current Task reference until it exits, to prevent overlap.
        }
    }

    public void ResetIfSafe()
    {
        lock (_sync)
        {
            if (_task is { IsCompleted: false }) return;
            _task = null;
            _completed = false;
            _cancelRequested = false;
            _failed = false;
            _timedOut = false;
            _settings = null;
        }
    }

    public void CheckOverrun()
    {
        lock (_sync)
        {
            if (_task is not { IsCompleted: false } || _settings is null || _warnedOverrun)
                return;
            if (DateTime.UtcNow - _startedUtc <= TimeSpan.FromMinutes(_settings.MaintenanceMaxMinutes))
                return;
            _warnedOverrun = true;
            _timedOut = true;
            _cancelRequested = true;
            Log.Write("Maintenance: maximum duration exceeded; no more stages will start. Waiting for active installer (never force-kill).");
        }
    }

    private bool MayStartNextStage()
    {
        lock (_sync)
            return !_cancelRequested && _settings is not null &&
                   DateTime.UtcNow - _startedUtc < TimeSpan.FromMinutes(_settings.MaintenanceMaxMinutes);
    }

    private async Task RunAsync()
    {
        try
        {
            AppConfig config;
            lock (_sync) config = _settings!;
            Log.Debug($"Maintenance configuration: WindowsUpdate={config.WindowsUpdateEnabled}, " +
                $"Drivers={config.DriverUpdatesEnabled}, OptionalDrivers={config.OptionalDriversEnabled}, " +
                $"Chocolatey={config.ChocolateyEnabled}, " +
                $"ChocolateySource={(config.ChocolateySource is null ? "system defaults" : "custom")}, " +
                $"maxDuration={config.MaintenanceMaxMinutes} minutes.");

            if (config.WindowsUpdateEnabled && MayStartNextStage())
                await RunWindowsUpdateAsync(config).ConfigureAwait(false);

            if (config.ChocolateyEnabled && MayStartNextStage())
                await RunChocolateyAsync(config).ConfigureAwait(false);

            bool success;
            lock (_sync)
            {
                _completed = !_cancelRequested || _timedOut;
                _failed = !_completed;
                success = _completed;
            }
            Log.Write(success
                ? "Maintenance: cycle finished; shutdown may proceed after final safety checks."
                : "Maintenance: cancelled by activity; automatic shutdown is not authorized.");
        }
        catch (Exception ex)
        {
            lock (_sync)
            {
                _completed = !_cancelRequested;
                _failed = true;
            }
            Log.Write($"Maintenance failed: {ex.GetType().Name}: {ex.Message}; " +
                "after the active stage has exited, the existing shutdown safety checks still apply.");
        }
    }

    private static async Task RunWindowsUpdateAsync(AppConfig config)
    {
        // Windows Update Agent API, executed within LocalSystem, without a desktop prompt.
        // Optional drivers are opt-in. This API does not request a system reboot.
        const string script = """
            $ErrorActionPreference = 'Stop'
            try {
                $session = New-Object -ComObject Microsoft.Update.Session
                $searcher = $session.CreateUpdateSearcher()
                $result = $searcher.Search("IsInstalled=0 and IsHidden=0")
                Write-Output ("IDLE_WU|FOUND|" + $result.Updates.Count)
                $selected = New-Object -ComObject Microsoft.Update.UpdateColl
                $skipped = 0
                $selectedDrivers = 0
                foreach ($u in $result.Updates) {
                    $isDriver = [int]$u.Type -eq 2
                    $reason = $null
                    if ($isDriver -and $env:IDLE_DRIVER_UPDATES -ne '1') { $reason = 'drivers-disabled' }
                    elseif ($isDriver -and $env:IDLE_OPTIONAL_DRIVERS -ne '1' -and $u.BrowseOnly) { $reason = 'optional-driver' }
                    elseif (-not $isDriver -and $u.BrowseOnly) { $reason = 'optional-software' }
                    elseif ($u.InstallationBehavior.CanRequestUserInput) { $reason = 'requires-user-input' }
                    if ($reason) {
                        $skipped++
                        Write-Output ("IDLE_WU|SKIP|" + $reason + "|" + $u.Title)
                        continue
                    }
                    if (-not $u.EulaAccepted) { $u.AcceptEula() }
                    [void]$selected.Add($u)
                    if ($isDriver) { $selectedDrivers++ }
                    Write-Output ("IDLE_WU|SELECT|" + $u.Title)
                }
                Write-Output ("IDLE_WU|SUMMARY|selected=" + $selected.Count + ";skipped=" + $skipped + ";drivers=" + $selectedDrivers)
                if ($selected.Count -eq 0) {
                    Write-Output "IDLE_WU|NONE|Nothing to download or install"
                    exit 0
                }
                $downloader = $session.CreateUpdateDownloader()
                $downloader.Updates = $selected
                $downloadResult = $downloader.Download()
                Write-Output ("IDLE_WU|DOWNLOAD_RESULT|code=" + $downloadResult.ResultCode)
                $ready = New-Object -ComObject Microsoft.Update.UpdateColl
                foreach ($u in $selected) {
                    if ($u.IsDownloaded) {
                        [void]$ready.Add($u)
                        Write-Output ("IDLE_WU|DOWNLOADED|" + $u.Title)
                    } else {
                        Write-Output ("IDLE_WU|DOWNLOAD_FAILED|" + $u.Title)
                    }
                }
                if ($ready.Count -eq 0) {
                    Write-Output "IDLE_WU|ERROR|No selected updates downloaded successfully"
                    exit 1
                }
                $installer = $session.CreateUpdateInstaller()
                $installer.Updates = $ready
                $installer.AllowSourcePrompts = $false
                $installResult = $installer.Install()
                for ($i = 0; $i -lt $ready.Count; $i++) {
                    $itemResult = $installResult.GetUpdateResult($i)
                    Write-Output ("IDLE_WU|UPDATE_RESULT|" + $ready.Item($i).Title +
                        "|code=" + $itemResult.ResultCode + ";hresult=" + $itemResult.HResult)
                }
                Write-Output ("IDLE_WU|INSTALL_RESULT|code=" + $installResult.ResultCode +
                    ";rebootRequired=" + $installResult.RebootRequired + ";attempted=" + $ready.Count)
                if ($installResult.ResultCode -ge 4) { exit 1 }
                exit 0
            } catch {
                Write-Output ("IDLE_WU|ERROR|" + $_.Exception.Message)
                exit 1
            }
            """;
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var psi = new ProcessStartInfo
        {
            FileName = Path.Combine(Environment.SystemDirectory, "WindowsPowerShell", "v1.0", "powershell.exe"),
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -EncodedCommand {encoded}",
            UseShellExecute = false,
            CreateNoWindow = true
        };
        psi.Environment["IDLE_DRIVER_UPDATES"] = config.DriverUpdatesEnabled ? "1" : "0";
        psi.Environment["IDLE_OPTIONAL_DRIVERS"] = config.OptionalDriversEnabled ? "1" : "0";
        await RunProcessAsync("Windows Update", psi).ConfigureAwait(false);
    }

    private static async Task RunChocolateyAsync(AppConfig config)
    {
        var home = Environment.GetEnvironmentVariable("ChocolateyInstall");
        var executable = !string.IsNullOrWhiteSpace(home)
            ? Path.Combine(home, "bin", "choco.exe")
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "chocolatey", "bin", "choco.exe");
        // Chocolatey is normally installed in C:\ProgramData\chocolatey.
        if (!File.Exists(executable))
        {
            Log.Write("Maintenance: Chocolatey not installed; skipping.");
            return;
        }

        var psi = new ProcessStartInfo
        {
            FileName = executable,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (var argument in new[] { "upgrade", "all", "-y", "--no-progress", "--except=idle-shutdown" })
            psi.ArgumentList.Add(argument);
        if (!string.IsNullOrWhiteSpace(config.ChocolateySource))
        {
            psi.ArgumentList.Add("--source");
            psi.ArgumentList.Add(config.ChocolateySource);
        }
        await RunProcessAsync("Chocolatey", psi).ConfigureAwait(false);
    }

    private static async Task RunProcessAsync(string stage, ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        Log.Write($"Maintenance: starting {stage}.");
        using var process = new Process { StartInfo = psi };
        process.Start();
        // Drain both pipes concurrently to avoid blocking an installer on full buffers.
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync().ConfigureAwait(false);
        var output = await stdout.ConfigureAwait(false);
        var error = await stderr.ConfigureAwait(false);
        // Avoid logging tokens or credentials from arbitrary updater output.
        Log.Write($"Maintenance: {stage} exited with code {process.ExitCode}; stdout chars={output.Length}; stderr chars={error.Length}.");
        if (process.ExitCode != 0 && process.ExitCode != 2 && process.ExitCode != 3010)
            Log.Write($"Maintenance: {stage} reported a non-success exit code.");
        if (process.ExitCode == 3010)
            Log.Write($"Maintenance: {stage} requires reboot later; no restart requested.");
        if (process.ExitCode == 1641)
            Log.Write($"Maintenance WARNING: {stage} reports that an installer initiated a reboot.");
    }
}
