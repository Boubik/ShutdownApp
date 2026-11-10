# Idle Shutdown 2.0 beta

Stable 1.3.x stays on main; development is on develop/2.0. VERSION begins at 2.0.0-beta.1.

Every branch push runs tests and a Windows x64 Chocolatey build and uploads a GitHub Actions artifact. Branch pushes never create releases. Push a v2.0.0-beta.N tag on a tested commit to trigger a GitHub prerelease (not latest). Increment VERSION for each beta.

Maintenance is opt-in via MaintenanceEnabled. ChocolateySource defaults to null, using the PC's configured sources. For UJEP tests, set it to https://choco.cpto.ujep.cz/nuget/choco-internal/.

Use a lab PC and DryRun for initial timer tests. No forced reboot is part of the design, but package scripts or Windows Update policies outside this app may schedule one.
