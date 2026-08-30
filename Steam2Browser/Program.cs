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
http.DefaultRequestHeaders.UserAgent.ParseAdd(
    "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
    "AppleWebKit/537.36 (KHTML, like Gecko) " +
    "Chrome/151.0.0.0 Safari/537.36");

var client = new ArchiveClient(http)
{
    Primary = Mirrors.ById(settings.MirrorId),
    Failover = settings.Failover,
    UseSegments = !settings.PhasedDownloads,
};
var loader = new IndexLoader(client, settings);
var torrent = new TorrentSource(settings);
var downloads = new DownloadManager(client, settings, torrent);
var extractor = new ExtractorRunner(settings);
var updates = new UpdateChecker(http);
updates.Initialise();
var labels = new LabelSource(http);
var changes = new ChangeIndex(client, settings);
var names = new NameCache(client, http, labels);
names.Load(Settings.RootFor(baseDir));

// Maintainer tool: `--build-index <path>` snapshots the whole catalog into one compact file, which
// the build then embeds so a release needs no network on first run. Always pulls fresh from a
// mirror rather than reusing local caches, since the point is to capture the archive as it is now.
int buildIndexAt = Array.FindIndex(args, a => a.Equals("--build-index", StringComparison.OrdinalIgnoreCase));
if (buildIndexAt >= 0)
{
    string outPath = Path.GetFullPath(
        buildIndexAt + 1 < args.Length ? args[buildIndexAt + 1] : "index.bin");

    // Sizes are the expensive part — two ~20 MB directory listings — so they are opt-in. Without
    // them the snapshot still carries every name and date, and sizes fill in later on demand.
    bool withSizes = args.Contains("--with-sizes", StringComparer.OrdinalIgnoreCase);

    Console.WriteLine("building a compact index snapshot");
    Console.WriteLine(withSizes
        ? "  sizes requested: fetching two directory listings (~40 MB)"
        : "  sizes skipped: pass --with-sizes to include them");

    // Local dats_dates.txt / blobs_dates.txt are used when present, anywhere up the tree.
    await loader.LoadAsync(refreshIndex: false, withSizes: false, ignoreEmbedded: true);
    if (loader.Catalog is null)
    {
        Console.Error.WriteLine($"  failed: {loader.Status.Error ?? loader.Status.Message}");
        return 1;
    }
    Console.WriteLine($"  index: {loader.Status.Message}");

    if (withSizes)
    {
        await loader.LoadSizesAsync(force: true);
        Console.WriteLine($"  sizes: {loader.Status.Message}");
    }

    var cat = loader.Catalog;
    Directory.CreateDirectory(Path.GetDirectoryName(outPath)!);
    CompactIndex.Write(
        outPath,
        cat.Ordered.SelectMany(d => d.Dats),
        cat.Ordered.SelectMany(d => d.Blobs));

    Console.WriteLine($"  wrote {outPath} ({new FileInfo(outPath).Length / 1048576.0:0.00} MB) " +
                      $"— {cat.Ordered.Count} depots, {cat.DatCount + cat.BlobCount} files" +
                      (cat.SizesLoaded ? ", with sizes" : ", no sizes"));
    return 0;
}

// ---------------- port ----------------

// Kestrel only discovers a busy port deep inside app.Run(), where the failure surfaces as a wall
// of stack trace. Settle it here instead: hand the user over to an instance that is already
// running, or step aside to the next free port.
static bool PortIsFree(int candidate)
{
    try
    {
        var probe = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, candidate);
        probe.Start();
        probe.Stop();
        return true;
    }
    catch (System.Net.Sockets.SocketException)
    {
        return false;
    }
}

static async Task<bool> AnotherInstanceAsync(int candidate)
{
    try
    {
        using var probe = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
        var body = await probe.GetStringAsync($"http://127.0.0.1:{candidate}/api/state");
        return body.Contains("\"mirrors\"", StringComparison.Ordinal);
    }
    catch
    {
        return false;
    }
}

