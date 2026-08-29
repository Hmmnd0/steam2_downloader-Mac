using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.FileProviders;
using Steam2Browser;

var baseDir = AppContext.BaseDirectory;
var settings = Settings.Load(baseDir);

int port = 5099;
foreach (var arg in args)
    if (arg.StartsWith("--port=", StringComparison.OrdinalIgnoreCase) && int.TryParse(arg[7..], out int p))
        port = p;
bool noBrowser = args.Contains("--no-browser", StringComparer.OrdinalIgnoreCase);

var handler = new SocketsHttpHandler
{
    MaxConnectionsPerServer = 64,
    PooledConnectionLifetime = TimeSpan.FromMinutes(10),
    AutomaticDecompression = System.Net.DecompressionMethods.All,
    ConnectTimeout = TimeSpan.FromSeconds(20),
};
var http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
http.DefaultRequestHeaders.UserAgent.ParseAdd("steam2browser/1.0");

var client = new ArchiveClient(http)
{
    Primary = Mirrors.ById(settings.MirrorId),
    Failover = settings.Failover,
};
var loader = new IndexLoader(client, settings);
var downloads = new DownloadManager(client, settings);
var extractor = new ExtractorRunner(client, settings);

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args, ContentRootPath = baseDir });
builder.Logging.ClearProviders();
builder.WebHost.UseUrls($"http://127.0.0.1:{port}");
builder.Services.ConfigureHttpJsonOptions(o =>
{
    o.SerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    o.SerializerOptions.DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull;
});

var app = builder.Build();

// UI assets are embedded so the published exe stands alone; a physical wwwroot wins during development.
IFileProvider assets = new ManifestEmbeddedFileProvider(typeof(Program).Assembly, "wwwroot");
string devAssets = Path.Combine(baseDir, "wwwroot");
if (Directory.Exists(devAssets))
    assets = new CompositeFileProvider(new PhysicalFileProvider(devAssets), assets);

app.UseDefaultFiles(new DefaultFilesOptions { FileProvider = assets });
app.UseStaticFiles(new StaticFileOptions { FileProvider = assets });

// ---------------- state ----------------

app.MapGet("/api/state", () => new
{
    status = new
    {
        loader.Status.Phase,
        loader.Status.Message,
        loader.Status.Percent,
        loader.Status.Ready,
        loader.Status.Error,
    },
    catalog = loader.Catalog is { } c
        ? new
        {
            depots = c.Ordered.Count,
            dats = c.DatCount,
            blobs = c.BlobCount,
            totalBytes = c.ApproxTotalBytes,
            sizesLoaded = c.SizesLoaded,
            resetDepots = c.Ordered.Count(d => d.HasReset),
            incompleteDepots = c.Ordered.Count(d => !d.IsComplete),
        }
        : null,
    settings = new
    {
        settings.DataDir,
        settings.IndexDir,
        settings.MirrorId,
        settings.Failover,
        settings.Concurrency,
        settings.VerifyHashes,
        settings.ExtractExePath,
        settings.ExtractOutDir,
    },
    mirrors = Mirrors.All.Select(m => new
    {
        m.Id, m.Name, m.Region, m.BaseUrl, m.SpeedBps, m.TtfbMs, m.Reachable, m.Error,
        tested = m.TestedUtc,
        active = m.Id == client.Primary.Id,
    }),
});

