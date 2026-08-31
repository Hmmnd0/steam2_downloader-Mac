namespace Steam2Browser;

/// <summary>
/// How much room is left where the archive is kept.
///
/// Depots run to tens of gigabytes and a chain can be hundreds, so running out part-way is a
/// realistic way to lose an hour of downloading. Checking first costs nothing.
/// </summary>
public sealed record DiskSpace(string Root, long FreeBytes, long TotalBytes, string? Error)
{
    public long UsedBytes => TotalBytes - FreeBytes;
}

public static class Disk
{
    /// <summary>
    /// Room left free even when a download would otherwise fit exactly.
    ///
    /// A disk with nothing at all left on it stops being a working disk: temporary files fail, and
    /// on the system volume so does most of the desktop. Two gigabytes is small against the sizes
    /// involved here and enough to keep the machine usable.
    /// </summary>
    public const long Headroom = 2_000_000_000L;

    /// <summary>
    /// Free and total bytes for the volume holding <paramref name="path"/>.
    ///
    /// The path itself need not exist yet — settings can name a directory that has not been created
    /// — so this walks up to the nearest parent that does. Everything here is best-effort: a failure
    /// is reported rather than thrown, because not knowing the free space must never be the reason
    /// a download is refused.
    /// </summary>
    public static DiskSpace For(string path)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(path))
                return new DiskSpace("", 0, 0, "no directory set");

            string full = Path.GetFullPath(path);

            // A drive that is not mounted, a directory not made yet, a UNC share that is down: all
            // arrive here the same way, as a path with no existing ancestor.
            string? probe = full;
            while (probe is not null && !Directory.Exists(probe))
                probe = Path.GetDirectoryName(probe);

            if (probe is null)
                return new DiskSpace(full, 0, 0, "the drive is not available");

            var drive = DriveFor(probe);
            if (drive is null)
                return new DiskSpace(full, 0, 0, "no mounted filesystem holds this path");
            if (!drive.IsReady)
                return new DiskSpace(drive.Name, 0, 0, "the drive is not ready");

            // AvailableFreeSpace, not TotalFreeSpace: with a quota in force the second is a number
            // this user cannot actually write into.
            return new DiskSpace(drive.Name, drive.AvailableFreeSpace, drive.TotalSize, null);
        }
        catch (Exception ex)
        {
            return new DiskSpace(path, 0, 0, ex.Message);
        }
    }

    /// <summary>
    /// The filesystem that actually holds <paramref name="path"/>: the mount point that is its
    /// longest matching parent.
    ///
    /// Not the path root, which is what this used to ask for. On Windows the root is the volume and
    /// the two agree, but on Linux the root of every absolute path is <c>/</c> — so a download
    /// directory on a second disk reported the free space of the system disk instead. Measured in
    /// WSL, a directory under a 378 GB mount was reported as the 906 GB root. Benign in that
    /// direction; the other way round, a small root and downloads on a large data disk, leaves the
    /// download button disabled and every download refused with the app insisting there is no room
    /// on a drive that is nearly empty.
    ///
    /// Longest prefix rather than first match, because mount points nest: <c>/mnt/data/archive</c>
    /// belongs to <c>/mnt/data</c> if that is mounted, and to <c>/</c> only if it is not.
    /// </summary>
    private static DriveInfo? DriveFor(string path)
    {
        string full = Path.GetFullPath(path);
        var cmp = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        DriveInfo? best = null;
        int bestLength = -1;

        foreach (var d in DriveInfo.GetDrives())
        {
            string root;
            try { root = d.RootDirectory.FullName; } catch { continue; }
            if (root.Length == 0) continue;

            // Compared with a trailing separator on both sides so /mnt/s does not claim
            // /mnt/steam-archive, while still matching /mnt/s itself.
            string withSep = root.EndsWith(Path.DirectorySeparatorChar) ? root : root + Path.DirectorySeparatorChar;
            string target = full.EndsWith(Path.DirectorySeparatorChar) ? full : full + Path.DirectorySeparatorChar;

            if (!target.StartsWith(withSep, cmp)) continue;
            if (withSep.Length <= bestLength) continue;

            // A mount that cannot be read is no answer, even if its path matches best.
            try { if (!d.IsReady) continue; } catch { continue; }

            best = d;
            bestLength = withSep.Length;
        }

        // GetDrives can come back short — a container with an unusual mount table, a permission
        // problem reading it — so the old behaviour stays as the fallback rather than nothing.
        if (best is not null) return best;

        try { return new DriveInfo(Path.GetPathRoot(full) ?? full); }
        catch { return null; }
    }

    /// <summary>
    /// Whether <paramref name="needed"/> bytes can be written to <paramref name="path"/>.
    ///
    /// Unknown counts as room. The check exists to catch the case that is certain to fail, and
    /// treating a drive it could not measure as full would block downloads that would have worked.
    /// </summary>
    public static bool Fits(string path, long needed, out DiskSpace space)
    {
        space = For(path);
        if (space.Error is not null) return true;
        return space.FreeBytes >= needed + Headroom;
    }
}
