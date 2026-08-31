using MonoTorrent;
using MonoTorrent.Client;
using System.Net;

namespace Steam2Browser;

public sealed class TorrentStatus
{
    /// <summary>off | starting | metadata | ready | downloading | error</summary>
    public string State = "off";

    /// <summary>Seeding runs on its own manager, so it reports its own state.</summary>
    public string SeedState = "off";

    public string SeedMessage = "";
    public int SeedFiles;
    public long SeedBytes;
    public double SeedUploadRate;
    public int SeedPeers;
    public long SeedUploaded;

    public string Message = "";
    public string? Error;

    public bool HasMetadata;
    public int TotalFiles;
    public int SelectedFiles;
    public long SelectedBytes;

    public int Trackers;
    public int Peers;
    public int Seeds;
    public double DownloadRate;
    public double UploadRate;
    public double SelectedProgress;
    public string TorrentState = "";
}

/// <summary>
/// The archive as a BitTorrent swarm, used as a fourth source alongside the three HTTP mirrors.
///
/// The torrent holds all 116 339 files — 13.32 TB, matching the archive exactly — so it is only
/// usable because BitTorrent can fetch selected files: the piece picker is handed the files a chain
/// actually needs and never asks the swarm for anything else.
///
/// Metadata is fetched from the swarm once (the magnet carries no file list) and cached on disk,
/// so later runs skip that wait.
/// </summary>
public sealed class TorrentSource(Settings settings)
{
    /// <summary>
    /// The published magnet for the archive. Its first tracker is spelled "dp://", which is not a
    /// scheme MonoTorrent (or anything else) understands, so trackers are filtered before parsing.
    /// </summary>
    public const string Magnet =
        "magnet:?xt=urn:btih:0f3e7a75c0f885dde481054d4bcd8cd14eab51c8&dn=steam2" +
        "&xl=13316620144984" +
        "&tr=udp%3A%2F%2Ftracker.publictracker.xyz%3A6969%2Fannounce" +
        "&tr=udp%3A%2F%2Ftracker.opentrackr.org%3A1337%2Fannounce";

    /// <summary>
    /// How long to wait for a peer to hand over the file list before giving up and saying so.
    /// Reaching it usually means the trackers are unreachable — all three in this magnet time out
    /// on some networks — and DHT alone found nobody.
    /// </summary>
    private static readonly TimeSpan MetadataTimeout = TimeSpan.FromMinutes(5);


    public TorrentStatus Status { get; } = new();

    private ClientEngine? _engine;
    private TorrentManager? _manager;

    /// <summary>Archive-relative path ("dats/x.dat") to the file inside the torrent.</summary>
    private readonly Dictionary<string, ITorrentManagerFile> _byArchivePath =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly SemaphoreSlim _seedGate = new(1, 1);

    private TorrentManager? _seedManager;

    /// <summary>
    /// What keeps the download to the files a chain asked for. It replaces the engine's own picker
    /// on the downloading manager and is the only thing standing between a start and all 13.32 TB,
    /// so it goes on before the manager is ever started.
    /// </summary>
    private SelectionPieceRequester? _requester;

    /// <summary>
    /// The files the running download selected, kept for the progress readings: the manager's own
    /// PartialProgress counts everything above DoNotDownload, which is now every file.
    /// </summary>
    private IReadOnlyList<ITorrentManagerFile> _selection = Array.Empty<ITorrentManagerFile>();

    /// <summary>
    /// Ready means the file list is known and the selection picker is on: without the picker a
    /// download would select files the manager knows nothing about and start on all 13.32 TB.
    /// </summary>
    public bool Ready => _manager is { HasMetadata: true } && _requester is not null;

    // ---------------- startup ----------------

