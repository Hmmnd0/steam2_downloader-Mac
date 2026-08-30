using System.Collections.Concurrent;
using System.Diagnostics;

namespace Steam2Browser;

public sealed class FileProgress
{
    public required string Name { get; init; }
    public long Done;
    public long Total;
    public string State = "running"; // running | done | failed | skipped
    public string? Error;
}

public sealed class DownloadJob
{
    public required string Id { get; init; }
    public int Depot;
    public int Version;
    public string? BlobCrc;
    public string Mode = "direct";
    public string ExtractArgs = "";

    public string Status = "queued"; // queued | running | done | failed | cancelled
    public string? Error;

    public int TotalFiles;
    public int DoneFiles;
    public int SkippedFiles;
    public int FailedFiles;

    public long TotalBytes;
    public long DoneBytes;
    public double SpeedBps;

    public DateTime StartedUtc = DateTime.UtcNow;
    public DateTime? FinishedUtc;

    public readonly ConcurrentDictionary<string, FileProgress> Active = new();
    public readonly ConcurrentQueue<string> Log = new();

    internal CancellationTokenSource Cts = new();
    internal List<PlanFile> Files = new();

    public void Say(string message)
    {
        Log.Enqueue($"{DateTime.Now:HH:mm:ss}  {message}");
        while (Log.Count > 400) Log.TryDequeue(out _);
    }
}

public sealed class DownloadManager(ArchiveClient client, Settings settings, TorrentSource torrent)
{
    private readonly ConcurrentDictionary<string, DownloadJob> _jobs = new();
    private int _seq;

    public IReadOnlyCollection<DownloadJob> Jobs => _jobs.Values.ToArray();

    public DownloadJob? Get(string id) => _jobs.GetValueOrDefault(id);

    public DownloadJob Start(ChainPlan plan)
    {
        var job = new DownloadJob
        {
            Id = $"job{Interlocked.Increment(ref _seq)}",
            Depot = plan.Depot,
            Version = plan.TargetVersion,
            BlobCrc = plan.BlobCrc,
            Mode = plan.Mode,
            ExtractArgs = plan.ExtractArgs,
            Files = plan.Files,
            TotalFiles = plan.Files.Count,
            TotalBytes = plan.TotalBytes,
        };
        _jobs[job.Id] = job;

        _ = Task.Run(() => RunAsync(job));
        return job;
    }

    public void Cancel(string id)
    {
        if (_jobs.TryGetValue(id, out var job)) job.Cts.Cancel();
    }

    public void Clear()
    {
        foreach (var kv in _jobs)
            if (kv.Value.Status is "done" or "failed" or "cancelled")
                _jobs.TryRemove(kv.Key, out _);
    }

    private async Task RunAsync(DownloadJob job)
    {
        job.Status = "running";
        job.Say($"depot {job.Depot} version {job.Version} — {job.TotalFiles} files, mode {job.Mode}");

        var ct = job.Cts.Token;
        using var sampler = StartSpeedSampler(job, ct);
        using var gate = new SemaphoreSlim(Math.Max(1, settings.Concurrency));

        // Blobs first: the extractor reads them to resolve the chain, and they are tiny.
        var ordered = job.Files
            .OrderBy(f => f.Entry.Kind == Kind.Blob ? 0 : 1)
            .ThenBy(f => f.Entry.Version)
            .ToList();

        // The swarm is a whole-selection transfer rather than a queue of individual GETs, so it
        // takes its own path. Anything the torrent does not carry falls back to HTTP below.
        if (client.Primary.IsTorrent)
        {
            ordered = await ViaTorrentAsync(job, ordered, ct);
            if (ordered.Count == 0)
            {
                Finish(job);
                return;
            }
            job.Say($"{ordered.Count} file(s) are not in the torrent — fetching those over HTTP");
        }

        try
        {
            if (settings.PhasedDownloads)
            {
                // Two phases, each with its own stream count. Blobs are kilobytes, so latency
                // dominates and many at once is free. Dats are large and these mirrors speed a
                // connection up the longer it keeps asking, so only a couple of streams are used —
                // more of them, or one file split into ranges, all sit at the cold starting rate.
                var blobs = ordered.Where(f => f.Entry.Kind == Kind.Blob).ToList();
                var dats = ordered.Where(f => f.Entry.Kind == Kind.Dat).ToList();

                int blobStreams = Math.Max(1, settings.BlobConcurrency);
                int datStreams = Math.Max(1, settings.DatConcurrency);

                job.Say($"phased: {blobs.Count} blob(s) at {blobStreams} stream(s), " +
                        $"then {dats.Count} dat(s) at {datStreams} stream(s)");

                // Only the dat phase warms ahead. Blobs are kilobytes and already run 32 at a
                // time, so there is nothing to hide the latency of.
                await OnePhaseAsync(job, blobs, "blobs", blobStreams, warmAhead: false, ct);
                await OnePhaseAsync(job, dats, "dats", datStreams, warmAhead: true, ct);
            }
            else
            {
                await Task.WhenAll(ordered.Select(async pf =>
                {
                    await gate.WaitAsync(ct);
                    try { await OneFileAsync(job, pf, ct); }
                    finally { gate.Release(); }
                }));
            }

            Finish(job);
        }
        catch (OperationCanceledException)
        {
            job.Status = "cancelled";
            job.Say("cancelled");
        }
        catch (Exception ex)
        {
            job.Status = "failed";
            job.Error = ex.Message;
            job.Say($"failed: {ex.Message}");
        }
        finally
        {
            job.FinishedUtc = DateTime.UtcNow;
            job.SpeedBps = 0;
            job.Active.Clear();
        }
    }

