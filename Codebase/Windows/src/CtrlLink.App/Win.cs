using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using CtrlLink.Core;
using Microsoft.Win32;
using SkiaSharp;

namespace CtrlLink.App;

/// <summary>Windows integration. Compiles everywhere, called only when OperatingSystem.IsWindows().</summary>
static class Paths
{
    // On macOS ApplicationData is ~/Library/Application Support, where the Mac app keeps CtrlLink/state.json:
    // a preview run of this app must never share that identity, so non-Windows runs get their own folder.
    static string Name => OperatingSystem.IsWindows() ? "Ctrl Link" : "CtrlLink-WindowsPreview";
    public static string State => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), Name, "state.json");
    public static string Received => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads", Name);
    public static string Welcomed => Path.Combine(Path.GetDirectoryName(State)!, "welcomed");
}

/// <summary>App-owned settings the engine does not persist (%APPDATA%\CtrlLink\app.json).</summary>
static class AppSettings
{
    static string File => Path.Combine(Path.GetDirectoryName(Paths.State)!, "app.json");
    static JsonObject? cache;

    static JsonObject Data
    {
        get
        {
            if (cache != null) return cache;
            try { cache = JsonNode.Parse(System.IO.File.ReadAllText(File)) as JsonObject; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException) { }
            return cache ??= [];
        }
    }

    static void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(File)!);
            System.IO.File.WriteAllText(File, Data.ToJsonString());
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    public static string? ReceivedDir
    {
        get => Data["receivedDir"] is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;
        set { Data["receivedDir"] = value; Save(); }
    }

    /// <summary>Settings › Notification sound. On by default, as in the Windows Settings frame (55:1754).</summary>
    public static bool Sound
    {
        get => Data["sound"] is not JsonValue v || !v.TryGetValue<bool>(out var b) || b;
        set { Data["sound"] = value; Save(); }
    }
}

/// <summary>The Windows notification sound for toasts (our toast window is silent by itself). Nothing when the setting is off.</summary>
static class ToastSound
{
    const uint SND_ASYNC = 0x1, SND_NODEFAULT = 0x2, SND_ALIAS = 0x10000;

    public static void Play()
    {
        if (!AppSettings.Sound || !OperatingSystem.IsWindows()) return;
        try { PlaySoundW("Notification.Default", 0, SND_ALIAS | SND_ASYNC | SND_NODEFAULT); }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { }
    }

    [DllImport("winmm.dll", CharSet = CharSet.Unicode)] static extern bool PlaySoundW(string sound, nint module, uint flags);
}

sealed class NoProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => plain;
    public byte[] Unprotect(byte[] sealedBytes) => sealedBytes;
}

[SupportedOSPlatform("windows")]
sealed class DpapiProtector : ISecretProtector
{
    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, null, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] sealedBytes) => ProtectedData.Unprotect(sealedBytes, null, DataProtectionScope.CurrentUser);
}

[SupportedOSPlatform("windows")]
static class StartWithWindows
{
    const string Key = @"Software\Microsoft\Windows\CurrentVersion\Run";

    public static bool Enabled
    {
        get => Registry.CurrentUser.OpenSubKey(Key)?.GetValue("Ctrl Link") is string;
        set
        {
            using var k = Registry.CurrentUser.CreateSubKey(Key);
            if (value) k.SetValue("Ctrl Link", $"\"{Environment.ProcessPath}\"");
            else k.DeleteValue("Ctrl Link", false);
        }
    }
}

static class SystemPrefs
{
    public static bool ReduceMotion => OperatingSystem.IsWindows() && SystemParametersInfoW(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out var on, 0) && !on;

    /// <summary>Taskbar theme (differs from the app theme on Windows 11's default "dark taskbar, light apps").</summary>
    public static bool LightTaskbar => OperatingSystem.IsWindows() && Registry.GetValue(
        @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "SystemUsesLightTheme", 0) is 1;

    [DllImport("user32.dll")] static extern bool SystemParametersInfoW(uint action, uint param, out bool value, uint ini);
}

/// <summary>Clipboard hook (AddClipboardFormatListener on a message-only window) plus read/write of text, files and images.</summary>
sealed class WinClipboard : IDisposable
{
    const uint WM_CLIPBOARDUPDATE = 0x031D, CF_UNICODETEXT = 13, CF_HDROP = 15, CF_DIB = 8, GMEM_MOVEABLE = 2;
    static readonly uint CF_PNG = RegisterClipboardFormatW("PNG");
    // Password managers and other apps mark secrets with these; such copies are neither sent nor kept in History.
    static readonly uint CF_EXCLUDE = RegisterClipboardFormatW("ExcludeClipboardContentFromMonitorProcessing"),
        CF_VIEWER_IGNORE = RegisterClipboardFormatW("Clipboard Viewer Ignore"),
        CF_HISTORY = RegisterClipboardFormatW("CanIncludeInClipboardHistory");

    readonly WndProc proc;   // keep the delegate alive while the window exists
    readonly nint hwnd;
    public event Action? Changed;

