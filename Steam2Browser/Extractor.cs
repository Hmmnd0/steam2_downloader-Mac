using System.Collections.Concurrent;

namespace Steam2Browser;

public sealed class ExtractRun
{
    public required string Id { get; init; }
    public int Depot;
    public int Version;
    public string? BlobCrc;
    public string OutDir = "";
    public string Status = "running"; // running | done | failed | cancelled
    public string? Error;
    public DateTime StartedUtc = DateTime.UtcNow;
    public DateTime? FinishedUtc;

    public readonly ExtractProgress Progress = new();
    public readonly ConcurrentQueue<string> Log = new();

    internal CancellationTokenSource Cts = new();

    public void Say(string line)
    {
        Log.Enqueue($"{DateTime.Now:HH:mm:ss}  {line}");
        while (Log.Count > 2000) Log.TryDequeue(out _);
    }
}

/// <summary>
/// Runs extraction in-process through <see cref="Steam2Extractor"/>. Nothing external is needed —
/// the depot keys, blob, manifest and chunk formats are all ported, so there is no extract.exe to
/// download and none of its path handling to work around.
/// </summary>
public sealed class ExtractorRunner(Settings settings)
{
    private readonly ConcurrentDictionary<string, ExtractRun> _runs = new();
    private int _seq;

    public IReadOnlyCollection<ExtractRun> Runs => _runs.Values.ToArray();
    public ExtractRun? Get(string id) => _runs.GetValueOrDefault(id);

    public ExtractRun Start(int depot, int version, string? blobCrc, string? filter, string? keyHex,
                            IReadOnlyDictionary<int, DateTime>? datesByVersion = null,
                            string? outDirOverride = null)
    {
        // An app install passes its own folder: the depots of a game overlay into one tree rather
        // than sitting in a directory each.
        string outDir = outDirOverride ?? Path.Combine(settings.ExtractOutDir, $"{depot}_{version}");

        var run = new ExtractRun
        {
            Id = $"ext{Interlocked.Increment(ref _seq)}",
            Depot = depot,
            Version = version,
            BlobCrc = blobCrc,
            OutDir = outDir,
        };
        _runs[run.Id] = run;

        run.Say($"depot {depot} version {version}{(blobCrc is null ? "" : $" crc {blobCrc}")}");
        run.Say($"output: {outDir}");

        _ = Task.Run(() =>
        {
            try
            {
                Directory.CreateDirectory(outDir);

                var key = DepotKeys.ParseHex(keyHex);
                if (keyHex is { Length: > 0 } && key is null)
                    throw new InvalidDataException("the supplied key is not 32 hex characters");

                Steam2Extractor.Extract(
                    settings.DataDir, depot, version, blobCrc, filter, outDir,
                    key, run.Progress, run.Say, run.Cts.Token, datesByVersion);

                run.Status = run.Progress.FailedFiles > 0 ? "failed" : "done";
                if (run.Progress.FailedFiles > 0)
                    run.Error = $"{run.Progress.FailedFiles} file(s) failed";
            }
            catch (OperationCanceledException)
            {
                run.Status = "cancelled";
                run.Say("cancelled");
            }
            catch (Exception ex)
            {
                run.Status = "failed";
                run.Error = ex.Message;
                run.Say($"failed: {ex.Message}");
            }
            finally
            {
                run.FinishedUtc = DateTime.UtcNow;
                run.Progress.Current = "";
            }
        });

        return run;
    }

    public void Cancel(string id)
    {
        if (_runs.TryGetValue(id, out var run)) run.Cts.Cancel();
    }

    public void Clear()
    {
        foreach (var kv in _runs)
            if (kv.Value.Status is "done" or "failed" or "cancelled")
                _runs.TryRemove(kv.Key, out _);
    }
}
