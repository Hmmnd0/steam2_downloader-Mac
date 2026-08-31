using System.Collections.Concurrent;

namespace Steam2Browser;

/// <summary>One depot of an install, and where it got to.</summary>
public sealed class InstallStep
{
    public int Depot;
    public int Version;
    public string? Role;
    public string? Name;

    /// <summary>queued | downloading | extracting | done | failed | cancelled</summary>
    public string Status = "queued";

    public string? Error;
    public string? JobId;
    public string? RunId;

    public long TotalBytes;
    public long DoneBytes;
    public int FilesWritten;
}

public sealed class Install
{
    public required string Id { get; init; }
    public int Appid;
    public string AppName = "";
    public string BuildId = "";
    public string OutDir = "";

    /// <summary>queued | running | done | failed | cancelled</summary>
    public string Status = "queued";

    public string? Error;
    public DateTime StartedUtc = DateTime.UtcNow;
    public DateTime? FinishedUtc;

    public readonly List<InstallStep> Steps = [];
    public readonly ConcurrentQueue<string> Log = new();

    public CancellationTokenSource Cts = new();

    public void Say(string line) => Log.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
}

/// <summary>
/// Installs an app: every depot of a build, downloaded and then unpacked into one directory.
///
/// Depots of a game overlay rather than sit side by side — the client, the content and each
/// localization of Counter-Strike: Source all unpack into the same `cstrike` tree, which is how
/// Steam2 assembled an installation. Extracting them into separate folders, as a per-depot run
/// does, produces something no one can actually run.
///
/// Reported as one piece of work rather than as N downloads, because that is what was asked for.
/// The per-depot jobs still exist underneath and keep their own chains and pruning.
/// </summary>
public sealed class InstallManager(
    Settings settings,
    ArchiveClient client,
    ChangeIndex changes,
    DownloadManager downloads,
    ExtractorRunner extractor)
{
    private readonly ConcurrentDictionary<string, Install> _installs = new();
    private int _seq;

    public IEnumerable<Install> All => _installs.Values.OrderByDescending(i => i.StartedUtc);

    public Install? Get(string id) => _installs.GetValueOrDefault(id);

    public bool Cancel(string id)
    {
        if (!_installs.TryGetValue(id, out var install)) return false;
        install.Cts.Cancel();
        return true;
    }

    /// <param name="nameOf">Depot display names live in the name cache, not the catalog.</param>
    public Install Start(Catalog catalog, AppEntry app, AppBuild build, List<AppDepot> picked,
                         Func<int, string?> nameOf)
    {
        // The folder is named for the app and build, not for a depot, since every depot lands in it.
        string outDir = Path.Combine(settings.ExtractOutDir, $"{app.Appid}_{Safe(build.Id)}");

        var install = new Install
        {
            Id = $"ins{Interlocked.Increment(ref _seq)}",
            Appid = app.Appid,
            AppName = app.Name,
            BuildId = build.Id,
            OutDir = outDir,
        };

        foreach (var pin in picked)
        {
            install.Steps.Add(new InstallStep
            {
                Depot = pin.Depot,
                Version = pin.Version,
                Role = pin.Role,
                Name = nameOf(pin.Depot),
            });
        }

        _installs[install.Id] = install;
        install.Say($"{app.Name} — build {build.Id}, {install.Steps.Count} depot(s)");
        install.Say($"output: {outDir}");

        _ = Task.Run(() => RunAsync(catalog, install));
        return install;
    }

    private async Task RunAsync(Catalog catalog, Install install)
    {
        var ct = install.Cts.Token;
        install.Status = "running";

        try
        {
            Directory.CreateDirectory(install.OutDir);

            foreach (var step in install.Steps)
            {
                ct.ThrowIfCancellationRequested();

                // Sequential on purpose: the mirrors speed a connection up the longer it keeps
                // asking, so two depots pulling at once are slower than the same two in turn.
                await OneStepAsync(catalog, install, step, ct);

                if (step.Status == "failed") install.Error ??= step.Error;
            }

            bool anyFailed = install.Steps.Any(s => s.Status == "failed");
            install.Status = anyFailed ? "failed" : "done";
            install.Say(anyFailed
                ? $"finished with {install.Steps.Count(s => s.Status == "failed")} failed depot(s)"
                : $"installed into {install.OutDir}");
        }
        catch (OperationCanceledException)
        {
            install.Status = "cancelled";
            install.Say("cancelled");
        }
        catch (Exception ex)
        {
            install.Status = "failed";
            install.Error = ex.Message;
            install.Say($"failed: {ex.Message}");
        }
        finally
        {
            install.FinishedUtc = DateTime.UtcNow;
        }
    }

    private async Task OneStepAsync(Catalog catalog, Install install, InstallStep step, CancellationToken ct)
    {
        string what = $"depot {step.Depot} v{step.Version}";

        try
        {
            step.Status = "downloading";
            install.Say($"{what}: resolving chain");

            var plan = await ChainResolver.ResolveAsync(
                catalog, client, settings.DataDir, step.Depot, step.Version, null, ct);

            if (plan.Error is { } err) throw new InvalidDataException(err);

            changes.Prune(plan);
            step.TotalBytes = plan.TotalBytes;

            var job = downloads.Start(plan);
            step.JobId = job.Id;

            while (job.Status is "queued" or "running")
            {
                ct.ThrowIfCancellationRequested();
                step.DoneBytes = job.DoneBytes;
                await Task.Delay(400, ct);
            }

            step.DoneBytes = job.DoneBytes;

            if (job.Status != "done")
                throw new InvalidDataException($"download {job.Status}{(job.Error is null ? "" : $": {job.Error}")}");

            step.Status = "extracting";
            install.Say($"{what}: unpacking into the shared folder");

            // Dates come from the version that wrote each file, same as a standalone extraction.
            var dates = catalog.Ordered.FirstOrDefault(d => d.Id == step.Depot)?.Blobs
                .GroupBy(b => b.Version)
                .ToDictionary(g => g.Key, g => g.Max(b => b.Date));

            var run = extractor.Start(step.Depot, step.Version, null, null, null, dates, install.OutDir);
            step.RunId = run.Id;

            while (run.Status == "running")
            {
                ct.ThrowIfCancellationRequested();
                step.FilesWritten = run.Progress.DoneFiles;
                await Task.Delay(400, ct);
            }

            step.FilesWritten = run.Progress.DoneFiles;

            if (run.Status != "done")
                throw new InvalidDataException($"extract {run.Status}{(run.Error is null ? "" : $": {run.Error}")}");

            step.Status = "done";
            install.Say($"{what}: {step.FilesWritten} file(s)");
        }
        catch (OperationCanceledException)
        {
            step.Status = "cancelled";
            throw;
        }
        catch (Exception ex)
        {
            step.Status = "failed";
            step.Error = ex.Message;
            install.Say($"{what}: failed — {ex.Message}");
        }
    }

    /// <summary>Build ids come from contributed JSON, so they cannot be trusted in a path.</summary>
    private static string Safe(string s)
    {
        var bad = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. s.Select(c => bad.Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "build" : cleaned;
    }
}