    [SupportedOSPlatform("windows")]
    public WinClipboard()
    {
        proc = (h, m, w, l) =>
        {
            // Skip updates we caused ourselves (we own the clipboard after Write), so received clips are not echoed back.
            if (m == WM_CLIPBOARDUPDATE && GetClipboardOwner() != hwnd) Changed?.Invoke();
            return DefWindowProcW(h, m, w, l);
        };
        var wc = new WNDCLASSEX { cbSize = (uint)Marshal.SizeOf<WNDCLASSEX>(), lpfnWndProc = Marshal.GetFunctionPointerForDelegate(proc), hInstance = GetModuleHandleW(null), lpszClassName = "CtrlLinkClipboard" };
        RegisterClassExW(ref wc);
        hwnd = CreateWindowExW(0, wc.lpszClassName!, "", 0, 0, 0, 0, 0, -3 /* HWND_MESSAGE */, 0, wc.hInstance, 0);
        if (hwnd == 0 || !AddClipboardFormatListener(hwnd)) throw new InvalidOperationException($"Clipboard listener failed ({Marshal.GetLastWin32Error()})");
    }

    [SupportedOSPlatform("windows")]
    public void Dispose()
    {
        RemoveClipboardFormatListener(hwnd);
        DestroyWindow(hwnd);
    }

