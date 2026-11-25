using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using static CtrlLink.App.Ui;

namespace CtrlLink.App;

/// <summary>Borderless 380×640 flyout above the taskbar, bottom-right of the work area. Hides on deactivate and Esc.</summary>
sealed class Flyout : Window
{
    public const double W = 380, H = 640, Gap = 12;
    public readonly Shell Shell;

    public Flyout(Shell shell)
    {
        Shell = shell;
        Title = "Ctrl Link";
        Icon = AppIcon;
        Width = W;
        Height = H;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        Topmost = true;
        Content = new Border { Child = shell, BorderThickness = new Thickness(1), BorderBrush = B(P.Hairline) };
        Deactivated += (_, _) => Hide();
        KeyDown += (_, e) => { if (e.Key == Key.Escape) Hide(); };
    }

    public static WindowIcon AppIcon => new(Avalonia.Platform.AssetLoader.Open(new Uri("avares://CtrlLink/Assets/icon.ico")));

    public DateTime HiddenAt { get; private set; }

    public override void Hide()
    {
        HiddenAt = DateTime.UtcNow;
        base.Hide();
    }

    public void Open()
    {
        Background = B(P.Bg);
        if (Content is Border b) b.BorderBrush = B(P.Hairline);
        Shell.Render();
        Show();
        Position = Corner(this, W, H, 0);
        Activate();
    }

    /// <summary>Bottom-right of the work area (above the taskbar), shifted left by <paramref name="leftOf"/> DIPs.</summary>
    public static PixelPoint Corner(Window w, double width, double height, double leftOf)
    {
        var s = w.Screens.ScreenFromWindow(w) ?? w.Screens.Primary ?? w.Screens.All[0];
        var wa = s.WorkingArea;
        var k = s.Scaling;
        return new PixelPoint(wa.Right - (int)((width + Gap + leftOf) * k), wa.Bottom - (int)((height + Gap) * k));
    }
}

/// <summary>Small notification card like the Figma toast; offers get Accept / Decline.</summary>
sealed class Toast : Window
{
    readonly DispatcherTimer timer = new();

    public Toast()
    {
        Width = 360;
        Icon = Flyout.AppIcon;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        SystemDecorations = SystemDecorations.None;
        ShowInTaskbar = false;
        ShowActivated = false;
        Topmost = true;
        timer.Tick += (_, _) => { timer.Stop(); Hide(); };
    }

    public void Show(string title, string sub, (string Text, Action Do)[] actions, Flyout? beside)
    {
        Background = B(P.Surface);
        Content = Card(title, sub, actions.Select(a => (a.Text, (Action)(() => { a.Do(); Hide(); }))).ToArray(), Hide);
        Show();
        Position = Flyout.Corner(this, Width, Bounds.Height, beside?.IsVisible == true ? Flyout.W + Flyout.Gap : 0);
        timer.Interval = TimeSpan.FromSeconds(actions.Length > 0 ? 30 : 6);
        timer.Stop();
        timer.Start();
    }

    public static Control Card(string title, string sub, (string Text, Action Do)[] actions, Action close)
    {
        var x = Clickable(Stroked(ClosePath, 14, P.Text2), close, "Dismiss", 14);
        x.Width = x.Height = 40;
        x.Margin = new Thickness(0, -6, -6, -6);
        var head = Row((HStack(8, Monogram(14, P.Text2), Label("Ctrl Link", size: 11)), "*"), (x, "Auto"));
        var body = Stack(4, head, Body(title, 14, P.Text, FontWeight.Medium).Trim(2).Margin(0, 4, 0, 0), Body(sub, 13, P.Text2).Trim(2));
        if (actions.Length > 0)
        {
            var row = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", actions.Select(_ => "*"))), Margin = new Thickness(0, 10, 0, 0) };
            for (var i = 0; i < actions.Length; i++)
            {
                var p = Pill(actions[i].Text, actions[i].Do, filled: i == 0).Margin(i == 0 ? 0 : 5, 0, i == actions.Length - 1 ? 0 : 5, 0);
                Grid.SetColumn(p, i);
                row.Children.Add(p);
            }
            body.Children.Add(row);
        }
        return new Border { Child = body, Padding = new Thickness(16, 10, 10, 16), Background = B(P.Surface), BorderBrush = B(P.Hairline), BorderThickness = new Thickness(1) };
    }
}
