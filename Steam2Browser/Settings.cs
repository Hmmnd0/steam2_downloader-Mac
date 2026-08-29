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
