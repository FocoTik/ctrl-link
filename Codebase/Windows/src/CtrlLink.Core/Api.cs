// Public surface of CtrlLink.Core: the contract between the core (protocol, crypto, network) and the WPF app.
// Mirrors Mac/Sources/CtrlLinkCore/Engine.swift. Change this file only together with both sides.
// Threading: Engine captures SynchronizationContext.Current in Start() and raises every event and
// PropertyChanged on it, so the app can bind directly. All public members must be called from that thread.

using System.ComponentModel;
using System.Text.Json.Serialization;

namespace CtrlLink.Core;

public enum ClipKind { Text, Url, Image, Files }

/// <summary>A clip shown in History. From == null means this PC.</summary>
public sealed record ClipRecord(string Id, ClipKind Kind, string? From, DateTimeOffset Date, string? Text, IReadOnlyList<string> Files)
{
    [JsonIgnore] public string Preview => Text ?? string.Join(", ", Files.Select(Path.GetFileName));
}

/// <summary>Something that arrived from a peer and should go on the clipboard. Files are absolute paths in ReceivedDir.</summary>
public sealed record Received(ClipKind Kind, string? Text, IReadOnlyList<string> Files, string? Mime, byte[] Fingerprint, string FromName);

/// <summary>What the app wants to send (from the clipboard or the Send button).</summary>
public abstract record Outgoing
{
    public sealed record TextClip(string Text) : Outgoing;
    public sealed record UrlClip(string Url) : Outgoing;
    public sealed record ImageClip(string PngPath) : Outgoing;
    public sealed record FilesClip(IReadOnlyList<string> Paths) : Outgoing;
}

public sealed record Pairing(string Id, string Name, string Pf, byte[] Key);

/// <summary>Ip: the LAN address to show for the peer (IPv4 preferred, never loopback or link-local), null when unknown.</summary>
public sealed record PeerStatus(string Id, string Name, string Pf, bool Online, DateTimeOffset? LastSeen, string? Ip = null);

public sealed record PairWindow(string Code, string Uri, DateTimeOffset Expires, PairRequest? Request);

/// <summary>Code-mode request waiting for the user on the host side (§4.2).</summary>
public sealed record PairRequest(string Name, string Sas);

public abstract record JoinState
{
    public sealed record Idle : JoinState;
    public sealed record Connecting : JoinState;
    public sealed record Confirm(string Sas, string Host) : JoinState;
    public sealed record Done(string PeerName) : JoinState;
    public sealed record Failed(string Reason) : JoinState;
}

/// <summary>One speed sample: progress 0–1 when it was taken and the bytes per second over the second before.</summary>
public sealed record SpeedSample(double Progress, double BytesPerSecond);

/// <summary>A running transfer. Speeds: one sample a second, taken by the engine (so closing the flyout leaves no gap).
/// Rate: smoothed bytes/s for "time left", 0 until the first sample. Finished: the end was reported and the transfer
/// stays listed (Done == Total) for a moment so the UI can show 100 %.</summary>
public sealed record Transfer(string Id, string Name, string Peer, bool Incoming, long Done, long Total, DateTimeOffset Started,
    IReadOnlyList<SpeedSample>? Speeds = null, double Rate = 0, bool Finished = false);

public sealed record Offer(string Id, string PeerId, string PeerName, int Count, long Size);

public abstract record Note
{
    public sealed record Connected(string Name) : Note;
    public sealed record Disconnected(string Name) : Note;
    public sealed record Clip(string Name, ClipKind Kind, string Preview) : Note;
    public sealed record OfferNote(Offer Offer) : Note;
    public sealed record Unpaired(string Name) : Note;
    public sealed record Unknown(string Name) : Note;
    public sealed record Version(string Name) : Note;
    public sealed record Failed(string Message) : Note;
}

public interface IEngine : INotifyPropertyChanged, IDisposable
{
    const string ServiceType = "_ctrllink._tcp";
    const string AppVersion = "1.0";
    const int DefaultPort = 47823;

    string DeviceId { get; }
    string DeviceName { get; set; }
    bool AutoSend { get; set; }
    long AutoAcceptLimit { get; set; }
    string ReceivedDir { get; set; }
    int? Port { get; }

    IReadOnlyList<PeerStatus> Peers { get; }
    IReadOnlyList<ClipRecord> Clips { get; }
    PairWindow? PairingWindow { get; }
    JoinState Join { get; }
    IReadOnlyList<Transfer> Transfers { get; }
    IReadOnlyList<Offer> Offers { get; }

    /// <summary>Clips per kind on a local calendar day, for the History chart (persisted counts, no content).</summary>
    IReadOnlyDictionary<ClipKind, int> CountsOn(DateOnly day);

    event Action<Received>? Receive;
    event Action<Note>? Notify;

    void Start();

    // Pairing (§4)
    void OpenPairingWindow();
    void ClosePairingWindow();
    void AnswerPairRequest(bool accept);
    void JoinWithUri(string ctrllinkUri);
    void JoinWithCode(string sixDigits);
    void ConfirmJoin(bool accept);   // joiner side after the SAS is shown; false aborts
    void ResetJoin();
    void Unpair(string peerId);

    // Clips (§6)
    void Send(Outgoing clip);
    /// <summary>Remember a fingerprint the app just wrote to the clipboard so it is not sent back (§6.4).</summary>
    void MarkWritten(byte[] fingerprint);
    void AcceptOffer(string clipId);
    void RejectOffer(string clipId);
    void CancelTransfer(string clipId);

    /// <summary>Forgets the clip list (history.json) and the daily counts, and deletes the app's own temporary copies
    /// (clipboard images, long text) unless a peer is still fetching them. Received files are never touched.</summary>
    void ClearHistory();
}

/// <summary>Where state lives. The app passes a DPAPI-backed protector; tests pass a no-op.</summary>
public interface ISecretProtector
{
    byte[] Protect(byte[] plain);
    byte[] Unprotect(byte[] sealedBytes);
}

// Construction (implemented in Engine.cs):
//   new Engine(EngineOptions options)
public sealed record EngineOptions(
    string StorePath,               // e.g. %APPDATA%\Ctrl Link\state.json
    string ReceivedDir,             // e.g. %USERPROFILE%\Downloads\Ctrl Link
    ISecretProtector Protector,
    string Platform = "windows",
    bool Advertise = true,          // mDNS advertise + browse
    int Port = IEngine.DefaultPort,
    double RetryDelaySeconds = 10,  // §5.1, tests shorten it
    string? DefaultDeviceName = null);