if (!PortIsFree(port))
{
    if (await AnotherInstanceAsync(port))
    {
        string running = $"http://127.0.0.1:{port}/";
        Console.WriteLine($"steam2browser is already running at {running} — opening that one");

        if (!noBrowser)
        {
            try { Process.Start(new ProcessStartInfo(running) { UseShellExecute = true }); }
            catch { /* the URL is printed above */ }
        }
        return 0;
    }

    int free = Enumerable.Range(port + 1, 20).FirstOrDefault(PortIsFree, -1);
    if (free < 0)
    {
        Console.Error.WriteLine($"port {port} is taken and nothing is free through {port + 20}.");
        Console.Error.WriteLine("Pass --port=NNNN to choose another one.");
        return 1;
    }

    Console.WriteLine($"port {port} is taken by something else — using {free}");
    port = free;
}

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
            keyedDepots = c.Ordered.Count(d => DepotKeys.Has(d.Id)),
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
        settings.PhasedDownloads,
        settings.BlobConcurrency,
        settings.DatConcurrency,
        settings.WarmupLookahead,
        settings.BigFileBytes,
        settings.VerifyHashes,
        settings.TorrentPort,
        settings.ExtractOutDir,
        trackers = settings.TrackersToUse,
    },
    mirrors = Mirrors.All.Select(m => new
    {
        m.Id, m.Name, m.Region, m.BaseUrl, m.SpeedBps, m.TtfbMs, m.Reachable, m.Error,
        tested = m.TestedUtc,
        active = m.Id == client.Primary.Id,
    }),
    update = new
    {
        updates.Status.State,
        updates.Status.Message,
        updates.Status.Repo,
        updates.Status.RepoUrl,
        updates.Status.BuiltUtc,
        updates.Status.LatestCommitUtc,
        updates.Status.CommitShort,
        updates.Status.CommitMessage,
        updates.Status.CommitAuthor,
        updates.Status.CommitUrl,
        updates.Status.CheckedUtc,
    },
    labels = new
    {
        labels.Status.State,
        labels.Status.Message,
        labels.Status.Error,
        labels.Status.Count,
        labels.Status.Source,
        labels.Status.FetchedUtc,
    },
    names = new
    {
        names.Status.Running,
        names.Status.Curated,
        names.Status.Cached,
        names.Status.Named,
        names.Status.Failed,
        names.Status.Current,
        names.Status.Remaining,
        names.Status.Message,
    },
    torrent = new
    {
        torrent.Status.State,
        torrent.Status.Message,
        torrent.Status.Error,
        torrent.Status.HasMetadata,
        torrent.Status.TotalFiles,
        torrent.Status.SelectedFiles,
        torrent.Status.SelectedBytes,
        torrent.Status.SelectedProgress,
        torrent.Status.Trackers,
        torrent.Status.Peers,
        torrent.Status.Seeds,
        torrent.Status.DownloadRate,
        torrent.Status.UploadRate,
        torrent.Status.TorrentState,
        magnet = TorrentSource.Magnet,
    },
    steam = new
    {
        names.Steam.Running,
        names.Steam.Checked,
        names.Steam.Found,
        names.Steam.Remaining,
        names.Steam.Current,
        names.Steam.Message,
    },
});

