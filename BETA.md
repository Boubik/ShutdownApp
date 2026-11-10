# Idle Shutdown 2.0 beta

Stable 1.3.x stays on main; development is on develop/2.0. VERSION begins at 2.0.0-beta.1.

Every push to develop/2.0 runs tests and a Windows x64 Chocolatey build and uploads a GitHub Actions artifact. After the first successful build of a new VERSION (2.0.0-beta.N), GitHub Actions automatically creates its tag and GitHub prerelease (never Latest). Further commits with the same VERSION build normally but do not overwrite the published prerelease. Increment VERSION before creating the next prerelease. Do not push beta tags manually.

Maintenance is opt-in via MaintenanceEnabled. ChocolateySource defaults to null, using the PC's configured sources. For UJEP tests, set it to https://choco.cpto.ujep.cz/nuget/choco-internal/.

Use a lab PC and DryRun for initial timer tests. No forced reboot is part of the design, but package scripts or Windows Update policies outside this app may schedule one.


## Prerelease diagnostics (2.0.0-beta.2 and later)

The build automatically defines PRERELEASE_VERBOSE for any VERSION containing
a prerelease suffix (for example 2.0.0-beta.2 or 2.0.0-rc.1).
Stable VERSION values (for example 2.0.0) do not enable it.
No setting or interactive prompt is required.

Diagnostics are **not** DryRun: DryRun=true still disables actual shutdown.
For an end-to-end maintenance test, use DryRun=false on an isolated machine.

View the rotating service/agent log here:

    C:\ProgramData\IdleShutdown\IdleShutdown.log

The beta log includes:
- VERBOSE PRERELEASE marker on service startup.
- Windows Update scan totals, selected/skipped updates (with reason), downloads,
  per-update installation result, and whether a later reboot is required.
- Chocolatey selected source type (default/custom), installed-package
  inventory size, exact package/version changes, final result code, and elapsed
  time. Missing Chocolatey is recorded and skipped.
- Maintenance completion, cancellation and duration-limit decisions.

Raw Chocolatey installer stdout/stderr is intentionally not copied into the
application log because third-party installer scripts may print passwords or
repository credentials. For deeper Chocolatey errors, inspect the normal
Chocolatey log under C:\ProgramData\chocolatey\logs\chocolatey.log.

## Short maintenance test on Windows (PowerShell as Administrator)

Publish the beta .nupkg to your private test Chocolatey feed first, then run:

    choco upgrade idle-shutdown -y --pre --version=2.0.0-beta.2 --source="https://choco.cpto.ujep.cz/nuget/choco-internal/" --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:https://choco.cpto.ujep.cz/nuget/choco-internal/ /MaintenanceMaxMinutes:30'"

Use this only on a disposable test PC: after one idle minute plus a 30-second
warning, the computer can begin installing updates and ultimately shut down.
Windows Update policies and third-party package installers can independently
require or initiate a reboot, which the application cannot fully control.

## 2.0.0-beta.3 log-reader compatibility

- Service and agent log appends now allow simultaneous readers such as
  `Get-Content ... -Wait` instead of requiring an exclusive file handle.
- The maintenance coordinator emits a once-per-minute prerelease heartbeat
  while update installation is in progress, including elapsed minutes.
- The initial countdown expiry starts maintenance; it is not a forced
  shutdown deadline. Windows Update may need several minutes or longer.
- Do not restart the IdleShutdown service or upgrade the package while
  Windows Update / Chocolatey is still processing installers.
- Runtime testing of concurrent readers and the Windows Update driver
  path is still required even after GitHub Actions tests succeed.

## Beta 2.0.0-beta.5 — parallel maintenance and persistent warning

Windows Update and Chocolatey are started independently when the warning expires. Windows Update can scan/download while Chocolatey performs upgrades. An OS-wide named semaphore serializes the actual Windows Update and Chocolatey installation phases to avoid MSI conflicts. Shutdown is permitted only after both running stages exit and the existing safety guards pass. If the user returns, the pending shutdown is cancelled; installers already running are allowed to finish.

