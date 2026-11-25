using System.ComponentModel;
using System.Runtime.CompilerServices;
using Avalonia.Threading;
using CtrlLink.Core;

namespace CtrlLink.App;

/// <summary>Sample-data engine for previews and --screenshots. State setters are public so scenarios can pose it.</summary>
sealed class FakeEngine : IEngine
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action<Received>? Receive { add { } remove { } }   // the fake never receives
    public event Action<Note>? Notify { add { } remove { } }

    static readonly DateTimeOffset Now = DateTimeOffset.Now;

    string name = "Studio PC";
    bool autoSend = true;
    long limit = 25L << 20;
    string received = @"C:\Users\Jubaer\Downloads\Ctrl Link";
    IReadOnlyList<PeerStatus> peers = [
        new("redmi", "Redmi Note 12", "android", true, Now, "192.168.1.21"),
    ];
    IReadOnlyList<ClipRecord> clips = [
        new("1", ClipKind.Text, "Redmi Note 12", Now.AddMinutes(-3), "Meeting moved to 4 pm", []),
        new("2", ClipKind.Url, "Pixel 7", Now.AddMinutes(-28), "figma.com/design/ctrl-link", []),
        new("3", ClipKind.Image, null, Now.AddMinutes(-55), null, [@"C:\Users\Jubaer\Downloads\Ctrl Link\Screenshot 13.04.png"]),
        new("4", ClipKind.Files, "Jubaer's MacBook", Now.AddHours(-2), null, [@"C:\Users\Jubaer\Downloads\Ctrl Link\report-q3.pdf", @"C:\Users\Jubaer\Downloads\Ctrl Link\notes.txt"]),
        new("5", ClipKind.Text, null, Now.AddHours(-3), "git push origin main", []),
    ];
    PairWindow? pairing;
    JoinState join = new JoinState.Idle();
    IReadOnlyList<Transfer> transfers = [];
    IReadOnlyList<Offer> offers = [];

    public string DeviceId => "studio-pc";
    public string DeviceName { get => name; set => Set(ref name, value); }
    public bool AutoSend { get => autoSend; set => Set(ref autoSend, value); }
    public long AutoAcceptLimit { get => limit; set => Set(ref limit, value); }
    public string ReceivedDir { get => received; set => Set(ref received, value); }
    public int? Port => IEngine.DefaultPort;
    public IReadOnlyList<PeerStatus> Peers { get => peers; set => Set(ref peers, value); }
    public IReadOnlyList<ClipRecord> Clips { get => clips; set => Set(ref clips, value); }
    public PairWindow? PairingWindow { get => pairing; set => Set(ref pairing, value); }
    public JoinState Join { get => join; set => Set(ref join, value); }
    public IReadOnlyList<Transfer> Transfers { get => transfers; set => Set(ref transfers, value); }
    public IReadOnlyList<Offer> Offers { get => offers; set => Set(ref offers, value); }

    // Text, Url, Image, Files for the last 7 days (oldest first); totals 71, today 14.
    public int[][] Week = [[4, 4, 0, 3], [3, 3, 2, 0], [3, 0, 2, 3], [5, 3, 2, 2], [4, 3, 0, 2], [3, 0, 2, 0], [4, 4, 2, 4]];

    public IReadOnlyDictionary<ClipKind, int> CountsOn(DateOnly day)
    {
        var i = 6 - (DateOnly.FromDateTime(DateTime.Now).DayNumber - day.DayNumber);
        var c = i is >= 0 and < 7 ? Week[i] : [0, 0, 0, 0];
        return new Dictionary<ClipKind, int> { [ClipKind.Text] = c[0], [ClipKind.Url] = c[1], [ClipKind.Image] = c[2], [ClipKind.Files] = c[3] };
    }

    public void Start() { }
    public void Dispose() { }

    public void OpenPairingWindow() => PairingWindow = new("042917", "ctrllink://pair?h=192.168.1.40&p=47823&c=042917&f=8F3A9C1D2B7E4F60", DateTimeOffset.Now.AddSeconds(42), null);
    public void ClosePairingWindow() => PairingWindow = null;
    public void AnswerPairRequest(bool accept) => PairingWindow = null;
    public void JoinWithUri(string uri) => JoinWithCode("000000");
    public void JoinWithCode(string code)
    {
        Join = new JoinState.Connecting();
        DispatcherTimer.RunOnce(() => Join = new JoinState.Confirm("482193", "Jubaer's MacBook"), TimeSpan.FromSeconds(1));
    }
    public void ConfirmJoin(bool accept) => Join = accept ? new JoinState.Done("Jubaer's MacBook") : new JoinState.Idle();
    public void ResetJoin() => Join = new JoinState.Idle();
    public void Unpair(string peerId) => Peers = peers.Where(p => p.Id != peerId).ToList();

    public void Send(Outgoing clip)
    {
        var (kind, text, files) = clip switch
        {
            Outgoing.TextClip t => (ClipKind.Text, t.Text, (IReadOnlyList<string>)[]),
            Outgoing.UrlClip u => (ClipKind.Url, u.Url, []),
            Outgoing.ImageClip i => (ClipKind.Image, null, [i.PngPath]),
            Outgoing.FilesClip f => (ClipKind.Files, null, f.Paths),
            _ => throw new ArgumentOutOfRangeException(nameof(clip)),
        };
        Clips = [new(Guid.NewGuid().ToString(), kind, null, DateTimeOffset.Now, text, files), .. clips];
    }

    public void MarkWritten(byte[] fingerprint) { }
    public void AcceptOffer(string clipId) => Offers = offers.Where(o => o.Id != clipId).ToList();
    public void RejectOffer(string clipId) => AcceptOffer(clipId);
    public void CancelTransfer(string clipId) => Transfers = transfers.Where(t => t.Id != clipId).ToList();
    public void ClearHistory() { Clips = []; Week = [.. Enumerable.Range(0, 7).Select(_ => new[] { 0, 0, 0, 0 })]; }

    void Set<T>(ref T field, T value, [CallerMemberName] string? prop = null)
    {
        field = value;
        PropertyChanged?.Invoke(this, new(prop));
    }
}
