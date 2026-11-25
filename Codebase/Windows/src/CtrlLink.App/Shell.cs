using System.ComponentModel;
using Avalonia;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using CtrlLink.Core;
using QRCoder;
using static CtrlLink.App.Ui;

namespace CtrlLink.App;

enum Tab { Devices, History, Settings }
enum Route { Home, Host, Join }

/// <summary>The flyout content: splash, welcome, header, Devices / History / Settings nav and every screen (Figma section
/// "Windows" 52:1426). Rebuilt from engine state on change.</summary>
sealed class Shell : Border
{
    public readonly IEngine E;
    public Tab Tab;
    public Route Route;
    public bool PairedOpen;                  // Settings › Paired devices
    public bool Welcome;                     // first run: shown on the Devices home until paired or dismissed (see App)
    public DateTime SplashUntil;             // the splash shows until then
    public int Day = 6;                      // History: index into the last 7 days, 6 = today
    public readonly HashSet<ClipKind> Hidden = [];   // History legend filters
    public string Digits = "";               // Join: typed code
    public Action<ClipRecord>? CopyClip;
    public static Func<string?> LocalIp = NetInfo.LocalIPv4;   // the screenshot mode poses a fixed address
    public Action? Welcomed;

    (Tab Tab, bool Paired) hostReturn = (Tab.Devices, false);
    (string Title, string Body, string Action, Action Do)? confirm;
    string? joinNote, doneId;
    bool copied, dirty, pending;
    readonly Dictionary<string, double> shown = [];   // last value each hero numeral displayed, for the count-up
    readonly Dictionary<string, TimeSpan> pairTotal = [];

    public Shell(IEngine engine)
    {
        E = engine;
        E.PropertyChanged += OnEngine;
        var tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        tick.Tick += (_, _) => { if (IsEffectivelyVisible && (Route == Route.Host || Splash)) Render(); };
        tick.Start();
    }

    bool Splash => DateTime.UtcNow < SplashUntil;
    const long BigTransfer = 5_000_000;

    // Engine changes come in bursts (and transfer progress up to ~7 times a second): one rebuild per dispatcher turn.
    void OnEngine(object? s, PropertyChangedEventArgs e)
    {
        if (pending) return;
        pending = true;
        Dispatcher.UIThread.Post(() => { pending = false; Render(); }, DispatcherPriority.Background);
    }

    static DateOnly Today => DateOnly.FromDateTime(DateTime.Now);

    public void Go(Tab tab, Route route = Route.Home)
    {
        if (Route == Route.Host && route != Route.Host) E.ClosePairingWindow();
        if (route == Route.Host)
        {
            hostReturn = Route == Route.Host ? hostReturn : (Tab, PairedOpen && Tab == Tab.Settings);
            if (E.PairingWindow == null) E.OpenPairingWindow();
            tab = Tab.Devices;
        }
        if (route == Route.Join) { E.ResetJoin(); Digits = ""; joinNote = null; }
        if (tab != Tab.Settings) PairedOpen = false;
        Tab = tab;
        Route = route;
        confirm = null;
        Render();
        if (route == Route.Join) FocusNamed("Code digits", NavigationMethod.Unspecified);
    }

    /// <summary>Back from Pair host: wherever it was opened from (Devices, or Settings › Paired devices).</summary>
    void BackFromHost()
    {
        var (tab, paired) = hostReturn;
        Go(tab);
        PairedOpen = paired;
        Render();
    }

    /// <summary>Rebuilds the screen, keeping keyboard focus on the control with the same name. Waits while a text box is being edited.</summary>
    public void Render()
    {
        var focused = TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() as Control;
        if (focused is TextBox && focused.IsAttachedToVisualTree() && this.IsVisualAncestorOf(focused))
        {
            dirty = true;
            return;
        }
        var key = focused != null && this.IsVisualAncestorOf(focused) ? AutomationProperties.GetName(focused) : null;
        var how = focused?.Classes.Contains(":focus-visible") == true ? NavigationMethod.Tab : NavigationMethod.Unspecified;
        if (Welcome && E.Peers.Count > 0) { Welcome = false; Welcomed?.Invoke(); }   // installs that pair elsewhere skip it
        Background = B(P.Bg);
        Child = Build();
        if (key != null) FocusNamed(key, how);
    }

    void FocusNamed(string name, NavigationMethod how) => Dispatcher.UIThread.Post(() =>
        this.GetVisualDescendants().OfType<Control>().FirstOrDefault(c => c.Focusable && AutomationProperties.GetName(c) == name)?.Focus(how),
        DispatcherPriority.Loaded);

