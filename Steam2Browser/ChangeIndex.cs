using System.Collections.Concurrent;

namespace Steam2Browser;

/// <summary>What one version of a depot did, as far as its blob and its predecessor's can tell.</summary>
public sealed class VersionChanges
{
    public int Version;
    public string Crc = "";
    public string? Date;

    /// <summary>Whether that version's blob is on disk. Without it nothing below is known.</summary>
    public bool Local;

    public int AddedCount;
    public int ChangedCount;
    public int RemovedCount;

    /// <summary>Bytes carried in this version's dat: the new files plus the rewritten ones.</summary>
    public long PayloadBytes;

    /// <summary>How much the depot grew or shrank, additions and removals included.</summary>
    public long DeltaBytes;

    public int FilesInVersion;

    /// <summary>True when the predecessor's blob is missing, so new and changed cannot be told apart.</summary>
    public bool Unclassified;

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
/// A version's blob holds the manifest (every path and size at that point) and the file id table
/// (only the files whose data sits in that version's dat). Comparing a version's manifest with its
/// predecessor's turns that into a real diff: which files are new, which were rewritten, which
/// disappeared, and how each one's size moved — all without touching a single dat.
/// </summary>
public sealed class ChangeIndex(ArchiveClient client, Settings settings)
{
    /// <summary>
    /// One blob, decoded and keyed by path rather than by file id.
    ///
    /// A rewritten file gets a fresh file id in the next version, so comparing ids reports every
    /// edit as a removal plus an addition — 67681 v1 came out as 143 new and 155 removed with not
    /// one changed. The path is what actually persists across versions.
    /// </summary>
    private sealed record Decoded(
        Dictionary<string, long> SizeByPath,
        Dictionary<string, byte> ModeByPath,
        HashSet<string> CarriedHere,
        uint? ParentCrc);

    private readonly ConcurrentDictionary<string, Decoded> _decoded = new();
    private readonly ConcurrentDictionary<string, VersionChanges> _summaries = new();
    private readonly ConcurrentDictionary<int, BlobFetchStatus> _fetches = new();

    public BlobFetchStatus StatusFor(int depot) =>
        _fetches.GetValueOrDefault(depot) ?? new BlobFetchStatus();

    private string PathOf(Entry blob) => Path.Combine(settings.DataDir, blob.DirName, blob.FileName);

    private Decoded? Decode(Entry blob)
    {
        if (_decoded.TryGetValue(blob.FileName, out var known)) return known;

        string blobPath = PathOf(blob);
        if (!File.Exists(blobPath)) return null;

        byte[] bytes = File.ReadAllBytes(blobPath);

        var tree = ManifestFormat.TreeFromBlob(bytes)
                   ?? throw new InvalidDataException("this blob carries no manifest");

        // The manifest records a size for every file at this version, not only the changed ones,
        // which is what makes a size delta against the previous version possible at all.
        var sizeByPath = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        var pathById = new Dictionary<uint, string>();

        foreach (var node in tree.Nodes)
        {
            if (node.Flags == 0) continue;
            sizeByPath[node.Path] = node.Size;
            pathById[node.FileId] = node.Path;
        }

        var table = ChecksumTable.Parse(bytes, blob.Version);

        var modeByPath = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var carried = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (id, loc) in table)
        {
            if (!pathById.TryGetValue(id, out var path)) continue;
            carried.Add(path);
            modeByPath[path] = loc.FileMode;
            sizeByPath[path] = (long)loc.FileSize;
        }

        var info = BlobFormat.Parse(bytes);

        var decoded = new Decoded(sizeByPath, modeByPath, carried, info.ParentCrc);
        _decoded[blob.FileName] = decoded;
        return decoded;
    }

    /// <summary>The blob this one was built on: by recorded parent crc where possible.</summary>
    private Entry? PreviousOf(Depot depot, Entry blob)
    {
        if (blob.Version == 0) return null;

        var below = depot.Blobs.Where(b => b.Version == blob.Version - 1).ToList();
        if (below.Count == 0) return null;
        if (below.Count == 1) return below[0];

        // Forked: the blob names its own parent, so there is no need to guess.
        var decoded = _decoded.GetValueOrDefault(blob.FileName);
        if (decoded?.ParentCrc is uint crc)
        {
            var exact = below.FirstOrDefault(b => b.Crc == crc);
            if (exact is not null) return exact;
        }
        return below[0];
    }

