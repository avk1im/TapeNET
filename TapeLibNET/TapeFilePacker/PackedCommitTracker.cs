using Microsoft.Extensions.Logging;

namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Bookkeeping helper for the packed backup path: consolidates the three collections
/// the agent would otherwise juggle by hand:
/// <list type="bullet">
///   <item><b>Pending</b> -- files written into the packer but whose commit notification
///         has not yet arrived (keyed by <see cref="CommitToken"/>).</item>
///   <item><b>Committed (TOC)</b> -- promoted on commit by appending a real
///         <see cref="TapeFileInfo"/> (with the resolved <see cref="TapeAddress"/>) to
///         the current set's TOC.</item>
///   <item><b>Awaiting post-process</b> -- committed files queued for
///         <c>NotifyPostProcessFile</c>, drained on the main loop thread to preserve
///         abort semantics.</item>
/// </list>
/// <para>
/// All mutation routes through this class so the agent's main loop only needs three
/// verbs: <see cref="Register"/>, <see cref="OnCommitted"/>, and <see cref="DrainPostProcess"/>.
/// </para>
/// </summary>
internal sealed class PackedCommitTracker(TapeSetTOC setTOC, ILogger logger)
{
    // Per-file state held until FilesCommitted fires for the file's token.
    private sealed class PendingEntry
    {
        public required TapeFileInfo Template;   // UID + FileDescr only -- Address is provisional
        public required int FileIndex;           // index in the agent's fileList
        public byte[]? Hash;                     // hash captured after writing the body
        public TapeFileCodec Codec;              // per-file codec (Stored or Zstd) decided at write time
    }

    private readonly Dictionary<CommitToken, PendingEntry> _pending = [];
    private readonly Queue<TapeFileInfo> _awaitingPostProcess = new();
    private readonly TapeSetTOC _setTOC = setTOC;
    private readonly ILogger _logger = logger;

    /// <summary>Number of files written into the packer but not yet committed.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>Number of committed files awaiting their post-process notification.</summary>
    public int AwaitingPostProcessCount => _awaitingPostProcess.Count;

    /// <summary>
    /// Records bookkeeping for a file just handed off to the packer. The matching
    /// <see cref="OnCommitted"/> call (driven by <c>Manager.FilesCommitted</c>) will
    /// promote it to the TOC and the post-process queue.
    /// </summary>
    public void Register(CommitToken token, TapeFileInfo template, int fileIndex, byte[]? hash,
        TapeFileCodec codec = TapeFileCodec.Stored)
    {
        _pending[token] = new PendingEntry
        {
            Template  = template,
            FileIndex = fileIndex,
            Hash      = hash,
            Codec     = codec,
        };
    }

    /// <summary>
    /// Promotes every committed file: constructs its real <see cref="TapeFileInfo"/>
    /// (with the address surfaced by the packer), appends it to the TOC, and queues
    /// it for post-process notification.
    /// <para>Tokens not present in the pending map are logged and skipped -- this
    /// indicates a logic bug rather than a recoverable condition.</para>
    /// </summary>
    public void OnCommitted(IReadOnlyList<CommittedFile> committed)
    {
        foreach (var cf in committed)
        {
            if (!_pending.Remove(cf.Token, out var entry))
            {
                _logger.LogWarning("PackedCommitTracker: unknown commit token {Token}", cf.Token.Sequence);
                continue;
            }

            var tfi = new TapeFileInfo(entry.Template.UID, cf.StartAddress, entry.Template.FileDescr)
            {
                Hash       = entry.Hash,
                SizeOnTape = cf.Length,
                Codec      = entry.Codec,
            };

            _setTOC.Append(tfi);
            _awaitingPostProcess.Enqueue(tfi);
        }
    }

    /// <summary>
    /// Removes the given rolled-back tokens from the pending map (e.g. on EOM) and
    /// returns the smallest <c>FileIndex</c> seen among them, along with the matching
    /// <see cref="TapeFileInfo"/> template. If no rolled-back token matches a pending
    /// entry, <paramref name="fallbackIndex"/> is returned and <paramref name="earliestTemplate"/>
    /// is set to <see langword="null"/>.
    /// </summary>
    public int RemoveRolledBack(IReadOnlyList<CommitToken> rolledBackTokens, int fallbackIndex,
        out TapeFileInfo? earliestTemplate)
    {
        int earliest = fallbackIndex;
        earliestTemplate = null;
        foreach (var token in rolledBackTokens)
        {
            if (_pending.Remove(token, out var entry))
            {
                if (entry.FileIndex <= earliest)
                {
                    earliest = entry.FileIndex;
                    earliestTemplate = entry.Template;
                }
            }
        }
        return earliest;
    }

    /// <summary>
    /// Drains the awaiting-post-process queue, invoking <paramref name="notify"/> for
    /// each file. If <paramref name="notify"/> throws <see cref="TapeAbortRequestedException"/>
    /// the drain stops and this method returns <see langword="false"/>; the dequeued file
    /// is considered processed (its TOC entry is already appended).
    /// </summary>
    public bool DrainPostProcess(Action<TapeFileInfo> notify)
    {
        while (_awaitingPostProcess.Count > 0)
        {
            var tfi = _awaitingPostProcess.Dequeue();
            try
            {
                notify(tfi);
            }
            catch (TapeAbortRequestedException)
            {
                _logger.LogTrace("PackedCommitTracker: abort requested while post-processing >{File}<",
                    tfi.FileDescr.FullName);
                return false;
            }
        }
        return true;
    }
}
