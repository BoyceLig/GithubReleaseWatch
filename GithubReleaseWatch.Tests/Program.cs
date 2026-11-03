using System.Windows;

namespace GithubReleaseWatch.Tests;

internal static class Program
{
    private static int _passed, _failed;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Contains("--browser")) System.Diagnostics.Trace.Listeners.Add(new System.Diagnostics.ConsoleTraceListener());
        var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        app.Startup += async (_, _) =>
        {
            try
            {
                DomainChecks.Run();
                await ApiChecks.RunAsync();
                await WindowChecks.RunAsync();
                if (args.Contains("--browser")) await BrowserChecks.RunAsync();
                if (args.Contains("--input-check")) await BrowserInputCheck.RunAsync();
            }
            catch (Exception ex) { _failed++; Console.WriteLine($"FAIL runner: {ex}"); }
            finally
            {
                Console.WriteLine($"RESULT: {_passed} passed, {_failed} failed");
                app.Shutdown(_failed == 0 ? 0 : 1);
            }
        };
        return app.Run();
    }

    internal static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    internal static void Test(string name, Action body) => TestAsync(name, () => { body(); return Task.CompletedTask; }).GetAwaiter().GetResult();

    internal static async Task TestAsync(string name, Func<Task> body)
    {
        try { await body(); _passed++; Console.WriteLine($"PASS {name}"); }
        catch (Exception ex) { _failed++; Console.WriteLine($"FAIL {name}: {ex}"); }
    }
}
