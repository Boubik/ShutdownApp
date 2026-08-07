using System.IO.Pipes;

namespace IdleShutdown.AgentApp;

internal sealed class AgentApplicationContext : ApplicationContext
{
    private const string PipeName = "IdleShutdown";

    private readonly System.Windows.Forms.Timer _timer;

    private bool _warningVisible;
    private bool _presentationDeferralLogged;

    public AgentApplicationContext()
    {
        var config = AppConfig.Load();

        _timer = new System.Windows.Forms.Timer
        {
            Interval =
                Math.Max(
                    1,
                    config.CheckIntervalSeconds) * 1000
        };

        _timer.Tick += TimerOnTick;
        _timer.Start();

        Log.Write(
            $"Agent started as " +
            $"{Environment.UserDomainName}\\" +
            $"{Environment.UserName}.");
    }

    private void TimerOnTick(
        object? sender,
        EventArgs e)
    {
        if (
            _warningVisible ||
            NativeMethods.IsWorkstationLocked())
        {
            return;
        }

        var config =
            AppConfig.Load();

        var idle =
            NativeMethods.GetIdleTime();

        if (
            idle <
            TimeSpan.FromMinutes(
                config.IdleMinutes))
        {
            _presentationDeferralLogged = false;
            return;
        }

        if (
            config.PauseWhenFullscreen &&
            PowerActivity.ShouldPauseForPresentation())
        {
            if (!_presentationDeferralLogged)
            {
                Log.Write(
                    "Idle timeout reached, but shutdown was " +
                    "deferred by an active power request or " +
                    "a fullscreen foreground window.");

                _presentationDeferralLogged = true;
            }

            return;
        }

        _warningVisible = true;
        _timer.Stop();

        try
        {
            var inputAtStart =
                NativeMethods.GetIdleTime();

            using var dialog =
                new WarningForm(
                    config.WarningSeconds,
                    inputAtStart);

            var result =
                dialog.ShowDialog();

            if (result == DialogResult.OK)
            {
                Log.Write(
                    "Warning expired without user activity; " +
                    "requesting shutdown.");

                SendShutdownRequest();
            }
            else
            {
                Log.Write(
                    "Shutdown warning cancelled by user " +
                    "activity or button.");
            }
        }
        finally
        {
            _warningVisible = false;
            _timer.Start();
        }
    }

    private static void SendShutdownRequest()
    {
        Exception? lastException = null;

        // A short retry covers the moment when the service is closing the
        // previous connection and creating the next pipe instance.
        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                Log.Write(
                    $"Connecting to named pipe '{PipeName}', " +
                    $"attempt {attempt}/3.");

                using var pipe =
                    new NamedPipeClientStream(
                        ".",
                        PipeName,
                        PipeDirection.Out,
                        PipeOptions.Asynchronous);

                pipe.Connect(5000);

                using var writer =
                    new StreamWriter(pipe)
                    {
                        AutoFlush = true
                    };

                writer.WriteLine(
                    "SHUTDOWN_IDLE");

                Log.Write(
                    "Shutdown request was successfully sent " +
                    "to the Windows service.");

                return;
            }
            catch (Exception ex)
            {
                lastException = ex;

                Log.Write(
                    $"Named pipe attempt {attempt}/3 failed: " +
                    $"{ex.GetType().Name}; " +
                    $"HResult=0x{ex.HResult:X8}; " +
                    $"{ex.Message}");

                if (attempt < 3)
                {
                    Thread.Sleep(750);
                }
            }
        }

        var errorText =
            lastException is null
                ? "Unknown communication error."
                : $"{lastException.GetType().Name}: " +
                  $"{lastException.Message}";

        Log.Write(
            $"Unable to contact service after 3 attempts: " +
            $"{errorText}");

        MessageBox.Show(
            "Nepodařilo se kontaktovat službu " +
            "automatického vypnutí.\r\n\r\n" +
            "Počítač nebude vypnut.\r\n\r\n" +
            $"Technický detail: {errorText}",
            "Automatické vypnutí",
            MessageBoxButtons.OK,
            MessageBoxIcon.Error);
    }
}
