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

    public string ExtractExePath { get; set; } = "";
    public string ExtractOutDir { get; set; } = "";

    [JsonIgnore] public string ConfigPath { get; set; } = "";

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load(string baseDir)
    {
        string path = Path.Combine(baseDir, "settings.json");
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
        if (string.IsNullOrWhiteSpace(s.DataDir)) s.DataDir = Path.Combine(baseDir, "archive");
        if (string.IsNullOrWhiteSpace(s.IndexDir)) s.IndexDir = Path.Combine(baseDir, "index");
        if (string.IsNullOrWhiteSpace(s.ExtractOutDir)) s.ExtractOutDir = Path.Combine(baseDir, "extracted");
        if (s.Concurrency is < 1 or > 64) s.Concurrency = 8;
        return s;
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
