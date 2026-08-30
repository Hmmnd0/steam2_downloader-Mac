using System.Buffers.Binary;
using System.Text;

namespace Steam2Browser;

/// <summary>
/// Reads the Steam2 file manifest that lives inside every blob, ported from steam2ng.hpp.
///
/// The blob's key 3 holds a compressed blob whose key 0 is the manifest:
///   TMstBinHeader (14 x u32, 56 bytes), then u32NumOfNodes x TDirNodeBin (7 x u32, 28 bytes),
///   then a NUL-separated string table.
/// A node's path is built by walking u32Parent until it reads 0xffffffff.
///
/// This is the only place in the archive that carries human-readable names, and it needs no
/// decryption key — unlike the file data in the dats.
/// </summary>
public static class ManifestFormat
{
    private const int HeaderSize = 56;
    private const int NodeSize = 28;

    public sealed record ManifestInfo(
        uint AppId,
        uint VerId,
        uint NodeCount,
        uint FileCount,
        IReadOnlyList<string> Roots);

    /// <summary>
    /// A manifest entry. Flags == 0 marks a directory, which has no data to extract.
    /// Size comes from u32CountOrSize, which holds the file's length at this version — checked
    /// against the file id table, which agrees on every id the two share.
    /// </summary>
    public readonly record struct Node(uint FileId, uint Flags, string Path, long Size);

    public sealed record ManifestTree(uint AppId, uint VerId, IReadOnlyList<Node> Nodes);

    /// <summary>Pulls the manifest out of raw blob bytes. Null when the blob carries no manifest.</summary>
    public static ManifestInfo? FromBlob(ReadOnlySpan<byte> blobBytes)
    {
        var outer = BlobFormat.ReadKeys(blobBytes);
        if (!outer.TryGetValue(3, out var manifestBlob) || manifestBlob.Length == 0) return null;

        var inner = BlobFormat.ReadKeys(manifestBlob);
        if (!inner.TryGetValue(0, out var manifest) || manifest.Length < HeaderSize) return null;

        return Parse(manifest);
    }

    public static ManifestInfo? Parse(byte[] m)
    {
        if (m.Length < HeaderSize) return null;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(m);
        if (version is not (3 or 4)) return null;

        uint appId = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4));
        uint verId = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8));
        uint nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(12));
        uint fileCount = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(16));
        uint binarySize = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(24));

        if (binarySize != m.Length) return null;

        long nodesEnd = (long)HeaderSize + (long)nodeCount * NodeSize;
        if (nodeCount == 0 || nodesEnd > m.Length) return null;

        int stringTable = (int)nodesEnd;

        // Only the top level is needed for a label: nodes whose parent is the root node (index 0).
        var roots = new List<string>();
        for (uint i = 0; i < nodeCount; i++)
        {
            int at = HeaderSize + (int)i * NodeSize;
            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at));
            uint parent = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 16));

            if (parent != 0) continue;

            var name = ReadString(m, stringTable, nameOffset);
            if (!string.IsNullOrWhiteSpace(name)) roots.Add(name);
            if (roots.Count >= 32) break;
        }

        return new ManifestInfo(appId, verId, nodeCount, fileCount, roots);
    }

    /// <summary>Full node list with resolved paths, needed to actually write files out.</summary>
    public static ManifestTree? TreeFromBlob(ReadOnlySpan<byte> blobBytes)
    {
        var outer = BlobFormat.ReadKeys(blobBytes);
        if (!outer.TryGetValue(3, out var manifestBlob) || manifestBlob.Length == 0) return null;

        var inner = BlobFormat.ReadKeys(manifestBlob);
        if (!inner.TryGetValue(0, out var manifest) || manifest.Length < HeaderSize) return null;

        return ParseTree(manifest);
    }

    public static ManifestTree? ParseTree(byte[] m)
    {
        if (m.Length < HeaderSize) return null;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(m);
        if (version is not (3 or 4)) return null;

        uint appId = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(4));
        uint verId = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(8));
        uint nodeCount = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(12));
        uint binarySize = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(24));

        if (binarySize != m.Length) return null;

        long nodesEnd = (long)HeaderSize + (long)nodeCount * NodeSize;
        if (nodeCount == 0 || nodesEnd > m.Length) return null;

        int stringTable = (int)nodesEnd;

        var nameOffsets = new uint[nodeCount];
        var sizes = new uint[nodeCount];
        var fileIds = new uint[nodeCount];
        var flags = new uint[nodeCount];
        var parents = new uint[nodeCount];

        for (uint i = 0; i < nodeCount; i++)
        {
            int at = HeaderSize + (int)i * NodeSize;
            nameOffsets[i] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at));
            sizes[i] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 4));
            fileIds[i] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 8));
            flags[i] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 12));
            parents[i] = BinaryPrimitives.ReadUInt32LittleEndian(m.AsSpan(at + 16));
        }

        var nodes = new List<Node>((int)nodeCount);
        var parts = new List<string>(16);

        for (uint i = 0; i < nodeCount; i++)
        {
            parts.Clear();

            // Walk up to the root, which is the node whose parent reads 0xffffffff.
            uint cur = i;
            int guard = 0;
            while (parents[cur] != 0xffffffff)
            {
                parts.Add(ReadString(m, stringTable, nameOffsets[cur]));
                cur = parents[cur];
                if (cur >= nodeCount || ++guard > 256) break;
            }

            parts.Reverse();
            nodes.Add(new Node(fileIds[i], flags[i], string.Join('/', parts), sizes[i]));
        }

        return new ManifestTree(appId, verId, nodes);
    }

    private static string ReadString(byte[] m, int tableStart, uint offset)
    {
        long start = tableStart + (long)offset;
        if (start < 0 || start >= m.Length) return "";

        int end = (int)start;
        while (end < m.Length && m[end] != 0) end++;

        return Encoding.UTF8.GetString(m, (int)start, end - (int)start);
    }

    /// <summary>
    /// Turns the top-level entries into something worth showing. A single root is almost always the
    /// product folder; otherwise the first few are joined.
    /// </summary>
    public static string Label(IReadOnlyList<string> roots)
    {
        if (roots.Count == 0) return "";
        if (roots.Count == 1) return roots[0];

        // A lone folder among loose files is usually the real name.
        var folders = roots.Where(r => !r.Contains('.')).ToList();
        if (folders.Count == 1) return folders[0];

        return string.Join(", ", roots.Take(3)) + (roots.Count > 3 ? $" (+{roots.Count - 3})" : "");
    }
}
