using MonoTorrent;
using MonoTorrent.PieceWriter;
using ReusableTasks;

namespace Steam2Browser;

/// <summary>
/// The disk layer the torrent engine reads and writes through, with one change: a read of a file
/// that is not on disk answers "no bytes" instead of being passed down.
///
/// Without it, sharing left about thirty thousand empty files behind. The torrent lists 116 346
/// files and this app has a handful of them, so the hash check reads through every one — and the
/// read itself was what created them, because the layer underneath opens a file it is asked to read
/// whether or not it is there. Measured against the real torrent: 32 752 files after 24 seconds
/// with the reads passed through, 6 431 with them stopped — and 6 431 is exactly how many files in
/// this torrent are genuinely empty, so what is left is only what the archive really contains.
///
/// It is also the honest answer. A file we do not have holds no bytes, and saying so is what makes
/// the hash check mark those pieces as missing, which they are. Anything downloaded later is
/// hard-linked in before it is offered, so by the time its bytes matter the file exists and reads
/// go through as normal.
///
/// The existence check costs a stat on every read. Against the 16 KB of disk the read itself does,
/// that is noise; and on the path that dominates — the startup hash check over files we do not have
/// — it replaces opening a file with not opening it, so it is faster than what it replaces.
/// </summary>
public sealed class ArchivePieceWriter(IPieceWriter inner) : IPieceWriter
{
    public int OpenFiles => inner.OpenFiles;

    public int MaximumOpenFiles => inner.MaximumOpenFiles;

    public ReusableTask<int> ReadAsync(ITorrentManagerFile file, long offset, Memory<byte> buffer)
        => OnDisk(file) ? inner.ReadAsync(file, offset, buffer) : ReusableTask.FromResult(0);

    // Writes are left alone. The swarm helper downloads into this directory and those files have to
    // be created; it is only reading a file that was never there that had no business making one.
    public ReusableTask WriteAsync(ITorrentManagerFile file, long offset, ReadOnlyMemory<byte> buffer)
        => inner.WriteAsync(file, offset, buffer);

    public ReusableTask<bool> ExistsAsync(ITorrentManagerFile file) => inner.ExistsAsync(file);

    public ReusableTask CloseAsync(ITorrentManagerFile file) => inner.CloseAsync(file);

    public ReusableTask FlushAsync(ITorrentManagerFile file) => inner.FlushAsync(file);

    public ReusableTask MoveAsync(ITorrentManagerFile file, string fullPath, bool overwrite)
        => inner.MoveAsync(file, fullPath, overwrite);

    public ReusableTask SetMaximumOpenFilesAsync(int maximumOpenFiles)
        => inner.SetMaximumOpenFilesAsync(maximumOpenFiles);

    public void Dispose() => inner.Dispose();

    /// <summary>
    /// All three paths, because which one a file answers to depends on whether the engine is
    /// keeping partial downloads under a separate name, and getting that wrong in the direction of
    /// "not there" would hide a file we actually hold.
    /// </summary>
    private static bool OnDisk(ITorrentManagerFile file)
        => File.Exists(file.FullPath)
        || File.Exists(file.DownloadCompleteFullPath)
        || File.Exists(file.DownloadIncompleteFullPath);
}
