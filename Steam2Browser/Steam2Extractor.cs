using System.Buffers.Binary;
using System.IO.Compression;
using System.Security.Cryptography;

namespace Steam2Browser;

public sealed class ExtractProgress
{
    public int TotalFiles;
    public int DoneFiles;
    public int SkippedFiles;
    public int FailedFiles;
    public long BytesWritten;
    public string Current = "";
}

/// <summary>
/// In-process port of the C++ extractor: resolves the delta chain from files already on disk,
/// reads the manifest and file id tables out of the blobs, then rebuilds each file from the dats.
///
/// Two things the original gets wrong are fixed here. It sanitizes the output path by deleting every
/// ':' — which eats the drive colon, so directories are created somewhere else and every file open
/// then fails silently (exit 0, nothing written). And it has no long-path handling, so deep trees
/// die on MAX_PATH. Here only the per-entry segments are sanitized, and long targets get the \\?\
/// prefix.
/// </summary>
public sealed class Steam2Extractor
{
    private const int BlockSize = 0x8000;
    private const int MaxChunk = 0x10000;

    public sealed record Chain(
        SortedDictionary<int, string> Dats,
        SortedDictionary<int, string> Blobs);

    // ---------------- chain from local files ----------------

    /// <summary>
    /// Rebuilds the chain from what is on disk. Without a blob crc this only works when no version
    /// is duplicated; with one, the parent links inside the blobs pick the right branch and the dat
    /// is matched by the exact size the blob records.
    /// </summary>
    public static Chain ResolveLocal(string dataDir, int depot, int version, string? blobCrc)
    {
        string blobDir = Path.Combine(dataDir, "blobs");
        string datDir = Path.Combine(dataDir, "dats");

        var blobFiles = Index(blobDir, depot, ".blob");
        var datFiles = Index(datDir, depot, ".dat");

        if (blobFiles.Count == 0) throw new InvalidDataException($"no blobs for depot {depot} in {blobDir}");
        if (datFiles.Count == 0) throw new InvalidDataException($"no dats for depot {depot} in {datDir}");

        var dats = new SortedDictionary<int, string>();
        var blobs = new SortedDictionary<int, string>();

        if (string.IsNullOrWhiteSpace(blobCrc))
        {
            foreach (var (v, list) in blobFiles)
            {
                if (v > version) continue;
                if (list.Count > 1)
                    throw new InvalidDataException(
                        $"version {v} has {list.Count} blobs — this depot was reset, pick a blob crc");
                blobs[v] = list[0].Path;
            }
            foreach (var (v, list) in datFiles)
            {
                if (v > version) continue;
                if (list.Count > 1)
                    throw new InvalidDataException(
                        $"version {v} has {list.Count} dats — this depot was reset, pick a blob crc");
                dats[v] = list[0].Path;
            }

            for (int v = version; v >= 0; v--)
                if (!dats.ContainsKey(v) || !blobs.ContainsKey(v))
                    throw new InvalidDataException($"missing a dat or blob for version {v}");

            return new Chain(dats, blobs);
        }

        if (!blobFiles.TryGetValue(version, out var heads))
            throw new InvalidDataException($"no blob for version {version}");

        var head = heads.FirstOrDefault(b => b.Crc.Equals(blobCrc.Trim(), StringComparison.OrdinalIgnoreCase))
                   ?? throw new InvalidDataException($"no blob with crc {blobCrc} at version {version}");

        var current = head;
        int at = version;

        while (true)
        {
            blobs[at] = current.Path;

            var info = BlobFormat.Parse(File.ReadAllBytes(current.Path));

            if (!datFiles.TryGetValue(at, out var candidates) || candidates.Count == 0)
                throw new InvalidDataException($"no dat for version {at}");

            if (candidates.Count == 1)
            {
                dats[at] = candidates[0].Path;
            }
            else
            {
                if (info.DatSize is not ulong want)
                    throw new InvalidDataException($"blob at version {at} records no dat size");

                var match = candidates.FirstOrDefault(c => (ulong)new FileInfo(c.Path).Length == want)
                            ?? throw new InvalidDataException($"no dat of size {want} at version {at}");
                dats[at] = match.Path;
            }

            if (at == 0) break;

            if (info.ParentCrc is not uint parentCrc)
                throw new InvalidDataException($"blob at version {at} records no parent crc");

            if (!blobFiles.TryGetValue(at - 1, out var parents))
                throw new InvalidDataException($"no blob for version {at - 1}");

            current = parents.FirstOrDefault(b => b.CrcValue == parentCrc)
                      ?? throw new InvalidDataException($"no blob with crc {parentCrc:x8} at version {at - 1}");
            at--;
        }

        return new Chain(dats, blobs);
    }

    private sealed record LocalFile(int Version, string Crc, uint CrcValue, string Path);

