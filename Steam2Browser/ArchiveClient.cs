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
    private const long SegmentedThresholdBytes = 32L * 1024 * 1024;
    private const int SegmentSizeBytes = 16 * 1024 * 1024;
    private const int SegmentsPerFile = 4;

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
        long resumeFrom = File.Exists(partPath) ? new FileInfo(partPath).Length : 0;

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
        if (resumeFrom == 0 && entry.Kind == Kind.Dat && (entry.ApproxSize < 0 || entry.ApproxSize >= SegmentedThresholdBytes))
        {
            var segmented = await TryPullSegmentedAsync(mirror, entry, partPath, onProgress, ct);
            if (segmented is not null) return segmented.Value;
        }

        using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath));
        if (resumeFrom > 0) req.Headers.Range = new RangeHeaderValue(resumeFrom, null);

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

        while ((read = await netStream.ReadAsync(buffer, ct)) > 0)
        {
            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            done += read;
            pulled += read;
            onProgress?.Invoke(done, total);
        }

        return pulled;
    }

    private async Task<long?> TryPullSegmentedAsync(
        Mirror mirror, Entry entry, string partPath,
        Action<long, long>? onProgress, CancellationToken ct)
    {
        long length = await GetMirrorLengthAsync(mirror, entry.RelPath, ct);
        if (length < SegmentedThresholdBytes) return null;

        Directory.CreateDirectory(Path.GetDirectoryName(partPath)!);

        try
        {
            await using (var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.ReadWrite, 1, useAsync: true))
                fs.SetLength(length);

            var ranges = new Queue<(int Index, long From, long To)>();
            int index = 0;
            for (long from = 0; from < length; from += SegmentSizeBytes)
                ranges.Enqueue((index++, from, Math.Min(length - 1, from + SegmentSizeBytes - 1)));

            var segmentProgress = new long[index];
            var progressLock = new object();
            long done = 0;

            var workers = Enumerable.Range(0, SegmentsPerFile).Select(async _ =>
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
            onProgress?.Invoke(length, length);
            return length;
        }
        catch
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { /* next mirror or caller handles the real error */ }
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
        using var req = new HttpRequestMessage(HttpMethod.Get, mirror.Url(entry.RelPath));
        req.Headers.Range = new RangeHeaderValue(from, to);

        using var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        if (resp.StatusCode != HttpStatusCode.PartialContent)
            throw new HttpRequestException($"range request was not honored by {mirror.Id}");
        resp.EnsureSuccessStatusCode();

        await using var netStream = await resp.Content.ReadAsStreamAsync(ct);
        await using var file = new FileStream(
            partPath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite,
            1 << 20, useAsync: true);
        file.Seek(from, SeekOrigin.Begin);

        var buffer = new byte[1 << 20];
        long remaining = to - from + 1;
        long done = 0;

        while (remaining > 0)
        {
            int read = await netStream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), ct);
            if (read == 0) throw new EndOfStreamException($"range {from}-{to} ended early");

            await file.WriteAsync(buffer.AsMemory(0, read), ct);
            remaining -= read;
            done += read;
            onSegmentProgress(done);
        }
    }

    public static async Task<bool> VerifyAsync(string path, string expectedSha, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20, useAsync: true);
        var hash = await SHA256.HashDataAsync(fs, ct);
        return Convert.ToHexStringLower(hash).Equals(expectedSha, StringComparison.OrdinalIgnoreCase);
    }
}
