using System.Collections.Concurrent;

namespace Steam2Browser;

/// <summary>What one version of a depot changed, as far as its blob can tell.</summary>
public sealed class VersionChanges
{
    public int Version;
    public string Crc = "";
    public string? Date;

    /// <summary>Whether that version's blob is on disk. Without it nothing below is known.</summary>
    public bool Local;

    public int ChangedCount;
    public long ChangedBytes;
    public int FilesInVersion;

    /// <summary>A first release lists every file, because nothing came before it.</summary>
    public bool WholeSet;

    public string? Error;
}

public sealed class BlobFetchStatus
{
    public bool Running;
    public int Done;
    public int Total;
    public int Failed;
    public string Message = "";
}

/// <summary>
/// Reads per-version change lists out of blobs, and pulls a depot's blobs in bulk so the whole
/// history can be browsed at once.
///
/// A version's blob holds both the manifest (every path at that point) and the file id table (only
/// the files whose data sits in that version's dat), so intersecting them names exactly what that
/// version changed — without touching a single dat. Blobs are kilobytes, so fetching all of them
/// for a depot costs a few megabytes even for the longest chains.
/// </summary>
public sealed class ChangeIndex(ArchiveClient client, Settings settings)
{
    /// <summary>Parsed results, keyed by the blob's own identity so a re-read is never needed.</summary>
    private readonly ConcurrentDictionary<string, VersionChanges> _parsed = new();

    private readonly ConcurrentDictionary<int, BlobFetchStatus> _fetches = new();

    public BlobFetchStatus StatusFor(int depot) =>
        _fetches.GetValueOrDefault(depot) ?? new BlobFetchStatus();

    private string PathOf(Entry blob) => Path.Combine(settings.DataDir, blob.DirName, blob.FileName);

    /// <summary>Every version of the depot, newest first, with its change counts where known.</summary>
    public List<VersionChanges> Summary(Depot depot)
    {
        var rows = new List<VersionChanges>(depot.Blobs.Count);

        foreach (var blob in depot.Blobs.OrderByDescending(b => b.Version).ThenBy(b => b.Date))
            rows.Add(Describe(blob));

        return rows;
    }

    /// <summary>Counts for one blob, parsed on first sight and remembered afterwards.</summary>
    public VersionChanges Describe(Entry blob)
    {
        if (_parsed.TryGetValue(blob.FileName, out var known)) return known;

        var row = new VersionChanges
        {
            Version = blob.Version,
            Crc = blob.CrcHex,
            Date = blob.Date == default ? null : blob.Date.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        string path = PathOf(blob);
        if (!File.Exists(path)) return row;   // not cached: it may appear later

        try
        {
            var (files, total, whole) = Read(File.ReadAllBytes(path), blob.Version);
            row.Local = true;
            row.ChangedCount = files.Count;
            row.ChangedBytes = files.Sum(f => f.Size);
            row.FilesInVersion = total;
            row.WholeSet = whole;
        }
        catch (Exception ex)
        {
            row.Local = true;
            row.Error = ex.Message;
        }

        _parsed[blob.FileName] = row;
        return row;
    }

    public readonly record struct ChangedFile(string Path, long Size, byte Mode, uint Blocks);

    /// <summary>The changed files of one version, or null when its blob is not on disk.</summary>
    public List<ChangedFile>? FilesFor(Entry blob)
    {
        string path = PathOf(blob);
        if (!File.Exists(path)) return null;

        return Read(File.ReadAllBytes(path), blob.Version).Files;
    }

    private static (List<ChangedFile> Files, int Total, bool Whole) Read(byte[] bytes, int version)
    {
        var tree = ManifestFormat.TreeFromBlob(bytes)
                   ?? throw new InvalidDataException("this blob carries no manifest");

        var paths = new Dictionary<uint, string>();
        foreach (var node in tree.Nodes)
            if (node.Flags != 0) paths[node.FileId] = node.Path;

        var table = ChecksumTable.Parse(bytes, version);

        var files = table
            .Where(kv => paths.ContainsKey(kv.Key))
            .Select(kv => new ChangedFile(paths[kv.Key], (long)kv.Value.FileSize, kv.Value.FileMode, kv.Value.BlockCount))
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .ToList();

        return (files, paths.Count, files.Count >= paths.Count);
    }

    // ---------------- bulk fetch ----------------

    /// <summary>
    /// Downloads every blob of the depot that is missing. Safe to call again; a second call while
    /// one is running is ignored.
    /// </summary>
    public void FetchAll(Depot depot)
    {
        var status = _fetches.GetOrAdd(depot.Id, _ => new BlobFetchStatus());
        lock (status)
        {
            if (status.Running) return;
            status.Running = true;
            status.Done = 0;
            status.Failed = 0;
            status.Total = 0;
            status.Message = "";
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var missing = depot.Blobs.Where(b => !File.Exists(PathOf(b))).ToList();
                status.Total = missing.Count;
                status.Message = missing.Count == 0
                    ? "every blob is already here"
                    : $"fetching {missing.Count} blob(s)";

                // Blobs are kilobytes, so latency dominates and many at once costs nothing.
                using var gate = new SemaphoreSlim(16);

                await Task.WhenAll(missing.Select(async blob =>
                {
                    await gate.WaitAsync();
                    try
                    {
                        string path = PathOf(blob);
                        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

                        byte[] bytes = await client.GetBytesAsync(blob.RelPath);
                        await File.WriteAllBytesAsync(path, bytes);

                        _parsed.TryRemove(blob.FileName, out _);   // re-read now that it is here
                        Interlocked.Increment(ref status.Done);
                    }
                    catch
                    {
                        Interlocked.Increment(ref status.Failed);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }));

                status.Message = status.Failed > 0
                    ? $"{status.Done} fetched, {status.Failed} failed"
                    : $"{status.Done} blob(s) fetched";
            }
            catch (Exception ex)
            {
                status.Message = ex.Message;
            }
            finally
            {
                status.Running = false;
            }
        });
    }
}
