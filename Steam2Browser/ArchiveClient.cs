using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;

namespace Steam2Browser;

/// <summary>
/// HTTP access to one archive mirror, with optional failover to the others.
/// Every mirror serves byte-identical files, so a failed part can be retried elsewhere.
/// </summary>
public sealed class ArchiveClient(HttpClient http)
{
    private const long SegmentedThresholdBytes = 1L * 1024 * 1024;
    private const int MinSegmentSizeBytes = 1 * 1024 * 1024;
    private const int MaxSegmentSizeBytes = 8 * 1024 * 1024;
    private const int SegmentsPerFile = 32;
    private const int MaxConcurrentTransfers = 32;
    private const int MaxRangeFailuresWithoutProgress = 6;
    private static readonly TimeSpan ReadInactivityTimeout = TimeSpan.FromSeconds(20);

    // DownloadManager limits files, while this gate limits the actual HTTP streams. Without it,
    // several files with many ranges each can overload the mirror and leave every stream starved.
    private readonly SemaphoreSlim transferGate = new(MaxConcurrentTransfers, MaxConcurrentTransfers);

    public Mirror Primary { get; set; } = Mirrors.All[0];

    /// <summary>When true, a failed request is retried against the remaining mirrors.</summary>
    public bool Failover { get; set; } = true;

    private IEnumerable<Mirror> Order()
    {
        yield return Primary;
        if (!Failover) yield break;
        foreach (var m in Mirrors.All)
            if (m.Id != Primary.Id)
                yield return m;
    }

