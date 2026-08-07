using System.ServiceProcess;

namespace IdleShutdown.ServiceApp;

internal static class Program
{
    private static void Main()
    {
        ServiceBase.Run(new IdleShutdownService());
    }
}
