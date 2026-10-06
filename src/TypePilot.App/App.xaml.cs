using System.Windows;

namespace TypePilot.App;

public partial class App : Application
{
    private Mutex? _instance;
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--ai-smoke"))
        {
            _ = SmokeChecks.RunAiAsync(this);
            return;
        }
        var smoke = e.Args.Contains("--ui-smoke") || e.Args.Contains("--ui-demo");
        _instance = new Mutex(true, "Local\\TypePilot.SingleInstance", out var first);
        if (!first && !smoke)
        {
            NativeMethods.BroadcastShow();
            Shutdown(); return;
        }
        var window = new MainWindow(smoke, e.Args.Contains("--ui-demo"));
        MainWindow = window;
        window.Show();
    }
    protected override void OnExit(ExitEventArgs e)
    {
        _instance?.Dispose();
        base.OnExit(e);
    }
}
