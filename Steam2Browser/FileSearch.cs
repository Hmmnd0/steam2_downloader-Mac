using System.Collections.Concurrent;

namespace Steam2Browser;

public sealed class FileSearchStatus
{
    public bool Running;
    public int DepotsIndexed;
    public int DepotsToIndex;
    public int PathCount;
    public bool Capped;
    public DateTime? BuiltUtc;
    public string Message = "";

    /// <summary>How many blob files were on disk when the index was built. Comparing it against
    /// the current count is what tells a reader their search is missing newly downloaded blobs.</summary>
    public int BlobsIndexed;
}

/// <summary>
/// Searches file paths across every blob already on disk.
///
/// A blob's manifest lists every path in that version, so the union over a depot's local blobs is
/// everything that depot ever shipped — including files that were removed again later. Nothing is
/// downloaded to search: what is not on disk simply is not in the index, and the status says how
/// much of the archive that covers.
/// </summary>
public sealed class FileSearch(Settings settings)
{
    /// <summary>
    /// Paths are held once per depot rather than per version, so a depot with 400 versions costs
    /// no more than its distinct file set. The cap stops a full-archive blob fetch from eating
    /// the process alive.
    /// </summary>
    private const int MaxPaths = 3_000_000;

    private readonly ConcurrentDictionary<int, string[]> _byDepot = new();

    public FileSearchStatus Status { get; } = new();

    public readonly record struct Hit(int Depot, string Path);

    public void Build(Catalog catalog)
    {
        lock (Status)
        {
            if (Status.Running) return;
            Status.Running = true;
            Status.DepotsIndexed = 0;
            Status.PathCount = 0;
            Status.Capped = false;
        }

        _ = Task.Run(() =>
        {
            try
            {
                string blobDir = Path.Combine(settings.DataDir, "blobs");
                if (!Directory.Exists(blobDir))
                {
                    Status.Message = "no blobs downloaded yet — nothing to search";
                    return;
                }

                // Only depots that actually have a blob on disk are worth opening.
                var present = new HashSet<string>(
                    Directory.EnumerateFiles(blobDir, "*.blob").Select(Path.GetFileName)!,
                    StringComparer.OrdinalIgnoreCase);

                Status.BlobsIndexed = present.Count;

                var todo = catalog.Ordered
                    .Where(d => d.Blobs.Any(b => present.Contains(b.FileName)))
                    .ToList();

                Status.DepotsToIndex = todo.Count;
                Status.Message = $"indexing {todo.Count} depot(s)";

                _byDepot.Clear();
                long total = 0;

                Parallel.ForEach(todo, new ParallelOptions { MaxDegreeOfParallelism = 4 }, depot =>
                {
                    if (Volatile.Read(ref total) >= MaxPaths) return;

                    var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                    foreach (var blob in depot.Blobs)
                    {
                        if (!present.Contains(blob.FileName)) continue;

                        try
                        {
                            var tree = ManifestFormat.TreeFromBlob(
                                File.ReadAllBytes(Path.Combine(blobDir, blob.FileName)));
                            if (tree is null) continue;

                            foreach (var node in tree.Nodes)
                                if (node.Flags != 0 && node.Path.Length > 0) paths.Add(node.Path);
                        }
                        catch
                        {
                            // A damaged blob costs its own paths, not the whole index.
                        }
                    }

                    if (paths.Count > 0)
                    {
                        _byDepot[depot.Id] = [.. paths];
                        Interlocked.Add(ref total, paths.Count);
                    }

                    Interlocked.Increment(ref Status.DepotsIndexed);
                    Status.PathCount = (int)Math.Min(int.MaxValue, Volatile.Read(ref total));
                });

                Status.Capped = Volatile.Read(ref total) >= MaxPaths;
                Status.BuiltUtc = DateTime.UtcNow;
                Status.Message = Status.Capped
                    ? $"{Status.PathCount:N0} paths from {_byDepot.Count} depot(s) — index full, stopped early"
                    : $"{Status.PathCount:N0} paths from {_byDepot.Count} depot(s)";
            }
            catch (Exception ex)
            {
                Status.Message = ex.Message;
            }
            finally
            {
                Status.Running = false;
            }
        });
    }

    /// <summary>
    /// How many blob files sit on disk right now. Counted at most once every few seconds: the
    /// search box asks on every keystroke, and the archive can hold tens of thousands of blobs.
    /// </summary>
    public int BlobsOnDisk()
    {
        if (DateTime.UtcNow - _countedAt < TimeSpan.FromSeconds(5)) return _blobCount;

        try
        {
            string blobDir = Path.Combine(settings.DataDir, "blobs");
            _blobCount = Directory.Exists(blobDir)
                ? Directory.EnumerateFiles(blobDir, "*.blob").Count()
                : 0;
        }
        catch
        {
            // Leave the previous count; a failed listing is not evidence the index went stale.
        }

        _countedAt = DateTime.UtcNow;
        return _blobCount;
    }

    private int _blobCount;
    private DateTime _countedAt = DateTime.MinValue;

    /// <summary>
    /// Substring match over indexed paths. Quoting the term requires the file name to match it
    /// exactly, which is how you find "config.ini" without also getting every path containing it.
    /// </summary>
    public List<Hit> Search(string query, int limit)
    {
        var needle = query.Trim();
        if (needle.Length < 2) return [];

        bool exact = needle.Length >= 2
                     && (needle[0] == '"' && needle[^1] == '"' || needle[0] == '\'' && needle[^1] == '\'');
        if (exact) needle = needle[1..^1].Trim();
        if (needle.Length == 0) return [];

        var hits = new List<Hit>(limit);

        foreach (var (depot, paths) in _byDepot.OrderBy(kv => kv.Key))
        {
            foreach (var path in paths)
            {
                bool match = exact
                    ? NameOf(path).Equals(needle, StringComparison.OrdinalIgnoreCase)
                    : path.Contains(needle, StringComparison.OrdinalIgnoreCase);

                if (!match) continue;

                hits.Add(new Hit(depot, path));
                if (hits.Count >= limit) return hits;
            }
        }

        return hits;
    }

    private static string NameOf(string path)
    {
        int slash = path.LastIndexOf('/');
        return slash < 0 ? path : path[(slash + 1)..];
    }
}