    /// <summary>What is on the clipboard now, as something to send. Files win over text, text over images
    /// (Office puts a picture of the selection next to copied text).</summary>
    [SupportedOSPlatform("windows")]
    public Outgoing? Read()
    {
        if (!Open()) return null;
        try
        {
            if (Sensitive()) return null;
            if (IsClipboardFormatAvailable(CF_HDROP))
            {
                var h = GetClipboardData(CF_HDROP);
                var n = DragQueryFileW(h, uint.MaxValue, null, 0);
                var files = new List<string>();
                for (uint i = 0; i < n; i++)
                {
                    var sb = new StringBuilder((int)DragQueryFileW(h, i, null, 0) + 1);
                    DragQueryFileW(h, i, sb, (uint)sb.Capacity);
                    files.Add(sb.ToString());
                }
                return files.Count > 0 ? new Outgoing.FilesClip(files) : null;
            }
            if (IsClipboardFormatAvailable(CF_UNICODETEXT))
            {
                var h = GetClipboardData(CF_UNICODETEXT);
                var text = Marshal.PtrToStringUni(GlobalLock(h));
                GlobalUnlock(h);
                if (string.IsNullOrEmpty(text)) return null;
                var t = text.Trim();
                return !t.Any(char.IsWhiteSpace) && Uri.TryCreate(t, UriKind.Absolute, out var u) && (u.Scheme == "http" || u.Scheme == "https")
                    ? new Outgoing.UrlClip(t) : new Outgoing.TextClip(text);
            }
            var png = IsClipboardFormatAvailable(CF_PNG) ? Bytes(CF_PNG) : IsClipboardFormatAvailable(CF_DIB) ? DibToPng(Bytes(CF_DIB)) : null;
            if (png == null) return null;
            var path = Path.Combine(Path.GetTempPath(), "Ctrl Link", $"clip-{DateTime.Now:yyyyMMdd-HHmmss-fff}.png");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, png);
            return new Outgoing.ImageClip(path);
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Puts a clip on the clipboard: text, URL, image (bitmap + PNG) or files (CF_HDROP).</summary>
    [SupportedOSPlatform("windows")]
    public void Write(ClipKind kind, string? text, IReadOnlyList<string> files)
    {
        if (!Open()) return;
        try
        {
            EmptyClipboard();
            switch (kind)
            {
                case ClipKind.Text or ClipKind.Url when text != null:
                    Put(CF_UNICODETEXT, Encoding.Unicode.GetBytes(text + "\0"));
                    break;
                case ClipKind.Image when files.Count > 0 && File.Exists(files[0]):
                    var png = File.ReadAllBytes(files[0]);
                    Put(CF_PNG, png);
                    if (PngToDib(png) is { } dib) Put(CF_DIB, dib);
                    break;
                default:
                    if (files.Count == 0) break;
                    // DROPFILES { pFiles = 20, pt, fNC, fWide = 1 } then a double-null-terminated UTF-16 list.
                    var list = Encoding.Unicode.GetBytes(string.Join("\0", files) + "\0\0");
                    var buf = new byte[20 + list.Length];
                    BitConverter.GetBytes(20).CopyTo(buf, 0);
                    BitConverter.GetBytes(1).CopyTo(buf, 16);
                    list.CopyTo(buf, 20);
                    Put(CF_HDROP, buf);
                    break;
            }
        }
        finally { CloseClipboard(); }
    }

    /// <summary>True when the clipboard owner asked monitors and clipboard history to skip this content
    /// ("ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore", or "CanIncludeInClipboardHistory" = 0).</summary>
    static bool Sensitive()
    {
        if (IsClipboardFormatAvailable(CF_EXCLUDE) || IsClipboardFormatAvailable(CF_VIEWER_IGNORE)) return true;
        return IsClipboardFormatAvailable(CF_HISTORY) && Bytes(CF_HISTORY) is { Length: >= 4 } b && BitConverter.ToInt32(b, 0) == 0;
    }

    bool Open()
    {
        for (var i = 0; i < 10; i++)   // another app may hold the clipboard for a moment
        {
            if (OpenClipboard(hwnd)) return true;
            Thread.Sleep(15);
        }
        return false;
    }

    static byte[]? Bytes(uint format)
    {
        var h = GetClipboardData(format);
        if (h == 0) return null;
        var size = (int)GlobalSize(h);
        var p = GlobalLock(h);
        try
        {
            var b = new byte[size];
            Marshal.Copy(p, b, 0, size);
            return b;
        }
        finally { GlobalUnlock(h); }
    }

    static void Put(uint format, byte[] data)
    {
        var h = GlobalAlloc(GMEM_MOVEABLE, (nuint)data.Length);
        Marshal.Copy(data, 0, GlobalLock(h), data.Length);
        GlobalUnlock(h);
        if (SetClipboardData(format, h) == 0) GlobalFree(h);   // on success the system owns the memory
    }

    internal static byte[]? DibToPng(byte[]? dib)
    {
        if (dib == null || dib.Length < 40) return null;
        int headerSize = BitConverter.ToInt32(dib, 0), bits = BitConverter.ToUInt16(dib, 14), compression = BitConverter.ToInt32(dib, 16), used = BitConverter.ToInt32(dib, 32);
        var offset = 14 + headerSize + (compression == 3 && headerSize == 40 ? 12 : 0) + (bits <= 8 ? (used == 0 ? 1 << bits : used) * 4 : 0);
        var bmp = new byte[14 + dib.Length];
        bmp[0] = (byte)'B'; bmp[1] = (byte)'M';
        BitConverter.GetBytes(bmp.Length).CopyTo(bmp, 2);
        BitConverter.GetBytes(offset).CopyTo(bmp, 10);
        dib.CopyTo(bmp, 14);
        using var image = SKBitmap.Decode(bmp);
        return image?.Encode(SKEncodedImageFormat.Png, 100).ToArray();
    }

    internal static byte[]? PngToDib(byte[] png)
    {
        using var src = SKBitmap.Decode(png);
        if (src == null) return null;
        using var img = src.Copy(SKColorType.Bgra8888);
        int w = img.Width, h = img.Height, stride = w * 4;
        var dib = new byte[40 + stride * h];
        BitConverter.GetBytes(40).CopyTo(dib, 0);
        BitConverter.GetBytes(w).CopyTo(dib, 4);
        BitConverter.GetBytes(h).CopyTo(dib, 8);           // positive height: bottom-up rows
        BitConverter.GetBytes((short)1).CopyTo(dib, 12);
        BitConverter.GetBytes((short)32).CopyTo(dib, 14);
        BitConverter.GetBytes(stride * h).CopyTo(dib, 20);
        var pixels = img.Bytes;
        for (var y = 0; y < h; y++) Buffer.BlockCopy(pixels, y * stride, dib, 40 + (h - 1 - y) * stride, stride);
        return dib;
    }

    delegate nint WndProc(nint h, uint m, nint w, nint l);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WNDCLASSEX
    {
        public uint cbSize, style;
        public nint lpfnWndProc;
        public int cbClsExtra, cbWndExtra;
        public nint hInstance, hIcon, hCursor, hbrBackground;
        public string? lpszMenuName, lpszClassName;
        public nint hIconSm;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern ushort RegisterClassExW(ref WNDCLASSEX wc);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] static extern nint CreateWindowExW(uint ex, string cls, string name, uint style, int x, int y, int w, int h, nint parent, nint menu, nint inst, nint param);
    [DllImport("user32.dll")] static extern nint DefWindowProcW(nint h, uint m, nint w, nint l);
    [DllImport("user32.dll")] static extern bool DestroyWindow(nint h);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern nint GetModuleHandleW(string? name);
    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(nint h);
    [DllImport("user32.dll")] static extern bool RemoveClipboardFormatListener(nint h);
    [DllImport("user32.dll")] static extern nint GetClipboardOwner();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern uint RegisterClipboardFormatW(string name);
    [DllImport("user32.dll")] static extern bool OpenClipboard(nint h);
    [DllImport("user32.dll")] static extern bool CloseClipboard();
    [DllImport("user32.dll")] static extern bool EmptyClipboard();
    [DllImport("user32.dll")] static extern bool IsClipboardFormatAvailable(uint f);
    [DllImport("user32.dll")] static extern nint GetClipboardData(uint f);
    [DllImport("user32.dll")] static extern nint SetClipboardData(uint f, nint h);
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] static extern uint DragQueryFileW(nint drop, uint i, StringBuilder? file, uint size);
    [DllImport("kernel32.dll")] static extern nint GlobalAlloc(uint flags, nuint size);
    [DllImport("kernel32.dll")] static extern nint GlobalFree(nint h);
    [DllImport("kernel32.dll")] static extern nint GlobalLock(nint h);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(nint h);
    [DllImport("kernel32.dll")] static extern nuint GlobalSize(nint h);
}