app.MapPost("/api/settings", async (SettingsPatch patch) =>
{
    var resetTorrent = false;
    if (patch.MirrorId is { } mid) { settings.MirrorId = mid; client.Primary = Mirrors.ById(mid); }
    if (patch.Failover is { } fo) { settings.Failover = fo; client.Failover = fo; }
    if (patch.Concurrency is { } cc) settings.Concurrency = Math.Clamp(cc, 1, 64);
    if (patch.PhasedDownloads is { } phased)
    {
        settings.PhasedDownloads = phased;
        client.UseSegments = !phased;
    }
    if (patch.BlobConcurrency is { } bc) settings.BlobConcurrency = Math.Clamp(bc, 1, 128);
    if (patch.DatConcurrency is { } dc) settings.DatConcurrency = Math.Clamp(dc, 1, 64);
    if (patch.WarmupLookahead is { } wl) settings.WarmupLookahead = Math.Clamp(wl, 0, 16);
    if (patch.BigFileMb is { } bm) settings.BigFileBytes = Math.Max(0, bm) * 1024L * 1024L;
    if (patch.VerifyHashes is { } vh) settings.VerifyHashes = vh;
    if (patch.TorrentPort is { } tp)
    {
        int port = tp is < 0 or > 65535 ? 0 : tp;
        resetTorrent = port != settings.TorrentPort;
        settings.TorrentPort = port;
    }
    if (!string.IsNullOrWhiteSpace(patch.DataDir)) settings.DataDir = patch.DataDir!;
    if (!string.IsNullOrWhiteSpace(patch.ExtractOutDir)) settings.ExtractOutDir = patch.ExtractOutDir!;
    if (patch.ExtraTrackers is { } tr) settings.ExtraTrackers = tr;
    settings.Save();
    if (resetTorrent) await torrent.ResetAsync();
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

app.MapPost("/api/names/start", (bool? retryFailed) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });
    names.Start(loader.Catalog, retryFailed: retryFailed ?? false);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/names/stop", () => { names.Stop(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/names/labels/refresh", async (CancellationToken ct) =>
{
    await labels.RefreshAsync(Settings.RootFor(AppContext.BaseDirectory), ct);
    return Results.Ok(new { labels.Status.State, labels.Status.Message, labels.Status.Count });
});

