using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steam2Browser;

public sealed class Settings
{
    /// <summary>Root for downloads; blobs land in DataDir/blobs and dats in DataDir/dats, the layout extract.exe expects.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Where the index files (dats_dates.txt, blobs_dates.txt) and the size cache live.</summary>
    public string IndexDir { get; set; } = "";

    public string MirrorId { get; set; } = "de";
    public bool Failover { get; set; } = true;

    public int Concurrency { get; set; } = 8;

    /// <summary>
    /// Download in two phases — every blob first, then every dat — each with its own stream count.
    /// Blobs are kilobytes, so many at once costs nothing; dats are large and the mirrors ramp a
    /// connection up over time, so only a couple of sustained streams are used for them.
    /// Turn off to fall back to <see cref="Concurrency"/> parallel files and ranged segments.
    /// </summary>
    public bool PhasedDownloads { get; set; } = true;

    /// <summary>Streams used during the blob phase. They are tiny, so latency dominates.</summary>
    public int BlobConcurrency { get; set; } = 32;

    /// <summary>
    /// Streams used during the dat phase. Kept low on purpose: these mirrors start a connection
    /// slow and speed it up while it keeps asking, so many parallel streams all sit at the cold rate.
    /// </summary>
    public int DatConcurrency { get; set; } = 2;

    /// <summary>
    /// How many dats ahead to touch with a one-byte request when a download starts, so the mirror
    /// has the next files ready. 0 disables it. Fire-and-forget, so it cannot slow anything down.
    /// </summary>
    public int WarmupLookahead { get; set; } = 2;

    /// <summary>
    /// Dats at least this large are fetched one at a time, after the smaller ones. Two concurrent
    /// long sequential reads make disk-backed storage seek between them, which costs more than the
    /// parallelism gains; short reads never get far enough for that to matter. 0 disables the split.
    /// </summary>
    public long BigFileBytes { get; set; } = 30_000_000L;

    /// <summary>
    /// Byte length of the last successful dats/ and blobs/ listing fetch, so the next one can show
    /// a real percentage. nginx builds these listings on the fly and sends them chunked with no
    /// Content-Length, so the size of the previous fetch is the only total available. The seeded
    /// figures are a measured estimate and are replaced by exact values after the first run.
    /// </summary>
    public long DatListingBytes { get; set; } = 21_000_000L;

    public long BlobListingBytes { get; set; } = 21_000_000L;
    public bool VerifyHashes { get; set; } = true;
    public int TorrentPort { get; set; }

    public string ExtractOutDir { get; set; } = "";

    /// <summary>
    /// Trackers to announce to on top of the ones inside the magnet. Useful when those are
    /// unreachable — the magnet's three all resolve to one address that some networks block
    /// outright. Null means "use the defaults"; an empty array means "none, rely on DHT".
    /// </summary>
    public string[]? ExtraTrackers { get; set; }

    public static readonly string[] DefaultExtraTrackers =
    [
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://tracker.dler.org:6969/announce",
        "http://tracker.openbittorrent.com:80/announce",
    ];

    public string[] TrackersToUse => ExtraTrackers ?? DefaultExtraTrackers;

    [JsonIgnore] public string ConfigPath { get; set; } = "";

    /// <summary>Everything the app writes lives here, in one folder beside the executable.</summary>
    public const string RootFolder = "steam2info";

    public static string RootFor(string baseDir) => Path.Combine(baseDir, RootFolder);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load(string baseDir)
    {
        string root = RootFor(baseDir);
        Directory.CreateDirectory(root);

        // Installs from before the move kept their files loose beside the exe; bring them along.
        Migrate(baseDir, root, "settings.json");
        Migrate(baseDir, root, "names.jsonl");

        string path = Path.Combine(root, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings()
                : new Settings();
        }
        catch
        {
            s = new Settings();
        }

        s.ConfigPath = path;
        if (string.IsNullOrWhiteSpace(s.DataDir)) s.DataDir = Path.Combine(root, "archive");
        if (string.IsNullOrWhiteSpace(s.IndexDir)) s.IndexDir = Path.Combine(root, "index");
        if (string.IsNullOrWhiteSpace(s.ExtractOutDir)) s.ExtractOutDir = Path.Combine(root, "extracted");
        if (s.Concurrency is < 1 or > 64) s.Concurrency = 8;
        if (s.BlobConcurrency is < 1 or > 128) s.BlobConcurrency = 32;
        if (s.DatConcurrency is < 1 or > 64) s.DatConcurrency = 2;
        if (s.WarmupLookahead is < 0 or > 16) s.WarmupLookahead = 2;
        if (s.BigFileBytes < 0) s.BigFileBytes = 30_000_000L;
        if (s.TorrentPort is < 0 or > 65535) s.TorrentPort = 0;
        return s;
    }

    private static void Migrate(string baseDir, string root, string name)
    {
        try
        {
            string old = Path.Combine(baseDir, name);
            string moved = Path.Combine(root, name);
            if (File.Exists(old) && !File.Exists(moved)) File.Move(old, moved);
        }
        catch
        {
            // Starting fresh is an acceptable outcome here.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // A read-only install directory should not take the app down.
        }
    }
}