While maintenance is enabled, the interactive warning stays visible at 00:00 with a localized message that shutdown is waiting for Windows Update/Chocolatey. The Continue Working button and verified keyboard/mouse cancellation remain available. When maintenance is disabled, the original countdown behavior is retained. This is an initial status message, not an individual update-progress percentage.

**Do not deploy a new beta on a machine actively updating:** Chocolatey installation of ShutdownApp restarts the service and may abandon a running child update process. Test a new beta only when the prior maintenance cycle has ended.

PowerShell sample for beta.5 once the .nupkg is uploaded to the internal test feed:

```powershell
$repo = 'https://choco.cpto.ujep.cz/nuget/choco-internal/'
choco upgrade idle-shutdown -y --pre --version=2.0.0-beta.5 `
  --source="$repo" `
  --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:$repo /MaintenanceMaxMinutes:30'"
```


## Beta 2.0.0-beta.6 — safe self-upgrade and uninstall

The automatic idle-maintenance Chocolatey runner continues to use
`choco upgrade all --except=idle-shutdown`. Self-updating the binaries of
the service currently orchestrating updates is unsafe. Upgrade IdleShutdown
as a separate Chocolatey operation (for example through Tactical RMM), not
from its own maintenance job.

Safety protections:
- The service exclusively holds `C:\ProgramData\IdleShutdown\maintenance.lock`
  while a maintenance cycle is running. Chocolatey install/uninstall attempts
  acquire the same lock; if it is unavailable they fail before stopping the
  service or changing installed binaries. Retry later.
- The installer checks for legacy beta 2.0 child updater processes even when
  the previous version has no maintenance lock yet.
- A new payload is fully staged and checked before touching the installed files.
- Existing scheduled tasks are stopped/unregistered, then **all running
  IdleShutdown.Agent.exe processes belonging to the installed path are stopped**
  before stopping the service and replacing binaries.
- Existing application binaries are moved to a backup directory before the
  staged payload is swapped into place.
- Installation failure attempts to restore binaries, previous config, task,
  service and inventory version; logs remain in ProgramData.
- A successful install starts the updated Windows service and agent task again.
- The uninstall script also refuses removal during active maintenance and
  closes running agents before deleting files.

**Limitations / mandatory manual tests:** GitHub Actions builds and PowerShell
syntax checks do not demonstrate that Windows file locks, RDP-session agents,
service stop/start and rollback operate correctly on all target machines.
Test upgrade between two betas with an active agent, RDP sessions and idle
maintenance in a lab before broad deployment. An external administrator who
force-kills the service, a power loss or an installer that schedules its own
reboot can still interrupt ongoing software changes. The service does not
perform hot self-replacement.

### Installing beta.6 with a full test configuration

After uploading the beta.6 GitHub asset to the UJEP Chocolatey test feed:

~~~powershell
$repo = "https://choco.cpto.ujep.cz/nuget/choco-internal/"
choco upgrade idle-shutdown -y --pre --version=2.0.0-beta.6 --source="$repo" --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /PauseWhenFullscreen:true /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:$repo /MaintenanceMaxMinutes:30'"
~~~

If an update is running, the installer refuses to touch the application and
the previous version continues running. Wait for Windows Update/Chocolatey to
finish, then rerun the exact command. Do not manually stop the service during
active maintenance. Use only a dedicated Windows test machine for beta.6.


## Beta 2.0.0-beta.7 — maintenance starts with the warning

Maintenance now begins when the interactive warning is shown, instead of
waiting for the countdown to reach zero. The agent sends WARNING_ACTIVE
and the service starts Windows Update and Chocolatey immediately.
However, WARNING_ACTIVE alone is NOT permission to shut down:
SHUTDOWN_IDLE confirmation after the countdown is still required.
The previous multi-session grace period remains effective.

