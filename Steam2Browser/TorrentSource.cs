using MonoTorrent;
using MonoTorrent.Client;
using System.Net;

namespace Steam2Browser;

public sealed class TorrentStatus
{
    /// <summary>off | starting | metadata | ready | downloading | error</summary>
    public string State = "off";

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
/// usable because BitTorrent can fetch selected files: everything is set to DoNotDownload and only
/// the files a chain actually needs are raised in priority.
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

    public bool Ready => _manager is { HasMetadata: true };

    // ---------------- startup ----------------

    /// <summary>
    /// Brings the engine up and waits for the file list. Safe to call repeatedly; only the first
    /// call does the work.
    /// </summary>
    public async Task<bool> EnsureStartedAsync(CancellationToken ct = default)
    {
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
                var link = BuildLink();

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

                _manager = await _engine.AddAsync(link, dataDir, torrentSettings);
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
            await DeselectAllAsync();

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
    /// Indexes the torrent by archive-relative path and parks every file at DoNotDownload, so
    /// nothing is fetched until a chain asks for it.
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

    /// <summary>Parks everything, so a new selection starts from a clean slate.</summary>
    private async Task DeselectAllAsync()
    {
        var manager = _manager!;
        foreach (var file in manager.Files)
            if (file.Priority != Priority.DoNotDownload)
                await manager.SetFilePriorityAsync(file, Priority.DoNotDownload);
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
        var missing = new List<Entry>();
        var selected = new List<(Entry Entry, ITorrentManagerFile File)>();

        await DeselectAllAsync();

        foreach (var entry in wanted)
        {
            if (_byArchivePath.TryGetValue(entry.RelPath, out var file))
            {
                await manager.SetFilePriorityAsync(file, Priority.High);
                selected.Add((entry, file));
            }
            else
            {
                missing.Add(entry);
            }
        }

        Status.SelectedFiles = selected.Count;
        Status.SelectedBytes = selected.Sum(x => x.File.Length);

        if (selected.Count == 0) return missing;

        Status.State = "downloading";
        Status.Message = $"{selected.Count} files selected from the swarm";

        try
        {
            await manager.StartAsync();

            // PartialProgress covers only the files above DoNotDownload, which is the selection.
            while (manager.PartialProgress < 100)
            {
                ct.ThrowIfCancellationRequested();
                Sample();

                long done = (long)(Status.SelectedBytes * manager.PartialProgress / 100.0);
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
            await DeselectAllAsync();
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
        Status.SelectedProgress = manager.PartialProgress;
        Status.TorrentState = manager.State.ToString();
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
        await _gate.WaitAsync();
        try
        {
            if (_manager is not null)
                await _manager.StopAsync(TimeSpan.FromSeconds(5));

            _engine?.Dispose();
            _engine = null;
            _manager = null;
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
            _gate.Release();
        }
    }
}
