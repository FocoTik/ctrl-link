using Avalonia;
using Avalonia.Threading;
using CtrlLink.Core;
using Velopack;

namespace CtrlLink.App;

static class Program
{
    [STAThread]
    static int Main(string[] args)
    {
        VelopackApp.Build().Run();   // must run first: handles the installer's install/update/uninstall hooks
        if (args is ["--screenshots", var dir]) return Screenshots.Run(dir);

        using var mutex = new Mutex(true, @"Local\CtrlLink.App", out var first);
        if (!first)
        {
            // Already running: ask that instance to open its flyout (Start menu shortcut, second launch).
            if (OperatingSystem.IsWindows() && EventWaitHandle.TryOpenExisting(ShowEvent, out var show)) show.Set();
            return 0;
        }
        if (OperatingSystem.IsWindows())
        {
            var show = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEvent);
            new Thread(() => { while (show.WaitOne()) Dispatcher.UIThread.Post(() => (Application.Current as App)?.Open()); }) { IsBackground = true }.Start();
        }
        return BuildAvaloniaApp().StartWithClassicDesktopLifetime(args, Avalonia.Controls.ShutdownMode.OnExplicitShutdown);
    }

    const string ShowEvent = @"Local\CtrlLink.App.Show";

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UsePlatformDetect().WithInterFont();

    /// <summary>The single place the engine is created.</summary>
    public static IEngine CreateEngine()
    {
        // Previews and --screenshots use FakeEngine instead; macOS runs get no DPAPI and no clipboard hook.
        return new Engine(new EngineOptions(Paths.State, AppSettings.ReceivedDir ?? Paths.Received, OperatingSystem.IsWindows() ? new DpapiProtector() : new NoProtector()));
    }
}