    /// <summary>
    /// Brings the engine up and waits for the file list. Safe to call repeatedly; only the first
    /// call does the work.
    /// </summary>
    /// <summary>
    /// Shares the archive files already on disk back to the swarm.
    ///
    /// This runs on its own manager rooted at the archive folder, which is the one thing the
    /// downloading manager is deliberately kept away from: pointed there, it allocates a file for
    /// everything it might want and once left 35 166 empty placeholders that looked like completed
    /// downloads. The protection here is that every file is parked at DoNotDownload before the
    /// manager is ever started, and only files that already exist on disk are lifted off it — so
    /// there is nothing it could decide to create.
    ///
    /// Nothing is downloaded by this manager. It hash-checks what is there and serves it.
    /// </summary>
    public async Task StartSeedingAsync(CancellationToken ct = default)
    {
        if (!settings.TorrentEnabled)
        {
            Status.SeedState = "off";
            Status.SeedMessage = "the torrent engine is switched off";
            return;
        }

        if (_seedManager is not null) return;

        await _seedGate.WaitAsync(ct);
        try
        {
            if (_seedManager is not null) return;

            Status.SeedState = "starting";
            Status.SeedMessage = "reading the file list";

            // Sharing needs the metadata, and the download side is what fetches and caches it.
            if (!await EnsureStartedAsync(ct) || _manager is null)
            {
                Status.SeedState = "error";
                Status.SeedMessage = "could not get the torrent file list";
                return;
            }

            string archive = settings.DataDir;
            Directory.CreateDirectory(archive);

            var torrentSettings = new TorrentSettingsBuilder
            {
                CreateContainingDirectory = false,
                AllowDht = true,
                AllowPeerExchange = true,
                // Nothing here should ever ask the swarm for data.
                UploadSlots = 8,
            }.ToSettings();

            var manager = await _engine!.AddAsync(_manager.Torrent!, archive, torrentSettings);

            // Park everything first, before a single byte can be allocated.
            foreach (var file in manager.Files)
                if (file.Priority != Priority.DoNotDownload)
                    await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);

            // Then lift only what is genuinely on disk.
            int have = 0;
            long bytes = 0;

            foreach (var file in manager.Files)
            {
                var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2) continue;

                string name = parts[^1];
                string folder = parts[^2].ToLowerInvariant();
                if (folder is not ("dats" or "blobs")) continue;

                string onDisk = Path.Combine(archive, folder, name);
                if (!File.Exists(onDisk)) continue;

                // A part-written file is not ours to serve.
                if (new FileInfo(onDisk).Length != file.Length) continue;

                await manager.SetFilePriorityAsync(file, Priority.Normal);
                have++;
                bytes += file.Length;
            }

            Status.SeedFiles = have;
            Status.SeedBytes = bytes;

            if (have == 0)
            {
                Status.SeedState = "idle";
                Status.SeedMessage = "nothing downloaded yet to share";
                await _engine.RemoveAsync(manager);
                return;
            }

            Status.SeedMessage = $"checking {have} file(s) before sharing them";
            await manager.HashCheckAsync(autoStart: true);