    public async Task<byte[]> GetBytesAsync(string relPath, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var m in Order())
        {
            try { return await http.GetByteArrayAsync(m.Url(relPath), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException($"could not fetch {relPath}");
    }

    public async Task<string> GetStringAsync(string relPath, CancellationToken ct = default)
    {
        Exception? last = null;
        foreach (var m in Order())
        {
            try { return await http.GetStringAsync(m.Url(relPath), ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex) { last = ex; }
        }
        throw last ?? new HttpRequestException($"could not fetch {relPath}");
    }

    /// <summary>Exact byte length from Content-Length. -1 when the file is missing everywhere.</summary>
    public async Task<long> GetLengthAsync(string relPath, CancellationToken ct = default)
    {
        foreach (var m in Order())
        {
            try
            {
                using var req = new HttpRequestMessage(HttpMethod.Head, m.Url(relPath));
                using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                if (resp.StatusCode == HttpStatusCode.NotFound) continue;
                resp.EnsureSuccessStatusCode();
                if (resp.Content.Headers.ContentLength is long len) return len;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch { /* try next mirror */ }
        }
        return -1;
    }

    /// <summary>
    /// Downloads one file to <paramref name="destPath"/>, resuming a partial .part file via Range,
    /// then verifies sha256 against the hash embedded in the file name.
    /// Returns the number of bytes pulled over the network this call.
    /// </summary>
    public async Task<long> DownloadFileAsync(
        Entry entry,
        string destPath,
        bool verify,
        Action<long, long>? onProgress,
        CancellationToken ct = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destPath)!);

        if (File.Exists(destPath))
        {
            if (!verify) { onProgress?.Invoke(new FileInfo(destPath).Length, new FileInfo(destPath).Length); return 0; }
            if (await VerifyAsync(destPath, entry.Sha, ct))
            {
                long have = new FileInfo(destPath).Length;
                onProgress?.Invoke(have, have);
                return 0;
            }
            File.Delete(destPath);
        }

        string partPath = destPath + ".part";
        string segmentedMarkerPath = partPath + ".segmented";

        // Segmented downloads preallocate the target, so its file length is not resume progress.
        // A marker left by a killed process means the file must be restarted instead of issuing a
        // bogus Range request from EOF.
        if (File.Exists(segmentedMarkerPath))
        {
            TryDelete(partPath);
            TryDelete(segmentedMarkerPath);
        }

        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

        // Builds before the marker was introduced can leave a partially-filled, full-size .part.
        // Recover a genuinely complete file, otherwise discard it so the ranged path can run.
        if (resumeFrom > 0 && entry.Kind == Kind.Dat)
        {
            long remoteLength = await GetLengthAsync(entry.RelPath, ct);
            if (remoteLength > 0 && resumeFrom >= remoteLength)
            {
                if (resumeFrom == remoteLength && await VerifyAsync(partPath, entry.Sha, ct))
                {
                    File.Move(partPath, destPath, overwrite: true);
                    onProgress?.Invoke(remoteLength, remoteLength);
                    return 0;
                }

                TryDelete(partPath);
                resumeFrom = 0;
            }
        }

        Exception? last = null;
        foreach (var m in Order())
        {
            try
            {
                long pulled = await PullAsync(m, entry, partPath, resumeFrom, onProgress, ct);

                if (verify && !await VerifyAsync(partPath, entry.Sha, ct))
                {
                    File.Delete(partPath);
                    resumeFrom = 0;
                    last = new InvalidDataException($"sha256 mismatch for {entry.FileName} from {m.Id}");
                    continue;
                }

                File.Move(partPath, destPath, overwrite: true);
                return pulled;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception ex)
            {
                last = ex;
                resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;
            }
        }
        throw last ?? new HttpRequestException($"could not download {entry.FileName}");
    }

    private async Task<long> PullAsync(
        Mirror mirror, Entry entry, string partPath, long resumeFrom,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        if (entry.Kind == Kind.Dat && (entry.ApproxSize < 0 || entry.ApproxSize >= SegmentedThresholdBytes))
        {
            var segmented = await TryPullSegmentedAsync(mirror, entry, partPath, resumeFrom, onProgress, ct);
            if (segmented is not null) return segmented.Value;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath));
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

        await transferGate.WaitAsync(ct);
        try
        {
            using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);

            // Server ignored the Range header, so start over from zero.
            if (resumeFrom > 0 && resp.StatusCode != HttpStatusCode.PartialContent)
            {
                resumeFrom = 0;
                if (File.Exists(partPath)) File.Delete(partPath);
            }
            resp.EnsureSuccessStatusCode();

            long total = (resp.Content.Headers.ContentLength ?? -1) + (resumeFrom > 0 ? resumeFrom : 0);

            await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
            await using var file = new FileStream(
                partPath, resumeFrom > 0 ? FileMode.Append : FileMode.Create,
                FileAccess.Write, FileShare.None, 1 << 20, useAsync: true);

            var buffer = new byte[1 << 20];
            long done = resumeFrom;
            long pulled = 0;
            int read;

            while ((read = await ReadWithInactivityTimeoutAsync(netStream, buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                done += read;
                pulled += read;
                onProgress?.Invoke(done, total);
            }

            return pulled;
        }
        finally
        {
            transferGate.Release();
        }
    }

    private async Task<long?> TryPullSegmentedAsync(
        Mirror mirror, Entry entry, string partPath, long resumeFrom,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        long length = await GetMirrorLengthAsync(mirror, entry.RelPath, ct);
        if (length < SegmentedThresholdBytes) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);
        string markerPath = partPath + ".segmented";

        try
        {
            await File.WriteAllTextAsync(markerPath, length.ToString(), ct);
            await using (var fs = new FileStream(
                partPath, resumeFrom > 0 ? FileMode.Open : FileMode.Create,
                FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true))
                fs.SetLength(length);

            var ranges = new Queue<(int Index, long From, long To)>();
            int index = 0;
            long segmentSize = Math.Clamp(
                (length + SegmentsPerFile - 1) / SegmentsPerFile,
                MinSegmentSizeBytes,
                MaxSegmentSizeBytes);
            for (long from = resumeFrom; from < length; from += segmentSize)
                ranges.Enqueue((index++, from, Math.Min(length - 1, from + segmentSize - 1)));

            var segmentProgress = new long[index];
            var progressLock = new object();
            long done = resumeFrom;
            onProgress?.Invoke(done, length);

            var workers = Enumerable.Range(0, Math.Min(SegmentsPerFile, index)).Select(async _ =>
            {
                while (true)
                {
                    (int Index, long From, long To) range;
                    lock (ranges)
                    {
                        if (ranges.Count == 0) return;
                        range = ranges.Dequeue();
                    }

                    await PullRangeAsync(mirror, entry, partPath, range.From, range.To, segmentDone =>
                    {
                        lock (progressLock)
                        {
                            long delta = segmentDone - segmentProgress[range.Index];
                            if (delta <= 0) return;

                            segmentProgress[range.Index] = segmentDone;
                            done += delta;
                            onProgress?.Invoke(done, length);
                        }
                    }, ct);
                }
            });

            await Task.WhenAll(workers);
            TryDelete(markerPath);
            onProgress?.Invoke(length, length);
            return length - resumeFrom;
        }
        catch
        {
            TryDelete(partPath);
            TryDelete(markerPath);
            throw;
        }
    }

    private async Task<long> GetMirrorLengthAsync(Mirror mirror, string relPath, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Head, mirror.Url(relPath));
        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode == HttpStatusCode.NotFound) return -1;
        resp.EnsureSuccessStatusCode();
        return resp.Content.Headers.ContentLength ?? -1;
    }

    private async Task PullRangeAsync(
        Mirror mirror, Entry entry, string partPath, long from, long to,
        Action<long> onSegmentProgress, CancellationToken ct)
    {
        var buffer = new byte[1 << 20];
        long done = 0;
        int failuresWithoutProgress = 0;

        while (from + done <= to)
        {
            long attemptStart = done;
            try
            {
                await transferGate.WaitAsync(ct);
                try
                {
                    long requestFrom = from + done;
                    using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath))
                    {
                        // Independent HTTP/1.1 connections are intentional here. HTTP/2 would
                        // multiplex ranges over fewer TCP connections and lose the measured gain.
                        Version = HttpVersion.Version11,
                        VersionPolicy = HttpVersionPolicy.RequestVersionExact,
                    };
                    req.Headers.Range = new RangeHeaderValue(requestFrom, to);

                    using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
                    if (resp.StatusCode != HttpStatusCode.PartialContent)
                        throw new HttpRequestException($"range request was not honored by {mirror.Id}");
                    resp.EnsureSuccessStatusCode();

                    long? returnedFrom = resp.Content.Headers.ContentRange?.From;
                    if (returnedFrom != requestFrom)
                        throw new InvalidDataException($"range response started at {returnedFrom}, expected {requestFrom}");

                    await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
                    await using var file = new FileStream(
                        partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
                        1 << 20, useAsync: true);
                    file.Seek(requestFrom, SeekOrigin.Begin);

                    while (from + done <= to)
                    {
                        int wanted = (int)Math.Min(buffer.Length, to - (from + done) + 1);
                        int read = await ReadWithInactivityTimeoutAsync(netStream, buffer.AsMemory(0, wanted), ct);
                        if (read == 0) throw new EndOfStreamException($"range {requestFrom}-{to} ended early");

                        await file.WriteAsync(buffer.AsMemory(0, read), ct);
                        done += read;
                        onSegmentProgress(done);
                    }
                }
                finally
                {
                    transferGate.Release();
                }

                return;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception) when (failuresWithoutProgress < MaxRangeFailuresWithoutProgress)
            {
                failuresWithoutProgress = done > attemptStart ? 0 : failuresWithoutProgress + 1;
                if (failuresWithoutProgress >= MaxRangeFailuresWithoutProgress) throw;

                await Task.Delay(TimeSpan.FromMilliseconds(250 * failuresWithoutProgress), ct);
            }
        }
    }

    private static async ValueTask<int> ReadWithInactivityTimeoutAsync(
        Stream stream, Memory<byte> buffer, CancellationToken ct)
    {
        using var inactivity = CancellationTokenSource.CreateLinkedTokenSource(ct);
        inactivity.CancelAfter(ReadInactivityTimeout);
        try
        {
            return await stream.ReadAsync(buffer, inactivity.Token);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"no download data received for {ReadInactivityTimeout.TotalSeconds:0} seconds");
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* Preserve the original network or verification error. */ }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash).Equals(expectedSha, StringComparison.OrdinalIgnoreCase);
    }
}
