using System.IO.Pipes;

namespace IdleShutdown.AgentApp;

internal static class MachineInputMonitor
{
    private const string Argument = "--machine-input-monitor";
    private const string PipeName = "IdleShutdown";
    private const int PollMilliseconds = 250;
    private const int MinimumNotificationMilliseconds = 500;

    public static bool TryRun(string[] args)
    {
        if (
            args.Length == 0 ||
            !string.Equals(args[0], Argument, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (
            args.Length != 2 ||
            !int.TryParse(args[1], out var expectedSessionId) ||
            expectedSessionId < 0 ||
            Environment.ProcessId <= 0 ||
            System.Diagnostics.Process.GetCurrentProcess().SessionId !=
                expectedSessionId)
        {
            return true;
        }

        uint? previousInputTick = null;
        var lastNotificationTick =
            Environment.TickCount64 - MinimumNotificationMilliseconds;

        while (true)
        {
            if (NativeMethods.TryGetLastInputTick(out var currentInputTick))
            {
                if (
                    previousInputTick.HasValue &&
                    currentInputTick != previousInputTick.Value &&
                    Environment.TickCount64 - lastNotificationTick >=
                        MinimumNotificationMilliseconds)
                {
                    SendInputNotification(expectedSessionId);
                    lastNotificationTick = Environment.TickCount64;
                }

                previousInputTick = currentInputTick;
            }

            Thread.Sleep(PollMilliseconds);
        }
    }

    private static void SendInputNotification(int sessionId)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(
                ".",
                PipeName,
                PipeDirection.Out,
                PipeOptions.Asynchronous);

            pipe.Connect(750);

            using var writer = new StreamWriter(pipe)
            {
                AutoFlush = true
            };

            writer.WriteLine($"MACHINE_INPUT:{sessionId}");
        }
        catch
        {
            // The service may be restarting. The next physical input event
            // will be reported after its pipe listener is available again.
        }
    }
}
