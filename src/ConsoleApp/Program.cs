using System.Diagnostics;
using DiagnosticExplorer;

namespace ConsoleApp;

internal static class Program
{
    private static async Task Main(string[] args)
    {
        if (args.Length != 1)
        {
            await Console.Error.WriteLineAsync("Usage: ConsoleApp <diagnostics-hub-url>");
            return;
        }

        Trace.Listeners.Add(new ConsoleTraceListener());
        DiagnosticHostingService.Start(args[0]);

        Console.WriteLine("Diagnostics started. Press Enter to stop.");
        Console.ReadLine();
        await DiagnosticHostingService.Stop();
    }
}
