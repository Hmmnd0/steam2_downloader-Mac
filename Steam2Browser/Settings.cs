using System.Text.Json;
using System.Text.Json.Serialization;

namespace Steam2Browser;

public sealed class Settings
{
    /// <summary>Root for downloads; blobs land in DataDir/blobs and dats in DataDir/dats, the layout extract.exe expects.</summary>
    public string DataDir { get; set; } = "";

    /// <summary>Where the index files (dats_dates.txt, blobs_dates.txt) and the size cache live.</summary>
    public string IndexDir { get; set; } = "";

    public string MirrorId { get; set; } = "de";
    public bool Failover { get; set; } = true;

    public int Concurrency { get; set; } = 8;

    /// <summary>
    /// Download in two phases — every blob first, then every dat — each with its own stream count.
    /// Blobs are kilobytes, so many at once costs nothing; dats are large and the mirrors ramp a
    /// connection up over time, so only a couple of sustained streams are used for them.
    /// Turn off to fall back to <see cref="Concurrency"/> parallel files and ranged segments.
    /// </summary>
    public bool PhasedDownloads { get; set; } = true;

    /// <summary>Streams used during the blob phase. They are tiny, so latency dominates.</summary>
    public int BlobConcurrency { get; set; } = 32;

    /// <summary>
    /// Streams used during the dat phase. Kept low on purpose: these mirrors start a connection
    /// slow and speed it up while it keeps asking, so many parallel streams all sit at the cold rate.
    /// </summary>
    public int DatConcurrency { get; set; } = 8;

    /// <summary>
    /// How many dats ahead to touch with a one-byte request when a download starts, so the mirror
    /// has the next files ready. 0 disables it. Fire-and-forget, so it cannot slow anything down.
    /// </summary>
    public int WarmupLookahead { get; set; } = 2;

    /// <summary>
    /// Dats at least this large are fetched one at a time, after the smaller ones. Two concurrent
    /// long sequential reads make disk-backed storage seek between them, which costs more than the
    /// parallelism gains; short reads never get far enough for that to matter. 0 disables the split.
    /// </summary>
    public long BigFileBytes { get; set; } = 0L;

    /// <summary>
    /// Master switch for everything BitTorrent: downloading from the swarm, sharing back to it, and
    /// the engine itself.
    ///
    /// Off by default. Loading the metadata from steam2.torrent gets as far as reading all 116 346
    /// files and then leaves the manager at "Stopped" — the cause is not yet understood, and an
    /// engine that half-starts is not something to leave running in the background of everyone's
    /// downloads. Nothing else in the app depends on it: the HTTP mirrors do the work.
    /// </summary>
    public bool TorrentEnabled { get; set; }

    /// <summary>
    /// Share what has already been downloaded back to the swarm. Requires
    /// <see cref="TorrentEnabled"/>, since sharing is the same engine.
    ///
    /// The archive survives because people seed it. Taking 13 TB out of a swarm of three seeders
    /// and giving nothing back is how it stops surviving, so this defaults to on — but it is a
    /// setting, because upload is the user's bandwidth to spend.
    /// </summary>
    public bool SeedDownloaded { get; set; } = true;

    /// <summary>
    /// Byte length of the last successful dats/ and blobs/ listing fetch, so the next one can show
    /// a real percentage. nginx builds these listings on the fly and sends them chunked with no
    /// Content-Length, so the size of the previous fetch is the only total available. The seeded
    /// figures are a measured estimate and are replaced by exact values after the first run.
    /// </summary>
    public long DatListingBytes { get; set; } = 21_000_000L;

    public long BlobListingBytes { get; set; } = 21_000_000L;
    public bool VerifyHashes { get; set; } = true;
    public int TorrentPort { get; set; }

    public string ExtractOutDir { get; set; } = "";

    /// <summary>
    /// Trackers to announce to on top of the ones inside the magnet. Useful when those are
    /// unreachable — the magnet's three all resolve to one address that some networks block
    /// outright. Null means "use the defaults"; an empty array means "none, rely on DHT".
    /// </summary>
    public string[]? ExtraTrackers { get; set; }

