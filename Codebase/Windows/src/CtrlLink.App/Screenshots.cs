using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Threading;
using CtrlLink.Core;
using SkiaSharp;
using static CtrlLink.App.Ui;

namespace CtrlLink.App;

/// <summary>--screenshots &lt;dir&gt;: renders every screen in dark and light with the fake engine, headless, to PNGs.</summary>
static class Screenshots
{
    public static int Run(string dir)
    {
        App.Headless = true;
        AppBuilder.Configure<App>().UseSkia().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false }).WithInterFont().SetupWithoutStarting();
        Directory.CreateDirectory(dir);
        ReduceMotion = true;   // show final states
        Shell.LocalIp = () => "192.168.1.30";
        var now = DateTimeOffset.Now;

        var thumb = Path.Combine(Path.GetTempPath(), "ctrllink-shot-thumb.png");
        using (var bmp = new SKBitmap(320, 200))
        {
            using var canvas = new SKCanvas(bmp);
            using var paint = new SKPaint { Shader = SKShader.CreateLinearGradient(new SKPoint(0, 0), new SKPoint(320, 200), [new SKColor(0xF5, 0xC2, 0x6B), new SKColor(0x3A, 0xA4, 0xF2)], SKShaderTileMode.Clamp) };
            canvas.DrawRect(0, 0, 320, 200, paint);
            File.WriteAllBytes(thumb, bmp.Encode(SKEncodedImageFormat.Png, 90).ToArray());
        }
        PeerStatus Peer(string id, string name, string pf, bool online, string? ip) => new(id, name, pf, online, now.AddHours(online ? 0 : -2), ip);
        Transfer Sending(double frac, bool finished = false, bool rate = true)
        {
            long total = 148L << 20, done = finished ? total : (long)(total * frac);
            double[] mbps = [0, 2, 10.5, 9.5, 10.5, 12, 8.5, 12, 13, 9.5, 11];
            var speeds = mbps.Select((v, i) => new SpeedSample(frac * i / (mbps.Length - 1), v * (1 << 20))).ToList();
            if (!rate) speeds = [new(0, 0)];
            if (finished) speeds.Add(new(1, speeds[^1].BytesPerSecond));
            return new("t1", "Project brief and assets.zip", "Pixel 7", false, done, total, now.AddSeconds(-25), speeds, rate ? 2.04 * (1 << 20) : 0, finished);
        }

        var scenes = new (string Name, Action<FakeEngine, Shell> Pose)[]
        {
            ("splash", (e, s) => s.SplashUntil = DateTime.UtcNow.AddMinutes(5)),
            ("welcome", (e, s) => { s.Welcome = true; e.Peers = []; }),
            ("devices", (e, s) => { }),
            ("devices-two", (e, s) => e.Peers = [Peer("r", "Redmi Note 12", "android", true, "192.168.32.5"), Peer("p", "Pixel 7", "android", true, "192.168.32.6")]),
            ("devices-four", (e, s) => e.Peers = [Peer("m", "Jubaer's MacBook", "mac", true, "192.168.1.12"), Peer("r", "Redmi Note 12", "android", true, "192.168.1.21"),
                Peer("i", "Studio iMac", "mac", false, null), Peer("p", "Pixel 7", "android", true, null)]),
            ("devices-more", (e, s) => e.Peers = [Peer("m", "Jubaer's MacBook", "mac", true, "192.168.1.12"), Peer("r", "Redmi Note 12", "android", true, "192.168.1.21"),
                Peer("i", "Mac mini", "mac", true, "192.168.1.30"), Peer("p", "Pixel 7", "android", false, null), Peer("w", "Office PC", "windows", false, null), Peer("t", "Tab S9", "android", false, null)]),
            ("devices-looking", (e, s) => e.Peers = [Peer("m", "Jubaer's MacBook", "mac", false, null), Peer("p", "Pixel 7", "android", false, null)]),
            ("devices-empty", (e, s) => { e.Peers = []; e.Clips = []; }),
            ("devices-offer", (e, s) => e.Offers = [new("o1", "redmi", "Redmi Note 12", 3, 412L << 20)]),
            ("pair-host", (e, s) =>
            {
                s.Go(Tab.Devices, Route.Host);
                e.PairingWindow = e.PairingWindow! with { Expires = now.AddSeconds(25) };
            }),
            ("pair-request", (e, s) =>
            {
                s.Go(Tab.Devices, Route.Host);
                e.PairingWindow = e.PairingWindow! with { Request = new("Pixel 7", "042917") };
            }),
            ("pair-expired", (e, s) => { s.Go(Tab.Devices, Route.Host); e.PairingWindow = e.PairingWindow! with { Expires = now.AddSeconds(-1) }; }),
            ("join", (e, s) => { s.Go(Tab.Devices, Route.Join); s.Digits = "0429"; }),
            ("join-connecting", (e, s) => { s.Go(Tab.Devices, Route.Join); e.Join = new JoinState.Connecting(); }),
            ("join-confirm", (e, s) => { s.Go(Tab.Devices, Route.Join); e.Join = new JoinState.Confirm("482193", "Jubaer's MacBook"); }),
            ("join-failed", (e, s) => { s.Go(Tab.Devices, Route.Join); s.Digits = "042917"; e.Join = new JoinState.Failed("No device is showing a pairing code"); }),
            ("transfer", (e, s) => e.Transfers = [Sending(0.42)]),
            ("transfer-start", (e, s) => e.Transfers = [Sending(0.02, rate: false)]),
            ("transfer-done", (e, s) => e.Transfers = [Sending(1, finished: true)]),
            ("transfer-offer", (e, s) => { e.Transfers = [Sending(0.42)]; e.Offers = [new("o1", "redmi", "Redmi Note 12", 1, 25L << 20)]; }),
            ("history", (e, s) => { s.Tab = Tab.History; e.Clips = [.. e.Clips.Select(c => c.Kind == ClipKind.Image ? c with { Files = [thumb] } : c)]; }),
            ("history-filtered", (e, s) => { s.Tab = Tab.History; s.Hidden.Add(ClipKind.Text); s.Hidden.Add(ClipKind.Url); }),
            ("history-empty-day", (e, s) => { s.Tab = Tab.History; s.Day = 5; }),
            ("settings", (e, s) => { s.Tab = Tab.Settings; e.Peers = [Peer("m", "Jubaer's MacBook", "mac", true, null), Peer("r", "Redmi Note 12", "android", true, null), Peer("p", "Pixel 7", "android", false, null)]; }),
            ("settings-clear", (e, s) => { s.Tab = Tab.Settings; s.Render(); s.GetType().GetMethod("Ask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                .Invoke(s, ["Clear history?", "Deletes the saved clips and the daily counts on this PC. Received files stay in their folder. Paired devices stay paired.", "Clear", (Action)(() => { })]); }),
            ("paired", (e, s) => { s.Tab = Tab.Settings; s.PairedOpen = true; e.Peers = [Peer("m", "Jubaer's MacBook", "mac", true, null), Peer("r", "Redmi Note 12", "android", true, null), Peer("p", "Pixel 7", "android", false, null)]; }),
        };

        foreach (var palette in new[] { Palette.DarkP, Palette.LightP })
        {
            P = palette;
            var mode = palette.Dark ? "dark" : "light";
            foreach (var (name, pose) in scenes)
            {
                var engine = new FakeEngine();
                var shell = new Shell(engine);
                pose(engine, shell);
                Save(new Flyout(shell) { Background = B(P.Bg) }, () => shell.Render(), Path.Combine(dir, $"{mode}-{name}.png"));
            }
            var toast = new Window
            {
                Width = 360, SizeToContent = SizeToContent.Height, SystemDecorations = SystemDecorations.None, Background = B(P.Surface),
                Content = Toast.Card("Studio PC wants to send 3 files", "report-q3.pdf, notes.txt and 1 more · 12.4 MB", [("Accept", () => { }), ("Decline", () => { })], () => { }),
            };
            Save(toast, null, Path.Combine(dir, $"{mode}-toast.png"));
        }
        CheckBitmapRoundTrip();
        Console.WriteLine($"Screenshots written to {Path.GetFullPath(dir)}");
        return 0;
    }

    /// <summary>Self-check for the clipboard image path (PNG → CF_DIB → PNG); pure Skia, so it runs on macOS too.</summary>
    static void CheckBitmapRoundTrip()
    {
        using var src = new SKBitmap(3, 2, SKColorType.Bgra8888, SKAlphaType.Unpremul);
        src.Erase(SKColors.White);
        src.SetPixel(2, 0, new SKColor(0x25, 0x49, 0xB8));
        using var back = SKBitmap.Decode(WinClipboard.DibToPng(WinClipboard.PngToDib(src.Encode(SKEncodedImageFormat.Png, 100).ToArray())));
        if (back is not { Width: 3, Height: 2 } || back.GetPixel(2, 0).Blue != 0xB8 || back.GetPixel(2, 0).Red != 0x25 || back.GetPixel(0, 1) != SKColors.White)
            throw new InvalidOperationException("Clipboard bitmap round trip failed");
    }

    static void Save(Window w, Action? render, string path)
    {
        render?.Invoke();
        w.Show();
        render?.Invoke();
        for (var i = 0; i < 5; i++)   // let thumbnails decode and posted focus/layout work run
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(20);
        }
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        using var frame = w.CaptureRenderedFrame();
        frame?.Save(path);
        w.Close();
    }
}
