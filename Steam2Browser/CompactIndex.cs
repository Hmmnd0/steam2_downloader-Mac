using System.IO.Compression;
using System.Reflection;

namespace Steam2Browser;

/// <summary>
/// A single gzipped binary file holding the whole catalog: every file name, date and size.
///
/// It exists so a release can ship the index inside the executable. Fetching it from a mirror means
/// 13 MB of *_dates.txt plus two ~20 MB directory listings for the sizes — about 54 MB before the
/// app shows anything, which is painful on a slow link. Compacted and gzipped the same data is
/// roughly 4.5 MB, most of which is the 116 339 sha256 hashes that cannot compress.
///
/// Layout, little-endian, gzipped as a whole:
///   magic "S2IX", int formatVersion, long generatedUtcTicks, int count,
///   then per entry: byte kind, int depot, int version, uint crc, 32 raw sha bytes,
///   long dateTicks, long size (-1 when unknown).
/// </summary>
public static class CompactIndex
{
    private static readonly byte[] Magic = "S2IX"u8.ToArray();
    private const int FormatVersion = 1;
    private const string ResourceName = "Steam2Browser.index.bin";

    public sealed record Snapshot(DateTime GeneratedUtc, List<Entry> Dats, List<Entry> Blobs);

    // ---------------- writing ----------------

    public static void Write(string path, IEnumerable<Entry> dats, IEnumerable<Entry> blobs)
    {
        var all = dats.Concat(blobs).ToList();

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);
        using var gzip = new GZipStream(file, CompressionLevel.SmallestSize);
        using var w = new BinaryWriter(gzip);

        w.Write(Magic);
        w.Write(FormatVersion);
        w.Write(DateTime.UtcNow.Ticks);
        w.Write(all.Count);

        foreach (var e in all)
        {
            w.Write((byte)e.Kind);
            w.Write(e.Depot);
            w.Write(e.Version);
            w.Write(e.Crc);
            w.Write(Convert.FromHexString(e.Sha));
            w.Write(e.Date.Ticks);
            w.Write(e.ApproxSize);
        }
    }

    // ---------------- reading ----------------

    /// <summary>The copy embedded in this executable, or null when the build did not include one.</summary>
    public static Snapshot? FromEmbedded()
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null) return null;

        try { return Read(stream); }
        catch { return null; }
    }

    public static Snapshot? FromFile(string path)
    {
        if (!File.Exists(path)) return null;

        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            return Read(stream);
        }
        catch
        {
            return null;
        }
    }

    private static Snapshot Read(Stream raw)
    {
        using var gzip = new GZipStream(raw, CompressionMode.Decompress);
        using var r = new BinaryReader(gzip);

        var magic = r.ReadBytes(4);
        if (!magic.AsSpan().SequenceEqual(Magic)) throw new InvalidDataException("compact index: bad magic");

        int version = r.ReadInt32();
        if (version != FormatVersion) throw new InvalidDataException($"compact index: unsupported version {version}");

        var generated = new DateTime(r.ReadInt64(), DateTimeKind.Utc);
        int count = r.ReadInt32();
        if (count is < 0 or > 5_000_000) throw new InvalidDataException("compact index: implausible entry count");

        var dats = new List<Entry>(count / 2 + 1);
        var blobs = new List<Entry>(count / 2 + 1);
        var sha = new byte[32];

        for (int i = 0; i < count; i++)
        {
            var kind = (Kind)r.ReadByte();
            int depot = r.ReadInt32();
            int version2 = r.ReadInt32();
            uint crc = r.ReadUInt32();
            r.ReadExactly(sha);
            long ticks = r.ReadInt64();
            long size = r.ReadInt64();

            var e = new Entry
            {
                Kind = kind,
                Depot = depot,
                Version = version2,
                Crc = crc,
                Sha = Convert.ToHexStringLower(sha),
                Date = ticks == 0 ? default : new DateTime(ticks, DateTimeKind.Utc),
                ApproxSize = size,
            };

            if (kind == Kind.Dat) dats.Add(e); else blobs.Add(e);
        }

        return new Snapshot(generated, dats, blobs);
    }

    /// <summary>Turns a snapshot into a catalog, folding in the sizes it already carries.</summary>
    public static Catalog ToCatalog(Snapshot snap)
    {
        var cat = Catalog.Build(snap.Dats, snap.Blobs);

        // Sizes travelled with the entries, so only the per-depot totals need adding up.
        cat.RecomputeTotals();
        cat.SizesLoaded = cat.ApproxTotalBytes > 0;
        return cat;
    }
}
