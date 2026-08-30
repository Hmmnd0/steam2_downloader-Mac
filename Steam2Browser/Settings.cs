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
    public bool VerifyHashes { get; set; } = true;

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

    /// <summary>Everything the app writes lives in one folder named this.</summary>
    public const string RootFolder = "steam2info";

    /// <summary>
    /// On macOS this cannot be beside the executable: a freshly downloaded, still-quarantined
    /// .app is run by Gatekeeper from a randomized, read-only location (App Translocation) unless
    /// the user has already moved it out of its download location, so writing there crashes on
    /// first launch. ~/Library/Application Support is always writable and stable regardless of
    /// where the .app itself lives. Windows and Linux keep the simpler beside-the-executable
    /// layout, which is portable (an install can be moved as one folder) and doesn't have an
    /// equivalent translocation mechanism.
    /// </summary>
    public static string RootFor(string baseDir) => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", RootFolder)
        : Path.Combine(baseDir, RootFolder);

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
