using System.Diagnostics;
using System.Text;

namespace IdleShutdown.ServiceApp;

// Coordinates one maintenance cycle at a time. Never kills an in-flight installer.
// Cancellation means "do not start another stage", not "terminate this installation".
internal sealed class IdleMaintenance
{
    private readonly object _sync = new();
    // Chocolateyinstall.ps1 acquires this exclusive file handle before it
    // stops the service. It is held throughout an active maintenance cycle.
    // This prevents an external upgrade from dismantling a running updater.
    private static readonly string MaintenanceGuardPath =
        Path.Combine(AppConfig.BaseDirectory, "maintenance.lock");
    private FileStream? _maintenanceGuard;
    private DateTime _lastGuardFailureLogUtc;
    private const string InstallationGateName = @"Global\IdleShutdown.MaintenanceInstallGate";
    private Task? _task;
    private bool _cancelRequested;
    private bool _completed;
    private bool _warnedOverrun;
    private bool _failed;
    private bool _timedOut;
    private DateTime _nextHeartbeatUtc;
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
            try
            {
                Directory.CreateDirectory(AppConfig.BaseDirectory);
                _maintenanceGuard = new FileStream(
                    MaintenanceGuardPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None);
            }
            catch (IOException ex)
            {
                if (DateTime.UtcNow - _lastGuardFailureLogUtc > TimeSpan.FromMinutes(1))
                {
                    Log.Write($"Maintenance: start deferred because upgrade/other maintenance owns guard: {ex.Message}");
                    _lastGuardFailureLogUtc = DateTime.UtcNow;
                }
                return;
            }
            catch (UnauthorizedAccessException ex)
            {
                if (DateTime.UtcNow - _lastGuardFailureLogUtc > TimeSpan.FromMinutes(1))
                {
                    Log.Write($"Maintenance: cannot acquire safety guard; shutdown deferred: {ex.Message}");
                    _lastGuardFailureLogUtc = DateTime.UtcNow;
                }
                return;
            }
            _cancelRequested = false;
            _warnedOverrun = false;
            _completed = false;
            _failed = false;
            _timedOut = false;
            _startedUtc = DateTime.UtcNow;
            _nextHeartbeatUtc = _startedUtc.AddMinutes(1);
            _settings = config;
            _task = Task.Run(async () =>
            {
                try
                {
                    await RunAsync().ConfigureAwait(false);
                }
                finally
                {
                    lock (_sync)
                    {
                        _maintenanceGuard?.Dispose();
                        _maintenanceGuard = null;
                    }
                }
            });
            Log.Write("Maintenance: cycle started (self-update guard held).");
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

    public void ResetCancelledIfSafe()
    {
        // An earlier warning may have been cancelled while an installer was
        // running. Once that task has exited, a NEW warning may start a
        // fresh cycle without overlapping the previous installer.
        lock (_sync)
        {
            if (_task is null || !_task.IsCompleted || !_cancelRequested)
                return;

            _task = null;
            _completed = false;
            _failed = false;
            _cancelRequested = false;
            _timedOut = false;
            _warnedOverrun = false;
            _settings = null;
            Log.Debug("Maintenance: cancelled previous cycle cleared; " +
                "a subsequent warning may start a new cycle.");
        }
    }