    Control Build()
    {
        if (Splash) return SplashView();
        // Welcome only on the Devices home: a pairing route (tray "Pair a device", Join) is never covered by it.
        var welcome = Welcome && Tab == Tab.Devices && Route == Route.Home;
        var body = welcome ? WelcomeView() : Route switch
        {
            Route.Host => Host(),
            Route.Join => JoinView(),
            _ => Tab switch
            {
                Tab.History => History(),
                Tab.Settings => PairedOpen ? PairedDevices() : Settings(),
                // Big files only (as on Android): small clips finish before the screen could be read.
                _ => E.Transfers.FirstOrDefault(t => t.Id != doneId && t.Total >= BigTransfer) is { } t ? TransferView(t) : Devices(),
            },
        };
        var root = new DockPanel();
        var header = Header();
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);
        if (!welcome)
        {
            var nav = Nav();
            DockPanel.SetDock(nav, Dock.Top);
            root.Children.Add(nav);
        }
        root.Children.Add(body);
        if (confirm is not { } c) return root;
        return new Panel { Children = { root, ConfirmOverlay(c.Title, c.Body, c.Action, c.Do) } };
    }

    // ---------- chrome ----------

    static Control Header()
    {
        var row = HStack(8, Monogram(20), Body("Ctrl Link", 14, P.Text, FontWeight.Medium));
        row.Margin = new Thickness(16, 16, 16, 8);
        return row;
    }

    /// <summary>Segmented Pill (18:586): hairline capsule, 2 px inset, three equal 26 px segments; the selected one is a
    /// translucent text-colour capsule (32 % fill, 68 % rim).</summary>
    Control Nav()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*") };
        foreach (var (tab, i) in Enum.GetValues<Tab>().Select((t, i) => (t, i)))
        {
            var on = tab == Tab;
            var seg = Clickable(Body(tab.ToString(), 13).Center(), () => Go(tab), tab.ToString(), 13, state: (hover, down) => { }, scale: false);
            seg.Height = 26;
            var fill = B(P.Text, P.Dark ? 0.32 : 0.10);
            seg.Background = on ? fill : Brushes.Transparent;
            seg.BorderBrush = on ? B(P.Text, P.Dark ? 0.68 : 0.32) : null;
            seg.BorderThickness = new Thickness(on ? 1 : 0);
            if (!on)
            {
                var b = seg;
                b.PointerEntered += (_, _) => b.Background = B(P.Text, 0.08);
                b.PointerExited += (_, _) => b.Background = Brushes.Transparent;
            }
            AutomationProperties.SetHelpText(seg, on ? "Selected" : "");
            Grid.SetColumn(seg, i);
            grid.Children.Add(seg);
        }
        return new Border
        {
            Child = grid, Padding = new Thickness(2), CornerRadius = new CornerRadius(16), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(1),
            Margin = new Thickness(16, 4, 16, 12),
        };
    }

    /// <summary>Back button (40 px hit area, 32 px icon button, 20 px arrow), the centred screen label and a balancing spacer.</summary>
    static Control TopRow(string label, Action back)
    {
        var icon = new Border { Width = 32, Height = 32, CornerRadius = new CornerRadius(8), Child = Stroked(BackPath, 20), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        var b = Clickable(icon, back, "Back", 8, state: (hover, down) => icon.Background = down ? B(P.Hairline) : hover ? B(P.Surface) : Brushes.Transparent);
        b.Width = b.Height = 40;
        var row = Row((b, "40"), (Label(label).Center(), "*"), (new Panel(), "40"));
        return row;
    }

    /// <summary>The serif hero numeral (96 light). Counts up over 400 ms ease-out when its value changes (at once under reduce motion).</summary>
    TextBlock Hero(string key, double value, double size = 96)
    {
        string Format(double v) => ((int)Math.Round(v)).ToString();
        var from = shown.GetValueOrDefault(key, 0);
        var tb = SerifText(Format(value), size).Trim();
        shown[key] = value;
        if (ReduceMotion || from == value) return tb;
        tb.Text = Format(from);
        var start = DateTime.UtcNow;
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(16) };
        timer.Tick += (_, _) =>
        {
            var p = Math.Min(1, (DateTime.UtcNow - start).TotalMilliseconds / 400);
            tb.Text = Format(from + (value - from) * (1 - Math.Pow(1 - p, 3)));
            if (p >= 1 || !tb.IsAttachedToVisualTree()) timer.Stop();
        };
        timer.Start();
        return tb;
    }

    /// <summary>Hero numeral with its sans unit on the baseline, 12 apart (Hero Numeral 21:603).</summary>
    static Control HeroWithUnit(TextBlock hero, string unit)
    {
        var u = Body(unit, 15, P.Text2);
        u.VerticalAlignment = VerticalAlignment.Bottom;
        u.Margin = new Thickness(0, 0, 0, hero.FontSize * 0.16);
        var row = HStack(12, hero, u);
        row.HorizontalAlignment = HorizontalAlignment.Center;
        return row;
    }

    /// <summary>Content above a footer bar (16 px padding, buttons in equal columns 8 apart).</summary>
    static Control WithFooter(Control content, params Control[] buttons) => WithFooter(content, new Thickness(16), buttons);

    static Control WithFooter(Control content, Thickness padding, params Control[] buttons)
    {
        var bar = new Grid { Margin = padding, ColumnSpacing = 8 };
        for (var i = 0; i < buttons.Length; i++)
        {
            bar.ColumnDefinitions.Add(new ColumnDefinition(buttons[i].Tag as string == "auto" ? GridLength.Auto : GridLength.Star));
            Grid.SetColumn(buttons[i], i);
            bar.Children.Add(buttons[i]);
        }
        var d = new DockPanel();
        DockPanel.SetDock(bar, Dock.Bottom);
        d.Children.Add(bar);
        d.Children.Add(content);
        return d;
    }

    // Wheel, touch and keyboard still scroll; the editorial look has no visible scrollbar.
    static ScrollViewer Scroll(Control c) => new()
    {
        Content = c, HorizontalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled,
        VerticalScrollBarVisibility = Avalonia.Controls.Primitives.ScrollBarVisibility.Hidden,
    };

    /// <summary>Horizon (22:599): a large dim sphere whose top edge rises behind the content, dark mode only. `top` is the
    /// sphere's top edge in the content area.</summary>
    static Control WithHorizon(Control content, double top)
    {
        if (!P.Dark) return content;
        var sphere = new Ellipse
        {
            Width = 648, Height = 648, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(-400, top, -400, -1000), IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                Center = new RelativePoint(0.5, 0, RelativeUnit.Relative), GradientOrigin = new RelativePoint(0.5, 0, RelativeUnit.Relative),
                RadiusX = new RelativeScalar(0.5, RelativeUnit.Relative), RadiusY = new RelativeScalar(0.5, RelativeUnit.Relative),
                GradientStops =
                {
                    new GradientStop(Color.Parse("#1C1C1E"), 0), new GradientStop(Color.FromArgb(140, 0x12, 0x12, 0x14), 0.4), new GradientStop(Color.FromArgb(0, 0, 0, 0), 0.7),
                },
            },
        };
        return new Panel { ClipToBounds = true, Children = { sphere, content } };
    }

    // ---------- Splash / Welcome ----------

    /// <summary>Splash (84:2490): the mark and the serif name, 16 apart, centred. No header, no caption.</summary>
    static Control SplashView()
    {
        var s = Stack(16, Monogram(160), SerifText("Ctrl Link", 34).Center());
        s.HorizontalAlignment = HorizontalAlignment.Center;
        s.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(s, "Ctrl Link, starting");
        return s;
    }

    /// <summary>Welcome (55:1867), first run only.</summary>
    Control WelcomeView()
    {
        var steps = Stack(0, Hair());
        string[] lines = ["Open Ctrl Link on another device on this network.", "Pair the two with a code or a QR code.", "Copy as usual. It shows up on the other device."];
        for (var i = 0; i < 3; i++)
        {
            var r = Row((SerifText($"{i + 1}", 26), "Auto"), (Body(lines[i], 15).Margin(16, 6, 0, 0), "*"));
            r.VerticalAlignment = VerticalAlignment.Top;
            r.Margin = new Thickness(0, 12);
            steps.Children.Add(r);
            steps.Children.Add(Hair());
        }

        void Done(Route next)
        {
            Welcome = false;
            Welcomed?.Invoke();
            Go(Tab.Devices, next);
        }

        var top = Stack(12,
            Label("Welcome to Ctrl Link").Center(),
            SerifText("Copy on one device, paste on another", 34).Center(),
            Body("Everything stays on your local network and is encrypted. Nothing is sent to the internet.", 13, P.Text2).Center(),
            steps.Margin(0, 12, 0, 0));
        var bottom = Stack(12,
            Body("Ctrl Link lives in the system tray, at the bottom right of the taskbar.", 13, P.Text2).Center(),
            Pill("Pair a device", () => Done(Route.Host), filled: true),
            Pill("Skip for now", () => Done(Route.Home)));
        var d = new DockPanel { Margin = new Thickness(16, 32, 16, 12) };
        DockPanel.SetDock(bottom, Dock.Bottom);
        d.Children.Add(bottom);
        d.Children.Add(Scroll(top));
        return d;
    }

    // ---------- Devices ----------

    Control OfferPanels()
    {
        var s = Stack(8);
        foreach (var o in E.Offers)
        {
            var text = Stack(2,
                Body($"{o.PeerName} wants to send {(o.Count == 1 ? "a file" : $"{o.Count} files")}", 14, P.Text, FontWeight.Medium).Trim(2),
                Body(Size(o.Size), 13, P.Text2));
            var accept = Pill("Accept", () => E.AcceptOffer(o.Id), filled: true, height: 32);
            var decline = Pill("Decline", () => E.RejectOffer(o.Id), height: 32);
            var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8, Margin = new Thickness(0, 10, 0, 0) };
            Grid.SetColumn(decline, 1);
            buttons.Children.Add(accept);
            buttons.Children.Add(decline);
            s.Children.Add(new Border
            {
                Child = Stack(0, text, buttons), Padding = new Thickness(12), CornerRadius = new CornerRadius(8),
                Background = B(P.Surface), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(1),
            });
        }
        s.Margin = new Thickness(16, 0, 16, E.Offers.Count > 0 ? 8 : 0);
        return s;
    }

    /// <summary>Devices (52:1459): connection graph, "On this network" and the headline, Pair device / Enter code.</summary>
    Control Devices()
    {
        var online = E.Peers.Count(p => p.Online);
        // Online first, so the ones that matter are the ones shown; four fit, beyond that three and "+N more".
        var nodes = E.Peers.OrderBy(p => !p.Online).ThenBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(p => new GraphNode(p.Name, p.Ip ?? (p.Online ? PlatformName(p.Pf) : "Offline"), p.Pf, p.Online)).ToList();
        if (nodes.Count > 4)
            nodes = [.. nodes.Take(3), new GraphNode($"{nodes.Count - 3} more", "Paired", "", nodes.Skip(3).Any(n => n.Online), More: nodes.Count - 3)];
        var graph = new DeviceGraph(new GraphNode(E.DeviceName, LocalIp() ?? "No network", "windows", true, Current: true), nodes);

        var summary = Stack(8,
            Label("On this network").Center(),
            SerifText(online == 0 ? "Looking for devices" : $"{online + 1} devices connected", 26).Center().Trim(2));
        summary.Margin = new Thickness(16, 12, 16, 28);
        var content = new DockPanel();
        var offers = OfferPanels();
        DockPanel.SetDock(offers, Dock.Top);
        DockPanel.SetDock(summary, Dock.Bottom);
        content.Children.Add(offers);
        content.Children.Add(summary);
        content.Children.Add(graph);
        return WithFooter(WithHorizon(content, 323),
            Pill("Pair device", () => Go(Tab.Devices, Route.Host), filled: true),
            Pill("Enter code", () => Go(Tab.Devices, Route.Join)));
    }

    // ---------- Transfer ----------

    /// <summary>Transfer (53:1582): who, file name, hero percent, speed graph, Size / Time left, Cancel. Holds at 99 % until
    /// the engine reports the end, then shows 100 % for a moment before Devices returns.</summary>
    Control TransferView(Transfer t)
    {
        var pct = t.Finished ? 100 : t.Total > 0 ? (int)Math.Min(99, t.Done * 100 / t.Total) : 0;
        var size = Size(t.Total).Split(' ');
        var (left, unit) = TimeLeft(t.Total - t.Done, t.Rate, t.Finished);

        static Control Stat(string label, string value, string unit, Thickness pad)
        {
            var v = HStack(4, SerifText(value, 26), Body(unit, 13, P.Text2).Margin(0, 0, 0, 5));
            v.Children[1].VerticalAlignment = VerticalAlignment.Bottom;
            return Stack(4, Label(label, size: 11), v).Margin(pad.Left, 0, pad.Right, 0);
        }
        var stats = Row(
            (Stat("Size", size[0], size.Length > 1 ? size[1] : "", new Thickness(0, 0, 16, 0)), "*"),
            (new Border { Width = 1, Height = 48, Background = B(P.Hairline) }, "Auto"),
            (Stat("Time left", left, unit, new Thickness(16, 0, 0, 0)), "*"));

        var body = Stack(8,
            Label(t.Incoming ? $"Receiving from {t.Peer}" : $"Sending to {t.Peer}").Center(),
            SerifText(t.Name, 26).Center().Trim(2),
            HeroWithUnit(Hero("pct-" + t.Id, pct), "%"),
            new TransferGraph(t.Speeds ?? []),
            stats);
        body.Margin = new Thickness(16, 12);
        body.VerticalAlignment = VerticalAlignment.Center;
        var content = new DockPanel();
        var offers = OfferPanels();
        DockPanel.SetDock(offers, Dock.Top);
        content.Children.Add(offers);
        content.Children.Add(Scroll(body));
        var id = t.Id;
        return WithFooter(WithHorizon(content, 424), t.Finished
            ? Pill("Done", () => { doneId = id; Render(); })
            : Pill("Cancel", () => E.CancelTransfer(id)));
    }

    // ---------- Pairing ----------

    /// <summary>Pair host (52:1643): QR, the code as one serif numeral, the countdown line, Copy pairing link.</summary>
    Control Host()
    {
        var w = E.PairingWindow;
        if (w?.Request is { } r)
            return Sas("Confirm on both screens", r.Sas, $"{r.Name} wants to pair", $"Accept only if {r.Name} shows the same six digits.",
                Pill("Decline", () => E.AnswerPairRequest(false)), Pill("Accept", () => E.AnswerPairRequest(true), filled: true));

        var remaining = w == null ? TimeSpan.Zero : w.Expires - DateTimeOffset.Now;
        if (w == null || remaining <= TimeSpan.Zero)
            return WithFooter(Stack(6, TopRow("Pair a device", BackFromHost), SerifText("The code expired", 26).Center().Margin(0, 40, 0, 0),
                    Body("Codes last a few minutes. Show a new one to keep pairing.", 13, P.Text2).Center()).Margin(16, 4, 16, 8),
                Pill("Show a new code", E.OpenPairingWindow, filled: true));

        var total = pairTotal.TryGetValue(w.Code, out var tt) ? tt : pairTotal[w.Code] = remaining;
        var frac = Math.Clamp(remaining / total, 0, 1);
        var qr = new Border
        {
            Width = 234, Height = 234, CornerRadius = new CornerRadius(12), Background = Brushes.White, HorizontalAlignment = HorizontalAlignment.Center,
            BorderBrush = B(P.Hairline), BorderThickness = new Thickness(P.Dark ? 0 : 1), Child = new QrView(w.Uri),
        };
        AutomationProperties.SetName(qr, "Pairing QR code");
        var code = SerifText(w.Code, 64).Center();
        code.LetterSpacing = 11.52;
        code.Height = 96;
        AutomationProperties.SetName(code, string.Join(" ", w.Code.ToCharArray()));

        var track = new Panel { Height = 8 };
        track.Children.Add(new Border { Background = B(P.Hairline), CornerRadius = new CornerRadius(4) });
        track.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(4), HorizontalAlignment = HorizontalAlignment.Left, Width = Math.Max(8, 348 * frac),
            Background = new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(P.Text, 0.394), new GradientStop(Palette.Amber, 0.764), new GradientStop(Palette.Sky, 1) },
            },
        });
        var countdown = Stack(8, Row((Label("Code expires in", size: 11), "*"), (Body($"{(int)remaining.TotalMinutes}:{remaining.Seconds:00}", 13), "Auto")), track);

        var uri = w.Uri;
        var body = Stack(6, TopRow("Pair a device", BackFromHost), SerifText("Scan or enter the code", 26).Center(), qr, code, countdown);
        body.Margin = new Thickness(16, 4, 16, 8);
        return WithFooter(Scroll(body), Pill(copied ? "Copied" : "Copy pairing link", async () =>
        {
            if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
            await cb.SetTextAsync(uri);
            copied = true;
            Render();
            DispatcherTimer.RunOnce(() => { copied = false; Render(); }, TimeSpan.FromSeconds(1.5));
        }));
    }

    /// <summary>SAS confirm (53:1507): the six digits as one serif numeral; Decline hugs its label, Accept fills (focused).</summary>
    Control Sas(string label, string sas, string headline, string body, Border? decline, Border accept)
    {
        var code = SerifText(sas, 64).Center();
        code.LetterSpacing = 11.52;
        code.Height = 80;
        AutomationProperties.SetName(code, string.Join(" ", sas.ToCharArray()));
        var s = Stack(12, Label(label).Center(), code, SerifText(headline, 26).Center().Trim(2), Body(body, 13, P.Text2).Center());
        s.Margin = new Thickness(16, 24, 16, 12);
        s.VerticalAlignment = VerticalAlignment.Center;
        FocusNamed(AutomationProperties.GetName(accept) ?? "", NavigationMethod.Tab);   // Enter accepts, as the frame's focus ring shows
        if (decline == null) return WithFooter(Scroll(s), new Thickness(16, 12), accept);
        decline.Tag = "auto";
        return WithFooter(Scroll(s), new Thickness(16, 12), decline, accept);
    }

    /// <summary>Join (52:1718): six digit slots, Paste pairing link.</summary>
    Control JoinView()
    {
        switch (E.Join)
        {
            case JoinState.Connecting:
                return WithFooter(Stack(8, TopRow("Join a device", Cancel), SerifText("Connecting…", 26).Center().Margin(0, 40, 0, 0),
                        Body("Keep both devices on this network.", 13, P.Text2).Center()).Margin(16, 12, 16, 12),
                    Pill("Cancel", Cancel));
            case JoinState.Confirm c:
                // The host accepts; the joiner shows the digits and can cancel (ConfirmJoin(true) is a no-op in the core).
                var cancel = Pill("Cancel", () => E.ConfirmJoin(false));
                return Sas("Confirm on both screens", c.Sas, $"Accept on {c.Host}", $"Check that {c.Host} shows the same six digits, then accept there.",
                    null, cancel);
            case JoinState.Done d:
                return WithFooter(Stack(8, Label("Paired").Center().Margin(0, 52, 0, 0), SerifText($"Paired with {d.PeerName}", 26).Center().Trim(2),
                        Body("Copy on one device and paste on the other.", 13, P.Text2).Center()).Margin(16, 12, 16, 12),
                    Pill("Done", () => { E.ResetJoin(); Go(Tab.Devices); }, filled: true));
            case JoinState.Failed f:
                Control[] buttons = Digits.Length == 6
                    ? [Pill("Enter another code", () => Go(Tab.Devices, Route.Join)), Pill("Retry", () => E.JoinWithCode(Digits), filled: true)]
                    : [Pill("Enter another code", () => Go(Tab.Devices, Route.Join), filled: true)];
                return WithFooter(Stack(8, TopRow("Couldn't pair", Cancel), SerifText(f.Reason, 26).Center().Trim(3).Margin(0, 40, 0, 0)).Margin(16, 12, 16, 12), buttons);
        }

        var body = Stack(8,
            TopRow("Join a device", Cancel),
            SerifText("Enter the code", 26).Center(),
            Body("Type the 6 digits shown on the other device.", 13, P.Text2).Center(),
            DigitSlots());
        if (joinNote != null) body.Children.Add(Body(joinNote, 13, P.Text2).Center().Margin(0, 8, 0, 0));
        body.Margin = new Thickness(16, 12);
        return WithFooter(Scroll(body), Pill("Paste pairing link", PasteLink));
    }

    async void PasteLink()
    {
        if (TopLevel.GetTopLevel(this)?.Clipboard is not { } cb) return;
        var text = (await cb.TryGetTextAsync())?.Trim() ?? "";
        if (text.StartsWith("ctrllink://", StringComparison.OrdinalIgnoreCase)) { E.JoinWithUri(text); return; }
        if (new string(text.Where(char.IsAsciiDigit).ToArray()) is { Length: 6 } code) { Digits = code; Render(); E.JoinWithCode(code); return; }
        joinNote = "Copy the pairing link on the other device first.";
        Render();
    }

    void Cancel()
    {
        E.ResetJoin();
        Go(Tab.Devices);
    }

    /// <summary>Code Digit Slots (20:622): 56 × 96, 2 apart in two groups of three 6 apart; strong underline when filled,
    /// 2 px at the cursor, hairline when empty.</summary>
    Control DigitSlots()
    {
        var row = HStack(0);
        for (var i = 0; i < 6; i++)
        {
            var filled = i < Digits.Length;
            var cursor = i == Digits.Length;
            row.Children.Add(new Border
            {
                Width = 56, Height = 96, Margin = new Thickness(i == 0 ? 0 : i == 3 ? 6 : 2, 0, 0, 0),
                BorderBrush = B(filled || cursor ? P.Text : P.Hairline), BorderThickness = new Thickness(0, 0, 0, cursor ? 2 : 1),
                Child = SerifText(filled ? Digits[i].ToString() : "", 64).Center(),
            });
        }
        row.HorizontalAlignment = HorizontalAlignment.Center;
        var box = new Border { Child = row, Focusable = true, Background = Brushes.Transparent, Cursor = new Cursor(StandardCursorType.Ibeam) }.Ring(4);
        AutomationProperties.SetName(box, "Code digits");
        box.PointerPressed += (_, _) => box.Focus();
        box.TextInput += (_, e) =>
        {
            var d = new string((e.Text ?? "").Where(char.IsAsciiDigit).ToArray());
            if (d.Length == 0) return;
            Digits = (Digits + d)[..Math.Min(6, Digits.Length + d.Length)];
            Render();
            if (Digits.Length == 6) E.JoinWithCode(Digits);
        };
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Back && Digits.Length > 0) { Digits = Digits[..^1]; Render(); }
            else if (e.Key == Key.V && e.KeyModifiers.HasFlag(KeyModifiers.Control)) PasteLink();
        };
        return box;
    }

    // ---------- History ----------

    /// <summary>History (55:1575): week strip, week total, stacked bars per day (a bar selects its day), legend filters, clip list.</summary>
    Control History()
    {
        var days = Enumerable.Range(0, 7).Select(i => Today.AddDays(i - 6)).ToArray();
        var counts = days.Select(d => E.CountsOn(d).Where(kv => !Hidden.Contains(kv.Key)).ToDictionary()).ToArray();
        var totals = counts.Select(c => c.Values.Sum()).ToArray();
        var total = totals.Sum();
        string Short(DateOnly d) => d.ToString("ddd").ToUpperInvariant();

        var strip = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
        for (var i = 0; i < 7; i++)
        {
            var i1 = i;
            var on = i == Day;
            var tab = Clickable(Stack(6, Body(Short(days[i]), 13, on ? P.Text : P.Text2).Center(),
                    new Border { Width = 40, Height = 1, Background = on ? B(P.Text) : Brushes.Transparent }).Margin(0, 6, 0, 6),
                () => { Day = i1; Render(); }, days[i].ToString("dddd d MMMM"), 4, scale: false);
            Grid.SetColumn(tab, i);
            strip.Children.Add(tab);
        }

        var summary = HStack(20,
            Hero("week", total),
            Stack(4, SerifText(total >= 70 ? "Busy week" : "Quiet week", 26), Label($"Average {total / 7.0:0.0} a day")));
        summary.Children[1].VerticalAlignment = VerticalAlignment.Center;
        summary.HorizontalAlignment = HorizontalAlignment.Center;

        const double barH = 173;   // 200 minus the axis, the day label and their 6 px gaps
        var max = Math.Max(1, totals.Max());
        var bars = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*,*,*,*") };
        for (var i = 0; i < 7; i++)
        {
            var seg = new StackPanel { Spacing = 2, Width = 28, VerticalAlignment = VerticalAlignment.Bottom, Height = barH };
            var stackH = 0.0;
            var parts = Kinds.Where(k => counts[i].GetValueOrDefault(k) > 0).ToList();
            foreach (var k in parts)
            {
                var h = Math.Max(4, barH * counts[i][k] / max - 2);
                stackH += h + 2;
                seg.Children.Add(new Border { Height = h, Background = B(KindColor(k)), CornerRadius = new CornerRadius(4) });
            }
            seg.Children.Insert(0, new Panel { Height = Math.Max(0, barH - stackH) });
            var col = Stack(6, seg, new Border { Height = 1, Background = B(i == Day ? P.Text : P.Hairline), Margin = new Thickness(2, 0) },
                Label(Short(days[i]), i == Day ? P.Text : P.Text2, 11).Center());
            var i1 = i;
            var cell = Clickable(col, () => { Day = i1; Render(); }, $"{days[i]:dddd}: {totals[i]} {(totals[i] == 1 ? "clip" : "clips")}", 4, scale: false);
            Grid.SetColumn(cell, i);
            bars.Children.Add(cell);
        }

        var legend = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 2 };
        foreach (var (k, i) in Kinds.Select((k, i) => (k, i)))
        {
            var off = Hidden.Contains(k);
            var item = Clickable(Stack(6, Label(KindName(k), size: 11), new Border { Height = 4, CornerRadius = new CornerRadius(2), Background = B(KindColor(k)) }).Margin(0, 6, 0, 6),
                () => { if (!Hidden.Remove(k)) Hidden.Add(k); Render(); }, $"{KindName(k)} filter", 4, scale: false);
            item.Opacity = off ? 0.3 : 1;
            AutomationProperties.SetHelpText(item, off ? "Hidden" : "Shown");
            Grid.SetColumn(item, i);
            legend.Children.Add(item);
        }

        var top = Stack(8, strip, summary, bars, legend);
        top.Margin = new Thickness(16, 8, 16, 12);
        var list = Stack(0, Hair());
        list.Margin = new Thickness(16, 0, 16, 12);
        var dayClips = E.Clips.Where(c => DateOnly.FromDateTime(c.Date.LocalDateTime) == days[Day] && !Hidden.Contains(c.Kind)).ToList();
        foreach (var c in dayClips) list.Children.Add(ClipRow(c));
        if (dayClips.Count == 0)
            list.Children.Add(Body(Hidden.Count == 0 ? "No clips on this day. Clips you send or receive show up here for 14 days; click one to copy it again."
                : "No matching clips on this day.", 13, P.Text2).Margin(0, 12, 0, 0));
        return Scroll(Stack(0, top, list));
    }

    /// <summary>Clip Row (23:678): 3 px kind bar, content and meta, the kind icon (an image shows its thumbnail); hover adds a surface fill.</summary>
    Control ClipRow(ClipRecord c)
    {
        var info = Stack(4,
            Body(c.Preview.ReplaceLineEndings(" "), 15).Trim(),
            Body($"{c.From ?? "This PC"} · {c.Date.LocalDateTime:HH:mm}", 13, P.Text2).Trim());
        Control trailing = c.Kind == ClipKind.Image && c.Files.Count > 0 ? Thumbs.View(c) : Stroked(c.Kind switch
        {
            ClipKind.Text => TextIconPath, ClipKind.Url => LinkPath, _ => FilePath,
        }, 20);
        var row = Row(
            (new Border { Width = 3, CornerRadius = new CornerRadius(1.5), Background = B(KindColor(c.Kind)), Margin = new Thickness(0, 0, 12, 0) }, "Auto"),
            (info, "*"),
            (trailing.Margin(12, 0, 0, 0), "Auto"));
        var inner = new Border { Child = row, Padding = new Thickness(12), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) };
        var click = Clickable(inner, () => CopyClip?.Invoke(c), $"Copy again: {c.Preview}", scale: false,
            state: (hover, down) => inner.Background = down ? B(P.Hairline) : hover ? B(P.Surface) : Brushes.Transparent);
        ToolTip.SetTip(click, "Copy again");
        return click;
    }

    // ---------- Settings ----------

    static readonly long[] Limits = [0, 10L << 20, 25L << 20, 100L << 20, 1L << 30];

    static Control SectionLabel(string text) => new Border
    {
        Child = Label(text), Padding = new Thickness(0, 8), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(0, 0, 0, 1),
    };

    /// <summary>Settings Row (23:697): title 15/20 and note 13/18 on the left, one control 12 to the right, 8 px padding, hairline
    /// below. With onClick the whole row is the control (one click target, one screen-reader stop).</summary>
    static Control SettingRow(string title, string? sub, Control trailing, Action? onClick = null, string? name = null)
    {
        var text = Stack(2, Body(title, 15).Trim());
        if (sub != null) text.Children.Add(Body(sub, 13, P.Text2).Trim());
        trailing.VerticalAlignment = VerticalAlignment.Center;
        var row = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 12 };
        Grid.SetColumn(trailing, 1);
        row.Children.Add(text);
        row.Children.Add(trailing);
        var inner = new Border { Child = row, Padding = new Thickness(0, 8), MinHeight = 56, BorderBrush = B(P.Hairline), BorderThickness = new Thickness(0, 0, 0, 1) };
        if (onClick == null) return inner;
        return Clickable(inner, onClick, name ?? title, scale: false, state: (hover, down) => inner.Background = down ? B(P.Hairline, 0.5) : hover ? B(P.Surface) : Brushes.Transparent);
    }

    Control Settings()
    {
        var name = new TextBox
        {
            Text = E.DeviceName, FontFamily = Sans, FontSize = 15, Foreground = B(P.Text2), Background = Brushes.Transparent, CaretBrush = B(P.Text),
            BorderThickness = new Thickness(0), TextAlignment = TextAlignment.Right, Width = 150, MinHeight = 40, Padding = new Thickness(0, 10), MaxLength = 40,
            VerticalContentAlignment = VerticalAlignment.Center,
        }.Ring(4);
        AutomationProperties.SetName(name, "Device name");
        void Commit()
        {
            if (name.Text?.Trim() is { Length: > 0 } n && n != E.DeviceName) E.DeviceName = n;
            if (dirty) { dirty = false; Dispatcher.UIThread.Post(Render); }
        }
        name.LostFocus += (_, _) => Commit();
        name.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(); Focus(); } };

        var limitText = E.AutoAcceptLimit <= 0 ? "Always ask" : Size(E.AutoAcceptLimit);
        var win = OperatingSystem.IsWindows();
        var startOn = win && StartWithWindows.Enabled;
        var sound = AppSettings.Sound;
        var names = E.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase).Select(p => p.Name).ToList();

        var rows = Stack(0,
            SectionLabel("This PC"),
            SettingRow("Device name", "Shown to other devices", name),
            SettingRow("Send copied items automatically", "Every copy goes to paired devices", ToggleVisual(E.AutoSend), () => E.AutoSend = !E.AutoSend),
            SettingRow("Accept files without asking up to", "Larger files ask first", Body(limitText, 15, P.Text2), () =>
            {
                var i = Array.IndexOf(Limits, E.AutoAcceptLimit);
                E.AutoAcceptLimit = Limits[(i + 1) % Limits.Length];
            }, $"Accept files without asking up to {limitText}"),
            win ? SettingRow("Start with Windows", "Open Ctrl Link in the tray at sign-in", ToggleVisual(startOn), () => { if (OperatingSystem.IsWindows()) StartWithWindows.Enabled = !startOn; Render(); })
                : SettingRow("Start with Windows", "Open Ctrl Link in the tray at sign-in", ToggleVisual(false, enabled: false)),
            SettingRow("Notification sound", "Play a sound when a clip arrives", ToggleVisual(sound), () => { AppSettings.Sound = !sound; Render(); }),
            SettingRow("Received files folder", E.ReceivedDir, Stroked(ChevronPath, 20), ChooseFolder, "Change received files folder"),
            SettingRow("Clear history", "Removes every clip saved on this PC", Pill("Clear", () => Ask("Clear history?",
                "Deletes the saved clips and the daily counts on this PC. Received files stay in their folder. Paired devices stay paired.",
                "Clear", E.ClearHistory))),
            SectionLabel("Devices").Margin(0, 0, 0, 0),
            SettingRow($"Paired devices · {names.Count}", names.Count == 0 ? "None yet" : string.Join(", ", names), Stroked(ChevronPath, 20),
                () => { PairedOpen = true; Render(); }, $"Paired devices, {names.Count}"),
            Label($"Ctrl Link {typeof(Shell).Assembly.GetName().Version?.ToString(3)} · {E.DeviceId[..8]}", size: 11).Center().Margin(0, 20, 0, 0));
        rows.Margin = new Thickness(16, 0, 16, 16);
        return Scroll(rows);
    }

    /// <summary>Settings › Paired devices (84:2371).</summary>
    Control PairedDevices()
    {
        var s = Stack(0, TopRow("Paired devices", () => { PairedOpen = false; Render(); }), new Panel { Height = 8 }, Hair());
        foreach (var p in E.Peers.OrderBy(p => p.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            var unpair = Pill("Unpair", () => Ask($"Unpair {p.Name}?", "Both devices forget each other. You can pair again any time.", "Unpair", () => E.Unpair(p.Id)));
            AutomationProperties.SetName(unpair, $"Unpair {p.Name}");
            var row = Row((Body(p.Name, 13).Trim(), "*"), (unpair, "Auto"));
            row.Margin = new Thickness(0, 6);
            s.Children.Add(row);
            s.Children.Add(Hair());
        }
        if (E.Peers.Count == 0) { s.Children.Add(Body("None yet.", 13, P.Text2).Margin(0, 14, 0, 14)); s.Children.Add(Hair()); }
        s.Children.Add(Body("Unpairing deletes the keys on both devices. To connect again, pair with a new code.", 12, P.Text2).Margin(0, 12, 0, 4));
        var pair = Pill("Pair a new device", () => Go(Tab.Devices, Route.Host));
        pair.HorizontalAlignment = HorizontalAlignment.Right;
        s.Children.Add(pair.Margin(0, 24, 0, 0));
        s.Margin = new Thickness(16, 0, 16, 12);
        return Scroll(s);
    }

    void Ask(string title, string body, string action, Action run)
    {
        confirm = (title, body, action, run);
        Render();
        FocusNamed("Cancel", NavigationMethod.Tab);
    }

    Control ConfirmOverlay(string title, string body, string action, Action run)
    {
        void Close() { confirm = null; Render(); }
        var buttons = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 8, Margin = new Thickness(0, 16, 0, 0) };
        var cancel = Pill("Cancel", Close);
        var go = Pill(action, () => { confirm = null; run(); Render(); }, filled: true);
        Grid.SetColumn(go, 1);
        buttons.Children.Add(cancel);
        buttons.Children.Add(go);
        var card = new Border
        {
            Child = Stack(8, SerifText(title, 26).Trim(2), Body(body, 13, P.Text2), buttons), Padding = new Thickness(20), Margin = new Thickness(16),
            CornerRadius = new CornerRadius(12), Background = B(P.Surface), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        var scrim = new Border { Background = B(P.Bg, 0.7), Child = card, Focusable = false };
        scrim.KeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
        scrim.PointerPressed += (_, e) => { if (e.Source == scrim) Close(); };
        return scrim;
    }

    async void ChooseFolder()
    {
        if (TopLevel.GetTopLevel(this)?.StorageProvider is not { } sp) return;
        var picked = await sp.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = "Received files folder", AllowMultiple = false });
        if (picked.FirstOrDefault()?.TryGetLocalPath() is not { } path) return;
        E.ReceivedDir = path;
        AppSettings.ReceivedDir = path;   // the engine does not persist it
        Render();
    }
}

