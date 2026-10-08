using FlashGuard.UI;

namespace FlashGuard;

static class Program
{
    const string InstanceName = "FlashGuard-7d1f0c2a";

    [STAThread]
    static int Main(string[] args)
    {
        if (args.Contains("--selftest", StringComparer.OrdinalIgnoreCase))
        {
            ApplicationConfiguration.Initialize();
            return SelfTest.Run();
        }

        using var mutex = new Mutex(true, $@"Local\{InstanceName}", out bool first);
        using var signal = new EventWaitHandle(false, EventResetMode.AutoReset, $@"Local\{InstanceName}-show");
        if (!first)
        {
            // Already running: ask that instance to open its settings instead.
            signal.Set();
            return 0;
        }

        ApplicationConfiguration.Initialize();
        Application.ThreadException += (_, e) => Log.Write($"UI exception: {e.Exception}");
        AppDomain.CurrentDomain.UnhandledException += (_, e) => Log.Write($"fatal: {e.ExceptionObject}");

        bool quiet = args.Contains("--background", StringComparer.OrdinalIgnoreCase);
        var app = new TrayApp(quiet);
        var wait = ThreadPool.RegisterWaitForSingleObject(signal, (_, _) => app.ShowSettings(), null, Timeout.Infinite, executeOnlyOnce: false);
        Application.Run(app);
        wait.Unregister(null);
        return 0;
    }
}