            _seedManager = manager;
            Status.SeedState = "sharing";
            Status.SeedMessage = $"sharing {have} file(s)";
        }
        catch (Exception ex)
        {
            Status.SeedState = "error";
            Status.SeedMessage = ex.Message;
        }
        finally
        {
            _seedGate.Release();
        }
    }

    public async Task StopSeedingAsync()
    {
        // Status is cleared first and unconditionally. There may be no manager yet — sharing spends
        // its first stretch inside EnsureStartedAsync — and returning early there used to leave the
        // display insisting it was still starting long after it had been switched off.
        var manager = _seedManager;
        _seedManager = null;

        Status.SeedState = "off";
        Status.SeedMessage = "";
        Status.SeedFiles = 0;
        Status.SeedBytes = 0;
        Status.SeedUploadRate = 0;
        Status.SeedPeers = 0;

        if (manager is null) return;

        try { await manager.StopAsync(); } catch { /* going away regardless */ }
    }

    public void SampleSeed()
    {
        var m = _seedManager;
        if (m is null) return;

        Status.SeedUploadRate = m.Monitor.UploadRate;
        Status.SeedUploaded = m.Monitor.DataBytesSent;
        Status.SeedPeers = m.Peers.Seeds + m.Peers.Leechs;
    }

    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
        // The single place the engine can come up, so the switch belongs here rather than at each
        // call site where one could be missed.
        if (!settings.TorrentEnabled)
        {
            Status.State = "off";
            Status.Message = "the torrent engine is switched off in Settings";
            return false;
        }

        if (Ready) return true;

        await _gate.WaitAsync(ct);
        try
        {
            if (Ready) return true;

            Status.State = "starting";
            Status.Error = null;
            Status.Message = "starting the torrent engine";

            string cacheDir = Path.Combine(settings.IndexDir, "torrent");
            Directory.CreateDirectory(cacheDir);

            if (_engine is null)
            {
                var builder = new EngineSettingsBuilder
                {
                    CacheDirectory = cacheDir,
                    AllowPortForwarding = true,
                    AllowLocalPeerDiscovery = true,

                    // The file list is several megabytes; caching it turns later starts instant.
                    AutoSaveLoadMagnetLinkMetadata = true,
                    AutoSaveLoadFastResume = true,
                    AutoSaveLoadDhtCache = true,
                    MaximumConnections = 200,
                };

                if (settings.TorrentPort > 0)
                {
                    builder.ListenEndPoints = new()
                    {
                        ["ipv4"] = new IPEndPoint(IPAddress.Any, settings.TorrentPort),
                        ["ipv6"] = new IPEndPoint(IPAddress.IPv6Any, settings.TorrentPort),
                    };
                    builder.DhtEndPoint = new IPEndPoint(IPAddress.Any, settings.TorrentPort);
                }

                _engine = new ClientEngine(builder.ToSettings());
            }

            if (_manager is null)
            {
                string? torrentPath = FindTorrentFile();

                // The engine gets its own directory rather than the archive folder. It allocates
                // files for whatever is selected, and the archive must only ever contain files this
                // app has verified — mixing the two made 35 166 empty placeholders look like
                // completed downloads.
                string dataDir = Path.Combine(settings.IndexDir, "torrent-data");
                Directory.CreateDirectory(dataDir);

                var torrentSettings = new TorrentSettingsBuilder
                {
                    CreateContainingDirectory = false,
                    AllowDht = true,
                    AllowPeerExchange = true,
                }.ToSettings();

                if (torrentPath is not null)
                {
                    // Straight from the file: no metadata round trip, and its 88 trackers come with
                    // it. The infohash is the torrent's own, so it joins exactly the same swarm.
                    var loaded = await Torrent.LoadAsync(torrentPath);
                    _manager = await _engine.AddAsync(loaded, dataDir, torrentSettings);
                    Status.Message = $"file list read from {Path.GetFileName(torrentPath)}";

                    // The file list came with the file, so the picker can be in place before the
                    // manager has ever run and there is no window in which it could ask for
                    // anything. The magnet path has to wait for metadata to know the files at all.
                    await AttachRequesterAsync();
                }
                else
                {
                    _manager = await _engine.AddAsync(BuildLink(), dataDir, torrentSettings);
                }

                // Deliberately not awaited. Adding ninety trackers one at a time means ninety DNS
                // lookups and announces, most of them to hosts that will never answer, and doing
                // that before the manager starts held the whole engine at "starting" indefinitely.
                // Extra trackers are an improvement to reach for, never a precondition.
                var manager = _manager;
                _ = Task.Run(async () =>
                {
                    foreach (var url in settings.TrackersToUse)
                    {
                        try { await manager.TrackerManager.AddTrackerAsync(new Uri(url)); }
                        catch { /* one unusable url is not worth the rest of the list */ }
                    }
                });

                _manager.PeersFound += (_, _) => Sample();
                _manager.TorrentStateChanged += (_, _) => Sample();
            }

            await _manager.StartAsync();

            if (!_manager.HasMetadata)
            {
                Status.State = "metadata";
                Status.Message = "asking the swarm for the file list — this can take a few minutes";

                // Without a deadline this waits forever when no peer ever answers, and the only
                // symptom the user sees is a spinner. Bounded, it can say what actually happened.
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(MetadataTimeout);

                try
                {
                    await _manager.WaitForMetadataAsync(deadline.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Status.State = "error";
                    Status.Error =
                        $"no peer supplied the file list within {MetadataTimeout.TotalMinutes:0} minutes " +
                        $"({_manager.OpenConnections} connections, {_manager.Peers.Available} peers known). " +
                        "The trackers in the magnet may be down or blocked; an HTTP mirror still works.";
                    Status.Message = Status.Error;
                    return false;
                }
            }

            // Stop before doing anything else. A running manager with everything at default
            // priority downloads the entire 13.32 TB — it had already pulled 38 GB before this
            // was caught. Nothing may transfer until a chain explicitly selects files.
            await _manager.StopAsync(TimeSpan.FromSeconds(10));

            MapFiles();

            // Only reached without a picker on the magnet path, where the file list did not exist
            // until now.
            if (_requester is null) await AttachRequesterAsync();

            Status.HasMetadata = true;
            Status.State = "ready";
            Status.Message = $"{Status.TotalFiles} files in the torrent, none selected";
            return true;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            Status.State = "off";
            Status.Message = "cancelled";
            return false;
        }
        catch (Exception ex)
        {
            Status.State = "error";
            Status.Error = ex.Message;
            Status.Message = ex.Message;
            return false;
        }
        finally
        {
            _gate.Release();
        }
    }

    /// <summary>
    /// The published magnet plus any extra trackers from settings. The magnet's own three all
    /// resolve to a single address, so on a network that blocks it there is nothing to announce to
    /// and only DHT is left; extra trackers give the swarm another way in.
    /// </summary>
    /// <summary>
    /// The metadata file, if it is anywhere we can see it.
    ///
    /// Fetching 30 MB of file list from a swarm with three seeders takes minutes and often never
    /// finishes at all, which left sharing stuck at "reading the file list". Having the file on
    /// disk removes that entirely: the torrent is known the moment the app starts, and its own
    /// announce-list carries far more trackers than any list kept by hand.
    /// </summary>
    private string? FindTorrentFile()
    {
        string exeDir = AppContext.BaseDirectory;

        var candidates = new List<string>
        {
            Path.Combine(exeDir, TorrentFileName),
            Path.Combine(settings.IndexDir, TorrentFileName),
            Path.Combine(Directory.GetCurrentDirectory(), TorrentFileName),
        };

        // Running from a build output during development, the repository root is several levels up.
        var dir = new DirectoryInfo(exeDir);
        for (int i = 0; i < 8 && dir is not null; i++, dir = dir.Parent)
            candidates.Add(Path.Combine(dir.FullName, TorrentFileName));

        return candidates.FirstOrDefault(File.Exists);
    }

    public const string TorrentFileName = "steam2.torrent";

    private MagnetLink BuildLink()
    {
        var baseLink = MagnetLink.Parse(Magnet);

        var announce = new List<string>(baseLink.AnnounceUrls);
        foreach (var extra in settings.TrackersToUse)
        {
            var url = extra?.Trim();
            if (string.IsNullOrEmpty(url)) continue;
            if (!announce.Contains(url, StringComparer.OrdinalIgnoreCase)) announce.Add(url);
        }

        Status.Trackers = announce.Count;

        return new MagnetLink(
            baseLink.InfoHashes,
            baseLink.Name,
            announce,
            baseLink.Webseeds,
            baseLink.Size);
    }

    /// <summary>
    /// Indexes the torrent by archive-relative path, which is how a chain names the files it is
    /// after.
    /// </summary>
    private void MapFiles()
    {
        var manager = _manager!;
        _byArchivePath.Clear();

        foreach (var file in manager.Files)
        {
            // Torrent paths may or may not carry a leading "steam2/" container; the tail is what
            // matters, and only dats/ and blobs/ entries are of interest.
            var parts = file.Path.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) continue;

            string folder = parts[^2];
            string name = parts[^1];
            if (!folder.Equals("dats", StringComparison.OrdinalIgnoreCase) &&
                !folder.Equals("blobs", StringComparison.OrdinalIgnoreCase)) continue;

            _byArchivePath[$"{folder.ToLowerInvariant()}/{name}"] = file;
        }

        Status.TotalFiles = _byArchivePath.Count;
    }

    /// <summary>
    /// Hands the manager the selection picker, selecting nothing. Can only be done once the file
    /// list is known and while the manager is stopped, which MonoTorrent enforces.
    /// </summary>
    private async Task AttachRequesterAsync()
    {
        _requester = new SelectionPieceRequester(_manager!.Files);
        await _manager.ChangePickerAsync(_requester);
    }

    // ---------------- downloading ----------------

    /// <summary>
    /// Fetches exactly the given files from the swarm. Returns the entries it could not find in the
    /// torrent, which the caller should fall back to HTTP for.
    /// </summary>
    public async Task<List<Entry>> DownloadAsync(
        IReadOnlyList<Entry> wanted,
        Action<long, long, double>? onProgress,
        CancellationToken ct)
    {
        if (!await EnsureStartedAsync(ct))
            throw new InvalidOperationException(Status.Error ?? "the torrent source is not available");

        var manager = _manager!;
        var requester = _requester!;
        var missing = new List<Entry>();
        var selected = new List<(Entry Entry, ITorrentManagerFile File)>();

        foreach (var entry in wanted)
        {
            if (_byArchivePath.TryGetValue(entry.RelPath, out var file))
                selected.Add((entry, file));
            else
                missing.Add(entry);
        }

        Status.SelectedFiles = selected.Count;
        Status.SelectedBytes = selected.Sum(x => x.File.Length);

        if (selected.Count == 0) return missing;

        _selection = selected.Select(x => x.File).ToList();
        requester.Select(_selection);

        Status.State = "downloading";
        Status.Message = $"{selected.Count} files selected from the swarm";

        try
        {
            await manager.StartAsync();

            // Every piece of every selected file, which is all the picker will ever ask for.
            while (!SelectionComplete())
            {
                ct.ThrowIfCancellationRequested();
                Sample();

                long done = (long)(Status.SelectedBytes * Status.SelectedProgress / 100.0);
                onProgress?.Invoke(done, Status.SelectedBytes, manager.Monitor.DownloadRate);

                await Task.Delay(1000, ct);
            }

            Sample();
            onProgress?.Invoke(Status.SelectedBytes, Status.SelectedBytes, 0);
        }
        finally
        {
            // Stop as soon as the selection is in; leaving it running would start on everything else.
            await manager.StopAsync(TimeSpan.FromSeconds(10));
            requester.SelectNone();
            _selection = Array.Empty<ITorrentManagerFile>();
        }

        // Hand the results to the archive, where the rest of the app expects to find them.
        foreach (var (entry, file) in selected)
        {
            if (!await PublishAsync(entry, file, ct)) missing.Add(entry);
        }

        Status.State = "ready";
        Status.Message = $"finished {selected.Count - missing.Count} of {selected.Count} files";

        return missing;
    }

    /// <summary>
    /// Moves one finished file out of the engine's directory into the archive, checking it against
    /// the sha256 carried in its own name first. Returns false when it is not usable, so the caller
    /// can fall back to HTTP for it.
    /// </summary>
    private async Task<bool> PublishAsync(Entry entry, ITorrentManagerFile file, CancellationToken ct)
    {
        try
        {
            string source = file.FullPath;
            if (!File.Exists(source) || new FileInfo(source).Length != file.Length) return false;

            // The sha256 is the fourth part of the file's own name, so the swarm's copy gets the
            // same check an HTTP download would get.
            if (!await ArchiveClient.VerifyAsync(source, entry.Sha, ct)) return false;

            string dest = Path.Combine(settings.DataDir, entry.DirName, entry.FileName);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);

            // Moved rather than copied: keeping both would double the disk cost of every chain.
            File.Move(source, dest, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void Sample()
    {
        var manager = _manager;
        if (manager is null) return;

        Status.Peers = manager.OpenConnections;
        Status.Seeds = manager.Peers.Seeds;
        Status.DownloadRate = manager.Monitor.DownloadRate;
        Status.UploadRate = manager.Monitor.UploadRate;
        Status.SelectedProgress = SelectionProgress();
        Status.TorrentState = manager.State.ToString();
    }

    /// <summary>
    /// How far the selection has got, weighted by file size. Each file carries its own bitfield of
    /// the pieces it spans, which is where the manager records what has arrived.
    /// </summary>
    private double SelectionProgress()
    {
        long total = 0;
        double done = 0;

        foreach (var file in _selection)
        {
            var pieces = file.BitField;
            total += file.Length;
            if (pieces.Length > 0) done += file.Length * ((double)pieces.TrueCount / pieces.Length);
        }

        return total == 0 ? 0 : done * 100.0 / total;
    }

    /// <summary>
    /// True once every piece of every selected file is in. Read from the bitfields rather than the
    /// percentage, which would leave the loop at the mercy of a rounding error.
    /// </summary>
    private bool SelectionComplete()
    {
        foreach (var file in _selection)
            if (!file.BitField.AllTrue) return false;

        return true;
    }

    public async Task StopAsync()
    {
        try
        {
            if (_manager is not null) await _manager.StopAsync(TimeSpan.FromSeconds(5));
            Status.State = "off";
            Status.Message = "stopped";
        }
        catch (Exception ex)
        {
            Status.Error = ex.Message;
        }
    }

    public async Task ResetAsync()
    {
        // Deliberately not an unbounded wait. The gate is held for the whole of EnsureStartedAsync,
        // and the case this has to handle is precisely that a start has hung inside it — waiting
        // politely would mean the shutdown could never run. Disposing the engine underneath a stuck
        // start is what releases it.
        bool held = await _gate.WaitAsync(TimeSpan.FromSeconds(3));
        try
        {
            if (_manager is not null)
                await _manager.StopAsync(TimeSpan.FromSeconds(5));

            _engine?.Dispose();
            _engine = null;
            _manager = null;
            _requester = null;
            _selection = Array.Empty<ITorrentManagerFile>();
            _byArchivePath.Clear();

            Status.State = "off";
            Status.Message = settings.TorrentPort > 0
                ? $"torrent engine reset; next start will listen on port {settings.TorrentPort}"
                : "torrent engine reset; next start will choose a random port";
            Status.Error = null;
            Status.HasMetadata = false;
            Status.TotalFiles = 0;
            Status.SelectedFiles = 0;
            Status.SelectedBytes = 0;
            Status.SelectedProgress = 0;
            Status.Peers = 0;
            Status.Seeds = 0;
            Status.DownloadRate = 0;
            Status.UploadRate = 0;
            Status.TorrentState = "";
        }
        finally
        {
            if (held) _gate.Release();
        }
    }
}
