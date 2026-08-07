namespace IdleShutdown.AgentApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();

        using var mutex = new Mutex(
            true,
            $"Local\\IdleShutdown.Agent.{Environment.UserName}",
            out var createdNew);

        if (!createdNew)
        {
            return;
        }

        Application.Run(new AgentApplicationContext());
    }
}