app.MapPost("/api/names/steam/start", (bool? recheckMisses) =>
{
    if (loader.Catalog is null) return Results.BadRequest(new { error = "index not loaded yet" });
    names.StartSteam(loader.Catalog, recheckMisses ?? false);
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/names/steam/stop", () => { names.StopSteam(); return Results.Ok(new { ok = true }); });

app.MapPost("/api/torrent/start", () =>
{
    _ = torrent.EnsureStartedAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/torrent/stop", async () =>
{
    await torrent.StopAsync();
    return Results.Ok(new { ok = true });
});

app.MapPost("/api/update/check", async (CancellationToken ct) =>
{
    await updates.CheckAsync(ct);
    return Results.Ok(new { updates.Status.State, updates.Status.Message });
});

// ---------------- browsing ----------------

app.MapGet("/api/depots", (string? q, string? sort, string? dir, string? filter, int? skip, int? take) =>
{
    var cat = loader.Catalog;
    if (cat is null) return Results.Ok(new { total = 0, items = Array.Empty<object>() });

    IEnumerable<Depot> items = cat.Ordered;

    if (!string.IsNullOrWhiteSpace(q))
    {
        var needle = q.Trim();

        // Wrapping the term in quotes asks for an exact match: "440" is depot 440 alone,
        // where a bare 440 also brings back 4400, 14400 and every other id containing it.
        bool exact = needle.Length >= 2
                     && (needle[0] == '"' && needle[^1] == '"' || needle[0] == '\'' && needle[^1] == '\'');
        if (exact) needle = needle[1..^1].Trim();

        if (needle.Length == 0)
        {
            // A lone pair of quotes filters nothing.
        }
        else if (exact)
        {
            items = items.Where(d =>
                d.Id.ToString() == needle ||
                string.Equals(names.DisplayFor(d.Id), needle, StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            items = items.Where(d =>
                d.Id.ToString().Contains(needle, StringComparison.Ordinal) ||
                names.DisplayFor(d.Id).Contains(needle, StringComparison.OrdinalIgnoreCase));
        }
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

    int pageSize = Math.Clamp(take is null or <= 0 ? 200 : take.Value, 1, 2000);
    int offset = Math.Max(0, skip ?? 0);

    return Results.Ok(new
    {
        total = list.Count,
        items = list.Skip(offset).Take(pageSize)
            .Select(d => Dto.Summary(d, names.Get(d.Id), names.DisplayFor(d.Id), names.SourceFor(d.Id))),
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
        summary = Dto.Summary(d, names.Get(d.Id), names.DisplayFor(d.Id), names.SourceFor(d.Id)),
        versions = Enumerable.Range(0, d.MaxVersion + 1).Select(v => new
        {
            version = v,
            dats = d.Dats.Where(e => e.Version == v).Select(e => Dto.File(e, datDir)),
            blobs = d.Blobs.Where(e => e.Version == v).Select(e => Dto.File(e, blobDir)),
        }).Where(x => x.dats.Any() || x.blobs.Any()),
    });
});

// The whole version history of a depot, newest first. Counts appear for versions whose blob is
// already on disk; the rest need the bulk fetch below, which costs kilobytes per version.
app.MapGet("/api/depots/{id:int}/versions", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    var fetch = changes.StatusFor(id);

    return Results.Ok(new
    {
        depot = id,
        fetch = new { fetch.Running, fetch.Done, fetch.Total, fetch.Failed, fetch.Message },
        versions = changes.Summary(depot).Select(v => new
        {
            v.Version, v.Crc, v.Date, v.Local,
            v.ChangedCount, v.ChangedBytes, v.FilesInVersion, v.WholeSet, v.Error,
        }),
    });
});

// The files one version changed. Read straight from that version's blob, no dat involved.
app.MapGet("/api/depots/{id:int}/versions/{version:int}/files", (int id, int version, string? crc) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    var candidates = depot.Blobs.Where(b => b.Version == version).ToList();
    var blob = !string.IsNullOrWhiteSpace(crc)
        ? candidates.FirstOrDefault(b => b.CrcHex.Equals(crc.Trim(), StringComparison.OrdinalIgnoreCase))
        : candidates.FirstOrDefault();

    if (blob is null) return Results.Ok(new { error = $"no blob for version {version}" });

    try
    {
        var files = changes.FilesFor(blob);
        if (files is null) return Results.Ok(new { needsFetch = true, crc = blob.CrcHex });

        return Results.Ok(new
        {
            version,
            crc = blob.CrcHex,
            count = files.Count,
            files = files.Take(20000).Select(f => new { path = f.Path, size = f.Size, mode = f.Mode }),
        });
    }
    catch (Exception ex)
    {
        return Results.Ok(new { error = ex.Message });
    }
});

// Pulls every blob the depot has, so the full history can be expanded offline.
app.MapPost("/api/depots/{id:int}/blobs", (int id) =>
{
    var cat = loader.Catalog;
    if (cat is null || !cat.Depots.TryGetValue(id, out var depot)) return Results.NotFound();

    changes.FetchAll(depot);
    return Results.Ok(new { ok = true, blobs = depot.Blobs.Count });
});

app.MapGet("/api/search", (string? q, int? take) =>
{
    var cat = loader.Catalog;
    if (cat is null || string.IsNullOrWhiteSpace(q)) return Results.Ok(Array.Empty<object>());

    var needle = q.Trim();
    int limit = Math.Clamp(take is null or <= 0 ? 100 : take.Value, 1, 500);

    var hits = new List<object>();
    foreach (var d in cat.Ordered)
    {
        foreach (var e in d.Dats.Concat(d.Blobs))
        {
            if (e.FileName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                hits.Add(Dto.File(e, Path.Combine(settings.DataDir, e.DirName)));
                if (hits.Count >= limit) return Results.Ok(hits);
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

app.MapPost("/api/extract", (ExtractRequest req) =>
{
    var run = extractor.Start(req.Depot, req.Version, req.BlobCrc, req.Filter, req.KeyHex);
    return Results.Ok(new { runId = run.Id });
});

app.MapGet("/api/extract", () => extractor.Runs
    .OrderByDescending(r => r.StartedUtc)
    .Select(r => new
    {
        r.Id, r.Depot, r.Version, r.BlobCrc, r.OutDir, r.Status, r.Error,
        started = r.StartedUtc, finished = r.FinishedUtc,
        progress = new
        {
            r.Progress.TotalFiles,
            r.Progress.DoneFiles,
            r.Progress.FailedFiles,
            r.Progress.BytesWritten,
            r.Progress.Current,
        },
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

_ = updates.CheckAsync();

// Startup: load the index, race the mirrors, switch to the fastest, then start naming depots.
_ = Task.Run(async () =>
{
    // Curated names first: they cover the whole archive today, which means both sweeps below
    // usually find nothing left to do and no blob or store request is made at all.
    await labels.LoadAsync(Settings.RootFor(baseDir));
    names.Refresh();

    await loader.LoadAsync(refreshIndex: false, withSizes: true);
    if (loader.Catalog is null) return;

    try
    {
        await Mirrors.TestAllAsync(http);
        var best = Mirrors.All.Where(m => !m.IsTorrent && m.Reachable && m.SpeedBps > 0).MaxBy(m => m.SpeedBps);
        if (best is not null && best.Id != client.Primary.Id)
        {
            client.Primary = best;
            settings.MirrorId = best.Id;
            settings.Save();
        }
    }
    catch
    {
        // Keep whatever mirror is configured if the race fails.
    }

    // Both passes run together: the mirror sweep is bandwidth-bound and the Steam pass is
    // rate-limited to well under one request a second, so neither holds the other up.
    names.Start(loader.Catalog);
    names.StartSteam(loader.Catalog);
});

string url = $"http://127.0.0.1:{port}/";
Console.WriteLine($"steam2browser  ->  {url}");
Console.WriteLine($"data dir: {settings.DataDir}");
Console.WriteLine("press Ctrl+C to stop");

if (!noBrowser)
{
    try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); }
    catch { /* headless is fine, the URL is printed above */ }
}

try
{
    app.Run();
}
catch (IOException ex)
{
    // Something grabbed the port between the probe above and Kestrel binding it.
    Console.Error.WriteLine($"could not start on port {port}: {ex.Message}");
    Console.Error.WriteLine("Pass --port=NNNN to choose another one.");
    return 1;
}

return 0;

// ---------------- request bodies ----------------

internal sealed record SettingsPatch(
    string? MirrorId, bool? Failover, int? Concurrency, bool? VerifyHashes, bool? PhasedDownloads, int? BlobConcurrency, int? DatConcurrency, int? WarmupLookahead, int? BigFileMb,
    int? TorrentPort, string? DataDir, string? ExtractOutDir, string[]? ExtraTrackers);

internal sealed record ReloadRequest(bool Refresh, bool Sizes);
internal sealed record PlanRequest(int Depot, int Version, string? BlobCrc);
internal sealed record ExtractRequest(int Depot, int Version, string? BlobCrc, string? Filter, string? KeyHex);
internal sealed record RevealRequest(string Path);

internal static class Dto
{
    public static object Summary(Depot d, NameRecord? name = null, string? display = null, string? source = null) => new
    {
        id = d.Id,
        name = string.IsNullOrEmpty(display) ? null : display,
        nameSource = source,
        manifestName = string.IsNullOrEmpty(name?.Label) ? null : name!.Label,
        steamType = name?.SteamType,
        roots = name?.Roots,
        manifestAppId = name is { Error: null } ? name.AppId : (uint?)null,
        nameError = name?.Error,
        versions = d.DistinctVersions,
        maxVersion = d.MaxVersion,
        dats = d.Dats.Count,
        blobs = d.Blobs.Count,
        datBytes = d.ApproxDatBytes,
        blobBytes = d.ApproxBlobBytes,
        first = d.FirstDate == default ? null : d.FirstDate.ToString("yyyy-MM-dd"),
        last = d.LastDate == default ? null : d.LastDate.ToString("yyyy-MM-dd"),
        hasKey = DepotKeys.Has(d.Id),
        // Whether it is encrypted at all is the real question; the key table only matters if it is.
        encrypted = name?.Encrypted,
        needsKey = name?.Encrypted == true && !DepotKeys.Has(d.Id),
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