On locked or no-user machines, maintenance begins at the beginning of
the silent grace period. Shutdown occurs only after both the grace
period and all active update stages have completed. The warning remains
visible at 00:00 while waiting for updates.

Cancelling a warning never force-kills an in-flight installer.
The WUA child checks a per-cycle cancellation marker before download and
installation, including after waiting on the Chocolatey installation gate.
Chocolatey checks for cancellation after inventory and before upgrading.
Further warning cycles can start only after a cancelled cycle exits.

The automatic Chocolatey upgrade intentionally excludes idle-shutdown.
A beta or stable release appearing on the feed does not automatically
self-update the running service; self-upgrade needs separate coordination.

### Full test install command

~~~powershell
$repo = "https://choco.cpto.ujep.cz/nuget/choco-internal/"
choco upgrade idle-shutdown -y --pre --version=2.0.0-beta.7 --source="$repo" --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /PauseWhenFullscreen:true /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:$repo /MaintenanceMaxMinutes:30'"
~~~

Upload the .nupkg to the internal test feed first. Use a dedicated
Windows lab PC. Do not try to upgrade while maintenance is running.


## 2.0.0-beta.8 — safe opt-in self-update

A SYSTEM Scheduled Task named 'Idle Shutdown Self Update' runs at Windows
startup and daily at 03:00. It uses a versioned PowerShell helper stored in
the ACL-restricted ProgramData/IdleShutdown/Updater directory (not the
application executable folder that Chocolatey replaces).

Self-update is opt-in (SelfUpdateEnabled defaults to false).
SelfUpdateAllowPrerelease defaults to false; set it true only to test
beta.8 -> beta.9. Once a stable version is installed, the helper ignores
prereleases even if the old beta testing configuration remains enabled.

Before an upgrade the helper verifies **no logged-in console, locked or RDP
sessions**, checks the exclusive maintenance lock, and holds a separate
selfupdate.lock that prevents IdleShutdown from starting maintenance or
shutting down. Chocolatey then performs the existing guarded service/agent
swap, with rollback on errors. The helper does not force restart Windows.
The helper skips when a user is logged in; this is intentionally strict.

Because beta.7 does not contain the task, you MUST install beta.8 once
manually. Once beta.9 is available in the internal feed, reboot the
test machine, leave it at the sign-in screen (no users logged in),
and inspect the self-update log after the upgrade. A daily retry also
occurs at 03:00 for always-on machines with no logged-on users.

Full beta.8 test configuration (after uploading .nupkg to the feed):

~~~powershell
$repo = 'https://choco.cpto.ujep.cz/nuget/choco-internal/'
choco upgrade idle-shutdown -y --pre --version=2.0.0-beta.8 --source="$repo" --params="'/IdleMinutes:1 /WarningSeconds:30 /CheckIntervalSeconds:5 /PauseWhenFullscreen:true /DryRun:false /MaintenanceEnabled:true /WindowsUpdateEnabled:true /DriverUpdatesEnabled:true /OptionalDriversEnabled:true /ChocolateyEnabled:true /ChocolateySource:$repo /MaintenanceMaxMinutes:30 /SelfUpdateEnabled:true /SelfUpdateAllowPrerelease:true'"

Get-ScheduledTask -TaskName 'Idle Shutdown Self Update'
Get-Content "$env:ProgramData\IdleShutdown\SelfUpdate.log" -Tail 50 -Wait
~~~

**Important:** Self-update is not performed inside the service's
`choco upgrade all` task (idle-shutdown stays excluded). Self-update
happens separately at startup or unattended daily time. If a user logs in
just after the helper's final session check, an in-flight upgrade will be
allowed to finish to avoid corrupting the installation; it will not
be forcibly terminated. Automatic CI does not emulate a Windows restart
with a real user login or a failed package rollback. Verify on a lab PC.
