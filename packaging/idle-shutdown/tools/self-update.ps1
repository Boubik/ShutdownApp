#requires -Version 5.1
# Detached SYSTEM scheduled-task helper for unattended, noninteractive upgrades.
# Never run this from the IdleShutdown Windows service itself.
$ErrorActionPreference = 'Stop'
$dataDir = Join-Path $env:ProgramData 'IdleShutdown'
$configPath = Join-Path $dataDir 'config.json'
$selfLockPath = Join-Path $dataDir 'selfupdate.lock'
$maintenanceLockPath = Join-Path $dataDir 'maintenance.lock'
$logPath = Join-Path $dataDir 'SelfUpdate.log'
$script:UpgradeVerificationFailed = $false

# Same retention policy as the primary service log:
# 2 MiB active file, .1 and .2 archives (about 6 MiB total).
$maxLogBytes = 2MB

function Write-SelfUpdateLog {
    param([string]$Message)
    $line = '[{0}] [SELF-UPDATE] {1}' -f (Get-Date -Format 'yyyy-MM-dd HH:mm:ss'), ($Message -replace '[\r\n]', ' ')
    try {
        New-Item -Path $dataDir -ItemType Directory -Force | Out-Null
        $stream = [System.IO.FileStream]::new(
            $logPath, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite,
            ([IO.FileShare]::ReadWrite -bor [IO.FileShare]::Delete))
        try {
            $lineSize = [Text.Encoding]::UTF8.GetByteCount($line) + 2
            if ($stream.Length + $lineSize -gt $maxLogBytes) {
                try {
                    if (Test-Path -LiteralPath "$logPath.2") {
                        Remove-Item -LiteralPath "$logPath.2" -Force -ErrorAction Stop
                    }
                    if (Test-Path -LiteralPath "$logPath.1") {
                        Move-Item -LiteralPath "$logPath.1" -Destination "$logPath.2" -ErrorAction Stop
                    }
                    $stream.Position = 0
                    $archive = [IO.FileStream]::new(
                        "$logPath.1", [IO.FileMode]::CreateNew,
                        [IO.FileAccess]::Write, [IO.FileShare]::Read)
                    try { $stream.CopyTo($archive) } finally { $archive.Dispose() }
                    $stream.SetLength(0)
                }
                catch {
                    # Do not let a reader holding an old archive interfere
                    # with the updater. Bound the active log in this case.
                    $stream.SetLength(0)
                }
            }
            $stream.Position = $stream.Length
            $writer = [IO.StreamWriter]::new($stream, [Text.UTF8Encoding]::new($false))
            try {
                $writer.WriteLine($line)
                $writer.Flush()
            }
            finally { $writer.Dispose() }
        }
        finally { $stream.Dispose() }
    }
    catch {
        # Logging must never cause an unsafe upgrade or an unhandled task crash.
    }
}

