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
