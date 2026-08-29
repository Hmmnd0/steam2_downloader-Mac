using System.Buffers.Binary;
using System.IO.Compression;

namespace Steam2Browser;

/// <summary>
/// Minimal reader for the Steam2 .blob container, mirroring blobng.hpp from the extractor source.
///
/// Layout: u16 magic 0x5001, u32 totalSize, u32 slackSize, then key/value records from offset 10
/// while pos &lt; totalSize: u16 keySize, u32 valueSize, key bytes, value bytes.
/// A blob may instead be wrapped: u16 magic 0x4301, u64 packed, u64 unpacked, u16 level, zlib stream.
///
/// Keys we care about are 4-byte little-endian integers:
///   0  -> format code (3 or 4)
///   3  -> nested compressed blob holding the file manifest
///   12 -> CRC of the parent blob, i.e. the previous link in the delta chain
///   13 -> size of the matching .dat (u32 when format code is 3, u64 when it is 4)
/// </summary>
public static class BlobFormat
{
    private const ushort MagicPlain = 0x5001;
    private const ushort MagicCompressed = 0x4301;

    public sealed record BlobInfo(uint FormatCode, uint? ParentCrc, ulong? DatSize);

    /// <summary>Reads every 4-byte-integer key into a dictionary, unwrapping compression if present.</summary>
    public static Dictionary<uint, byte[]> ReadKeys(ReadOnlySpan<byte> raw)
    {
        var result = new Dictionary<uint, byte[]>();

        if (raw.Length >= 2 && BinaryPrimitives.ReadUInt16LittleEndian(raw) == MagicCompressed)
            raw = Decompress(raw);

        if (raw.Length < 10 || BinaryPrimitives.ReadUInt16LittleEndian(raw) != MagicPlain)
            throw new InvalidDataException("blob: bad magic");

        uint totalSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[2..]);
        int end = (int)Math.Min(totalSize, (uint)raw.Length);

        int pos = 10;
        while (pos + 6 <= end)
        {
            ushort keySize = BinaryPrimitives.ReadUInt16LittleEndian(raw[pos..]);
            uint valueSize = BinaryPrimitives.ReadUInt32LittleEndian(raw[(pos + 2)..]);
            pos += 6;

            if (pos + keySize + (long)valueSize > end) break;

            if (keySize == 4)
            {
                uint key = BinaryPrimitives.ReadUInt32LittleEndian(raw.Slice(pos, keySize));
                result[key] = raw.Slice(pos + keySize, (int)valueSize).ToArray();
            }

            pos += keySize + (int)valueSize;
        }

        return result;
    }

    public static BlobInfo Parse(ReadOnlySpan<byte> raw)
    {
        var keys = ReadKeys(raw);

        uint formatCode = keys.TryGetValue(0, out var fc) && fc.Length == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(fc)
            : 0;

        uint? parentCrc = keys.TryGetValue(12, out var pc) && pc.Length == 4
            ? BinaryPrimitives.ReadUInt32LittleEndian(pc)
            : null;

        // Key 13 is u32 for format code 3 and u64 for format code 4; trust the actual width.
        ulong? datSize = null;
        if (keys.TryGetValue(13, out var ds))
        {
            datSize = ds.Length switch
            {
                4 => BinaryPrimitives.ReadUInt32LittleEndian(ds),
                8 => BinaryPrimitives.ReadUInt64LittleEndian(ds),
                _ => null
            };
        }

        return new BlobInfo(formatCode, parentCrc, datSize);
    }

    private static byte[] Decompress(ReadOnlySpan<byte> raw)
    {
        if (raw.Length < 20) throw new InvalidDataException("blob: compressed header truncated");

        ulong unpacked = BinaryPrimitives.ReadUInt64LittleEndian(raw[10..]);
        if (unpacked > int.MaxValue) throw new InvalidDataException("blob: unpacked size too large");

        var output = new byte[(int)unpacked];
        using var input = new MemoryStream(raw[20..].ToArray(), writable: false);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        zlib.ReadExactly(output);
        return output;
    }
}