/// <summary>Image clip thumbnails: decoded off the UI thread at 84 px wide (42 px at 2×), cached by clip id.</summary>
static class Thumbs
{
    static readonly Dictionary<string, Bitmap?> cache = [];
    static readonly HashSet<string> loading = [];

    public static Control View(ClipRecord c)
    {
        var img = new Image { Width = 42, Height = 42, Stretch = Stretch.UniformToFill };
        var box = new Border { Width = 42, Height = 42, CornerRadius = new CornerRadius(4), ClipToBounds = true, Background = B(P.Surface), Child = img };
        if (cache.TryGetValue(c.Id, out var bmp))
        {
            if (bmp == null) return Stroked(FilePath, 20);   // gone (Clear history) or unreadable
            img.Source = bmp;
            return box;
        }
        if (loading.Add(c.Id))
        {
            var path = c.Files[0];
            Task.Run(() =>
            {
                try
                {
                    using var s = File.OpenRead(path);
                    return Bitmap.DecodeToWidth(s, 84, BitmapInterpolationMode.MediumQuality);
                }
                catch (Exception) { return null; }   // missing or not an image: the file icon instead
            }).ContinueWith(t => Dispatcher.UIThread.Post(() =>
            {
                loading.Remove(c.Id);
                if (cache.Count > 400) cache.Clear();
                cache[c.Id] = t.Result;
                img.Source = t.Result;
            }));
        }
        return box;
    }
}

/// <summary>QR modules (quiet zone included by QRCoder) drawn black on the white square.</summary>
sealed class QrView(string text) : Control
{
    readonly List<System.Collections.BitArray> m = new QRCodeGenerator().CreateQrCode(text, QRCodeGenerator.ECCLevel.M).ModuleMatrix;

    public override void Render(DrawingContext ctx)
    {
        var inset = 6.0;   // keeps modules clear of the 12 px corner radius
        var cell = (Math.Min(Bounds.Width, Bounds.Height) - inset * 2) / m.Count;
        var off = (Math.Min(Bounds.Width, Bounds.Height) - cell * m.Count) / 2;
        for (var y = 0; y < m.Count; y++)
            for (var x = 0; x < m.Count; x++)
                if (m[y][x]) ctx.FillRectangle(Brushes.Black, new Rect(off + x * cell, off + y * cell, cell + 0.25, cell + 0.25));
    }
}
