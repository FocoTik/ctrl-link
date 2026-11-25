using Avalonia;
using Avalonia.Animation;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Transformation;
using Avalonia.Threading;
using CtrlLink.Core;
using Path = Avalonia.Controls.Shapes.Path;

namespace CtrlLink.App;

/// <summary>DESIGN.md tokens.</summary>
sealed record Palette(bool Dark, Color Bg, Color Surface, Color Hairline, Color Text, Color Text2, Color Positive, Color Danger)
{
    public static readonly Palette DarkP = new(true, C("#000000"), C("#0F0F10"), C("#2A2A2C"), C("#FFFFFF"), C("#8E8E93"), C("#3DD68C"), C("#F0472E"));
    public static readonly Palette LightP = new(false, C("#FFFFFF"), C("#F4F4F5"), C("#DEDEE0"), C("#000000"), C("#6B6B70"), C("#1E9E5E"), C("#D63A22"));
    public static readonly Color Sky = C("#3AA4F2"), Amber = C("#F5C26B");
    static Color C(string hex) => Color.Parse(hex);
}

/// <summary>Small building blocks for the code-built views: type, hairlines, buttons, toggles, focus rings, icons.</summary>
static class Ui
{
    public static Palette P = Palette.DarkP;
    public static bool ReduceMotion;

    public static readonly FontFamily Serif = new("avares://CtrlLink/Assets/Newsreader-Variable.ttf#Newsreader");
    public static readonly FontFamily SerifLight = new("avares://CtrlLink/Assets/NewsreaderDisplay-Light.ttf#Newsreader Display");   // hero numerals, see make-fonts.py
    public static readonly FontFamily Sans = new("Segoe UI Variable Text, Segoe UI, fonts:Inter#Inter");

    public static IBrush B(Color c) => new SolidColorBrush(c);
    public static IBrush B(Color c, double alpha) => new SolidColorBrush(c, alpha);

    public static Color KindColor(ClipKind k) => Color.Parse(k switch
    {
        ClipKind.Text => "#F5C26B",
        ClipKind.Url => "#3AA4F2",
        ClipKind.Image => "#E9B6E0",
        _ => "#2549B8",
    });

    public static readonly ClipKind[] Kinds = [ClipKind.Text, ClipKind.Url, ClipKind.Image, ClipKind.Files];

    // Outlined 24-unit icons from the design (1.5 stroke, round caps), drawn with Stroked().
    public const string ChevronPath = "M9 6L15 12L9 18";
    public const string BackPath = "M19 12H5M11 18L5 12L11 6";
    public const string ClosePath = "M6 6L18 18M18 6L6 18";
    public const string TextIconPath = "M5 7V4.5H12M12 4.5H19V7M12 4.5V19.5M12 19.5H9M12 19.5H15";
    public const string LinkPath = "M10 14C10.3722 14.3779 10.8158 14.6781 11.3051 14.8829C11.7944 15.0878 12.3196 15.1933 12.85 15.1933C13.3804 15.1933 13.9056 15.0878 14.3949 14.8829C14.8842 14.6781 15.3278 14.3779 15.7 14L18.7 11C19.4559 10.2441 19.8805 9.21896 19.8805 8.15C19.8805 7.08104 19.4559 6.05587 18.7 5.3C17.9441 4.54413 16.919 4.11949 15.85 4.11949C14.781 4.11949 13.7559 4.54413 13 5.3L12 6.3M14 10C13.6278 9.62206 13.1842 9.32192 12.6949 9.11706C12.2056 8.91219 11.6804 8.80669 11.15 8.80669C10.6196 8.80669 10.0944 8.91219 9.60513 9.11706C9.11585 9.32192 8.6722 9.62206 8.3 10L5.3 13C4.54413 13.7559 4.11949 14.781 4.11949 15.85C4.11949 16.919 4.54413 17.9441 5.3 18.7C6.05587 19.4559 7.08104 19.8805 8.15 19.8805C9.21896 19.8805 10.2441 19.4559 11 18.7L12 17.7";
    public const string FilePath = "M14 3H7C6.46957 3 5.96086 3.21071 5.58579 3.58579C5.21071 3.96086 5 4.46957 5 5V19C5 19.5304 5.21071 20.0391 5.58579 20.4142C5.96086 20.7893 6.46957 21 7 21H17C17.5304 21 18.0391 20.7893 18.4142 20.4142C18.7893 20.0391 19 19.5304 19 19V8L14 3ZM14 3V8H19";

