namespace Steam2Browser;

public sealed class LoadStatus
{
    public string Phase = "idle";      // idle | index | parse | sizes | ready | error
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
    private const int SizeCacheMagic = 0x32534253; // "SBS2"

    public async Task LoadAsync(bool refreshIndex, bool withSizes, CancellationToken ct = default)
    {
        try
        {
            Directory.CreateDirectory(settings.IndexDir);

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
            Status.Phase = "idle";
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

            Status.Phase = "sizes";
            Status.Message = "fetching dats/ listing (~20 MB)";
            Status.Percent = 0;
            await CollectAsync("dats/", Kind.Dat, sizes, ct);

            Status.Message = "fetching blobs/ listing (~20 MB)";
            Status.Percent = 50;
            await CollectAsync("blobs/", Kind.Blob, sizes, ct);

            cat.ApplySizes(sizes);
            await Task.Run(() => WriteSizeCache(cachePath, sizes), ct);
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

    private void Done(Catalog cat)
    {
        Status.Phase = "ready";
        Status.Percent = 100;
        Status.Message = $"{cat.Ordered.Count} depots · {Fmt(cat.ApproxTotalBytes)} total";
    }

    private static string Fmt(long b)
    {
        string[] u = ["B", "KiB", "MiB", "GiB", "TiB", "PiB"];
        double v = b;
        int i = 0;
        while (v >= 1024 && i < u.Length - 1) { v /= 1024; i++; }
        return $"{v:0.##} {u[i]}";
    }

    private async Task CollectAsync(
        string dir, Kind kind, Dictionary<(Kind, int, int, uint), long> sizes, CancellationToken ct)
    {
        string html = await client.GetStringAsync(dir, ct);
        foreach (var (name, size) in Catalog.ParseListing(html))
        {
            if (size < 0) continue;
            var e = Catalog.ParseName(name, kind);
            if (e is null) continue;
            sizes[(kind, e.Depot, e.Version, e.Crc)] = size;
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

        // A copy sitting next to the executable or in the working directory saves a download.
        if (!refresh)
        {
            foreach (var dir in new[] { AppContext.BaseDirectory, Directory.GetCurrentDirectory() })
            {
                string candidate = Path.Combine(dir, name);
                if (File.Exists(candidate) && new FileInfo(candidate).Length > 0)
                {
                    File.Copy(candidate, target, overwrite: true);
                    Status.Message = $"{name} (local copy)";
                    Status.Percent = to;
                    return target;
                }
            }
        }

        Status.Message = $"downloading {name}";
        Status.Percent = from;

        byte[] data = await client.GetBytesAsync(name, ct);
        await File.WriteAllBytesAsync(target, data, ct);

        Status.Percent = to;
        return target;
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