    public readonly record struct ChangedFile(string Path, long Size, long Delta, byte Mode, string Change);

    /// <summary>The per-file diff for one version, or null when its blob is not on disk.</summary>
    public (List<ChangedFile> Files, bool Unclassified)? Diff(Depot depot, Entry blob)
    {
        var now = Decode(blob);
        if (now is null) return null;

        var prevEntry = PreviousOf(depot, blob);
        var before = prevEntry is null ? null : Decode(prevEntry);

        // Version 0 has nothing before it, so everything in it is genuinely new.
        bool unclassified = blob.Version > 0 && before is null;

        var files = new List<ChangedFile>(now.CarriedHere.Count + 8);

        foreach (var path in now.CarriedHere)
        {
            long size = now.SizeByPath.GetValueOrDefault(path);
            byte mode = now.ModeByPath.GetValueOrDefault(path);

            string change;
            long delta;

            if (before is null)
            {
                change = blob.Version == 0 ? "new" : "changed";
                delta = blob.Version == 0 ? size : 0;
            }
            else if (before.SizeByPath.TryGetValue(path, out long was))
            {
                change = "changed";
                delta = size - was;
            }
            else
            {
                change = "new";
                delta = size;
            }

            files.Add(new ChangedFile(path, size, delta, mode, change));
        }

        // A file that was there and is not any more carries no data in this dat, so it would
        // otherwise be invisible — but it is exactly what a diff should show.
        if (before is not null)
        {
            foreach (var (path, was) in before.SizeByPath)
            {
                if (now.SizeByPath.ContainsKey(path)) continue;
                files.Add(new ChangedFile(path, 0, -was, 0, "removed"));
            }
        }

        files.Sort((a, b) => string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase));
        return (files, unclassified);
    }

    /// <summary>Every version of the depot, newest first, with its counts where the blobs allow.</summary>
    public List<VersionChanges> Summary(Depot depot)
    {
        var rows = new List<VersionChanges>(depot.Blobs.Count);

        foreach (var blob in depot.Blobs.OrderByDescending(b => b.Version).ThenBy(b => b.Date))
            rows.Add(Describe(depot, blob));

        return rows;
    }

    public VersionChanges Describe(Depot depot, Entry blob)
    {
        // Keyed on the predecessor too: counts change once the previous blob arrives.
        var prev = PreviousOf(depot, blob);
        string key = blob.FileName + "|" + (prev is null ? "-" : (File.Exists(PathOf(prev)) ? prev.FileName : "?"));

        if (_summaries.TryGetValue(key, out var known)) return known;

        var row = new VersionChanges
        {
            Version = blob.Version,
            Crc = blob.CrcHex,
            Date = blob.Date == default ? null : blob.Date.ToString("yyyy-MM-dd HH:mm:ss"),
        };

        try
        {
            var result = Diff(depot, blob);
            if (result is null) return row;   // not cached: the blob may arrive later

            var (files, unclassified) = result.Value;

            row.Local = true;
            row.Unclassified = unclassified;
            row.AddedCount = files.Count(f => f.Change == "new");
            row.ChangedCount = files.Count(f => f.Change == "changed");
            row.RemovedCount = files.Count(f => f.Change == "removed");
            row.PayloadBytes = files.Where(f => f.Change != "removed").Sum(f => f.Size);
            row.DeltaBytes = files.Sum(f => f.Delta);
            row.FilesInVersion = Decode(blob)?.SizeByPath.Count ?? 0;
        }
        catch (Exception ex)
        {
            row.Local = true;
            row.Error = ex.Message;
        }

        _summaries[key] = row;
        return row;
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

                // Counts were computed while blobs were missing, so drop them and let them rebuild.
                _summaries.Clear();

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