    // Figma Device-icons (69:3749), filled, 32-unit grid.
    public const string PhoneGlyph = "M22 2H10C9.20435 2 8.44129 2.31607 7.87868 2.87868C7.31607 3.44129 7 4.20435 7 5V27C7 27.7956 7.31607 28.5587 7.87868 29.1213C8.44129 29.6839 9.20435 30 10 30H22C22.7956 30 23.5587 29.6839 24.1213 29.1213C24.6839 28.5587 25 27.7956 25 27V5C25 4.20435 24.6839 3.44129 24.1213 2.87868C23.5587 2.31607 22.7956 2 22 2ZM9 8H23V24H9V8ZM10 4H22C22.2652 4 22.5196 4.10536 22.7071 4.29289C22.8946 4.48043 23 4.73478 23 5V6H9V5C9 4.73478 9.10536 4.48043 9.29289 4.29289C9.48043 4.10536 9.73478 4 10 4ZM22 28H10C9.73478 28 9.48043 27.8946 9.29289 27.7071C9.10536 27.5196 9 27.2652 9 27V26H23V27C23 27.2652 22.8946 27.5196 22.7071 27.7071C22.5196 27.8946 22.2652 28 22 28Z";
    public const string WindowsGlyph = "M27 17H17C16.7348 17 16.4804 17.1053 16.2929 17.2928C16.1054 17.4804 16 17.7347 16 18V25.1812C15.9997 25.4156 16.0818 25.6426 16.2319 25.8227C16.382 26.0027 16.5906 26.1243 16.8212 26.1662L26.8213 27.9837C26.8802 27.9947 26.94 28.0002 27 28C27.2652 28 27.5196 27.8946 27.7071 27.7071C27.8946 27.5195 28 27.2652 28 27V18C28 17.7347 27.8946 17.4804 27.7071 17.2928C27.5196 17.1053 27.2652 17 27 17ZM26 25.8012L18 24.3475V19H26V25.8012ZM13 17H5C4.73478 17 4.48043 17.1053 4.29289 17.2928C4.10536 17.4804 4 17.7347 4 18V23C4.00003 23.2341 4.08226 23.4609 4.23234 23.6407C4.38242 23.8204 4.59083 23.9418 4.82125 23.9837L12.8212 25.4387C12.8803 25.4491 12.9401 25.4546 13 25.455C13.2652 25.455 13.5196 25.3496 13.7071 25.1621C13.8946 24.9745 14 24.7202 14 24.455V18C14 17.7347 13.8946 17.4804 13.7071 17.2928C13.5196 17.1053 13.2652 17 13 17ZM12 23.2562L6 22.165V19H12V23.2562ZM27.6413 4.23245C27.5288 4.13853 27.397 4.07047 27.2553 4.0331C27.1136 3.99573 26.9654 3.98996 26.8213 4.0162L16.8212 5.83495C16.5908 5.87681 16.3824 5.99821 16.2323 6.17799C16.0823 6.35777 16 6.58452 16 6.8187V14C16 14.2652 16.1054 14.5195 16.2929 14.7071C16.4804 14.8946 16.7348 15 17 15H27C27.2652 15 27.5196 14.8946 27.7071 14.7071C27.8946 14.5195 28 14.2652 28 14V4.99995C28 4.85356 27.9679 4.70895 27.9059 4.57633C27.8439 4.44372 27.7536 4.32633 27.6413 4.23245ZM26 13H18V7.65245L26 6.1987V13ZM13.6412 6.77745C13.5288 6.68353 13.397 6.61547 13.2553 6.5781C13.1136 6.54073 12.9654 6.53496 12.8212 6.5612L4.82125 8.0162C4.59083 8.05806 4.38242 8.17946 4.23234 8.35924C4.08226 8.53902 4.00003 8.76577 4 8.99995V14C4 14.2652 4.10536 14.5195 4.29289 14.7071C4.48043 14.8946 4.73478 15 5 15H13C13.2652 15 13.5196 14.8946 13.7071 14.7071C13.8946 14.5195 14 14.2652 14 14V7.54495C14 7.39856 13.9679 7.25395 13.9059 7.12133C13.8439 6.98872 13.7536 6.87133 13.6412 6.77745ZM12 13H6V9.83495L12 8.74995V13Z";
    public const string LaptopGlyph = "M29 20.5H28V8.49996C28 7.70431 27.6839 6.94125 27.1213 6.37864C26.5587 5.81603 25.7956 5.49996 25 5.49996H7C6.20435 5.49996 5.44129 5.81603 4.87868 6.37864C4.31607 6.94125 4 7.70431 4 8.49996V20.5H3C2.73478 20.5 2.48043 20.6053 2.29289 20.7929C2.10536 20.9804 2 21.2347 2 21.5V23.5C2 24.2956 2.31607 25.0587 2.87868 25.6213C3.44129 26.1839 4.20435 26.5 5 26.5H27C27.7956 26.5 28.5587 26.1839 29.1213 25.6213C29.6839 25.0587 30 24.2956 30 23.5V21.5C30 21.2347 29.8946 20.9804 29.7071 20.7929C29.5196 20.6053 29.2652 20.5 29 20.5ZM6 8.49996C6 8.23474 6.10536 7.98039 6.29289 7.79285C6.48043 7.60531 6.73478 7.49996 7 7.49996H25C25.2652 7.49996 25.5196 7.60531 25.7071 7.79285C25.8946 7.98039 26 8.23474 26 8.49996V20.5H6V8.49996ZM28 23.5C28 23.7652 27.8946 24.0195 27.7071 24.2071C27.5196 24.3946 27.2652 24.5 27 24.5H5C4.73478 24.5 4.48043 24.3946 4.29289 24.2071C4.10536 24.0195 4 23.7652 4 23.5V22.5H28V23.5ZM19 10.5C19 10.7652 18.8946 11.0195 18.7071 11.2071C18.5196 11.3946 18.2652 11.5 18 11.5H14C13.7348 11.5 13.4804 11.3946 13.2929 11.2071C13.1054 11.0195 13 10.7652 13 10.5C13 10.2347 13.1054 9.98039 13.2929 9.79285C13.4804 9.60532 13.7348 9.49996 14 9.49996H18C18.2652 9.49996 18.5196 9.60532 18.7071 9.79285C18.8946 9.98039 19 10.2347 19 10.5Z";
    public const string DesktopMacGlyph = "M28.9141 23.1987C28.8391 23.0274 28.7179 22.8802 28.5641 22.7737C26.4428 21.3162 26.0016 18.83 26.0016 17C26.0016 14.7912 27.6853 12.8675 28.6891 11.9162C28.7878 11.8228 28.8664 11.7102 28.9202 11.5853C28.9739 11.4604 29.0016 11.3259 29.0016 11.19C29.0016 11.054 28.9739 10.9195 28.9202 10.7947C28.8664 10.6698 28.7878 10.5572 28.6891 10.4637C27.1041 8.96749 24.4791 7.99999 22.0016 7.99999C20.2219 8.00136 18.4823 8.52888 17.0016 9.51624C15.2742 8.35794 13.1972 7.83774 11.1278 8.04512C9.05845 8.25249 7.12599 9.17447 5.66282 10.6525C4.78811 11.5455 4.1018 12.6051 3.64463 13.7686C3.18747 14.932 2.96879 16.1754 3.00157 17.425C3.05106 19.5341 3.52024 21.6122 4.38172 23.538C5.2432 25.4638 6.47972 27.1986 8.01907 28.6412C8.94632 29.5173 10.1747 30.0037 11.4503 30H22.4103C23.0923 30.0013 23.7673 29.8625 24.3934 29.5922C25.0196 29.3219 25.5835 28.9259 26.0503 28.4287C26.9151 27.4981 27.6631 26.4655 28.2778 25.3537C29.1553 23.75 29.0428 23.5 28.9141 23.1987ZM24.5853 27.0662C24.3062 27.3627 23.969 27.5985 23.5949 27.7591C23.2207 27.9198 22.8175 28.0018 22.4103 28H11.4503C10.6863 28.0024 9.95054 27.7111 9.39532 27.1862C8.04791 25.9247 6.96538 24.4074 6.21099 22.7228C5.45659 21.0383 5.04544 19.2202 5.00157 17.375C4.97422 16.3946 5.14439 15.4187 5.50194 14.5054C5.85949 13.5921 6.39711 12.76 7.08282 12.0587C7.72347 11.4042 8.48884 10.8848 9.33369 10.5312C10.1785 10.1776 11.0857 9.99696 12.0016 9.99999H12.0991C13.6569 10.0164 15.1638 10.5571 16.3766 11.535C16.5539 11.677 16.7744 11.7544 17.0016 11.7544C17.2288 11.7544 17.4492 11.677 17.6266 11.535C18.8654 10.5362 20.4102 9.99417 22.0016 9.99999C23.5907 10.0184 25.1453 10.4654 26.5016 11.2937C24.8766 13.1087 24.0016 15.1025 24.0016 17C24.0016 19.9712 24.9566 22.3412 26.7741 23.9125C26.2017 25.0646 25.4644 26.127 24.5853 27.0662ZM17.0303 5.74999C17.3077 4.67588 17.9343 3.72449 18.8117 3.0456C19.689 2.36672 20.7672 1.99888 21.8766 1.99999H22.0016C22.2668 1.99999 22.5211 2.10535 22.7087 2.29288C22.8962 2.48042 23.0016 2.73477 23.0016 2.99999C23.0016 3.26521 22.8962 3.51956 22.7087 3.7071C22.5211 3.89463 22.2668 3.99999 22.0016 3.99999H21.8766C21.2114 3.99993 20.5651 4.22092 20.0393 4.6282C19.5134 5.03549 19.1379 5.60598 18.9716 6.24999C18.9053 6.50692 18.7396 6.72698 18.5111 6.86178C18.2825 6.99657 18.0097 7.03504 17.7528 6.96874C17.4959 6.90244 17.2758 6.73678 17.141 6.50822C17.0062 6.27966 16.9678 6.00692 17.0341 5.74999H17.0303Z";

