using System.Collections.Concurrent;
using System.Diagnostics;

namespace Steam2Browser;

public sealed class ExtractRun
{
    public required string Id { get; init; }
    public int Depot;
    public int Version;
    public string? BlobCrc;
    public string CommandLine = "";
    public string Status = "running"; // running | done | failed | cancelled
    public int? ExitCode;
    public string? Error;
    public DateTime StartedUtc = DateTime.UtcNow;
    public DateTime? FinishedUtc;
    public readonly ConcurrentQueue<string> Log = new();

    internal Process? Proc;

    public void Say(string line)
    {
        Log.Enqueue(line);
        while (Log.Count > 2000) Log.TryDequeue(out _);
    }
}

/// <summary>
/// Drives the bundled extract.exe. Its CLI, from the published source:
///   extract &lt;blob_path&gt; &lt;dat_path&gt; &lt;depot&gt; &lt;version&gt; [--blobcrc X] [--filter re] [--key K] [--out dir]
/// </summary>
public sealed class ExtractorRunner(ArchiveClient client, Settings settings)
{
    private readonly ConcurrentDictionary<string, ExtractRun> _runs = new();
    private int _seq;

    public IReadOnlyCollection<ExtractRun> Runs => _runs.Values;
    public ExtractRun? Get(string id) => _runs.GetValueOrDefault(id);

    /// <summary>Path to extract.exe, downloading it from the mirror the first time if needed.</summary>
    public async Task<string> EnsureExeAsync(CancellationToken ct = default)
    {
        if (!string.IsNullOrWhiteSpace(settings.ExtractExePath) && File.Exists(settings.ExtractExePath))
            return settings.ExtractExePath;

        string target = Path.Combine(settings.IndexDir, "extract.exe");
        if (File.Exists(target) && new FileInfo(target).Length > 0)
        {
            settings.ExtractExePath = target;
            settings.Save();
            return target;
        }

        Directory.CreateDirectory(settings.IndexDir);
        byte[] data = await client.GetBytesAsync("extractor/extract.exe", ct);
        await File.WriteAllBytesAsync(target, data, ct);

        settings.ExtractExePath = target;
        settings.Save();
        return target;
    }

    public async Task<ExtractRun> StartAsync(int depot, int version, string? blobCrc, string? filter, CancellationToken ct = default)
    {
        string exe = await EnsureExeAsync(ct);

        string blobDir = Path.Combine(settings.DataDir, "blobs");
        string datDir = Path.Combine(settings.DataDir, "dats");
        string outDir = Path.Combine(settings.ExtractOutDir, $"{depot}_{version}");
        Directory.CreateDirectory(outDir);

        var args = new List<string> { blobDir, datDir, depot.ToString(), version.ToString() };
        if (!string.IsNullOrWhiteSpace(blobCrc)) { args.Add("--blobcrc"); args.Add(blobCrc.Trim()); }
        if (!string.IsNullOrWhiteSpace(filter)) { args.Add("--filter"); args.Add(filter.Trim()); }
        args.Add("--out"); args.Add(outDir);

        var run = new ExtractRun
        {
            Id = $"ext{Interlocked.Increment(ref _seq)}",
            Depot = depot,
            Version = version,
            BlobCrc = blobCrc,
            CommandLine = Quote(exe) + " " + string.Join(' ', args.Select(Quote)),
        };
        _runs[run.Id] = run;

        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = settings.DataDir,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        run.Say(run.CommandLine);
        run.Say($"output directory: {outDir}");
        run.Say("--");

        try
        {
            var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
            proc.OutputDataReceived += (_, e) => { if (e.Data is not null) run.Say(e.Data); };
            proc.ErrorDataReceived += (_, e) => { if (e.Data is not null) run.Say(e.Data); };

            proc.Start();
            run.Proc = proc;
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();

            _ = Task.Run(async () =>
            {
                try
                {
                    await proc.WaitForExitAsync();
                    run.ExitCode = proc.ExitCode;
                    run.Status = proc.ExitCode == 0 ? "done" : "failed";
                    if (proc.ExitCode != 0) run.Error = $"exit code {proc.ExitCode}";
                }
                catch (Exception ex)
                {
                    run.Status = "failed";
                    run.Error = ex.Message;
                }
                finally
                {
                    run.FinishedUtc = DateTime.UtcNow;
                }
            });
        }
        catch (Exception ex)
        {
            run.Status = "failed";
            run.Error = ex.Message;
            run.FinishedUtc = DateTime.UtcNow;
            run.Say($"could not start: {ex.Message}");
        }

        return run;
    }

    public void Cancel(string id)
    {
        if (_runs.TryGetValue(id, out var run) && run.Proc is { HasExited: false } p)
        {
            try { p.Kill(entireProcessTree: true); run.Status = "cancelled"; }
            catch { /* already gone */ }
        }
    }

    public void Clear()
    {
        foreach (var kv in _runs)
            if (kv.Value.Status is "done" or "failed" or "cancelled")
                _runs.TryRemove(kv.Key, out _);
    }

    private static string Quote(string s) => s.Contains(' ') ? $"\"{s}\"" : s;
}
