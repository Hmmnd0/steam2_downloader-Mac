using System.Text;
namespace Steam2Browser;

public sealed class LoadStatus
{
    public string Phase = "loading...";      // loading | index | parse | sizes | ready | error
    public string Message = "";
    public double Percent;
    public bool Ready;
    public string? Error;
}

/// <summary>
/// Brings the catalog into memory: the two *_dates.txt indexes give every file name and date, and the
/// directory listings give sizes. The sha256 in a file name is the sha256 of its contents (verified across
/// all 116339 entries), so dats.sha256 / blobs.sha256 are redundant and never fetched.
/// </summary>
public sealed class IndexLoader(ArchiveClient client, Settings settings)
{
    public LoadStatus Status { get; } = new();
    public Catalog? Catalog { get; private set; }

    private const string DatsDates = "dats_dates.txt";
    private const string BlobsDates = "blobs_dates.txt";
    private const string SizeCache = "sizes.bin";
    private const string CompactFile = "index.bin";
    private const int SizeCacheMagic = 0x32534253; // "SBS2"

    public async Task LoadAsync(bool refreshIndex, bool withSizes, bool ignoreEmbedded = false, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(settings.IndexDir);

            // Order of preference: a refreshed compact index on disk, then the one baked into this
            // build, then the mirror. The baked-in copy is what keeps a first run off the network —
            // otherwise it is 13 MB of *_dates.txt plus ~40 MB of directory listings for the sizes.
            if (!refreshIndex && !ignoreEmbedded)
            {
                var snapshot = CompactIndex.FromFile(Path.Combine(settings.IndexDir, CompactFile))
                               ?? CompactIndex.FromEmbedded();

                if (snapshot is not null)
                {
                    Status.Phase = "parse";
                    Status.Message = "reading the built-in index";
                    Status.Percent = 50;

                    var built = await Task.Run(() => CompactIndex.ToCatalog(snapshot), ct);
                    Catalog = built;

                    Status.Ready = true;
                    Done(built, $"snapshot from {snapshot.GeneratedUtc:yyyy-MM-dd}");

                    // A snapshot built without --with-sizes carries none, and this branch used to
                    // return before anything could fill them in — leaving every depot at 0 bytes
                    // and the archive total blank, permanently.
                    if (withSizes && !built.SizesLoaded) await LoadSizesAsync(false, ct);

                    return;
                }
            }

            Status.Phase = "index";
            Status.Percent = 0;

            string datsPath = await EnsureAsync(DatsDates, refreshIndex, 0, 40, ct);
            string blobsPath = await EnsureAsync(BlobsDates, refreshIndex, 40, 80, ct);

            Status.Phase = "parse";
            Status.Message = "parsing index";
            Status.Percent = 82;

            var cat = await Task.Run(() =>
            {
                var dats = Catalog.ParseDatesFile(datsPath, Kind.Dat);
                var blobs = Catalog.ParseDatesFile(blobsPath, Kind.Blob);
                return Catalog.Build(dats, blobs);
            }, ct);

            Catalog = cat;
            Status.Ready = true;
            Status.Phase = "ready";
            Status.Percent = 100;
            Status.Message = $"{cat.Ordered.Count} depots, {cat.DatCount} dats, {cat.BlobCount} blobs";

            if (withSizes) await LoadSizesAsync(false, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status.Phase = "loading...";
            Status.Message = "cancelled";
        }
        catch (Exception ex)
        {
            Status.Phase = "error";
            Status.Error = ex.Message;
            Status.Message = ex.Message;
        }
    }

    /// <summary>Fills in file sizes, from the local cache when possible or the two directory listings otherwise.</summary>
    public async Task LoadSizesAsync(bool force, CancellationToken ct = default)
    {
        var cat = Catalog;
        if (cat is null) return;

        string cachePath = Path.Combine(settings.IndexDir, SizeCache);

        try
        {
            if (!force && File.Exists(cachePath))
            {
                Status.Phase = "sizes";
                Status.Message = "reading cached sizes";
                var cached = await Task.Run(() => ReadSizeCache(cachePath), ct);
                if (cached.Count > 0)
                {
                    cat.ApplySizes(cached);
                    Done(cat);
                    return;
                }
            }

            var sizes = new Dictionary<(Kind, int, int, uint), long>(120000);

            // Each listing is about half the work, so the two fill 0-50% and 50-100% of the bar.
            Status.Phase = "sizes";
            await CollectAsync("dats/", Kind.Dat, sizes, "dats", 0, ct);
            await CollectAsync("blobs/", Kind.Blob, sizes, "blobs", 50, ct);

            cat.ApplySizes(sizes);
            await Task.Run(() => WriteSizeCache(cachePath, sizes), ct);
            SaveCompact(cat);
            Done(cat);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status.Phase = "ready";
            Status.Message = "size load cancelled";
        }
        catch (Exception ex)
        {
            Status.Phase = "ready";
            Status.Message = $"sizes unavailable: {ex.Message}";
        }
    }

    private void Done(Catalog cat, string? note = null)
    {
        Status.Phase = "ready";
        Status.Percent = 100;
        Status.Message = $"{cat.Ordered.Count} depots · {Fmt(cat.ApproxTotalBytes)} total"
                         + (note is null ? "" : $" · {note}");
    }