function Test-NoLoggedOnUsers {
    # WTS detects console AND RDP sessions, including disconnected and locked
    # sessions. Fail closed when querying any session is not possible.
    if (-not ('IdleShutdownWtsSessionCheck' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
public static class IdleShutdownWtsSessionCheck {
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Info {
        public int SessionId;
        [MarshalAs(UnmanagedType.LPWStr)] public string WinStationName;
        public int State;
    }
    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSEnumerateSessions(
        IntPtr server, int reserved, int version, out IntPtr sessions, out int count);
    [DllImport("wtsapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool WTSQuerySessionInformation(
        IntPtr server, int sessionId, int infoClass, out IntPtr buffer, out int bytes);
    [DllImport("wtsapi32.dll")]
    private static extern void WTSFreeMemory(IntPtr buffer);
    public static bool HasLoggedOnUsers() {
        IntPtr sessions;
        int count;
        if (!WTSEnumerateSessions(IntPtr.Zero, 0, 1, out sessions, out count))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        try {
            int size = Marshal.SizeOf(typeof(Info));
            for (int i = 0; i < count; i++) {
                var entry = (Info)Marshal.PtrToStructure(
                    IntPtr.Add(sessions, i * size), typeof(Info));
                IntPtr buffer;
                int bytes;
                // WTSUserName = 5. Any non-empty username means an interactive
                // or disconnected logged-on user, so the upgrade must wait.
                if (!WTSQuerySessionInformation(
                    IntPtr.Zero, entry.SessionId, 5, out buffer, out bytes))
                    throw new Win32Exception(Marshal.GetLastWin32Error());
                try {
                    if (bytes > 2 && !String.IsNullOrWhiteSpace(Marshal.PtrToStringUni(buffer)))
                        return true;
                }
                finally {
                    if (buffer != IntPtr.Zero) WTSFreeMemory(buffer);
                }
            }
            return false;
        }
        finally {
            if (sessions != IntPtr.Zero) WTSFreeMemory(sessions);
        }
    }
}
'@ -ErrorAction Stop
    }
    return -not [IdleShutdownWtsSessionCheck]::HasLoggedOnUsers()
}

function Get-InstalledVersion {
    param([string]$Choco)
    $lines = & $Choco list --limit-output 2>$null
    if ($LASTEXITCODE -ne 0) { throw "Chocolatey inventory failed (code $LASTEXITCODE)." }
    foreach ($line in $lines) {
        if ($line -match '^idle-shutdown\|([^|]+)$') { return $Matches[1].Trim() }
    }
    return $null
}


# Read REAL installed binaries; Chocolatey's package ledger is not enough to
# establish success after an unexpected Windows restart.
function Get-DeployedBinaryVersion {
    param([string]$Name)
    $exe = Join-Path (Join-Path $env:ProgramFiles 'IdleShutdown') $Name
    if (-not (Test-Path -LiteralPath $exe)) { return '[missing]' }
    try {
        $v = [string](Get-Item -LiteralPath $exe -ErrorAction Stop).VersionInfo.ProductVersion
        if ($v -match '^([0-9]+[.][0-9]+[.][0-9]+)(?:[+.-].*)?$') {
            return $Matches[1]
        }
        return '[unknown]'
    }
    catch { return '[unreadable]' }
}

function Get-SelfUpdateTaskVersion {
    try {
        $task = Get-ScheduledTask -TaskName 'Idle Shutdown Self Update' -ErrorAction Stop
        $action = @($task.Actions) | Select-Object -First 1
        if ([string]$action.Arguments -match 'SelfUpdate-([0-9]+[.][0-9]+[.][0-9]+)[.]ps1') {
            return $Matches[1]
        }
        return '[unknown]'
    }
    catch { return '[missing]' }
}

function Get-UpgradeInconsistencies {
    param([string]$ExpectedVersion, [psobject]$CurrentConfig)
    $reasons = @()
    $agent = Get-DeployedBinaryVersion 'IdleShutdown.Agent.exe'
    $service = Get-DeployedBinaryVersion 'IdleShutdown.Service.exe'
    if ($agent -ne $ExpectedVersion) { $reasons += "agent=$agent" }
    if ($service -ne $ExpectedVersion) { $reasons += "service=$service" }

    # 2.0.0/2.0.1 did not contain ConfigVersion and used the 2.0.0
    # task script even when the application itself had version 2.0.1.
    if ($ExpectedVersion -match '^[0-9]+[.][0-9]+[.][0-9]+$' -and
        [version]$ExpectedVersion -ge [version]'2.0.2') {
        if ([string]$CurrentConfig.ConfigVersion -ne $ExpectedVersion) {
            $reasons += "configVersion=$([string]$CurrentConfig.ConfigVersion)"
        }
        $helperVersion = Get-SelfUpdateTaskVersion
        if ($helperVersion -ne $ExpectedVersion) {
            $reasons += "taskHelper=$helperVersion"
        }
    }
    return @($reasons)
}

function Test-RebootPending {
    foreach ($path in @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending',
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired'
    )) {
        if (Test-Path -LiteralPath $path) { return $true }
    }
    return $false
}

if (-not (Test-Path -LiteralPath $configPath)) { return }
try {
    $config = Get-Content -LiteralPath $configPath -Raw -ErrorAction Stop | ConvertFrom-Json -ErrorAction Stop
    if ($config.SelfUpdateEnabled -ne $true) { return }
    if ($config.DryRun -eq $true) {
        Write-SelfUpdateLog 'DryRun enabled; automatic self-upgrade skipped.'
        return
    }
    $chocoHome = $env:ChocolateyInstall
    if ([string]::IsNullOrWhiteSpace($chocoHome)) {
        $chocoHome = Join-Path $env:ProgramData 'chocolatey'
    }
    $choco = Join-Path $chocoHome 'bin\choco.exe'
    if (-not (Test-Path -LiteralPath $choco)) {
        Write-SelfUpdateLog 'Chocolatey not installed; self-upgrade skipped.'
        return
    }
}
catch {
    Write-SelfUpdateLog ('Preflight failed: ' + $_.Exception.Message)
    return
}

# Only the helper owns this lock. The Windows service checks it before
# beginning maintenance and before any final shutdown. It stays held while
# the external Chocolatey installer stops/restarts the service.
$selfLock = $null
try {
    $selfLock = [IO.FileStream]::new(
        $selfLockPath, [IO.FileMode]::OpenOrCreate,
        [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
}
catch {
    # Another helper is running, or the machine does not permit safe locking.
    return
}

try {
    if (-not (Test-NoLoggedOnUsers)) {
        Write-SelfUpdateLog 'Skipped: one or more console/RDP users are still logged on.'
        return
    }
    if (Test-RebootPending) {
        Write-SelfUpdateLog 'Skipped: Windows reboot is pending; postpone self-update.'
        return
    }

    # A conservative extra check for sessions that may not have fully
    # completed interactive logon enumeration.
    if (@(Get-Process explorer -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-SelfUpdateLog 'Skipped: explorer.exe still running.'
        return
    }

    try {
        $probe = [IO.FileStream]::new(
            $maintenanceLockPath, [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    }
    catch {
        Write-SelfUpdateLog 'Skipped: Windows Update or Chocolatey maintenance holds safety guard.'
        return
    }

    $installedCurrent = Get-InstalledVersion -Choco $choco
    if (-not $installedCurrent) {
        Write-SelfUpdateLog 'No Chocolatey idle-shutdown inventory found; refusing automatic repair.'
        return
    }
    $repairReasons = @()
    if ($installedCurrent -match '^[0-9]+[.][0-9]+[.][0-9]+$') {
        $repairReasons = @(Get-UpgradeInconsistencies -ExpectedVersion $installedCurrent -CurrentConfig $config)
    }
    # Policy: automatic self-update NEVER installs prerelease builds,
    # regardless of what is currently installed or what a feed advertises.
    $source = [string]$config.ChocolateySource
    if ($source -and $source -notmatch '^https?://[^ ]+$') {
        Write-SelfUpdateLog 'Invalid custom Chocolatey source; self-upgrade skipped.'
        return
    }

    $queryArgs = @('outdated', 'idle-shutdown', '--limit-output')
    if ($source) { $queryArgs += @('--source', $source) }

    $output = & $choco @queryArgs 2>$null
    $status = $LASTEXITCODE
    if ($status -notin @(0, 2)) {
        Write-SelfUpdateLog ("Chocolatey outdated check failed (code $status).")
        return
    }

    $targetVersion = $null
    $currentVersion = $null
    foreach ($line in $output) {
        if ($line -match '^idle-shutdown\|([^|]+)\|([^|]+)(?:\||$)') {
            $currentVersion = $Matches[1].Trim()
            $targetVersion = $Matches[2].Trim()
            break
        }
    }
    $forceRepair = $false
    if (-not $targetVersion) {
        if ($repairReasons.Count -eq 0) {
            Write-SelfUpdateLog 'No newer stable idle-shutdown package available; files agree.'
            return
        }
        $targetVersion = $installedCurrent
        $forceRepair = $true
        Write-SelfUpdateLog ("INCOMPLETE previous installation: " +
            ($repairReasons -join ', ') + "; requesting repair of stable $targetVersion.")
    }
    # The remote feed may be misconfigured. This strict allow-list ensures
    # prerelease versions are NEVER eligible for automatic self-update.
    if ($targetVersion -notmatch '^[0-9]+[.][0-9]+[.][0-9]+(?:[.][0-9]+)?$') {
        Write-SelfUpdateLog ("Skipped non-stable upgrade target: $targetVersion")
        return
    }

    # Revalidate just before touching running services. If the machine became
    # occupied, no upgrade starts. Never terminate a running WUA installer.
    if (-not (Test-NoLoggedOnUsers) -or
        @(Get-Process explorer -ErrorAction SilentlyContinue).Count -gt 0) {
        Write-SelfUpdateLog 'Upgrade postponed: user logged on during update check.'
        return
    }
    try {
        $probe = [IO.FileStream]::new(
            $maintenanceLockPath, [IO.FileMode]::OpenOrCreate,
            [IO.FileAccess]::ReadWrite, [IO.FileShare]::None)
        $probe.Dispose()
    }
    catch {
        Write-SelfUpdateLog 'Upgrade postponed: idle maintenance started during update check.'
        return
    }

    if (-not $forceRepair) {
        Write-SelfUpdateLog ("Stable upgrade available: $currentVersion -> $targetVersion; starting detached Chocolatey installer.")
    }
    # Pin the exact verified stable version, never using --pre.
    $upgradeArgs = @('upgrade', 'idle-shutdown', '-y', '--no-progress',
                     "--version=$targetVersion")
    if ($forceRepair) { $upgradeArgs += '--force' }
    if ($source) { $upgradeArgs += @('--source', $source) }

    # This is a separate SYSTEM task. Chocolatey writes its detailed output
    # to its own log; persist the start so interrupted upgrades are visible.
    Write-SelfUpdateLog ("Chocolatey installer invoked; target=$targetVersion; repair=$forceRepair.")
    & $choco @upgradeArgs *> $null
    $upgradeExit = $LASTEXITCODE

    $installed = $null
    try { $installed = Get-InstalledVersion -Choco $choco }
    catch { Write-SelfUpdateLog ('Inventory verification failed: ' + $_.Exception.Message) }
    $actualConfig = $null
    try { $actualConfig = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json }
    catch { Write-SelfUpdateLog ('Config verification failed: ' + $_.Exception.Message) }
    $agent = Get-DeployedBinaryVersion 'IdleShutdown.Agent.exe'
    $service = Get-DeployedBinaryVersion 'IdleShutdown.Service.exe'
    $helper = Get-SelfUpdateTaskVersion
    $configVersion = [string]$actualConfig.ConfigVersion
    $verified = ($upgradeExit -in @(0, 3010)) -and
        ($installed -eq $targetVersion) -and
        ($agent -eq $targetVersion) -and
        ($service -eq $targetVersion) -and
        ($configVersion -eq $targetVersion) -and
        ($helper -eq $targetVersion)
    if ($verified) {
        Write-SelfUpdateLog ("VERIFIED complete: $targetVersion; binaries/config/task match; exitCode=$upgradeExit.")
    }
    else {
        Write-SelfUpdateLog ("INCOMPLETE: choco=$installed; agent=$agent; service=$service; " +
            "config=$configVersion; task=$helper; target=$targetVersion; exitCode=$upgradeExit. " +
            'Retry at next unattended start; inspect Chocolatey log for errors.')
        $script:UpgradeVerificationFailed = $true
    }
}
catch {
    Write-SelfUpdateLog ('Self-upgrade failed safely: ' + $_.Exception.Message)
    $script:UpgradeVerificationFailed = $true
}
finally {
    if ($selfLock) { $selfLock.Dispose() }
}
if ($script:UpgradeVerificationFailed) {
    # Nonzero task result when the reported upgrade did not fully install.
    exit 1
}
