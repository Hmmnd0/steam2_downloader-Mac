using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steam2Browser;

/// <summary>One depot at one version, as part of a build.</summary>
public sealed class AppDepot
{
    public int Depot { get; set; }
    public int Version { get; set; }
    public string? Role { get; set; }
    public bool Optional { get; set; }
}

/// <summary>One shippable state of an app: the depots and versions it is made of.</summary>
public sealed class AppBuild
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
    public string? Date { get; set; }
    public string? Notes { get; set; }
    public List<AppDepot> Depots { get; set; } = [];
}

public sealed class AppEntry
{
    public int Appid { get; set; }
    public string Name { get; set; } = "";
    public List<AppBuild> Builds { get; set; } = [];
}

/// <summary>
/// The community-maintained map from Steam appid to the Steam2 depots and versions that make up a
/// build of it.
///
/// This mapping is in no blob and no dat — the archive records what is inside a depot, never which
/// depots belong together, because that lived on Steam's side and was not dumped. So it is written
/// by hand in apps/*.json, one file per app, and validated against the real catalog before it can
/// be merged. See apps/README.md for the format.
/// </summary>
public sealed class AppCatalog(HttpClient http)
{
    private const string Owner = "extremebleem";
    private const string Repo = "steam2_downloader";

    /// <summary>
    /// One combined file rather than a request per app: the GitHub API allows 60 calls an hour to
    /// an unauthenticated caller, which a per-file fetch would exhaust with fifty apps in the
    /// folder. CI regenerates this from apps/*.json whenever main changes.
    /// </summary>
    private const string PathInRepo = "apps.json";

    private const string CacheFile = "apps.json";

    private static readonly string ApiUrl =
        $"https://api.github.com/repos/{Owner}/{Repo}/contents/{PathInRepo}";

    private static readonly string RawUrl =
        $"https://raw.githubusercontent.com/{Owner}/{Repo}/main/{PathInRepo}";

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        // The combined file is public and contributors read it, so it has to come out in the same
        // lower-case shape the hand-written apps/*.json use and apps/README.md documents.
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public sealed class AppStatus
    {
        public string State = "idle";
        public string Message = "";
        public string? Source;
        public DateTime? FetchedUtc;
        public int Count;
    }

    public AppStatus Status { get; } = new();

    private List<AppEntry> _apps = [];

    public IReadOnlyList<AppEntry> Apps => _apps;

    /// <summary>Reads the cached copy. Local file access only, so it never delays startup.</summary>
    public async Task LoadCachedAsync(string dataDir, CancellationToken ct = default)
    {
        string path = Path.Combine(dataDir, CacheFile);
        if (!File.Exists(path)) return;

        try
        {
            Apply(await File.ReadAllTextAsync(path, ct), "cache");
        }
        catch
        {
            // A damaged cache just means fetching again.
        }
    }

    public async Task RefreshAsync(string dataDir, CancellationToken ct = default)
    {
        Status.State = _apps.Count > 0 ? "ready" : "loading";

        try
        {
            string? text = await FetchViaApiAsync(ct) ?? await FetchRawAsync(ct);
            if (text is null)
            {
                if (_apps.Count == 0)
                {
                    Status.State = "error";
                    Status.Message = "could not reach GitHub for the app list";
                }
                return;
            }

            Apply(text, Status.Source ?? "github");
            Status.FetchedUtc = DateTime.UtcNow;

            Directory.CreateDirectory(dataDir);
            await File.WriteAllTextAsync(Path.Combine(dataDir, CacheFile), text,
                                         new UTF8Encoding(false), ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex)
        {
            if (_apps.Count == 0)
            {
                Status.State = "error";
                Status.Message = ex.Message;
            }
        }
    }

    private void Apply(string text, string source)
    {
        var parsed = JsonSerializer.Deserialize<List<AppEntry>>(text, Json) ?? [];

        _apps = [.. parsed.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase)];
        Status.Source = source;
        Status.Count = _apps.Count;
        Status.State = "ready";
        Status.Message = $"{_apps.Count} app(s) from {source}";
    }

    private async Task<string?> FetchViaApiAsync(CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, ApiUrl);
            req.Headers.Accept.ParseAdd("application/vnd.github+json");
            req.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            using var resp = await http.SendAsync(req, timeout.Token);
            if (!resp.IsSuccessStatusCode) return null;