    private static Dictionary<int, List<LocalFile>> Index(string dir, int depot, string ext)
    {
        var result = new Dictionary<int, List<LocalFile>>();
        if (!Directory.Exists(dir)) return result;

        foreach (var path in Directory.EnumerateFiles(dir, $"{depot}_*{ext}"))
        {
            var entry = Catalog.ParseName(Path.GetFileName(path), ext == ".dat" ? Kind.Dat : Kind.Blob);
            if (entry is null || entry.Depot != depot) continue;

            if (!result.TryGetValue(entry.Version, out var list))
                result[entry.Version] = list = new List<LocalFile>();

            list.Add(new LocalFile(entry.Version, entry.CrcHex, entry.Crc, path));
        }

        return result;
    }

    // ---------------- extraction ----------------

    public static void Extract(
        string dataDir,
        int depot,
        int version,
        string? blobCrc,
        string? filter,
        string outDir,
        byte[]? keyOverride,
        ExtractProgress progress,
        Action<string> log,
        CancellationToken ct)
    {
        // Not every depot is encrypted. Files carry a filemode: 1 is plain zlib and needs no key at
        // all, only 2 and 3 involve AES. So the key is fetched here but demanded later, and only if
        // a file actually being extracted turns out to need it.
        byte[]? key = keyOverride ?? DepotKeys.Get(depot);

        var chain = ResolveLocal(dataDir, depot, version, blobCrc);
        log($"chain resolved: {chain.Dats.Count} dats, {chain.Blobs.Count} blobs");

        // Later versions replace earlier entries, which is what makes the deltas resolve.
        var fileIds = new Dictionary<uint, FileLocation>();
        foreach (var (v, path) in chain.Blobs)
        {
            ct.ThrowIfCancellationRequested();
            foreach (var (id, loc) in ChecksumTable.Parse(File.ReadAllBytes(path), v))
                fileIds[id] = loc;
        }
        log($"file id table built: {fileIds.Count} entries");

        var tree = ManifestFormat.TreeFromBlob(File.ReadAllBytes(chain.Blobs[chain.Blobs.Keys.Max()]))
                   ?? throw new InvalidDataException("newest blob carries no manifest");
        log($"manifest loaded: app {tree.AppId} version {tree.VerId}, {tree.Nodes.Count} nodes");

        var regex = string.IsNullOrWhiteSpace(filter)
            ? null
            : new System.Text.RegularExpressions.Regex(filter, System.Text.RegularExpressions.RegexOptions.Compiled);

        var dats = new Dictionary<int, FileStream>();
        try
        {
            foreach (var (v, path) in chain.Dats)
                dats[v] = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);

            var wanted = tree.Nodes
                .Where(n => n.Flags != 0)
                .Where(n => regex is null || regex.IsMatch(n.Path))
                .ToList();

            // Now that the wanted set is known, see whether any of it is actually encrypted.
            var encryptedModes = wanted
                .Select(n => fileIds.TryGetValue(n.FileId, out var l) ? l.FileMode : (byte)0)
                .Where(m => m is 2 or 3)
                .ToList();

            if (encryptedModes.Count > 0 && key is null)
                throw new InvalidDataException(
                    $"{encryptedModes.Count} of {wanted.Count} files in depot {depot} are encrypted and " +
                    $"no key is known for it — supply one with --key if you have it");

            log(encryptedModes.Count == 0
                ? "nothing in this depot is encrypted, so no key is needed"
                : $"{encryptedModes.Count} of {wanted.Count} files are encrypted, using the depot key");

            progress.TotalFiles = wanted.Count;
            log($"extracting {wanted.Count} files to {outDir}");

            var chunk = new byte[MaxChunk];
            var inflated = new byte[BlockSize];

            foreach (var node in wanted)
            {
                ct.ThrowIfCancellationRequested();
                progress.Current = node.Path;

                try
                {
                    WriteOne(node, fileIds, dats, key, outDir, chunk, inflated, progress);
                    progress.DoneFiles++;
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    progress.FailedFiles++;
                    log($"FAILED {node.Path}: {ex.Message}");
                }
            }

            progress.Current = "";
            log($"done — {progress.DoneFiles} files, {progress.FailedFiles} failed, {progress.BytesWritten:N0} bytes");
        }
        finally
        {
            foreach (var s in dats.Values) s.Dispose();
        }
    }

    private static void WriteOne(
        ManifestFormat.Node node,
        Dictionary<uint, FileLocation> fileIds,
        Dictionary<int, FileStream> dats,
        byte[]? key,
        string outDir,
        byte[] chunk,
        byte[] inflated,
        ExtractProgress progress)
    {
        if (!fileIds.TryGetValue(node.FileId, out var loc))
            throw new InvalidDataException($"file id {node.FileId} is not in the table");

        if (!dats.TryGetValue(loc.Part, out var dat))
            throw new InvalidDataException($"no dat loaded for part {loc.Part}");

        string full = SafePath(outDir, node.Path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);

        using var output = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20);

        long offset = (long)loc.Offset;
        foreach (var block in loc.Blocks)
        {
            if (block.CompressedSize == 0) continue;
            if (block.CompressedSize > MaxChunk)
                throw new InvalidDataException($"chunk of {block.CompressedSize} bytes is impossible");

            dat.Position = offset;
            dat.ReadExactly(chunk, 0, (int)block.CompressedSize);

            int written = HandleChunk(chunk, (int)block.CompressedSize, loc.FileMode, key, inflated, output);
            progress.BytesWritten += written;

            offset += block.CompressedSize;
        }
    }

    /// <summary>Unpacks one chunk into <paramref name="output"/> and reports how many bytes it wrote.</summary>
    private static int HandleChunk(byte[] chunk, int count, byte filemode, byte[]? key, byte[] inflated, Stream output)
    {
        switch (filemode)
        {
            case 0:
                output.Write(chunk, 0, count);
                return count;

            case 1:
            {
                int n = Inflate(chunk, 0, count, inflated);
                output.Write(inflated, 0, n);
                return n;
            }

            case 2:
            {
                // The first 8 bytes stay in the clear: encrypted size, then decompressed size.
                if (count < 8) throw new InvalidDataException("chunk too short for filemode 2");
                uint decompressedSize = BinaryPrimitives.ReadUInt32LittleEndian(chunk.AsSpan(4));
                if (decompressedSize > BlockSize)
                    throw new InvalidDataException($"filemode 2 chunk claims {decompressedSize} bytes");

                DecryptCfb(chunk, 8, count - 8, RequireKey(key, filemode));

                int n = Inflate(chunk, 8, count - 8, inflated);
                output.Write(inflated, 0, n);
                return n;
            }

            case 3:
            {
                DecryptCfb(chunk, 0, count, RequireKey(key, filemode));
                output.Write(chunk, 0, count);
                return count;
            }

            default:
                throw new InvalidDataException($"unknown filemode {filemode}");
        }
    }

    private static byte[] RequireKey(byte[]? key, byte filemode) =>
        key ?? throw new InvalidDataException($"filemode {filemode} is encrypted but no key is available");

    private static int Inflate(byte[] source, int offset, int length, byte[] destination)
    {
        using var input = new MemoryStream(source, offset, length, writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);

        int total = 0;
        while (total < destination.Length)
        {
            int read = zlib.Read(destination, total, destination.Length - total);
            if (read == 0) break;
            total += read;
        }
        return total;
    }

    /// <summary>
    /// AES-128 CFB with a full 128-bit segment and a zero IV, decrypting in place.
    /// Written by hand because .NET refuses a trailing partial block with PaddingMode.None,
    /// while CFB is a stream mode and these chunks are not block-aligned.
    /// </summary>
    private static void DecryptCfb(byte[] buffer, int offset, int length, byte[] key)
    {
        if (length <= 0) return;

        using var aes = Aes.Create();
        aes.Key = key;
        aes.Mode = CipherMode.ECB;
        aes.Padding = PaddingMode.None;
        using var encryptor = aes.CreateEncryptor();

        var feedback = new byte[16];   // zero IV
        var keystream = new byte[16];
        var savedCipher = new byte[16];

        for (int at = 0; at < length; at += 16)
        {
            encryptor.TransformBlock(feedback, 0, 16, keystream, 0);

            int n = Math.Min(16, length - at);
            Array.Copy(buffer, offset + at, savedCipher, 0, n);

            for (int i = 0; i < n; i++)
                buffer[offset + at + i] ^= keystream[i];

            Array.Copy(savedCipher, feedback, n);
        }
    }

    // ---------------- paths ----------------

    private static readonly char[] Invalid = Path.GetInvalidFileNameChars();

    /// <summary>
    /// Joins a manifest path onto the output directory, cleaning each segment separately so the
    /// drive colon survives, and rejecting any traversal out of the output directory.
    /// </summary>
    public static string SafePath(string outDir, string relative)
    {
        var segments = relative.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        var clean = new List<string>(segments.Length);

        foreach (var raw in segments)
        {
            if (raw == "." || raw == "..") continue;

            var chars = raw.ToCharArray();
            for (int i = 0; i < chars.Length; i++)
                if (Array.IndexOf(Invalid, chars[i]) >= 0)
                    chars[i] = '_';

            var seg = new string(chars).TrimEnd(' ', '.');
            if (seg.Length > 0) clean.Add(seg);
        }

        string full = Path.GetFullPath(Path.Combine(outDir, Path.Combine([.. clean])));

        // Windows still caps at MAX_PATH unless the target is spelled the long way.
        if (OperatingSystem.IsWindows() && full.Length >= 250 && !full.StartsWith(@"\\?\", StringComparison.Ordinal))
            full = @"\\?\" + full;

        return full;
    }
}