    /// <summary>Device glyph by platform; Macs with "mini", "iMac" or "Studio" in the name get the desktop glyph.</summary>
    public static string DeviceGlyph(string pf, string name) => pf switch
    {
        "windows" => WindowsGlyph,
        "mac" => name.Contains("mini", StringComparison.OrdinalIgnoreCase) || name.Contains("imac", StringComparison.OrdinalIgnoreCase)
            || name.Contains("studio", StringComparison.OrdinalIgnoreCase) ? DesktopMacGlyph : LaptopGlyph,
        "android" => PhoneGlyph,
        _ => LaptopGlyph,
    };

    public static string PlatformName(string pf) => pf switch { "mac" => "Mac", "android" => "Android", "windows" => "Windows", _ => "Device" };

    static readonly Dictionary<string, Geometry> geometries = [];
    static Geometry Geo(string d) => geometries.TryGetValue(d, out var g) ? g : geometries[d] = Geometry.Parse(d);

    /// <summary>A filled glyph scaled into size × size.</summary>
    public static Control Icon(string path, double size = 16, Color? color = null) => new Path
    {
        Data = Geo(path), Fill = B(color ?? P.Text), Stretch = Stretch.Uniform, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>An outlined 24-unit icon (1.5 stroke) drawn at size × size, keeping the 24-unit padding.</summary>
    public static Control Stroked(string path, double size = 20, Color? color = null)
    {
        var p = new Path
        {
            Data = Geo(path), Stroke = B(color ?? P.Text), StrokeThickness = 1.5, StrokeLineCap = PenLineCap.Round, StrokeJoin = PenLineJoin.Round,
            Width = 24, Height = 24, Stretch = Stretch.None,
        };
        return new Viewbox { Child = new Canvas { Width = 24, Height = 24, Children = { p } }, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center };
    }

    /// <summary>The app mark (design/logo/options/6-merged-monogram.svg): two round-capped loops with arrowheads, rotated -45°.</summary>
    public static Control Monogram(double size, Color? color = null)
    {
        var c = B(color ?? P.Text);
        var canvas = new Canvas { Width = 24, Height = 24, RenderTransform = new RotateTransform(-45), RenderTransformOrigin = RelativePoint.Center };
        foreach (var loop in new[] { "M11 5H7A5 5 0 0 0 7 15H10.8", "M13 19H17A5 5 0 0 0 17 9H13.2" })
            canvas.Children.Add(new Path { Data = Geo(loop), Stroke = c, StrokeThickness = 3, StrokeLineCap = PenLineCap.Round });
        foreach (var head in new[] { "M14.2 5L11.2 2.9V7.1Z", "M9.8 19L12.8 21.1V16.9Z" })
            canvas.Children.Add(new Path { Data = Geo(head), Fill = c, Stroke = c, StrokeThickness = 1.5, StrokeJoin = PenLineJoin.Round });
        return new Viewbox { Child = canvas, Width = size, Height = size, VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
    }

    /// <summary>Figma line heights for the Windows text styles.</summary>
    static double LineHeightFor(double size) => size switch { <= 9 => 10, <= 11 => 14, <= 12 => 16, <= 13 => 18, <= 15 => 20, _ => Math.Round(size * 1.3) };

    /// <summary>Uppercase tracked label: 12/16 (UI/Windows/Label) by default, 11/14 (Label S), 6 % tracking.</summary>
    public static TextBlock Label(string text, Color? color = null, double size = 12) => new()
    {
        Text = text.ToUpperInvariant(),
        FontFamily = Sans,
        FontSize = size,
        LineHeight = LineHeightFor(size),
        FontWeight = FontWeight.Medium,
        LetterSpacing = size * 0.06,
        Foreground = B(color ?? P.Text2),
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    public static TextBlock Body(string text, double size = 15, Color? color = null, FontWeight weight = FontWeight.Normal) => new()
    {
        Text = text,
        FontFamily = Sans,
        FontSize = size,
        LineHeight = LineHeightFor(size),
        FontWeight = weight,
        Foreground = B(color ?? P.Text),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    /// <summary>Newsreader: 26/32 headlines, 34/40 large; 48 and up use the light display cut (hero numerals, code digits).</summary>
    public static TextBlock SerifText(string text, double size, Color? color = null) => new()
    {
        Text = text,
        FontFamily = size >= 48 ? SerifLight : Serif,
        FontSize = size,
        LineHeight = size switch { 26 => 32, 34 => 40, 64 => 68, >= 90 => size, _ => Math.Round(size * 1.23) },
        FontWeight = size >= 48 ? FontWeight.Light : FontWeight.Normal,
        LetterSpacing = size >= 90 ? -size * 0.02 : 0,
        Foreground = B(color ?? P.Text),
        TextWrapping = TextWrapping.Wrap,
        VerticalAlignment = VerticalAlignment.Center,
    };

    public static T Center<T>(this T t) where T : TextBlock
    {
        t.TextAlignment = TextAlignment.Center;
        t.HorizontalAlignment = HorizontalAlignment.Stretch;
        return t;
    }

    public static T Trim<T>(this T t, int lines = 1) where T : TextBlock
    {
        t.TextWrapping = lines == 1 ? TextWrapping.NoWrap : TextWrapping.Wrap;
        t.MaxLines = lines;
        t.TextTrimming = TextTrimming.CharacterEllipsis;
        return t;
    }

    public static T Margin<T>(this T c, double l, double t, double r, double b) where T : Control
    {
        c.Margin = new Thickness(l, t, r, b);
        return c;
    }

    public static Border Hair() => new() { Height = 1, Background = B(P.Hairline) };

    public static Grid Row(params (Control c, string width)[] cells)
    {
        var g = new Grid { ColumnDefinitions = new ColumnDefinitions(string.Join(",", cells.Select(x => x.width))) };
        for (var i = 0; i < cells.Length; i++)
        {
            Grid.SetColumn(cells[i].c, i);
            g.Children.Add(cells[i].c);
        }
        return g;
    }

    public static StackPanel Stack(double spacing, params Control[] children)
    {
        var s = new StackPanel { Spacing = spacing };
        s.Children.AddRange(children);
        return s;
    }

    public static StackPanel HStack(double spacing, params Control[] children)
    {
        var s = Stack(spacing, children);
        s.Orientation = Orientation.Horizontal;
        return s;
    }

    /// <summary>Keyboard focus ring: 2 px in the text colour, shown only for keyboard focus (Avalonia's FocusAdorner).</summary>
    public static T Ring<T>(this T c, double radius) where T : Control
    {
        c.FocusAdorner = new FuncTemplate<Control>(() =>
        {
            var ring = new Border { BorderBrush = B(P.Text), BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(radius + 3), Margin = new Thickness(-3), IsHitTestVisible = false };
            Avalonia.Controls.Primitives.AdornerLayer.SetIsClipEnabled(ring, false);   // the ring sits outside the control's bounds
            return ring;
        });
        return c;
    }

    static readonly TransformOperations Pressed = TransformOperations.Parse("scale(0.97)"), Rest = TransformOperations.Parse("scale(1)");

    /// <summary>Hover eases over 120 ms; press scales to 0.97 at once and springs back on release (no motion under reduce motion).</summary>
    static void Animate(Control c)
    {
        if (ReduceMotion) return;
        c.Transitions =
        [
            new DoubleTransition { Property = Visual.OpacityProperty, Duration = TimeSpan.FromMilliseconds(120), Easing = new CubicEaseOut() },
            new TransformOperationsTransition { Property = Visual.RenderTransformProperty, Duration = TimeSpan.FromMilliseconds(140), Easing = new CubicEaseOut() },
        ];
    }

    /// <summary>A focusable, clickable surface (Enter/Space activate). `state` gets (hover, pressed) changes; by default hover
    /// dims a little and press scales to 0.97.</summary>
    public static Border Clickable(Control child, Action onClick, string name, double radius = 0, bool enabled = true, Action<bool, bool>? state = null, bool scale = true)
    {
        var b = new Border { Child = child, Focusable = enabled, Cursor = enabled ? new Cursor(StandardCursorType.Hand) : Cursor.Default, Background = Brushes.Transparent, CornerRadius = new CornerRadius(radius) };
        b.Ring(radius);
        Avalonia.Automation.AutomationProperties.SetName(b, name);
        if (!enabled) return b;
        Animate(b);
        var hover = false;
        var down = false;
        void Update()
        {
            if (state != null) state(hover, down);
            else b.Opacity = hover && !down ? 0.82 : 1;
            if (scale && !ReduceMotion) b.RenderTransform = down ? Pressed : Rest;
        }
        b.PointerEntered += (_, _) => { hover = true; Update(); };
        b.PointerExited += (_, _) => { hover = false; down = false; Update(); };
        b.PointerPressed += (_, e) => { down = true; Update(); e.Handled = true; };
        b.PointerCaptureLost += (_, _) => { if (down) { down = false; Update(); } };
        b.PointerReleased += (_, e) =>
        {
            down = false;
            Update();
            // Released inside the control (by position: IsPointerOver can lag in a window that was shown without activation).
            var at = e.GetPosition(b);
            if (at.X >= 0 && at.Y >= 0 && at.X <= b.Bounds.Width && at.Y <= b.Bounds.Height) Dispatcher.UIThread.Post(onClick);
            e.Handled = true;
        };
        b.KeyDown += (_, e) =>
        {
            if (e.Key is not (Key.Enter or Key.Space)) return;
            e.Handled = true;
            Dispatcher.UIThread.Post(onClick);
        };
        return b;
    }

    /// <summary>The design's Button (19:596): 40 px capsule, 14/20 medium label, 20 px side padding. Filled = text-colour fill with
    /// two soft colour blobs (amber, sky) behind the label; Outlined = strong hairline whose lower edge fades to green on the left
    /// and red on the right. Hover 85 % / surface fill, pressed 70 % / hairline fill, disabled 35 %.</summary>
    public static Border Pill(string text, Action onClick, bool filled = false, bool enabled = true, double height = 40)
    {
        var label = Body(text, 14, filled ? P.Bg : P.Text, FontWeight.Medium).Trim();
        label.HorizontalAlignment = HorizontalAlignment.Center;
        label.TextAlignment = TextAlignment.Center;
        var layers = new Panel { ClipToBounds = true };
        Panel? blobs = null;
        if (filled && enabled)
        {
            blobs = new Panel { IsHitTestVisible = false };
            blobs.Children.Add(Blob(Palette.Amber, -32));
            blobs.Children.Add(Blob(Palette.Sky, 44));
            layers.Children.Add(blobs);
        }
        layers.Children.Add(new Border { Child = label, Padding = new Thickness(20, 0) });
        var body = new Border { Child = layers, CornerRadius = new CornerRadius(height / 2), ClipToBounds = true, Background = filled ? B(P.Text) : Brushes.Transparent };
        var host = new Panel { Children = { body } };
        if (!filled)
        {
            host.Children.Add(new Border { CornerRadius = new CornerRadius(height / 2), BorderBrush = B(P.Text), BorderThickness = new Thickness(1), IsHitTestVisible = false });
            if (enabled)
                host.Children.Add(new Border
                {
                    CornerRadius = new CornerRadius(height / 2), BorderThickness = new Thickness(1), IsHitTestVisible = false,
                    BorderBrush = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                        GradientStops =
                        {
                            new GradientStop(P.Positive, 0), new GradientStop(Color.FromArgb(0, P.Positive.R, P.Positive.G, P.Positive.B), 0.4),
                            new GradientStop(Color.FromArgb(0, P.Danger.R, P.Danger.G, P.Danger.B), 0.6), new GradientStop(P.Danger, 1),
                        },
                    },
                    OpacityMask = new LinearGradientBrush
                    {
                        StartPoint = new RelativePoint(0.5, 0, RelativeUnit.Relative), EndPoint = new RelativePoint(0.5, 1, RelativeUnit.Relative),
                        GradientStops = { new GradientStop(Colors.Transparent, 0), new GradientStop(Colors.Transparent, 0.5), new GradientStop(Colors.Black, 1) },
                    },
                });
        }
        var b = Clickable(host, onClick, text, height / 2, enabled, (hover, down) =>
        {
            if (filled)
            {
                body.Opacity = down ? 0.7 : hover ? 0.85 : 1;
                if (blobs != null) blobs.IsVisible = !hover && !down;
            }
            else body.Background = down ? B(P.Hairline) : hover ? B(P.Surface) : Brushes.Transparent;
        });
        b.Height = height;
        b.MinWidth = 64;
        if (!enabled) b.Opacity = 0.35;
        return b;
    }

    /// <summary>Soft diagonal colour smear inside the filled button (the Figma "gradient blob", a blurred 16 × 52 capsule at 39°).</summary>
    static Control Blob(Color c, double dx) => new Ellipse
    {
        Width = 44, Height = 84, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        RenderTransform = new TransformGroup { Children = { new RotateTransform(39), new TranslateTransform(dx, 0) } },
        Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop(Color.FromArgb(200, c.R, c.G, c.B), 0), new GradientStop(Color.FromArgb(120, c.R, c.G, c.B), 0.35),
                new GradientStop(Color.FromArgb(40, c.R, c.G, c.B), 0.7), new GradientStop(Color.FromArgb(0, c.R, c.G, c.B), 1),
            },
        },
    };

    /// <summary>Windows Toggle (19:625), 40 × 20. On: track from the text colour to amber with a background-coloured 14 px knob.
    /// Off: strong outline with a 12 px secondary knob. Visual only: the Settings row around it is the click target.</summary>
    public static Control ToggleVisual(bool on, bool enabled = true)
    {
        var knob = new Ellipse
        {
            Width = on ? 14 : 12, Height = on ? 14 : 12, Fill = B(on ? P.Bg : P.Text2),
            HorizontalAlignment = on ? HorizontalAlignment.Right : HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(on ? 3 : 4, 0),
        };
        return new Border
        {
            Width = 40, Height = 20, CornerRadius = new CornerRadius(10), Child = knob, Opacity = enabled ? 1 : 0.35,
            Background = on ? new LinearGradientBrush
            {
                StartPoint = new RelativePoint(0, 0.5, RelativeUnit.Relative), EndPoint = new RelativePoint(1, 0.5, RelativeUnit.Relative),
                GradientStops = { new GradientStop(P.Text, 0), new GradientStop(Palette.Amber, 1) },
            } : Brushes.Transparent,
            BorderBrush = B(P.Text), BorderThickness = new Thickness(on ? 0 : 1),
        };
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.#} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>"0:42" (with unit "min") under an hour, "1:02:03" ("h") over; an en dash until there is a rate.</summary>
    public static (string Value, string Unit) TimeLeft(long remaining, double rate, bool finished)
    {
        if (finished) return ("0:00", "min");
        if (rate <= 0) return ("–", "min");
        var s = (long)Math.Ceiling(remaining / rate);
        return s >= 3600 ? ($"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}", "h") : ($"{s / 60}:{s % 60:00}", "min");
    }

    public static string KindName(ClipKind k) => k switch { ClipKind.Url => "Link", ClipKind.Files => "Files", _ => k.ToString() };
}