app.MapPost("/api/settings", (SettingsPatch patch) =>
{
    if (patch.MirrorId is { } mid) { settings.MirrorId = mid; client.Primary = Mirrors.ById(mid); }
    if (patch.Failover is { } fo) { settings.Failover = fo; client.Failover = fo; }
    if (patch.Concurrency is { } cc) settings.Concurrency = Math.Clamp(cc, 1, 64);
    if (patch.VerifyHashes is { } vh) settings.VerifyHashes = vh;
    if (!string.IsNullOrWhiteSpace(patch.DataDir)) settings.DataDir = patch.DataDir!;
    if (!string.IsNullOrWhiteSpace(patch.ExtractExePath)) settings.ExtractExePath = patch.ExtractExePath!;
    if (!string.IsNullOrWhiteSpace(patch.ExtractOutDir)) settings.ExtractOutDir = patch.ExtractOutDir!;
    settings.Save();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/mirrors/test", async (CancellationToken ct) =>
{
    await Mirrors.TestAllAsync(http, ct);
    return Results.Ok(Mirrors.All.Select(m => new { m.Id, m.SpeedBps, m.TtfbMs, m.Reachable, m.Error }));
});

app.MapPost("/api/index/reload", (ReloadRequest req) =>
{
    _ = loader.LoadAsync(req.Refresh, req.Sizes);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/index/sizes", () =>
{
    _ = loader.LoadSizesAsync(force: true);
    return Results.Ok(new { ok = true });
});

// ---------------- browsing ----------------

app.MapGet("/api/depots", (string? q, string? sort, string? dir, string? filter, int skip, int take) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.Ok(new { total = 0, items = Array.Empty<object>() });

    IEnumerable<Depot> items = cat.Ordered;

    if (!string.IsNullOrWhiteSpace(q))
    {
        var needle = q.Trim();
        items = int.TryParse(needle, out int exact)
            ? items.Where(d => d.Id == exact || d.Id.ToString().Contains(needle, StringComparison.Ordinal))
            : items.Where(d => d.Id.ToString().Contains(needle, StringComparison.Ordinal));
    }

    items = filter switch
    {
        "reset" => items.Where(d => d.HasReset),
        "incomplete" => items.Where(d => !d.IsComplete),
        "single" => items.Where(d => d.DistinctVersions == 1),
        "big" => items.Where(d => d.ApproxDatBytes >= 1L << 30),
        _ => items,
    };

    bool desc = dir == "desc";
    Func<Depot, object> key = sort switch
    {
        "versions" => d => d.DistinctVersions,
        "size" => d => d.ApproxDatBytes + d.ApproxBlobBytes,
        "date" => d => d.LastDate,
        "files" => d => d.Dats.Count + d.Blobs.Count,
        _ => d => d.Id,
    };

    var list = items.ToList();
    list.Sort((a, b) =>
    {
        int c = Comparer<object>.Default.Compare(key(a), key(b));
        if (c == 0) c = a.Id.CompareTo(b.Id);
        return desc ? -c : c;
    });

    take = Math.Clamp(take <= 0 ? 200 : take, 1, 2000);
    skip = Math.Max(0, skip);

    return Results.Ok(new
    {
        total = list.Count,
        items = list.Skip(skip).Take(take).Select(Dto.Summary),
    });
});

app.MapGet("/api/depots/{id:int}", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var d)) return Results.NotFound();

    string blobDir = Path.Combine(settings.DataDir, "blobs");
    string datDir = Path.Combine(settings.DataDir, "dats");

    return Results.Ok(new
    {
        summary = Dto.Summary(d),
        versions = Enumerable.Range(0, d.MaxVersion + 1).Select(v => new
        {
            version = v,
            dats = d.Dats.Where(e => e.Version == v).Select(e => Dto.File(e, datDir)),
            blobs = d.Blobs.Where(e => e.Version == v).Select(e => Dto.File(e, blobDir)),
        }).Where(x => x.dats.Any() || x.blobs.Any()),
    });
});

app.MapGet("/api/search", (string? q, int take) =>
{
    var cat = loader.Catalog;
    if (cat is null || string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());

    var needle = q.Trim();
    take = Math.Clamp(take <= 0 ? 100 : take, 1, 500);

    var hits = new List<object>();
    foreach (var d in cat.Ordered)
    {
        foreach (var e in d.Dats.Concat(d.Blobs))
        {
            if (e.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(Dto.File(e, Path.Combine(settings.DataDir, e.DirName)));
                if (hits.Count >= take) return Results.Ok(hits);
            }
        }
    }
    return Results.Ok(hits);
});

// ---------------- plan / download / extract ----------------

app.MapPost("/api/plan", async (PlanRequest req, CancellationToken ct) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var plan = await ChainResolver.ResolveAsync(cat, client, settings.DataDir, req.Depot, req.Version, req.BlobCrc, ct);
    return Results.Ok(Dto.Plan(plan, settings));
});

app.MapPost("/api/download", async (PlanRequest req, CancellationToken ct) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.BadRequest(new { error = "index not loaded yet" });

    var plan = await ChainResolver.ResolveAsync(cat, client, settings.DataDir, req.Depot, req.Version, req.BlobCrc, ct);
    if (plan.Error is not null || plan.NeedsChoice) return Results.Ok(Dto.Plan(plan, settings));

    var job = downloads.Start(plan);
    return Results.Ok(new { jobId = job.Id, plan = Dto.Plan(plan, settings) });
});

app.MapGet("/api/jobs", () => downloads.Jobs
    .OrderByDescending(j => j.StartedUtc)
    .Select(Dto.Job));