    /// <summary>
    /// Trackers to announce to, beyond whatever the torrent source already carries.
    ///
    /// The first group answered when the archive infohash was announced to them on
    /// 2026-08-30; seeder counts were observed at that moment and several reported three.
    /// The rest come from the announce-list inside steam2.torrent itself, which is the
    /// authoritative set for this swarm and much wider than anything worth curating by hand.
    ///
    /// The list is long deliberately. The two trackers named in the magnet both time out on
    /// some networks, which is exactly why the swarm can look empty while it is not, and an
    /// announce costs a couple of UDP packets — so breadth is close to free.
    /// </summary>
    public static readonly string[] DefaultExtraTrackers =
    [
        // Verified reachable from here.
        "udp://open.stealth.si:80/announce",
        "udp://tracker.torrent.eu.org:451/announce",
        "udp://tracker.auctor.tv:6969/announce",
        "udp://tracker.qu.ax:6969/announce",
        "udp://retracker01-msk-virt.corbina.net:80/announce",
        "udp://tracker.bittor.pw:1337/announce",
        "udp://open.demonii.com:1337/announce",
        "udp://tracker.dler.org:6969/announce",
        "udp://tracker.cyberia.is:6969/announce",
        "udp://explodie.org:6969/announce",
        "udp://tracker.sbsub.com:2710/announce",
        "udp://tracker.0x7c0.com:6969/announce",
        "udp://martin-gebhardt.eu:25/announce",
        "udp://evan.im:6969/announce",

        // From the torrent file.
        "dp://zer0day.ch:1337/announce",
        "udp://tracker.publictracker.xyz:6969/announce",
        "udp://tracker.opentrackr.org:1337/announce",
        "http://tracker.opentrackr.org:1337/announce",
        "udp://tracker2.dler.org:80/announce",
        "udp://tracker.wildkat.net:6969/announce",
        "udp://tracker.ducks.party:1984/announce",
        "udp://tracker.corpscorp.online:80/announce",
        "udp://tracker-udp.gbitt.info:80/announce",
        "udp://tr4ck3r.duckdns.org:6969/announce",
        "udp://torrentclub.online:54123/announce",
        "udp://torrentclub.online:1984/announce",
        "udp://t.overflow.biz:6969/announce",
        "udp://seedpeer.net:6969/announce",
        "udp://rekcart.duckdns.org:15480/announce",
        "udp://ns575949.ip-51-222-82.net:6969/announce",
        "udp://ipv4announce.sktorrent.eu:6969/announce",
        "udp://exodus.desync.com:6969/announce",
        "udp://bittorrent-tracker.e-n-c-r-y-p-t.net:1337/announce",
        "https://tracker.zhuqiy.com:443/announce",
        "https://tracker.pmman.tech:443/announce",
        "https://tracker.nekomi.cn:443/announce",
        "https://tracker.leechshield.link:443/announce",
        "https://tracker.gcrenwp.top:443/announce",
        "https://tracker.bt4g.com:443/announce",
        "https://tracker.7471.top:443/announce",
        "https://tr.zukizuki.org:443/announce",
        "https://tr.nyacat.pw:443/announce",
        "https://pybittrack.retiolus.net:443/announce",
        "https://open.ftorrent.com:443/announce",
        "https://004430.xyz:443/announce",
        "http://www.torrentsnipe.info:2701/announce",
        "http://tracker.zhuqiy.dgj055.icu:80/announce",
        "http://tracker.zhuqiy.com:80/announce",
        "http://tracker.waaa.moe:6969/announce",
        "http://tracker.sbsub.com:2710/announce",
        "http://tracker.renfei.net:8080/announce",
        "http://tracker.qu.ax:6969/announce",
        "http://tracker.privateseedbox.xyz:2710/announce",
        "http://tracker.mywaifu.best:6969/announce",
        "http://tracker.ipv6tracker.org:80/announce",
        "http://tracker.dler.org:6969/announce",
        "http://tracker.dler.com:6969/announce",
        "http://tracker.dhitechnical.com:6969/announce",
        "http://tracker.bt4g.com:2095/announce",
        "http://tracker.bt-hash.com:80/announce",
        "http://tr.nyacat.pw:80/announce",
        "http://torrent.hificode.in:6969/announce",
        "http://t.overflow.biz:6969/announce",
        "http://share.hkg-fansub.info:80/announce.php",
        "http://retracker.spark-rostov.ru:80/announce",
        "http://ipv4announce.sktorrent.eu:6969/announce",
        "http://home.yxgz.club:6969/announce",
        "http://bittorrent-tracker.e-n-c-r-y-p-t.net:1337/announce",
        "http://aboutbeautifulgallopinghorsesinthegreenpasture.online:80/announce",
        "http://004430.xyz:80/announce",
        "udp://yuptracker-eu.gaijinent.com:27022/announce",
        "udp://tracker.teambelgium.net:6969/announce",
        "udp://tracker.playground.ru:6969/announce",
        "udp://tracker.peerfect.org:6969/announce",
        "udp://tracker.opentrackr.com:6969/announce",
        "udp://tracker.nexusstream.eu:6969/announce",
        "udp://tracker.ilibr.org:6969/announce",
        "udp://tracker.gmi.gd:6969/announce",
        "udp://tracker.farted.net:6969/announce",
        "udp://tracker.ddunlimited.net:6969/announce",
        "udp://tracker.aruku.ovh:8081/announce",
        "udp://open.ftorrent.com:443/announce",
        "udp://open.demonoid.ch:6969/announce",
        "udp://mail.segso.net:6969/announce",
        "udp://leet-tracker.moe:1337/announce",
        "udp://admin.52ywp.com:6969/announce",
        "https://t.213891.xyz:443/announce",
        "http://tracker2.dler.org:80/announce",
        "http://tracker.nexusstream.eu:6969/announce",
        "http://1337.abcvg.info:80/announce",
    ];

