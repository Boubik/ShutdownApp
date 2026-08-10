using System.Reflection;
using System.Runtime.InteropServices;

namespace IdleShutdown.ServiceApp;

internal sealed record SystemActivityStatus(
    bool ShouldDefer,
    string? Reason,
    string? ProbeError = null);

internal static class SystemActivityGuard
{
    private const uint EsSystemRequired = 0x00000001;
    private const uint EsDisplayRequired = 0x00000002;

    public static SystemActivityStatus GetStatus()
    {
        if (HasActiveWindowsPowerRequest(out var powerError))
        {
            return new SystemActivityStatus(
                true,
                "an active Windows system/display power request");
        }

        if (IsWindowsUpdateBusy(out var updateError))
        {
            return new SystemActivityStatus(
                true,
                "Windows Update installation or uninstallation");
        }

        if (IsWindowsInstallerBusy(out var installerError))
        {
            return new SystemActivityStatus(
                true,
                "an active Windows Installer transaction");
        }

        var errors = new[]
            {
                powerError,
                updateError,
                installerError
            }
            .Where(error => !string.IsNullOrWhiteSpace(error));

        var combinedError = string.Join("; ", errors);

        return new SystemActivityStatus(
            false,
            null,
            string.IsNullOrWhiteSpace(combinedError)
                ? null
                : combinedError);
    }

    private static bool HasActiveWindowsPowerRequest(
        out string? error)
    {
        try
        {
            uint executionState = 0;

            var status = CallNtPowerInformation(
                PowerInformationLevel.SystemExecutionState,
                IntPtr.Zero,
                0,
                out executionState,
                sizeof(uint));

            if (status != 0)
            {
                error = $"CallNtPowerInformation returned {status}";
                return false;
            }

            error = null;

            return (
                executionState &
                (EsSystemRequired | EsDisplayRequired)
            ) != 0;
        }
        catch (Exception ex)
        {
            error = $"power-request probe failed: {ex.Message}";
            return false;
        }
    }

    private static bool IsWindowsUpdateBusy(
        out string? error)
    {
        object? installer = null;

        try
        {
            var installerType = Type.GetTypeFromProgID(
                "Microsoft.Update.Installer",
                throwOnError: false);

            if (installerType is null)
            {
                error = "Microsoft.Update.Installer is unavailable";
                return false;
            }

            installer = Activator.CreateInstance(installerType);

            if (installer is null)
            {
                error = "Microsoft.Update.Installer could not be created";
                return false;
            }

            var value = installerType.InvokeMember(
                "IsBusy",
                BindingFlags.GetProperty,
                binder: null,
                target: installer,
                args: null);

            error = null;
            return value is true;
        }
        catch (Exception ex)
        {
            error = $"Windows Update probe failed: {ex.Message}";
            return false;
        }
        finally
        {
            if (installer is not null && Marshal.IsComObject(installer))
            {
                Marshal.FinalReleaseComObject(installer);
            }
        }
    }

    private static bool IsWindowsInstallerBusy(
        out string? error)
    {
        try
        {
            if (!Mutex.TryOpenExisting(
                    @"Global\_MSIExecute",
                    out var mutex))
            {
                error = null;
                return false;
            }

            using (mutex)
            {
                try
                {
                    if (!mutex.WaitOne(0))
                    {
                        error = null;
                        return true;
                    }

                    mutex.ReleaseMutex();
                    error = null;
                    return false;
                }
                catch (AbandonedMutexException)
                {
                    mutex.ReleaseMutex();
                    error = null;
                    return false;
                }
            }
        }
        catch (Exception ex)
        {
            error = $"Windows Installer probe failed: {ex.Message}";
            return false;
        }
    }

    private enum PowerInformationLevel
    {
        SystemExecutionState = 16
    }

    [DllImport("powrprof.dll")]
    private static extern uint CallNtPowerInformation(
        PowerInformationLevel informationLevel,
        IntPtr inputBuffer,
        uint inputBufferSize,
        out uint outputBuffer,
        uint outputBufferSize);
}
