using System.Globalization;

namespace Steam2Browser;

public enum Kind : byte { Dat = 0, Blob = 1 }

/// <summary>One file in the archive. Name layout: depot_version_crc_sha256.(dat|blob)</summary>
public sealed class Entry
{
    public int Depot;
    public int Version;
    public uint Crc;
    public string Sha = "";
    public DateTime Date;

    /// <summary>Size from the directory listing. nginx rounds it, so treat as approximate. -1 = unknown.</summary>
    public long ApproxSize = -1;

    public Kind Kind;

    public string Ext => Kind == Kind.Dat ? ".dat" : ".blob";
    public string DirName => Kind == Kind.Dat ? "dats" : "blobs";
    public string CrcHex => Crc.ToString("x8");
    public string FileName => $"{Depot}_{Version}_{CrcHex}_{Sha}{Ext}";
    public string RelPath => $"{DirName}/{FileName}";
}

public sealed class Depot
{
    public int Id;
    public List<Entry> Dats = new();
    public List<Entry> Blobs = new();

    public int MaxVersion;
    public int DistinctVersions;

    /// <summary>Versions that appear more than once — Valve reset the depot there, so the chain forks.</summary>
    public List<int> ForkedVersions = new();

    public List<int> MissingDats = new();
    public List<int> MissingBlobs = new();

    public long ApproxDatBytes;
    public long ApproxBlobBytes;
    public DateTime FirstDate;
    public DateTime LastDate;

    public bool HasReset => ForkedVersions.Count > 0;
    public bool IsComplete => MissingDats.Count == 0 && MissingBlobs.Count == 0;
}

public sealed class Catalog
{
    public Dictionary<int, Depot> Depots = new();
    public List<Depot> Ordered = new();
    public int DatCount, BlobCount;
    public long ApproxTotalBytes;
    public bool SizesLoaded;
    public DateTime BuiltUtc = DateTime.UtcNow;

    // ---------- name / date parsing ----------

    /// <summary>Parses "2009-09-12+00:28:36.0000000000". The fraction has 10 digits, more than DateTime holds.</summary>
    public static DateTime ParseDate(ReadOnlySpan<char> s)
    {
        int plus = s.IndexOf('+');
        if (plus < 0) return default;

        var date = s[..plus];
        var time = s[(plus + 1)..];

        int dot = time.IndexOf('.');
        if (dot >= 0)
        {
            int keep = Math.Min(7, time.Length - dot - 1);
            time = time[..(dot + 1 + keep)];
        }

        var whole = string.Concat(date, "T", time);
        return DateTime.TryParse(whole, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed)
            ? parsed
            : default;
    }

    /// <summary>depot_version_crc_sha.ext to Entry. Null when the name lacks the 4 expected parts.</summary>
    public static Entry? ParseName(ReadOnlySpan<char> name, Kind kind)
    {
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];

        Span<Range> parts = stackalloc Range[5];
        int n = name.Split(parts, '_');
        if (n != 4) return null;

        if (!int.TryParse(name[parts[0]], out int depot)) return null;
        if (!int.TryParse(name[parts[1]], out int version)) return null;

        var crcSpan = name[parts[2]];
        if (crcSpan.Length != 8) return null;
        if (!uint.TryParse(crcSpan, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint crc)) return null;

        var sha = name[parts[3]];
        if (sha.Length != 64) return null;