            await using var body = await resp.Content.ReadAsStreamAsync(timeout.Token);
            using var doc = await JsonDocument.ParseAsync(body, cancellationToken: timeout.Token);

            if (!doc.RootElement.TryGetProperty("content", out var content)) return null;
            string? encoded = content.GetString();
            if (string.IsNullOrEmpty(encoded)) return null;

            Status.Source = "api.github.com";
            return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    private async Task<string?> FetchRawAsync(CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(FetchTimeout);

            using var resp = await http.GetAsync(RawUrl, timeout.Token);
            if (!resp.IsSuccessStatusCode) return null;

            Status.Source = "raw.githubusercontent.com";
            return await resp.Content.ReadAsStringAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { return null; }
    }

    // ---------------- folder tooling, used by CI ----------------

    /// <summary>
    /// Reads apps/*.json and reports what is wrong with them. An empty list means the folder is
    /// good. Depot and version existence is checked against the real catalog when one is supplied,
    /// which is what stops a pull request from adding a build nobody can download.
    /// </summary>
    public static List<string> Validate(string folder, Catalog? catalog, out List<AppEntry> apps)
    {
        var problems = new List<string>();
        apps = [];

        if (!Directory.Exists(folder))
        {
            problems.Add($"{folder} does not exist");
            return problems;
        }

        var byAppid = new Dictionary<int, string>();

        foreach (string path in Directory.EnumerateFiles(folder, "*.json").OrderBy(p => p))
        {
            string file = Path.GetFileName(path);

            AppEntry? app;
            try
            {
                app = JsonSerializer.Deserialize<AppEntry>(File.ReadAllText(path), Json);
            }
            catch (JsonException ex)
            {
                problems.Add($"{file}: not valid JSON — {ex.Message}");
                continue;
            }

            if (app is null)
            {
                problems.Add($"{file}: empty");
                continue;
            }

            string stem = Path.GetFileNameWithoutExtension(path);
            if (!int.TryParse(stem, out int fromName))
                problems.Add($"{file}: file name must be the appid, like 240.json");
            else if (fromName != app.Appid)
                problems.Add($"{file}: appid is {app.Appid} but the file is named {stem}.json");

            if (app.Appid <= 0) problems.Add($"{file}: appid must be a positive number");
            if (string.IsNullOrWhiteSpace(app.Name)) problems.Add($"{file}: name is required");

            if (byAppid.TryGetValue(app.Appid, out string? other))
                problems.Add($"{file}: appid {app.Appid} is already defined in {other}");
            else
                byAppid[app.Appid] = file;

            if (app.Builds.Count == 0) problems.Add($"{file}: needs at least one build");

            var buildIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var build in app.Builds)
            {
                string where = $"{file} build '{build.Id}'";

                if (string.IsNullOrWhiteSpace(build.Id))
                    problems.Add($"{file}: every build needs an id");
                else if (!buildIds.Add(build.Id))
                    problems.Add($"{file}: two builds share the id '{build.Id}'");

                if (build.Date is { } d && !DateOnly.TryParse(d, out _))
                    problems.Add($"{where}: date '{d}' is not YYYY-MM-DD");

                if (build.Depots.Count == 0)
                    problems.Add($"{where}: needs at least one depot");

                var seen = new HashSet<int>();

                foreach (var item in build.Depots)
                {
                    if (!seen.Add(item.Depot))
                        problems.Add($"{where}: depot {item.Depot} is listed twice");

                    if (item.Version < 0)
                        problems.Add($"{where}: depot {item.Depot} has a negative version");

                    if (catalog is null) continue;

                    var depot = catalog.Ordered.FirstOrDefault(x => x.Id == item.Depot);
                    if (depot is null)
                    {
                        problems.Add($"{where}: depot {item.Depot} is not in the archive");
                        continue;
                    }

                    if (!depot.Blobs.Any(b => b.Version == item.Version))
                        problems.Add(
                            $"{where}: depot {item.Depot} has no version {item.Version} " +
                            $"(it goes up to {depot.MaxVersion})");
                }
            }

            apps.Add(app);
        }

        return problems;
    }

    /// <summary>Combines the folder into the single file the app fetches at runtime.</summary>
    public static void WriteCombined(string outPath, List<AppEntry> apps)
    {
        string? dir = Path.GetDirectoryName(outPath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

        File.WriteAllText(
            outPath,
            JsonSerializer.Serialize(apps.OrderBy(a => a.Appid), Json),
            new UTF8Encoding(false));
    }
}