    /// <summary>Runs one kind of file to completion before the caller moves on to the next phase.</summary>
    private async Task OnePhaseAsync(
        DownloadJob job, List<PlanFile> files, string what, int streams, bool warmAhead, CancellationToken ct)
    {
        if (files.Count == 0) return;

        int lookahead = warmAhead ? Math.Max(0, settings.WarmupLookahead) : 0;

        job.Say($"{what}: {files.Count} file(s), {streams} stream(s)"
                + (lookahead > 0 ? $", warming {lookahead} ahead" : ""));

        using var gate = new SemaphoreSlim(streams);

        await Task.WhenAll(files.Select(async (pf, index) =>
        {
            await gate.WaitAsync(ct);
            try
            {
                if (lookahead > 0) WarmAhead(files, index + lookahead, ct);
                await OneFileAsync(job, pf, ct);
            }
            finally { gate.Release(); }
        }));
    }

    /// <summary>
    /// Touches an upcoming file so the mirror can have it ready. Deliberately not awaited: the point
    /// is only that the request reaches the mirror, and its outcome never affects this download.
    /// </summary>
    private void WarmAhead(List<PlanFile> files, int index, CancellationToken ct)
    {
        if (index < 0 || index >= files.Count) return;

        var entry = files[index].Entry;
        if (File.Exists(Path.Combine(settings.DataDir, entry.DirName, entry.FileName))) return;

        _ = client.WarmAsync(entry, ct);
    }

    private static void Finish(DownloadJob job)
    {
        job.Status = job.FailedFiles > 0 ? "failed" : "done";
        if (job.FailedFiles > 0) job.Error = $"{job.FailedFiles} file(s) failed";
        job.Say(job.FailedFiles > 0
            ? $"finished with {job.FailedFiles} failure(s)"
            : $"finished — {job.DoneFiles} downloaded, {job.SkippedFiles} already present");
    }

    /// <summary>
    /// Pulls what the swarm has. Returns the files it could not supply, for the HTTP path to pick up.
    /// </summary>
    private async Task<List<PlanFile>> ViaTorrentAsync(DownloadJob job, List<PlanFile> files, CancellationToken ct)
    {
        // Files already on disk need neither source.
        var needed = files
            .Where(f => !File.Exists(Path.Combine(settings.DataDir, f.Entry.DirName, f.Entry.FileName)))
            .ToList();

        job.SkippedFiles += files.Count - needed.Count;
        if (needed.Count == 0) return [];

        job.Say("waiting for the torrent file list");

        var missing = await torrent.DownloadAsync(
            needed.Select(f => f.Entry).ToList(),
            (done, _, _) => Interlocked.Exchange(ref job.DoneBytes, done),
            ct);

        var missingNames = missing.Select(e => e.FileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        job.DoneFiles += needed.Count - missingNames.Count;

        return needed.Where(f => missingNames.Contains(f.Entry.FileName)).ToList();
    }

    /// <summary>Samples DoneBytes once a second so the UI has a throughput figure to show.</summary>
    private static Timer StartSpeedSampler(DownloadJob job, CancellationToken ct)
    {
        long previous = Interlocked.Read(ref job.DoneBytes);
        var clock = Stopwatch.StartNew();
        var lastAt = TimeSpan.Zero;

        return new Timer(_ =>
        {
            if (ct.IsCancellationRequested) return;

            long now = Interlocked.Read(ref job.DoneBytes);
            var at = clock.Elapsed;
            double secs = (at - lastAt).TotalSeconds;
            if (secs > 0) job.SpeedBps = Math.Max(0, (now - previous) / secs);

            previous = now;
            lastAt = at;
        }, null, TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
    }

    private async Task OneFileAsync(DownloadJob job, PlanFile pf, CancellationToken ct)
    {
        var entry = pf.Entry;
        string dest = Path.Combine(settings.DataDir, entry.DirName, entry.FileName);

        var fp = new FileProgress { Name = entry.FileName, Total = Math.Max(0, pf.Size) };
        job.Active[entry.FileName] = fp;

        long counted = 0;

        try
        {
            bool existed = File.Exists(dest);

            await client.DownloadFileAsync(entry, dest, settings.VerifyHashes, (done, total) =>
            {
                if (total > 0)
                {
                    long previousTotal = Interlocked.Exchange(ref fp.Total, total);
                    if (previousTotal != total)
                        Interlocked.Add(ref job.TotalBytes, total - previousTotal);
                }
                fp.Done = done;

                long delta = done - counted;
                if (delta != 0)
                {
                    counted = done;
                    Interlocked.Add(ref job.DoneBytes, delta);
                }
            }, ct);

            fp.State = existed ? "skipped" : "done";
            if (existed) Interlocked.Increment(ref job.SkippedFiles);
            else Interlocked.Increment(ref job.DoneFiles);

            job.Active.TryRemove(entry.FileName, out _);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            job.Active.TryRemove(entry.FileName, out _);
            throw;
        }
        catch (Exception ex)
        {
            fp.State = "failed";
            fp.Error = ex.Message;
            Interlocked.Increment(ref job.FailedFiles);
            job.Say($"FAILED {entry.FileName}: {ex.Message}");
        }
    }
}
