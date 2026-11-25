using System.Text.Json;

namespace CtrlLink.Core;

/// Same fields as Mac's StoreData. Pairing keys are base64 of ISecretProtector-protected bytes.
sealed class StoreData
{
    public string DeviceId { get; set; } = Guid.NewGuid().ToString();
    public string DeviceName { get; set; } = "";
    public List<StoredPairing> Pairings { get; set; } = [];
    public bool AutoSend { get; set; } = true;
    public long AutoAcceptLimit { get; set; } = 100_000_000;
    public Dictionary<string, Dictionary<string, int>>? Daily { get; set; } // "yyyy-MM-dd" → kind → count, no content
}

sealed record StoredPairing(string Id, string Name, string Pf, string Key);

sealed class Store(string path, ISecretProtector protector)
{
    static readonly JsonSerializerOptions Opt = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    public (StoreData, List<Pairing>) Load(string defaultName)
    {
        if (File.Exists(path))
        {
            try
            {
                var d = JsonSerializer.Deserialize<StoreData>(File.ReadAllBytes(path), Opt) ?? throw new JsonException("null");
                return (d, d.Pairings.Select(p => new Pairing(p.Id, p.Name, p.Pf, protector.Unprotect(Convert.FromBase64String(p.Key)))).ToList());
            }
            catch (Exception e)
            {
                // Unreadable: keep it aside instead of overwriting the identity.
                J.Log($"store unreadable ({e.Message}), moving it aside");
                try { File.Move(path, $"{path}.bad-{DateTimeOffset.UtcNow.ToUnixTimeSeconds()}"); } catch (IOException) { }
            }
        }
        var s = new StoreData { DeviceName = defaultName };
        Save(s, []);
        return (s, []);
    }

    public void Save(StoreData d, IEnumerable<Pairing> pairings)
    {
        d.Pairings = pairings.Select(p => new StoredPairing(p.Id, p.Name, p.Pf, Convert.ToBase64String(protector.Protect(p.Key)))).ToList();
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
            File.WriteAllBytes(path + ".tmp", JsonSerializer.SerializeToUtf8Bytes(d, Opt));
            File.Move(path + ".tmp", path, true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { J.Log("cannot save store: " + e.Message); }
    }
}