    /// <summary>Decimal units: MB is 10^6 bytes, as the unit is actually defined. The mirror
    /// listings quote MiB, so <see cref="Catalog.ParseHumanSize"/> still reads those — that is the
    /// format of the source, not a display choice.</summary>
    private static string Fmt(long b)
    {
        string[] u = ["B", "KB", "MB", "GB", "TB", "PB"];
        double v = b;
        int i = 0;
        while (v >= 1000 && i < u.Length - 1) { v /= 1000; i++; }
        return $"{v:0.##} {u[i]}";
    }

    private async Task CollectAsync(
        string dir, Kind kind, Dictionary<(Kind, int, int, uint), long> sizes,
        string label, double percentBase, CancellationToken ct)
    {
        bool isDats = kind == Kind.Dat;
        long expectedBytes() => isDats ? settings.DatListingBytes : settings.BlobListingBytes;
        void storeBytes(long n)
        {
            if (isDats) settings.DatListingBytes = n;
            else settings.BlobListingBytes = n;
        }

        // The listings are generated by nginx on the fly and sent chunked, so no Content-Length
        // ever arrives and the only total available is how big this listing was last time. It
        // barely changes between runs, which makes it a good estimate — but an estimate, so the
        // bar is clamped and the figure is marked approximate.
        long expected = expectedBytes();

        Status.Message = $"fetching {label} listing";
        Status.Percent = percentBase;

        string html = await client.GetStringAsync(dir, ct, (read, _) =>
        {
            if (expected > 0)
            {
                double share = Math.Min(1d, (double)read / expected);
                Status.Percent = percentBase + 50d * share;
                Status.Message = $"fetching {label} listing — {Fmt(read)} of ~{Fmt(expected)}";
            }
            else
            {
                Status.Message = $"fetching {label} listing — {Fmt(read)}";
            }
        });

        // Now it is known exactly, so the next run does not have to guess.
        storeBytes(Encoding.UTF8.GetByteCount(html));
        settings.Save();

        Status.Percent = percentBase + 50d;
        Status.Message = $"parsing {label} listing";
        foreach (var (name, size) in Catalog.ParseListing(html))
        {
            if (size < 0) continue;
            var e = Catalog.ParseName(name, kind);
            if (e is null) continue;
            sizes[(kind, e.Depot, e.Version, e.Crc)] = size;
        }
    }

    /// <summary>
    /// Existing copies of an index file, searched next to the executable and the working directory
    /// and then upwards through their parents.
    /// </summary>
    private static IEnumerable<string> NearbyCopies(string name)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var start in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
        {
            var dir = new DirectoryInfo(start);
            for (int up = 0; up < 6 && dir is not null; up++, dir = dir.Parent)
            {
                string candidate = Path.Combine(dir.FullName, name);
                if (!seen.Add(candidate)) continue;
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                    yield return candidate;
            }
        }
    }

    /// <summary>Uses a local copy of an index file when present, otherwise pulls it from the mirror.</summary>
    private async Task<string> EnsureAsync(string name, bool refresh, double from, double to, CancellationToken ct)
    {
        string target = Path.Combine(settings.IndexDir, name);

        if (!refresh && File.Exists(target) && new FileInfo(target).Length > 0)
        {
            Status.Message = $"{name} (cached)";
            Status.Percent = to;
            return target;
        }

        // A copy already on disk saves a 7 MB download. Walk up from both the executable and the
        // working directory, since a dev build sits several levels below the project root where
        // these files usually live.
        if (!refresh)
        {
            foreach (var candidate in NearbyCopies(name))
            {
                File.Copy(candidate, target, overwrite: true);
                Status.Message = $"{name} (local copy)";
                Status.Percent = to;
                return target;
            }
        }

        Status.Message = $"downloading {name}";
        Status.Percent = from;

        byte[] data = await client.GetBytesAsync(name, ct);
        await File.WriteAllBytesAsync(target, data, ct);

        Status.Percent = to;
        return target;
    }

    /// <summary>
    /// Persists the freshly built catalog in the compact format, so later starts skip the network
    /// entirely and a maintainer can embed the result in the next release.
    /// </summary>
    private void SaveCompact(Catalog cat)
    {
        try
        {
            var dats = cat.Ordered.SelectMany(d => d.Dats);
            var blobs = cat.Ordered.SelectMany(d => d.Blobs);
            CompactIndex.Write(Path.Combine(settings.IndexDir, CompactFile), dats, blobs);
        }
        catch
        {
            // Only an optimisation; the text files remain the source of truth on disk.
        }
    }

    private static void WriteSizeCache(string path, Dictionary<(Kind, int, int, uint), long> sizes)
    {
        using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var w = new BinaryWriter(fs);
        w.Write(SizeCacheMagic);
        w.Write(sizes.Count);
        foreach (var ((kind, depot, version, crc), size) in sizes)
        {
            w.Write((byte)kind);
            w.Write(depot);
            w.Write(version);
            w.Write(crc);
            w.Write(size);
        }
    }

    private static Dictionary<(Kind, int, int, uint), long> ReadSizeCache(string path)
    {
        var result = new Dictionary<(Kind, int, int, uint), long>(120000);
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            using var r = new BinaryReader(fs);
            if (r.ReadInt32() != SizeCacheMagic) return result;

            int count = r.ReadInt32();
            for (int i = 0; i < count; i++)
            {
                var kind = (Kind)r.ReadByte();
                int depot = r.ReadInt32();
                int version = r.ReadInt32();
                uint crc = r.ReadUInt32();
                long size = r.ReadInt64();
                result[(kind, depot, version, crc)] = size;
            }
        }
        catch
        {
            result.Clear();
        }
        return result;
    }
}