    public void CheckOverrun()
    {
        lock (_sync)
        {
            if (_task is not { IsCompleted: false } || _settings is null)
                return;

            var now = DateTime.UtcNow;
            if (Log.DebugEnabled && now >= _nextHeartbeatUtc)
            {
                Log.Debug($"Maintenance heartbeat: running for " +
                    $"{(now - _startedUtc).TotalMinutes:F1} minute(s), " +
                    $"maximum configured duration {_settings.MaintenanceMaxMinutes} minute(s); " +
                    "waiting for active update operation.");
                _nextHeartbeatUtc = now.AddMinutes(1);
            }

            if (_warnedOverrun ||
                now - _startedUtc <= TimeSpan.FromMinutes(_settings.MaintenanceMaxMinutes))
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

            // Independent update tasks: WUA can scan and download while
            // Chocolatey upgrades packages. Both coordinate *installation*
            // via the same cross-process semaphore to avoid MSI conflicts.
            var tasks = new List<Task>(2);
            if (config.WindowsUpdateEnabled && MayStartNextStage())
                tasks.Add(RunWindowsUpdateAsync(config));

            if (config.ChocolateyEnabled && MayStartNextStage())
                tasks.Add(RunChocolateyAsync(config));

            Log.Write($"Maintenance: {tasks.Count} update stage(s) started independently; " +
                "shutdown will wait until every stage exits.");
            await Task.WhenAll(tasks).ConfigureAwait(false);

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
            # Produce readable localized update names in the service UTF-8 log.
            [Console]::OutputEncoding = [System.Text.UTF8Encoding]::new($false)
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

                # Chocolatey may install MSI packages. Share this semaphore
                # with the service's Chocolatey runner to avoid concurrency
                # during installation (scan/download stay independent).
                $installGate = [System.Threading.Semaphore]::new(
                    1, 1, 'Global\IdleShutdown.MaintenanceInstallGate')
                Write-Output "IDLE_WU|WAITING_FOR_INSTALL_GATE|Chocolatey may still be installing"
                [void] $installGate.WaitOne()
                try {
                    Write-Output "IDLE_WU|INSTALLING|Installation gate acquired"
                    $installResult = $installer.Install()
                    for ($i = 0; $i -lt $ready.Count; $i++) {
                        $itemResult = $installResult.GetUpdateResult($i)
                        Write-Output ("IDLE_WU|UPDATE_RESULT|" + $ready.Item($i).Title +
                            "|code=" + $itemResult.ResultCode + ";hresult=" + $itemResult.HResult)
                    }
                    Write-Output ("IDLE_WU|INSTALL_RESULT|code=" + $installResult.ResultCode +
                        ";rebootRequired=" + $installResult.RebootRequired + ";attempted=" + $ready.Count)
                } finally {
                    [void] $installGate.Release()
                    $installGate.Dispose()
                }
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
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
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
        if (!File.Exists(executable))
        {
            Log.Write("Maintenance: Chocolatey not installed; skipping.");
            return;
        }

        // Chocolatey 2.x 'list --limit-output' is a local package inventory.
        // Record version changes, never unfiltered installer output (which may contain secrets).
        var before = await ReadChocolateyInventoryAsync(executable).ConfigureAwait(false);

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
        Log.Debug($"Chocolatey upgrade source: {(config.ChocolateySource is null ? "machine defaults" : "custom feed")}; " +
            "excluding idle-shutdown to prevent replacing the running service.");
        // SemaphoreSlim would not coordinate with the separate WUA PowerShell
        // process. A named system semaphore works across both processes.
        using var installationGate = new Semaphore(1, 1, InstallationGateName);
        Log.Debug("Chocolatey waiting for Windows Update installer gate.");
        await Task.Run(() => installationGate.WaitOne()).ConfigureAwait(false);

        int code;
        try
        {
            Log.Debug("Chocolatey acquired installer gate; starting upgrade.");
            code = await RunProcessAsync("Chocolatey", psi).ConfigureAwait(false);
        }
        finally
        {
            installationGate.Release();
            Log.Debug("Chocolatey released installer gate.");
        }
        var after = await ReadChocolateyInventoryAsync(executable).ConfigureAwait(false);

        if (before is null || after is null)
        {
            Log.Write("Maintenance: Chocolatey package inventory unavailable; see Chocolatey log for package details.");
            return;
        }

        var changed = 0;
        foreach (var pair in after.OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase))
        {
            if (!before.TryGetValue(pair.Key, out var previous))
            {
                changed++;
                Log.Debug($"Chocolatey package installed: {pair.Key} {pair.Value}.");
            }
            else if (!string.Equals(previous, pair.Value, StringComparison.OrdinalIgnoreCase))
            {
                changed++;
                Log.Debug($"Chocolatey package updated: {pair.Key} {previous} -> {pair.Value}.");
            }
        }

        if (changed == 0)
        {
            Log.Write(code == 0 || code == 2
                ? "Maintenance: Chocolatey completed; no package version changes detected."
                : $"Maintenance: Chocolatey reported exit code {code}; no package version changes detected.");
        }
        else
        {
            Log.Write($"Maintenance: Chocolatey completed with {changed} installed/updated package(s), exit code {code}.");
        }
    }

    private static async Task<Dictionary<string, string>?> ReadChocolateyInventoryAsync(
        string executable)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("list");
            psi.ArgumentList.Add("--limit-output");
            using var process = new Process { StartInfo = psi };
            process.Start();
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync().ConfigureAwait(false);
            var output = await outputTask.ConfigureAwait(false);
            await errorTask.ConfigureAwait(false);
            if (process.ExitCode != 0)
            {
                Log.Debug($"Chocolatey inventory exit code: {process.ExitCode}.");
                return null;
            }

