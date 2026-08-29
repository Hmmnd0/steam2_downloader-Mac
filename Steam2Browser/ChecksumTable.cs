using System.Buffers.Binary;

namespace Steam2Browser;

/// <summary>One compressed block of a file inside a dat.</summary>
public readonly record struct BlockEntry(uint CompressedSize, uint Checksum);

/// <summary>
/// Where one file id lives: which dat part holds it, at what offset, in how many blocks,
/// and how those blocks are packed (filemode 1 = zlib, 2 = AES then zlib, 3 = AES only).
/// </summary>
public sealed class FileLocation
{
    public byte FileMode;
    public ulong Offset;
    public ulong FileSize;
    public uint BlockCount;
    public int Part;
    public BlockEntry[] Blocks = [];
}

/// <summary>
/// Reads the file id table stored under key 4 of every blob, ported from parse_out_checksum_info
/// in main.cpp.
///
/// Header (0x20 bytes): magic 0x34457234, version, numFileBlocks, numItems, offset1, offset2,
/// blockSize (always 0x8000), largestNumBlocks. Then numFileBlocks x (fileIdStart, fileCount,
/// offset, dummy). Each of those points at a run of per-file mappings, each followed by its
/// block list. A trailing copy of the magic closes the table.
/// </summary>
public static class ChecksumTable
{
    private const uint Magic = 0x34457234;

    /// <summary>
    /// Whether any file in this blob is actually encrypted. Filemode 1 is plain zlib and needs no
    /// key; only 2 and 3 involve AES. Most depots absent from the key table are simply not
    /// encrypted, so this — not the key table — is what decides if a depot can be unpacked.
    /// Null when the table could not be read.
    /// </summary>
    public static bool? AnyEncrypted(ReadOnlySpan<byte> blobBytes)
    {
        try
        {
            var keys = BlobFormat.ReadKeys(blobBytes);
            if (!keys.TryGetValue(4, out var table)) return null;
            return ParseTable(table, 0).Values.Any(l => l.FileMode is 2 or 3);
        }
        catch
        {
            return null;
        }
    }

    public static Dictionary<uint, FileLocation> Parse(ReadOnlySpan<byte> blobBytes, int part)
    {
        var keys = BlobFormat.ReadKeys(blobBytes);
        if (!keys.TryGetValue(4, out var table))
            throw new InvalidDataException("blob has no file id table (key 4)");

        return ParseTable(table, part);
    }

    public static Dictionary<uint, FileLocation> ParseTable(byte[] t, int part)
    {
        if (t.Length < 0x20) throw new InvalidDataException("file id table truncated");

        uint magic = ReadU32(t, 0);
        uint version = ReadU32(t, 4);
        uint numFileBlocks = ReadU32(t, 8);
        uint numItems = ReadU32(t, 12);
        uint offset1 = ReadU32(t, 16);
        uint offset2 = ReadU32(t, 20);
        uint blockSize = ReadU32(t, 24);
        uint largestNumBlocks = ReadU32(t, 28);

        if (magic != Magic) throw new InvalidDataException("file id table: bad magic");
        if (blockSize != 0x8000) throw new InvalidDataException("file id table: block size is not 0x8000");
        if (version is not (0 or 1)) throw new InvalidDataException($"file id table: unsupported version {version}");
        if (offset1 != 0x20) throw new InvalidDataException("file id table: bad table offset");
        if (offset2 != 0x20 + 0x10 * numFileBlocks) throw new InvalidDataException("file id table: bad mapping offset");

        var result = new Dictionary<uint, FileLocation>((int)numItems);

        int pos = 0x20;
        var blocks = new (uint Start, uint Count, uint Offset)[numFileBlocks];
        for (int i = 0; i < numFileBlocks; i++)
        {
            if (pos + 16 > t.Length) throw new InvalidDataException("file id table: block list truncated");
            blocks[i] = (ReadU32(t, pos), ReadU32(t, pos + 4), ReadU32(t, pos + 8));
            pos += 16;
        }

        uint filesSeen = 0;
        uint maxBlocks = 0;

        foreach (var (start, count, offset) in blocks)
        {
            if (pos != offset) throw new InvalidDataException("file id table: reader drifted from the recorded offset");
            filesSeen += count;

            for (uint f = 0; f < count; f++)
            {
                var loc = new FileLocation { Part = part };

                if (version == 0)
                {
                    Need(t, pos, 12);
                    loc.FileSize = ReadU32(t, pos);
                    loc.Offset = ReadU32(t, pos + 4);
                    pos += 8;
                }
                else
                {
                    Need(t, pos, 20);
                    loc.FileSize = ReadU64(t, pos);
                    loc.Offset = ReadU64(t, pos + 8);
                    pos += 16;
                }

                uint packed = ReadU32(t, pos);
                pos += 4;

                loc.FileMode = (byte)(packed >> 24);
                loc.BlockCount = packed & 0x00ffffff;

                if (loc.FileMode is not (1 or 2 or 3))
                    throw new InvalidDataException($"file id table: filemode {loc.FileMode} out of range");

                maxBlocks = Math.Max(maxBlocks, loc.BlockCount);

                var list = new BlockEntry[loc.BlockCount];
                Need(t, pos, (long)loc.BlockCount * 8);
                for (uint j = 0; j < loc.BlockCount; j++)
                {
                    list[j] = new BlockEntry(ReadU32(t, pos), ReadU32(t, pos + 4));
                    pos += 8;
                }
                loc.Blocks = list;

                result[start + f] = loc;
            }
        }

        Need(t, pos, 4);
        if (ReadU32(t, pos) != Magic) throw new InvalidDataException("file id table: bad footer magic");

        if (maxBlocks != largestNumBlocks)
            throw new InvalidDataException("file id table: largest block count disagrees with the header");
        if (filesSeen != numItems)
            throw new InvalidDataException("file id table: item count disagrees with the header");

        return result;
    }

    private static void Need(byte[] t, int pos, long bytes)
    {
        if (pos < 0 || pos + bytes > t.Length) throw new InvalidDataException("file id table: truncated");
    }

    private static uint ReadU32(byte[] t, int at) => BinaryPrimitives.ReadUInt32LittleEndian(t.AsSpan(at));
    private static ulong ReadU64(byte[] t, int at) => BinaryPrimitives.ReadUInt64LittleEndian(t.AsSpan(at));
}
