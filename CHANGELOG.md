# Changelog

## 1.3.1

- Makes Windows service/WTS lock and disconnect observations authoritative over an unreliable agent `UNLOCKED` heartbeat.
- Prevents an RDP or driver `SYSTEM`-only execution request such as `Legacy Kernel Caller` from blocking idle shutdown indefinitely.
- Keeps presentation protection for `DISPLAY` execution requests and true-fullscreen foreground windows.
- Makes `PauseWhenFullscreen: false` disable presentation power-request protection in both the agent and the service while retaining Windows Update and MSI protection.

## 1.3.0

- Broadcasts the first warning deadline through the Windows service to every unlocked local and Remote Desktop agent.
- Makes agents that have not independently reached their idle boundary join the shared warning with its remaining time.
- Cancels a shared warning if any participating session reports presentation/fullscreen protection.
- Replaces separate unlocked, locked and no-user timeout settings with one machine-wide `IdleMinutes` value.
- Applies `WarningSeconds` as an additional protection period in every state, with a visible popup only while unlocked.
- Removes `LockedMinutes` and `NoUserMinutes` from the configuration and Chocolatey parameters.

## 1.2.2

- Coordinates warning deadlines across local and Remote Desktop sessions.
- Waits for the latest visible warning countdown before requesting shutdown.
- Cancels every warning and the shared pending shutdown when any session reports user input.

## 1.2.1

- Runs a separate interactive agent in every local and Remote Desktop session.
- Synchronizes input and warning cancellation across user sessions.
- Prevents an idle or incorrectly classified session from shutting down a computer while another unlocked user is active.
- Uses fresh agent heartbeats as a safe fallback when WTS session information is incomplete.

## 1.2.0

- Adds protection for active Windows Update, MSI transactions and Windows power requests.
- Uses an internal cancellable grace period followed by a non-forced shutdown command.
- Adds fail-safe configuration validation and bounded log rotation.
- Improves locked and no-user input monitoring and multi-session shutdown policy.
- Changes the production idle, locked and no-user defaults from 60 to 90 minutes.

## 1.1.5

- Starts the hidden sign-in-screen input monitor by duplicating the existing `winlogon` SYSTEM token in the physical console session.
- Fixes the access-denied failure caused by trying to change the session ID of a newly created SYSTEM token.
- Fixes automatic GitHub releases when the target version tag does not exist yet.

## 1.1.4

- Adds a hidden monitor for physical-console mouse and keyboard input when WTS `LastInputTime` is unavailable.
- Resets locked and no-user timers from the hidden monitor, including immediately after reboot before the first login.
- Adds a cancellable 60-second grace period before locked/no-user shutdowns.
- Adds the initial GitHub Actions build and release workflow.

## 1.1.2

- Shows the running application version unobtrusively in the warning dialog.
- Adds detailed DryRun diagnostics for locked and no-user input monitoring.
- Removes unsupported Chocolatey `<readme>` and `<repository>` metadata that produced CLI warnings.

## 1.1.1

- Resets the no-user timeout when input is detected on the Windows sign-in screen.
- Adds logic tests for idle boundaries, no-user timing, final input checks and session changes.
- Improves WTS input timestamp handling and prevents repeated no-user actions after one timeout.

## 1.1.0

- Adds automatic Czech, English, German and Spanish warning-dialog localization with English fallback.
- Follows the Windows light or dark application theme.
- Uses the root `config.json` as the single packaged default configuration.
- Keeps runtime logs in English independently of the selected UI language.

## 1.0.2

- Resets the locked-session timer from mouse and keyboard input observed by the session agent.
- Starts the agent immediately when Chocolatey installs into an already active user session.
- Improves named-pipe retries, session monitoring and shutdown handling.
- Fixes popup foreground/focus native calls.

## 1.0.1

- Introduces the Windows service and per-user agent architecture.
- Adds idle warnings, locked/no-user shutdown modes and fullscreen/power-request protection.
- Adds Chocolatey installation, upgrade, uninstall and health-check support.
- Adds repeatable macOS/Linux shell and Windows batch build scripts that create the `.nupkg`.