            var packages = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var line in output.Split('\n'))
            {
                var parts = line.Trim().Split('|');
                if (parts.Length != 2) continue;
                var id = parts[0].Trim();
                var version = parts[1].Trim();
                if (id.Length == 0 || version.Length == 0 || id.Length > 120 || version.Length > 120 ||
                    id.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.')) ||
                    version.Any(c => !(char.IsLetterOrDigit(c) || c == '-' || c == '_' || c == '.' || c == '+')))
                    continue;
                packages[id] = version;
            }
            Log.Debug($"Chocolatey inventory: {packages.Count} installed package(s).");
            return packages;
        }
        catch (Exception ex)
        {
            Log.Write($"Maintenance: Chocolatey inventory query failed ({ex.GetType().Name}).");
            return null;
        }
    }

    private static async Task<int> RunProcessAsync(string stage, ProcessStartInfo psi)
    {
        psi.RedirectStandardOutput = true;
        psi.RedirectStandardError = true;
        Log.Write($"Maintenance: starting {stage}.");
        var timer = Stopwatch.StartNew();
        using var process = new Process { StartInfo = psi };
        process.Start();

        // Always drain both pipes concurrently. Stream output so verbose installers do
        // not consume unbounded memory or deadlock when filling redirected pipes.
        var stdout = DrainOutputAsync(process.StandardOutput, stage, isError: false);
        var stderr = DrainOutputAsync(process.StandardError, stage, isError: true);
        await process.WaitForExitAsync().ConfigureAwait(false);
        var counts = await Task.WhenAll(stdout, stderr).ConfigureAwait(false);

        Log.Write($"Maintenance: {stage} finished; exitCode={process.ExitCode}; " +
            $"durationSeconds={timer.Elapsed.TotalSeconds:F1}; stdoutLines={counts[0]}; stderrLines={counts[1]}.");

        if (process.ExitCode != 0 && process.ExitCode != 2 && process.ExitCode != 3010)
            Log.Write($"Maintenance: {stage} reported an error or non-success exit code.");
        if (process.ExitCode == 3010)
            Log.Write($"Maintenance: {stage} requires a later reboot; no restart requested.");
        if (process.ExitCode == 1641)
            Log.Write($"Maintenance WARNING: {stage} reports that an installer initiated a reboot.");

        return process.ExitCode;
    }

    private static async Task<int> DrainOutputAsync(
        StreamReader reader, string stage, bool isError)
    {
        var count = 0;
        var reported = 0;
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            count++;
            // Only log structured WUA results and safe, selected Chocolatey summaries.
            // Untrusted package scripts can print credentials to stdout/stderr.
            if (isError) continue;
            if (stage == "Windows Update" && line.StartsWith("IDLE_WU|", StringComparison.Ordinal))
            {
                var entry = line.Length > 500 ? line[..500] + " [truncated]" : line;
                if (entry.StartsWith("IDLE_WU|SUMMARY|", StringComparison.Ordinal) ||
                    entry.StartsWith("IDLE_WU|NONE|", StringComparison.Ordinal) ||
                    entry.StartsWith("IDLE_WU|INSTALL_RESULT|", StringComparison.Ordinal) ||
                    entry.StartsWith("IDLE_WU|ERROR|", StringComparison.Ordinal))
                    Log.Write("Maintenance: Windows Update " + entry);
                else if (reported++ < 250)
                    Log.Debug("Maintenance: Windows Update " + entry);
            }
            else if (stage == "Chocolatey" && reported < 30 &&
                     (line.StartsWith("Chocolatey upgraded ", StringComparison.OrdinalIgnoreCase) ||
                      line.StartsWith("Chocolatey installed ", StringComparison.OrdinalIgnoreCase) ||
                      line.StartsWith("You have ", StringComparison.OrdinalIgnoreCase) && line.Contains("outdated", StringComparison.OrdinalIgnoreCase)))
            {
                reported++;
                // These messages are package-manager summaries; never log command lines,
                // raw errors, URLs or arbitrary installer output.
                var summary = line.Length > 250 ? line[..250] + " [truncated]" : line;
                Log.Debug("Maintenance: Chocolatey " + summary);
            }
        }
        if (reported >= 250)
            Log.Debug($"Maintenance: {stage} verbose update details capped at 250 lines.");
        return count;
    }
}