    public string[] TrackersToUse => ExtraTrackers ?? DefaultExtraTrackers;

    [JsonIgnore] public string ConfigPath { get; set; } = "";

    /// <summary>Everything the app writes lives in one folder named this.</summary>
    public const string RootFolder = "steam2info";

    /// <summary>
    /// On macOS this cannot be beside the executable: a freshly downloaded, still-quarantined
    /// .app is run by Gatekeeper from a randomized, read-only location (App Translocation) unless
    /// the user has already moved it out of its download location, so writing there crashes on
    /// first launch. ~/Library/Application Support is always writable and stable regardless of
    /// where the .app itself lives. Windows and Linux keep the simpler beside-the-executable
    /// layout, which is portable (an install can be moved as one folder) and doesn't have an
    /// equivalent translocation mechanism.
    /// </summary>
    public static string RootFor(string baseDir) => OperatingSystem.IsMacOS()
        ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            "Library", "Application Support", RootFolder)
        : Path.Combine(baseDir, RootFolder);

    private static readonly JsonSerializerOptions JsonOpts = new() { WriteIndented = true };

    public static Settings Load(string baseDir)
    {
        string root = RootFor(baseDir);
        Directory.CreateDirectory(root);

        // Installs from before the move kept their files loose beside the exe; bring them along.
        Migrate(baseDir, root, "settings.json");
        Migrate(baseDir, root, "names.jsonl");

        string path = Path.Combine(root, "settings.json");
        Settings s;
        try
        {
            s = File.Exists(path)
                ? JsonSerializer.Deserialize<Settings>(File.ReadAllText(path)) ?? new Settings()
                : new Settings();
        }
        catch
        {
            s = new Settings();
        }

        s.ConfigPath = path;
        if (string.IsNullOrWhiteSpace(s.DataDir)) s.DataDir = Path.Combine(root, "archive");
        if (string.IsNullOrWhiteSpace(s.IndexDir)) s.IndexDir = Path.Combine(root, "index");
        if (string.IsNullOrWhiteSpace(s.ExtractOutDir)) s.ExtractOutDir = Path.Combine(root, "extracted");
        if (s.Concurrency is < 1 or > 64) s.Concurrency = 8;
        if (s.BlobConcurrency is < 1 or > 128) s.BlobConcurrency = 32;
        if (s.DatConcurrency is < 1 or > 64) s.DatConcurrency = 8;
        if (s.WarmupLookahead is < 0 or > 16) s.WarmupLookahead = 2;
        if (s.BigFileBytes < 0) s.BigFileBytes = 0L;

        // Two settings shipped badly wrong and throttled every download to roughly a twelfth of
        // what the mirrors give. Measured: one connection sustains ~0.25 MB/s and eight sustain
        // ~5 MB/s, so a dat concurrency of 2 — and a rule that sent anything over 30 MB down a
        // single connection on its own — cost users hours.
        //
        // Only values left at those exact defaults are moved. Someone who deliberately typed 2
        // keeps 2; the point is to rescue the settings nobody chose, not to overrule a choice.
        if (s.DatConcurrency == 2) s.DatConcurrency = 8;
        if (s.BigFileBytes is 30_000_000L or 31_457_280L) s.BigFileBytes = 0L;
        if (s.TorrentPort is < 0 or > 65535) s.TorrentPort = 0;
        return s;
    }

    private static void Migrate(string baseDir, string root, string name)
    {
        try
        {
            string old = Path.Combine(baseDir, name);
            string moved = Path.Combine(root, name);
            if (File.Exists(old) && !File.Exists(moved)) File.Move(old, moved);
        }
        catch
        {
            // Starting fresh is an acceptable outcome here.
        }
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(ConfigPath)!);
            File.WriteAllText(ConfigPath, JsonSerializer.Serialize(this, JsonOpts));
        }
        catch
        {
            // A read-only install directory should not take the app down.
        }
    }
}