        return new Entry { Depot = depot, Version = version, Crc = crc, Sha = new string(sha), Kind = kind };
    }

    /// <summary>Reads a *_dates.txt file, one "filename &lt;tab&gt; date" per line.</summary>
    public static List<Entry> ParseDatesFile(string path, Kind kind)
    {
        var list = new List<Entry>(60000);
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.AsSpan().TrimEnd();
            if (line.IsEmpty) continue;

            int tab = line.IndexOf('\t');
            if (tab < 0) continue;

            var e = ParseName(line[..tab], kind);
            if (e is null) continue;

            e.Date = ParseDate(line[(tab + 1)..]);
            list.Add(e);
        }
        return list;
    }

    // ---------- directory listing (the only source of sizes) ----------

    private const string LinkTag = "<td class=\"link\"><a href=\"";
    private const string SizeTag = "<td class=\"size\">";

    /// <summary>Pulls (filename, approx bytes) out of the nginx autoindex HTML.</summary>
    public static IEnumerable<(string Name, long Size)> ParseListing(string html)
    {
        int pos = 0;
        while (true)
        {
            int a = html.IndexOf(LinkTag, pos, StringComparison.Ordinal);
            if (a < 0) yield break;
            a += LinkTag.Length;

            int b = html.IndexOf('"', a);
            if (b < 0) yield break;
            var name = html[a..b];

            int s = html.IndexOf(SizeTag, b, StringComparison.Ordinal);
            if (s < 0) yield break;
            s += SizeTag.Length;

            int e = html.IndexOf('<', s);
            if (e < 0) yield break;

            pos = e;
            if (name.EndsWith('/')) continue;
            yield return (Uri.UnescapeDataString(name), ParseHumanSize(html.AsSpan(s, e - s)));
        }
    }

    /// <summary>"5.5 MiB" to bytes. -1 when it is not a size (nginx prints "-" for directories).</summary>
    public static long ParseHumanSize(ReadOnlySpan<char> s)
    {
        s = s.Trim();

        int i = 0;
        while (i < s.Length && (char.IsAsciiDigit(s[i]) || s[i] == '.')) i++;
        if (i == 0) return -1;

        if (!double.TryParse(s[..i], NumberStyles.Float, CultureInfo.InvariantCulture, out double v)) return -1;

        double mul = s[i..].Trim() switch
        {
            "B" => 1d,
            "KiB" => 1024d,
            "MiB" => 1024d * 1024,
            "GiB" => 1024d * 1024 * 1024,
            "TiB" => 1024d * 1024 * 1024 * 1024,
            _ => -1d
        };
        if (mul < 0) return -1;

        return (long)(v * mul);
    }

    // ---------- assembly ----------

    public static Catalog Build(List<Entry> dats, List<Entry> blobs)
    {
        var cat = new Catalog { DatCount = dats.Count, BlobCount = blobs.Count };

        foreach (var e in dats) cat.GetOrAdd(e.Depot).Dats.Add(e);
        foreach (var e in blobs) cat.GetOrAdd(e.Depot).Blobs.Add(e);

        foreach (var d in cat.Depots.Values) Finish(d);

        cat.Ordered = cat.Depots.Values.OrderBy(x => x.Id).ToList();
        return cat;
    }

    private static void Finish(Depot d)
    {
        d.Dats.Sort(CompareEntry);
        d.Blobs.Sort(CompareEntry);

        var datVers = new Dictionary<int, int>();
        var blobVers = new Dictionary<int, int>();
        foreach (var e in d.Dats) datVers[e.Version] = datVers.GetValueOrDefault(e.Version) + 1;
        foreach (var e in d.Blobs) blobVers[e.Version] = blobVers.GetValueOrDefault(e.Version) + 1;

        var allVers = new SortedSet<int>(datVers.Keys);
        allVers.UnionWith(blobVers.Keys);

        d.DistinctVersions = allVers.Count;
        d.MaxVersion = allVers.Count > 0 ? allVers.Max : 0;

        foreach (int v in allVers)
        {
            if (datVers.GetValueOrDefault(v) > 1 || blobVers.GetValueOrDefault(v) > 1)
                d.ForkedVersions.Add(v);
            if (!datVers.ContainsKey(v)) d.MissingDats.Add(v);
            if (!blobVers.ContainsKey(v)) d.MissingBlobs.Add(v);
        }

        // Blob timestamps are the trustworthy ones. Every single dat in the archive sits on an
        // exact second (57898 of 57898), while 99.6% of blobs carry sub-second precision — the dat
        // times were stamped when the dump was assembled, not when the version was published.
        var first = DateTime.MaxValue;
        var last = DateTime.MinValue;

        var dated = d.Blobs.Where(e => e.Date != default).ToList();
        if (dated.Count == 0) dated = d.Dats.Where(e => e.Date != default).ToList();

        foreach (var e in dated)
        {
            if (e.Date < first) first = e.Date;
            if (e.Date > last) last = e.Date;
        }
        d.FirstDate = first == DateTime.MaxValue ? default : first;
        d.LastDate = last == DateTime.MinValue ? default : last;
    }

    private static int CompareEntry(Entry a, Entry b)
    {
        int c = a.Version.CompareTo(b.Version);
        return c != 0 ? c : a.Date.CompareTo(b.Date);
    }

    private Depot GetOrAdd(int id)
    {
        if (!Depots.TryGetValue(id, out var d))
        {
            d = new Depot { Id = id };
            Depots[id] = d;
        }
        return d;
    }

    /// <summary>Adds up per-depot totals from sizes the entries already carry.</summary>
    public void RecomputeTotals()
    {
        ApproxTotalBytes = 0;

        foreach (var d in Depots.Values)
        {
            d.ApproxDatBytes = 0;
            d.ApproxBlobBytes = 0;

            foreach (var e in d.Dats)
                if (e.ApproxSize > 0) d.ApproxDatBytes += e.ApproxSize;
            foreach (var e in d.Blobs)
                if (e.ApproxSize > 0) d.ApproxBlobBytes += e.ApproxSize;

            ApproxTotalBytes += d.ApproxDatBytes + d.ApproxBlobBytes;
        }
    }

    /// <summary>Folds listing sizes into the built entries and recomputes per-depot totals.</summary>
    public void ApplySizes(Dictionary<(Kind, int, int, uint), long> sizes)
    {
        ApproxTotalBytes = 0;

        foreach (var d in Depots.Values)
        {
            d.ApproxDatBytes = 0;
            d.ApproxBlobBytes = 0;

            foreach (var e in d.Dats)
            {
                if (sizes.TryGetValue((e.Kind, e.Depot, e.Version, e.Crc), out long s)) e.ApproxSize = s;
                if (e.ApproxSize > 0) d.ApproxDatBytes += e.ApproxSize;
            }
            foreach (var e in d.Blobs)
            {
                if (sizes.TryGetValue((e.Kind, e.Depot, e.Version, e.Crc), out long s)) e.ApproxSize = s;
                if (e.ApproxSize > 0) d.ApproxBlobBytes += e.ApproxSize;
            }

            ApproxTotalBytes += d.ApproxDatBytes + d.ApproxBlobBytes;
        }

        SizesLoaded = true;
    }
}
