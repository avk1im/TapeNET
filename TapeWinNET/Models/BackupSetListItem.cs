using System.ComponentModel;
using System.Windows.Media;
using TapeLibNET.Compression;
using TapeLibNET.Toc;
using Windows.Win32.System.SystemServices; // for Helpers

namespace TapeWinNET.Models;

/// <summary>
/// Represents a backup set item for display in the backup sets ListView.
///  Filter and checked counts are externally managed and pushed in via
///  <see cref="FilteredFileCount"/> and <see cref="CheckedFileCount"/>.
/// </summary>
/// <remarks>
/// <b>Locked sets (Design-Encryption §11.1.1, §11.1.9).</b> An encrypted set without its key answers only its stub; every
///  other member of <see cref="TapeSetTOC"/> throws. Each property here therefore asks <see cref="IsLocked"/> first and
///  shows <see cref="TapeSetTOC.DisplayName"/>, a dash or zero instead — WPF evaluates every bound column, so one
///  unguarded getter would take the whole list down. The set can unlock (or lock again) while the item lives:
///  <see cref="Refresh"/> re-reads everything.
/// </remarks>
public class BackupSetListItem(TapeSetTOC setTOC, int setIndex, int altIndex, bool isOnCurrentVolume)
    : INotifyPropertyChanged
{
    private const string Dash = "\u2014";

    private readonly TapeSetTOC _setTOC = setTOC;
    private readonly int _setIndex = setIndex;
    private readonly int _altIndex = altIndex;
    private readonly bool _isOnCurrentVolume = isOnCurrentVolume;

    /// <summary>The plain backup-set icon (shared, frozen).</summary>
    public static ImageSource? DefaultIcon => TapeIcons.BackupSetIconSmall;

    /// <summary>The icon by state: plain, locked (padlock) or unlocked encrypted (open padlock).</summary>
    public ImageSource? Icon => !_setTOC.IsEncrypted ? TapeIcons.BackupSetIconSmall
        : _setTOC.IsLocked ? TapeIcons.LockedBackupSetIconSmall
        : TapeIcons.UnlockedBackupSetIconSmall;

    /// <summary>Encrypted and without its key: only the stub is known.</summary>
    public bool IsLocked => _setTOC.IsLocked;

    /// <summary>Encrypted, locked or not.</summary>
    public bool IsEncrypted => _setTOC.IsEncrypted;

    /// <summary>
    /// Whether this backup set resides on the currently loaded volume.
    /// Used to dim icons of sets from previous volumes.
    /// </summary>
    public bool IsOnCurrentVolume => _isOnCurrentVolume;

    /// <summary>Standard set index (1-based, from oldest to newest)</summary>
    public int SetIndex => _setIndex;

    /// <summary>Alternative index (0 = latest, negative = older)</summary>
    public int AltIndex => _altIndex;

    /// <summary>Display format: "1 | -2" for set 1 of 3</summary>
    public string IndexDisplay => $"#{SetIndex} | {AltIndex}";

    /// <summary>The set's description, or the padlock placeholder while locked.</summary>
    public string Description => IsLocked ? $"\U0001F512 {_setTOC.DisplayName}"
        : string.IsNullOrEmpty(_setTOC.Description) ? "(unnamed)" : _setTOC.Description;

    /// <summary>File count; 0 while locked (the count is hidden — see <see cref="FileCountFormatted"/>).</summary>
    public int FileCount => IsLocked ? 0 : _setTOC.Count;

    private int? _filteredFileCount;

    /// <summary>
    /// Number of files after filtering, or <c>null</c> when no filter narrows results.
    ///  Set externally (e.g. by <c>RestoreViewModel</c>) after async filter computation.
    /// </summary>
    public int? FilteredFileCount
    {
        get => _filteredFileCount;
        set
        {
            if (_filteredFileCount != value)
            {
                _filteredFileCount = value;
                Notify(nameof(FilteredFileCount));
                Notify(nameof(FilteredFileCountFormatted));
                Notify(nameof(FileCountFormatted));
            }
        }
    }

    /// <summary>Display format for the "Filtered" column: filtered count or "—" when no filter is active.</summary>
    public string FilteredFileCountFormatted => _filteredFileCount is int count ? count.ToString("N0") : Dash;

    private int? _checkedFileCount;

    /// <summary>
    /// Number of checked (selected) files, or <c>null</c> when nothing is checked.
    ///  Set externally (e.g. by <c>MainViewModel.ToggleBackupSetChecked</c>) after checked-state changes.
    /// </summary>
    public int? CheckedFileCount
    {
        get => _checkedFileCount;
        set
        {
            if (_checkedFileCount != value)
            {
                _checkedFileCount = value;
                Notify(nameof(CheckedFileCount));
                Notify(nameof(SelectedFileCountFormatted));
            }
        }
    }

    /// <summary>Display format for the "Selected" column: checked count or "—" when nothing is checked.</summary>
    public string SelectedFileCountFormatted => _checkedFileCount is int count and > 0 ? count.ToString("N0") : Dash;

    /// <summary>Display format: plain count, "total → filtered" when a filter narrows results, "—" while locked.</summary>
    public string FileCountFormatted => IsLocked ? Dash
        : _filteredFileCount is int filtered && filtered != _setTOC.Count
            ? $"{_setTOC.Count:N0} \u2192 {filtered:N0}"
            : _setTOC.Count.ToString("N0");

    /// <summary>Creation time (local); <see cref="DateTime.MinValue"/> while locked.</summary>
    public DateTime CreatedOn => IsLocked ? DateTime.MinValue : _setTOC.CreationTime.ToLocalTime();

    public string CreatedOnFormatted => IsLocked ? Dash : _setTOC.CreationTime.ToLocalTime().ToString("G");

    /// <summary>Whether the set is incremental; <see langword="false"/> while locked (unknown).</summary>
    public bool IsIncremental => !IsLocked && _setTOC.Incremental;

    public string IncrementalDisplay => IsLocked ? Dash : _setTOC.Incremental ? "Yes" : "No";

    public string BlockSize => IsLocked ? Dash : Helpers.BytesToString(_setTOC.BlockSize);

    public string HashAlgorithm => IsLocked ? Dash : _setTOC.HashAlgorithm.ToString();

    /// <summary>Human-readable compression mode and level for this set.</summary>
    public string Compression => IsLocked ? Dash
        : CompressionPreset.DisplayName(_setTOC.Compression, _setTOC.CompressionLevel);

    public int Volume => _setTOC.Volume;

    public bool ContinuedFromPrevVolume => _setTOC.ContinuedFromPrevVolume;

    /// <summary>Total logical file size (sum of all file lengths); 0 while locked (hidden).</summary>
    public long TotalSize => IsLocked ? 0L : _setTOC.TotalFileSize;

    /// <summary>Total logical file size, formatted; "—" while locked.</summary>
    public string TotalSizeFormatted => IsLocked ? Dash : Helpers.BytesToString(TotalSize);

    /// <summary>
    /// Total size occupied on tape — known from the stub even while locked.
    /// </summary>
    public long SizeOnTape => _setTOC.ComputeTotalFileSizeOnTape(IsLocked ? 0u : _setTOC.BlockSize);

    /// <summary>Total size occupied on tape, formatted.</summary>
    public string SizeOnTapeFormatted => Helpers.BytesToString(SizeOnTape);

    public TapeSetTOC SetTOC => _setTOC;

    private bool? _isCheckedForRestore = false;

    /// <summary>
    /// Whether this backup set is checked for restore/validate/verify operations.
    /// Tri-state: <see langword="true"/> = all files checked, <see langword="false"/> = unchecked,
    ///  <c>null</c> = partially checked (some files selected). Sets that have never
    ///  been navigated to can only be <see langword="false"/> or <see langword="true"/>; the indeterminate
    ///  state arises only when a <see cref="BackupSetView"/> with partial file selection exists.
    /// </summary>
    public bool? IsCheckedForRestore
    {
        get => _isCheckedForRestore;
        set
        {
            if (_isCheckedForRestore != value)
            {
                _isCheckedForRestore = value;
                Notify(nameof(IsCheckedForRestore));
            }
        }
    }

    /// <summary>
    /// Whether this item has a partial file selection (some but not all files checked).
    ///  Never for a locked set: its files are unknown.
    /// </summary>
    public bool HasPartialSelection => !IsLocked && _checkedFileCount is > 0 and var c && c < FileCount;

    /// <summary>The set was unlocked or locked: re-read every property.</summary>
    public void Refresh() => Notify(string.Empty);

    private void Notify(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}
