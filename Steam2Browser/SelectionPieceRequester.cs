using MonoTorrent;
using MonoTorrent.PiecePicking;

namespace Steam2Browser;

/// <summary>
/// Restricts the swarm to a chosen handful of files, without touching the manager's file
/// priorities.
///
/// MonoTorrent's own way of selecting files is SetFilePriorityAsync, which rebuilds a bitfield
/// spanning every piece in the torrent on each call. At 793 733 pieces that is 4.4 ms a file, and
/// parking all 116 346 of them cost eight and a half minutes before a download could start.
///
/// The picker never needed the priorities recorded on the manager's files, only the priorities it
/// is shown while choosing pieces. So the manager's files are left at their defaults and the
/// standard requester is handed a view of them whose priorities are ours to set: selecting is then
/// one field write per selected file. The guarantee is unchanged — a file that is not selected is
/// never requested, so nothing is ever written for it.
/// </summary>
public sealed class SelectionPieceRequester : IPieceRequester
{
    /// <summary>
    /// One file as the picker sees it: everything passed through to the real file except the
    /// priority, which reflects this selection rather than the manager's.
    /// </summary>
    private sealed class FileView(ITorrentManagerFile file) : ITorrentManagerFile
    {
        public Priority Priority { get; set; } = Priority.DoNotDownload;

        public ReadOnlyBitField BitField => file.BitField;
        public string FullPath => file.FullPath;
        public string DownloadCompleteFullPath => file.DownloadCompleteFullPath;
        public string DownloadIncompleteFullPath => file.DownloadIncompleteFullPath;

        public string Path => file.Path;
        public int StartPieceIndex => file.StartPieceIndex;
        public int EndPieceIndex => file.EndPieceIndex;
        public int PieceCount => file.PieceCount;
        public long Length => file.Length;
        public long Padding => file.Padding;
        public long OffsetInTorrent => file.OffsetInTorrent;
        public MerkleRoot PiecesRoot => file.PiecesRoot;
    }

    /// <summary>
    /// The torrent as the picker sees it: the real thing, with the file views swapped in.
    /// </summary>
    private sealed class TorrentView(IPieceRequesterData data, IList<ITorrentManagerFile> files)
        : IPieceRequesterData
    {
        public IList<ITorrentManagerFile> Files { get; } = files;

        public int PieceCount => data.PieceCount;
        public int PieceLength => data.PieceLength;

        public int SegmentsPerPiece(int piece) => data.SegmentsPerPiece(piece);
        public int ByteOffsetToPieceIndex(long byteOffset) => data.ByteOffsetToPieceIndex(byteOffset);
        public int BytesPerPiece(int piece) => data.BytesPerPiece(piece);
    }

    private readonly StandardPieceRequester _inner = new(PieceRequesterSettings.Default);

    private readonly List<ITorrentManagerFile> _views;
    private readonly Dictionary<ITorrentManagerFile, FileView> _byFile;
    private readonly List<FileView> _selected = new();

    public SelectionPieceRequester(IList<ITorrentManagerFile> files)
    {
        _views = new List<ITorrentManagerFile>(files.Count);
        _byFile = new Dictionary<ITorrentManagerFile, FileView>(files.Count);

        foreach (var file in files)
        {
            var view = new FileView(file);
            _views.Add(view);
            _byFile[file] = view;
        }
    }

    /// <summary>
    /// Asks for these files and nothing else. Any earlier selection is dropped.
    ///
    /// The picker notices the change by itself on its next pass, so this is safe to call while the
    /// manager is running, but a selection made before it starts is what the download path wants.
    /// </summary>
    public void Select(IEnumerable<ITorrentManagerFile> files)
    {
        SelectNone();

        foreach (var file in files)
        {
            if (!_byFile.TryGetValue(file, out var view)) continue;

            view.Priority = Priority.High;
            _selected.Add(view);
        }
    }

    /// <summary>Puts everything back out of reach, so a started manager asks for nothing.</summary>
    public void SelectNone()
    {
        foreach (var view in _selected)
            view.Priority = Priority.DoNotDownload;

        _selected.Clear();
    }

    public bool InEndgameMode => _inner.InEndgameMode;

    public void Initialise(
        IPieceRequesterData torrentData,
        IMessageEnqueuer enqueuer,
        ReadOnlySpan<ReadOnlyBitField> ignorableBitfields)
        => _inner.Initialise(new TorrentView(torrentData, _views), enqueuer, ignorableBitfields);

    public void AddRequests(ReadOnlySpan<(IRequester Peer, ReadOnlyBitField Available)> peers)
        => _inner.AddRequests(peers);

    public void AddRequests(IRequester peer, ReadOnlyBitField available, ReadOnlySpan<ReadOnlyBitField> allPeers)
        => _inner.AddRequests(peer, available, allPeers);

    public bool ValidatePiece(
        IRequester peer,
        PieceSegment pieceSegment,
        out bool pieceComplete,
        HashSet<IRequester> peersInvolved)
        => _inner.ValidatePiece(peer, pieceSegment, out pieceComplete, peersInvolved);

    public bool IsInteresting(IRequester peer, ReadOnlyBitField bitField)
        => _inner.IsInteresting(peer, bitField);

    public void CancelRequests(IRequester peer, int startIndex, int endIndex)
        => _inner.CancelRequests(peer, startIndex, endIndex);

    public void RequestRejected(IRequester peer, PieceSegment pieceRequest)
        => _inner.RequestRejected(peer, pieceRequest);

    public int CurrentRequestCount() => _inner.CurrentRequestCount();
}
