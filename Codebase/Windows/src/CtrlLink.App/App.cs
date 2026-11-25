using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using Avalonia.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.Platform;
using Avalonia.Styling;
using Avalonia.Themes.Simple;
using CtrlLink.Core;
using static CtrlLink.App.Ui;

namespace CtrlLink.App;

/// <summary>Tray app: no main window, a tray icon, the flyout, toasts, and the clipboard bridge (Windows only).</summary>
sealed class App : Application
{
    public static bool Headless;

    IEngine engine = null!;
    Shell shell = null!;
    Flyout flyout = null!;
    readonly Toast toast = new();
    TrayIcon tray = null!;
    WinClipboard? clipboard;
    bool paused;

    [SupportedOSPlatformGuard("windows")]
    [MemberNotNullWhen(true, nameof(clipboard))]
    bool Win => clipboard != null && OperatingSystem.IsWindows();
    bool splashShown;

    public override void Initialize()
    {
        Styles.Add(new SimpleTheme());
        RequestedThemeVariant = ThemeVariant.Default;   // follow the system light/dark setting
    }

    public override void OnFrameworkInitializationCompleted()
    {
        base.OnFrameworkInitializationCompleted();
        if (Headless || ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop) return;
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        P = ActualThemeVariant == ThemeVariant.Light ? Palette.LightP : Palette.DarkP;
        ActualThemeVariantChanged += (_, _) =>
        {
            P = ActualThemeVariant == ThemeVariant.Light ? Palette.LightP : Palette.DarkP;
            tray.Icon = TrayImage();
            shell.Render();
        };

        engine = Program.CreateEngine();
        // First run only; installs that already have pairings never see it (and remember that).
        var welcome = !File.Exists(Paths.Welcomed) && engine.Peers.Count == 0;
        shell = new Shell(engine) { CopyClip = Copy, Welcome = welcome, Welcomed = MarkWelcomed };
        if (!welcome) MarkWelcomed();
        flyout = new Flyout(shell);

        var pause = new NativeMenuItem("Pause sync") { ToggleType = NativeMenuItemToggleType.CheckBox };
        pause.Click += (_, _) =>
        {
            paused = !paused;
            pause.IsChecked = paused;
            tray.ToolTipText = paused ? "Ctrl Link (paused)" : "Ctrl Link";
        };
        var menu = new NativeMenu
        {
            Item("Open", Open),
            Item("Pair a device", () => { shell.Go(Tab.Devices, Route.Host); Open(); }),
            new NativeMenuItemSeparator(),
            pause,
            new NativeMenuItemSeparator(),
            Item("Quit", Quit),
        };
        tray = new TrayIcon { Icon = TrayImage(), ToolTipText = "Ctrl Link", Menu = menu, IsVisible = true };
        tray.Clicked += (_, _) => Toggle();
        TrayIcon.SetIcons(this, [tray]);

        engine.Receive += OnReceive;
        engine.Notify += OnNote;
        if (OperatingSystem.IsWindows())
        {
            clipboard = new WinClipboard();
            clipboard.Changed += () => { if (!paused && engine.AutoSend && OperatingSystem.IsWindows() && clipboard.Read() is { } clip) engine.Send(clip); };
        }
        Dispatcher.UIThread.Post(engine.Start);   // Start captures the UI SynchronizationContext; run it once the dispatcher loop is up
        if (welcome) Open();
    }

    static void MarkWelcomed()
    {
        if (File.Exists(Paths.Welcomed)) return;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Paths.Welcomed)!);
            File.WriteAllText(Paths.Welcomed, "");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    static NativeMenuItem Item(string header, Action action)
    {
        var item = new NativeMenuItem(header);
        item.Click += (_, _) => action();
        return item;
    }

    static WindowIcon TrayImage() =>
        new(AssetLoader.Open(new Uri($"avares://CtrlLink/Assets/tray-{(SystemPrefs.LightTaskbar ? "light" : "dark")}.ico")));

    public void Open()
    {
        ReduceMotion = SystemPrefs.ReduceMotion;
        if (!splashShown)
        {
            // Once per launch, while the engine settles: long enough to read, no longer.
            splashShown = true;
            shell.SplashUntil = DateTime.UtcNow.AddMilliseconds(600);
            DispatcherTimer.RunOnce(shell.Render, TimeSpan.FromMilliseconds(620));
        }
        flyout.Open();
    }

    void Toggle()
    {
        // A click on the tray icon deactivates the open flyout first, which hides it; don't reopen it straight away.
        if (flyout.IsVisible || DateTime.UtcNow - flyout.HiddenAt < TimeSpan.FromMilliseconds(400)) flyout.Hide();
        else Open();
    }

    void OnReceive(Received r)
    {
        if (paused || !Win) return;
        engine.MarkWritten(r.Fingerprint);
        // The clipboard gets the PNG file's exact bytes (plus a DIB); mark them too in case the watcher reads them back.
        if (r.Kind == ClipKind.Image && r.Files.Count > 0 && File.Exists(r.Files[0])) engine.MarkWritten(SHA256.HashData(File.ReadAllBytes(r.Files[0])));
        clipboard.Write(r.Kind, r.Text, r.Files);
    }

    void OnNote(Note note)
    {
        (string, string, (string, Action)[]) t = note switch
        {
            Note.Connected n => ($"{n.Name} connected", "Copies now sync both ways.", []),
            Note.Disconnected n => ($"{n.Name} disconnected", "Ctrl Link will reconnect when it is back on this network.", []),
            Note.Clip { Kind: ClipKind.Files } n => ($"Files from {n.Name}", n.Preview, [("Show in folder", ShowReceived)]),
            Note.Clip n => ($"{(n.Kind == ClipKind.Image ? "Image" : "Text")} from {n.Name}", paused ? "Sync is paused, so it was not copied." : n.Preview, []),
            Note.OfferNote { Offer: var o } => ($"{o.PeerName} wants to send {(o.Count == 1 ? "a file" : $"{o.Count} files")}", Size(o.Size),
                [("Accept", () => engine.AcceptOffer(o.Id)), ("Decline", () => engine.RejectOffer(o.Id))]),
            Note.Unpaired n => ($"{n.Name} removed this pairing", "Pair again to keep syncing.", []),
            Note.Unknown n => ($"{n.Name} no longer knows this PC", "Unpair it in Settings and pair again.", []),
            Note.Version n => ($"Update Ctrl Link on {n.Name}", "It runs a version this PC can't talk to.", []),
            Note.Failed n => ("Transfer failed", n.Message, []),
            _ => ("Ctrl Link", note.ToString(), []),
        };
        toast.Show(t.Item1, t.Item2, t.Item3, flyout);
        if (note is Note.Clip or Note.OfferNote) ToastSound.Play();
    }

    void ShowReceived()
    {
        Directory.CreateDirectory(engine.ReceivedDir);
        Process.Start(new ProcessStartInfo(engine.ReceivedDir) { UseShellExecute = true });
    }

    void Copy(ClipRecord c)
    {
        if (!Win) return;
        clipboard.Write(c.Kind, c.Text, c.Files);
        toast.Show("Copied", c.Preview, [], flyout);
    }

    void Quit()
    {
        flyout.Hide();
        toast.Hide();
        tray.IsVisible = false;
        if (Win) clipboard.Dispose();
        engine.Dispose();   // can take up to 3 s (mDNS goodbyes)
        ((IClassicDesktopStyleApplicationLifetime)ApplicationLifetime!).Shutdown();
    }
}
