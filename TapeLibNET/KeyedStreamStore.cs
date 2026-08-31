using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;
using Microsoft.Extensions.Logging;

namespace TapeLibNET;

/// <summary>
/// Generic, root-agnostic keyed stream store. Each stream lives in its OWN sub-folder
/// (named from a sanitized key + a short stable hash) under the supplied root; the payload
/// sits in a data file (by default the legacy <c>blob.bin</c>, or an optionally supplied
/// human-readable name such as <c>profile.tapecal.json</c>), and the raw key in <c>key.txt</c>.
/// <para>
/// The store stays deliberately opaque: it moves <see cref="Stream"/>s keyed by
/// <see cref="string"/> and never interprets the payload. Scope (app-private vs.
/// shared) belongs to the CALLER — pick the root accordingly. Living in
/// <c>TapeLibNET</c> lets both the library and any app reuse it (dependency flows
/// app → library, never the reverse).
/// </para>
/// <para>
/// CROSS-PROCESS SAFE: because a shared root may be hit by several TapeLibNET apps
/// at once (TapeWinNET, TapeConNET, …), every mutating op and every read runs under
/// a per-KEY named <see cref="Mutex"/>. Distinct keys never block each other; distinct
/// roots stay independent too. Reads return a detached in-memory copy, so a caller's
/// open stream can never collide with a concurrent replace.
/// </para>
/// <para>
/// ERROR MODEL: derives from <see cref="ErrorManageableBase"/> for the same tracing +
/// error surface every TapeLibNET class exposes. Failing to access a key is NOT treated
/// as catastrophic — the public API NEVER throws for runtime conditions. Instead it
/// records the error (<see cref="ErrorManageableBase.SetError(Exception,string)"/>), logs it,
/// and signals via the return value: <see langword="false"/> / <see langword="null"/>.
/// Absence is not even an error (return null/false with <c>WentOK</c> still true). Inspect
/// <c>WentOK</c> / <c>WentBad</c> / <see cref="ErrorManageableBase.LastError"/> to tell an
/// empty result from a genuine failure. The sole exception is a <see langword="null"/>
/// <c>writer</c> argument (a caller contract violation), which throws.
/// </para>
/// </summary>
public sealed class KeyedStreamStore : ErrorManageableBase
{
    #region *** Constants ***

    // Legacy on-disk data filename, kept as the default and as the read fallback so
    //  existing profiles are never "lost" when a caller opts into a human-readable name.
    private const string c_legacyDataFile = "blob.bin";
    private const string c_keyFile        = "key.txt";
    private const string c_mutexScope     = "TapeLibNET.KeyedStreamStore";

    #endregion

    #region *** Fields ***

    // Category folder; every key gets its own sub-folder beneath this.
    private readonly string m_root;

    // Stable hash of the root, so two stores at different roots never share a mutex.
    private readonly uint m_rootTag;

    // Log prefix derived from the leaf folder, e.g. "StreamStore[Calibrations]".
    private readonly string m_prefix;

    // Optional human-readable data filename (e.g. "profile.tapecal.json"). When null, the
    //  legacy "blob.bin" name is used both for writing and reading — unchanged behavior.
    private readonly string? m_dataFileName;

    #endregion

    #region *** Construction ***