app.MapPost("/api/jobs/{id}/cancel", (string id) => { downloads.Cancel(id); return Results.Ok(new { ok = true }); });
app.MapPost("/api/jobs/clear", () => { downloads.Clear(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/extract", async (ExtractRequest req, CancellationToken ct) =>
{
    var run = await extractor.StartAsync(req.Depot, req.Version, req.BlobCrc, req.Filter, ct);
    return Results.Ok(new { runId = run.Id });
});

app.MapGet("/api/extract", () => extractor.Runs
    .OrderByDescending(r => r.StartedUtc)
    .Select(r => new
    {
        r.Id, r.Depot, r.Version, r.BlobCrc, r.CommandLine, r.Status, r.ExitCode, r.Error,
        started = r.StartedUtc, finished = r.FinishedUtc,
        log = r.Log.ToArray(),
    }));

app.MapPost("/api/extract/{id}/cancel", (string id) => { extractor.Cancel(id); return Results.Ok(new { ok = true }); });
app.MapPost("/api/extract/clear", () => { extractor.Clear(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/reveal", (RevealRequest req) =>
{
    try
    {
        string target = req.Path;
        if (string.IsNullOrWhiteSpace(target)) return Results.BadRequest(new { error = "empty path" });
        if (!Directory.Exists(target) && !File.Exists(target)) Directory.CreateDirectory(target);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
        return Results.Ok(new { ok = true });
    }
    catch (Exception ex)
    {
        return Results.BadRequest(new { error = ex.Message });
    }
});

// ---------------- go ----------------

_ = loader.LoadAsync(refreshIndex: false, withSizes: true);

string url = $"http://127.0.0.1:{port}/";
Console.WriteLine($"steam2browser  ->  {url}");
Console.WriteLine($"data dir: {settings.DataDir}");
Console.WriteLine("press Ctrl+C to stop");

if (!noBrowser)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* headless is fine, the URL is printed above */ }
}

app.Run();

// ---------------- request bodies ----------------

internal sealed record SettingsPatch(
    string? MirrorId, bool? Failover, int? Concurrency, bool? VerifyHashes,
    string? DataDir, string? ExtractExePath, string? ExtractOutDir);

internal sealed record ReloadRequest(bool Refresh, bool Sizes);
internal sealed record PlanRequest(int Depot, int Version, string? BlobCrc);
internal sealed record ExtractRequest(int Depot, int Version, string? BlobCrc, string? Filter);
internal sealed record RevealRequest(string Path);

internal static class Dto
{
    public static object Summary(Depot d) => new
    {
        id = d.Id,
        versions = d.DistinctVersions,
        maxVersion = d.MaxVersion,
        dats = d.Dats.Count,
        blobs = d.Blobs.Count,
        datBytes = d.ApproxDatBytes,
        blobBytes = d.ApproxBlobBytes,
        first = d.FirstDate == default ? null : d.FirstDate.ToString("yyyy-MM-dd"),
        last = d.LastDate == default ? null : d.LastDate.ToString("yyyy-MM-dd"),
        hasReset = d.HasReset,
        forkedVersions = d.ForkedVersions,
        complete = d.IsComplete,
        missingDats = d.MissingDats,
        missingBlobs = d.MissingBlobs,
    };

    public static object File(Entry e, string localDir) => new
    {
        name = e.FileName,
        depot = e.Depot,
        version = e.Version,
        crc = e.CrcHex,
        sha = e.Sha,
        kind = e.Kind == Kind.Dat ? "dat" : "blob",
        size = e.ApproxSize,
        date = e.Date == default ? null : e.Date.ToString("yyyy-MM-dd HH:mm:ss"),
        local = System.IO.File.Exists(Path.Combine(localDir, e.FileName)),
    };

    public static object Plan(ChainPlan p, Settings s) => new
    {
        depot = p.Depot,
        version = p.TargetVersion,
        blobCrc = p.BlobCrc,
        mode = p.Mode,
        error = p.Error,
        needsChoice = p.NeedsChoice,
        choices = p.Choices,
        warnings = p.Warnings,
        totalBytes = p.TotalBytes,
        totalExact = p.TotalExact,
        fileCount = p.Files.Count,
        datCount = p.Files.Count(f => f.Entry.Kind == Kind.Dat),
        blobCount = p.Files.Count(f => f.Entry.Kind == Kind.Blob),
        alreadyLocal = p.Files.Count(f =>
            System.IO.File.Exists(Path.Combine(s.DataDir, f.Entry.DirName, f.Entry.FileName))),
        extractArgs = p.ExtractArgs,
        files = p.Files.Take(2000).Select(f => new
        {
            name = f.Entry.FileName,
            kind = f.Entry.Kind == Kind.Dat ? "dat" : "blob",
            version = f.Entry.Version,
            crc = f.Entry.CrcHex,
            size = f.Size,
            exact = f.SizeExact,
            local = System.IO.File.Exists(Path.Combine(s.DataDir, f.Entry.DirName, f.Entry.FileName)),
        }),
    };

    public static object Job(DownloadJob j) => new
    {
        id = j.Id,
        depot = j.Depot,
        version = j.Version,
        blobCrc = j.BlobCrc,
        mode = j.Mode,
        status = j.Status,
        error = j.Error,
        totalFiles = j.TotalFiles,
        doneFiles = j.DoneFiles,
        skippedFiles = j.SkippedFiles,
        failedFiles = j.FailedFiles,
        totalBytes = j.TotalBytes,
        doneBytes = Interlocked.Read(ref j.DoneBytes),
        speedBps = j.SpeedBps,
        extractArgs = j.ExtractArgs,
        started = j.StartedUtc,
        finished = j.FinishedUtc,
        active = j.Active.Values
            .Where(f => f.State == "running")
            .OrderByDescending(f => f.Done)
            .Take(12)
            .Select(f => new { f.Name, f.Done, f.Total }),
        log = j.Log.ToArray(),
    };
}