    /// <summary>
    /// Creates a store rooted at <paramref name="root"/> (created lazily on first save).
    /// </summary>
    /// <param name="root">The category folder this store manages.</param>
    /// <param name="logger">Logger for tracing + error reporting.</param>
    /// <param name="dataFileName">
    /// Optional human-readable data filename (e.g. <c>profile.tapecal.json</c>) written for NEW
    /// saves. When <see langword="null"/>, the legacy <c>blob.bin</c> name is used — unchanged
    /// behavior. Either way, <see cref="Open"/> falls back to <c>blob.bin</c> when the named file
    /// is absent, so profiles saved before a caller opted into a human-readable name are never lost.
    /// </param>
    public KeyedStreamStore(string root, ILogger logger, string? dataFileName = null)
        : base(logger)
    {
        m_root = root ?? throw new ArgumentNullException(nameof(root));
        m_rootTag = Fnv1a(m_root);
        m_dataFileName = dataFileName;

        string leaf = Path.GetFileName(m_root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        m_prefix = $"StreamStore[{(string.IsNullOrEmpty(leaf) ? m_root : leaf)}]";
    }

    /// <summary>The category folder this store manages.</summary>
    public string Root => m_root;

    protected override string LogPrefix => m_prefix;

    #endregion

    #region *** Query ***

    /// <summary>
    /// True when a stream for <paramref name="key"/> exists (either under the configured data
    /// filename or the legacy <c>blob.bin</c>). On an access error returns <see langword="false"/>
    /// and sets the error state (check <c>WentBad</c> to distinguish "absent" from "could not probe").
    /// </summary>
    public bool Exists(string key)
    {
        ResetError();
        try
        {
            return WithKeyLock(key, () => File.Exists(DataPath(key)) || File.Exists(LegacyPath(key)));
        }
        catch (Exception ex)
        {
            RecordError(ex, $"cannot probe stream for key '{key}'");
            return false;
        }
    }

    /// <summary>
    /// Enumerates the RAW keys currently stored, read back verbatim from each
    /// <c>key.txt</c> (so round-tripping survives the folder-name sanitizing).
    /// The directory list is snapshotted up front; a folder that later vanishes or
    /// is mid-write is simply skipped. On an enumeration error the sequence ends
    /// early with the error state set (no throw).
    /// </summary>
    public IEnumerable<string> Keys()
    {
        ResetError();

        string[] dirs;
        try
        {
            dirs = Directory.Exists(m_root) ? Directory.GetDirectories(m_root) : [];
        }
        catch (Exception ex)
        {
            RecordError(ex, "cannot enumerate stream store");
            yield break;
        }

        foreach (var dir in dirs)
        {
            string? key = TryReadKey(Path.Combine(dir, c_keyFile));
            if (key is not null)
                yield return key;
        }
    }

    #endregion

    #region *** Read / Write / Delete ***

    /// <summary>
    /// Writes a stream via callback so the caller streams straight in (e.g. an object's
    /// own <c>SaveTo(Stream)</c>). Runs under the per-key lock, writes to a temp file
    /// first, then swaps atomically — so neither a crash nor a rival writer ever leaves
    /// a half-written file behind. Returns <see langword="true"/> on success; on failure records
    /// the error, logs it, and returns <see langword="false"/>.
    /// </summary>
    /// <param name="key">The stream's key (folder identity).</param>
    /// <param name="writer">Callback that writes the payload to the supplied stream.</param>
    /// <param name="dataFileName">
    /// Optional PER-CALL override of the data filename (e.g. a caller that wants a filename derived
    /// from the payload itself, such as <c>QUANTUM_ULTRIUM-4_U52F_780GB@2026-08-14.tapecal.json</c>).
    /// Falls back to the constructor's <c>dataFileName</c>, then to the legacy <c>blob.bin</c>.
    /// </param>
    public bool Save(string key, Action<Stream> writer, string? dataFileName = null)
    {
        ArgumentNullException.ThrowIfNull(writer);   // caller contract violation → throw
        ResetError();

        try
        {
            WithKeyLock(key, () =>
            {
                string dir = FolderFor(key);
                Directory.CreateDirectory(dir);

                // Persist the raw key once so Keys() can round-trip it later.
                string keyFile = Path.Combine(dir, c_keyFile);
                if (!File.Exists(keyFile))
                    File.WriteAllText(keyFile, key);

                string effectiveDataFileName = dataFileName ?? m_dataFileName ?? c_legacyDataFile;
                string tmp = Path.Combine(dir, effectiveDataFileName + ".tmp");
                using (var s = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
                    writer(s);

                File.Move(tmp, Path.Combine(dir, effectiveDataFileName), overwrite: true);
            });

            m_logger.LogTrace("{Prefix}: saved stream for key '{Key}'", LogPrefix, key);
            return true;
        }
        catch (Exception ex)
        {
            RecordError(ex, $"cannot save stream for key '{key}'");
            return false;
        }
    }

    /// <summary>
    /// Returns a DETACHED, seekable copy of the stream (position 0), or <see langword="null"/>.
    /// A null result means EITHER the key is absent (<c>WentOK</c> stays true) OR the read
    /// failed (<c>WentBad</c>, error state set). The bytes are read under the per-key lock and
    /// handed back as an in-memory stream, so the caller's read never races a concurrent
    /// replace. Caller disposes.
    /// <para>
    /// LEGACY FALLBACK: when the named data file (constructor-supplied or the legacy <c>blob.bin</c>)
    /// is absent, the folder is probed for ANY other file next to <c>key.txt</c> — so a folder that
    /// only carries an older per-call filename (e.g. a prior <see cref="MeasuredUtc"/>-stamped name)
    /// or the legacy <c>blob.bin</c> is still read.
    /// </para>
    /// </summary>
    public Stream? Open(string key)
    {
        ResetError();
        try
        {
            return WithKeyLock(key, () =>
            {
                string? path = DataPath(key);
                if (!File.Exists(path))
                {
                    path = LegacyPath(key);
                    if (!File.Exists(path))
                    {
                        path = FindAnyDataFile(key);
                        if (path is null)
                            return (Stream?)null;
                    }
                }

                byte[] bytes = File.ReadAllBytes(path);
                m_logger.LogTrace("{Prefix}: loaded stream for key '{Key}' ({Bytes} bytes)",
                    LogPrefix, key, bytes.Length);

                return new MemoryStream(bytes, writable: false);
            });
        }
        catch (Exception ex)
        {
            RecordError(ex, $"cannot open stream for key '{key}'");
            return null;
        }
    }

    /// <summary>
    /// Removes the stream (and its sub-folder) under the per-key lock. Returns <see langword="true"/>
    /// on success (including the no-op when the key is absent); on failure records the error and
    /// returns <see langword="false"/>.
    /// </summary>
    public bool Delete(string key)
    {
        ResetError();
        try
        {
            WithKeyLock(key, () =>
            {
                string dir = FolderFor(key);
                if (Directory.Exists(dir))
                    Directory.Delete(dir, recursive: true);
            });

            m_logger.LogTrace("{Prefix}: deleted stream for key '{Key}'", LogPrefix, key);
            return true;
        }
        catch (Exception ex)
        {
            RecordError(ex, $"cannot delete stream for key '{key}'");
            return false;
        }
    }

    #endregion

    #region *** Cross-Process Locking ***

    // Void convenience overload.
    private void WithKeyLock(string key, Action body)
        => WithKeyLock(key, () => { body(); return true; });

    /// <summary>
    /// Runs <paramref name="body"/> while holding a system-wide named mutex unique to
    /// (root, key). An abandoned mutex (prior owner crashed) is treated as acquired.
    /// </summary>
    private T WithKeyLock<T>(string key, Func<T> body)
    {
        using var mutex = CreateMutex(key);
        bool held = false;
        try
        {
            try { held = mutex.WaitOne(); }
            catch (AbandonedMutexException) { held = true; } // previous owner died; ownership is ours
            return body();
        }
        finally
        {
            if (held)
                mutex.ReleaseMutex();
        }
    }

    // Prefer the cross-session Global namespace; fall back to session-local where denied.
    private Mutex CreateMutex(string key)
    {
        string name = $@"Global\{c_mutexScope}.{m_rootTag:x8}.{Fnv1a(key):x8}";
        try
        {
            return new Mutex(initiallyOwned: false, name);
        }
        catch (UnauthorizedAccessException)
        {
            m_logger.LogWarning(
                "{Prefix}: Global mutex denied; falling back to session-local — cross-session sharing disabled",
                LogPrefix);
            return new Mutex(initiallyOwned: false, @"Local\" + name[@"Global\".Length ..]);
        }
    }

    #endregion

    #region *** Error Recording (non-throwing) ***

    /// <summary>
    /// Records + logs a failure into the inherited error state. Does NOT throw: the public
    /// API signals failure through its return value instead. Builds a <see cref="TapeIOException"/>
    /// purely to attach a breadcrumb trail to the log line (wrapping non-tape exceptions).
    /// </summary>
    private void RecordError(Exception ex, string message, [CallerMemberName] string methodName = "")
    {
        SetError(ex);
        var tio = ex as TapeIOException ?? new TapeIOException(ex, message);
        tio.AddTrail(this, message, methodName);
        LogTapeException(tio, message, methodName);
    }

    #endregion

    #region *** Path Mapping ***

    // The current data filename this store writes/reads primarily (human-readable when supplied,
    //  else the legacy name — so a store never configured with one behaves exactly as before).
    private string DataPath(string key) => Path.Combine(FolderFor(key), m_dataFileName ?? c_legacyDataFile);

    // The legacy "blob.bin" path, always probed as a fallback on read.
    private string LegacyPath(string key) => Path.Combine(FolderFor(key), c_legacyDataFile);

    // Last-resort fallback: the folder's only non-key, non-temp file — covers a per-call
    //  filename (e.g. an older MeasuredUtc-stamped name) that no longer matches the current
    //  DataPath/LegacyPath candidates. Returns null when none or ambiguous (more than one).
    private string? FindAnyDataFile(string key)
    {
        string dir = FolderFor(key);
        if (!Directory.Exists(dir))
            return null;

        string? found = null;
        foreach (var file in Directory.EnumerateFiles(dir))
        {
            string name = Path.GetFileName(file);
            if (name == c_keyFile || name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                continue;

            if (found is not null)
                return null;   // ambiguous — more than one candidate, refuse to guess
            found = file;
        }

        return found;
    }

    private string FolderFor(string key) => Path.Combine(m_root, SafeName(key));

    // Reads a key.txt tolerantly: returns null on any transient IO race or missing file.
    private static string? TryReadKey(string keyFile)
    {
        try
        {
            return File.Exists(keyFile) ? File.ReadAllText(keyFile) : null;
        }
        catch (IOException)
        {
            return null;   // mid-write / vanished — skip this round
        }
    }

    /// <summary>
    /// Maps an arbitrary key to a legal, human-readable, collision-free folder name:
    /// illegal chars → '_', plus an 8-hex FNV-1a suffix so two keys that sanitize to
    /// the same string still land in distinct folders.
    /// </summary>
    private static string SafeName(string key)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var sb = new StringBuilder(key.Length + 9);

        foreach (char ch in key)
            sb.Append(Array.IndexOf(invalid, ch) >= 0 ? '_' : ch);

        return $"{sb}_{Fnv1a(key):x8}";
    }

    // FNV-1a, 32-bit — stable across runs and machines. Shared by folder + mutex naming.
    private static uint Fnv1a(string s)
    {
        uint h = 2166136261u;
        foreach (char ch in s)
        {
            h ^= ch;
            h *= 16777619u;
        }
        return h;
    }

    #endregion
}
