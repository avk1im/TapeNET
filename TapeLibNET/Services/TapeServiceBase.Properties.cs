using TapeLibNET.Compression;
using TapeLibNET.Headers;
using TapeLibNET.Toc;

using Windows.Win32.System.SystemServices; // Helpers

namespace TapeLibNET.Services;


/// <summary>
/// One labelled fact about the drive, the media, a backup set or a calibration run — the single source both apps
///  render: TapeConNET as a log line, TapeWinNET as a property-pane row.
/// </summary>
/// <param name="Label">Display label, Title Case ("Block Size").</param>
/// <param name="Value">Display value, already formatted.</param>
/// <param name="Level">
/// Emphasis: <see cref="ServiceReportLevel.None"/> for a plain fact, <see cref="ServiceReportLevel.Warning"/> for one the
///  user should notice (TOC from a file, legacy TOC, early warning reached).
/// </param>
/// <param name="Ratio">
/// Optional fill ratio in 0..1 for a capacity figure. A graphical host may derive its own highlight from it; a text
///  host ignores it.
/// </param>
public readonly record struct ServiceProperty(
    string Label,
    string Value,
    ServiceReportLevel Level = ServiceReportLevel.None,
    double? Ratio = null);


public partial class TapeServiceBase
{
    #region Property sheets — one source for every app

    // ── Property sheets ───────────────────────────────────────────────────────
    //  WHAT is shown about a drive, a medium, a set or a calibration run is decided here, once. HOW it is shown is
    //   the app's business: the List hooks log each property as a sub-entry, the WPF view model adds each one as a
    //   PropertyItem. A new fact is added here and appears in both apps.
    //
    //  Pure reads of in-memory state, no tape I/O and no lock — callable from the UI thread between operations,
    //   exactly as the view model read the same properties before. DescribeSet moves TOC.CurrentSetIndex, as the
    //   callers already did, because the volume-continuation flags are relative to the current set.

    /// <summary>The drive model ("vendor product rev X"), or empty when the drive reports none.</summary>
    public string DeviceModel
    {
        get
        {
            string model = DeviceVendor;
            if (!string.IsNullOrEmpty(DeviceProduct))
                model += $" {DeviceProduct}";
            if (!string.IsNullOrEmpty(DeviceRevision))
                model += $" rev {DeviceRevision}";
            return model.Trim();
        }
    }

    /// <summary>
    /// The drive's identity, capabilities and — with media loaded — partition count and capacity.
    /// </summary>
    /// <param name="includeCapabilities">
    /// <see langword="false"/> omits capabilities and block sizes — for panes where the drive is context, not subject
    ///  (the calibration cartridge).
    /// </param>
    public IReadOnlyList<ServiceProperty> DescribeDrive(bool includeCapabilities = true)
    {
        var props = new List<ServiceProperty>
        {
            new("Device Name", DeviceName),
        };
        if (DeviceModel is { Length: > 0 } model)
            props.Add(new("Device Model", model));
        props.Add(new("Drive Open", YesNo(IsDriveOpen)));

        if (!IsDriveOpen)
            return props;

        if (includeCapabilities)
        {
            props.Add(new("Supports Multiple Partitions", YesNo(SupportsInitiatorPartition)));
            props.Add(new("Supports Setmarks", YesNo(SupportsSetmarks)));
            props.Add(new("Supports Sequential Filemarks", YesNo(SupportsSeqFilemarks)));
            props.Add(new("Block Size (Min)", Helpers.BytesToString(MinimumBlockSize)));
            props.Add(new("Block Size (Default)", Helpers.BytesToString(DefaultBlockSize)));
            props.Add(new("Block Size (Max)", Helpers.BytesToString(MaximumBlockSize)));
        }

        props.Add(new("Media Loaded", YesNo(IsMediaLoaded)));
        if (IsMediaLoaded)
        {
            props.Add(new("Partition Count", PartitionCount.ToString()));
            AddCapacity(props);
        }
        return props;
    }

    /// <summary>
    /// The loaded medium as its TOC describes it, with capacity and TOC provenance. A single "Status" row when no TOC
    ///  is loaded.
    /// </summary>
    public IReadOnlyList<ServiceProperty> DescribeMedia()
    {
        if (_toc is not TapeTOC toc)
            return [new("Status", "No TOC available", ServiceReportLevel.Warning)];

        var props = new List<ServiceProperty>
        {
            new("Description", toc.Description ?? "(unnamed)"),
        };
        if (toc.MediaId != Guid.Empty)
            props.Add(new("Media ID", toc.MediaId.ToString()));
        props.Add(new("Created On", toc.CreationTime.ToLocalTime().ToString("G")));
        props.Add(new("Last Saved", toc.LastSaveTime.ToLocalTime().ToString("G")));
        props.Add(new("TOC Format", DescribeTocFormat(toc),
            toc.LoadedFromLegacy ? ServiceReportLevel.Warning : ServiceReportLevel.None));
        if (!string.IsNullOrEmpty(toc.WrittenBy))
            props.Add(new("Written By", toc.WrittenBy));
        props.Add(new("Backup Sets", toc.Count.ToString()));
        props.Add(new("Used", Helpers.BytesToStringLong(Used)));
        AddCapacity(props);

        // Where the TOC came from matters as much as where it lives: a TOC from a file or a scan was not located by
        //  the navigator, and may be older than the cartridge.
        string placement = TOCIsFrom is TOCSource.File
            ? $"File: {TOCFilePath}"
            : (HasInitiatorPartition ? "Partition" : "Set")
                + (TOCIsFrom is TOCSource.Recovered ? " (recovered TOC)" : string.Empty);
        props.Add(new("TOC Placement", placement,
            TOCIsFrom is TOCSource.File or TOCSource.Recovered ? ServiceReportLevel.Warning : ServiceReportLevel.None));

        props.Add(new("Volume", $"#{toc.Volume}"));
        props.Add(new("Continued on Next Volume", YesNo(toc.ContinuedOnNextVolume)));
        return props;
    }

    /// <summary>One backup set. Makes it the TOC's current set (the continuation flags are relative to it).</summary>
    /// <param name="setIndex">Standard (1-based) set index.</param>
    public IReadOnlyList<ServiceProperty> DescribeSet(int setIndex)
    {
        if (_toc is not TapeTOC toc)
            return [];

        toc.CurrentSetIndex = setIndex;
        var set = toc.CurrentSetTOC;

        return
        [
            new("Description", set.Description ?? "(unnamed)"),
            new("Set Index", $"#{setIndex} | {toc.SetIndexToAlt(setIndex)}"),
            new("Files", set.Count.ToString("N0")),
            new("Total File Size", Helpers.BytesToStringLong(set.Sum(tfi => tfi.FileDescr.Length))),
            new("Total File Size on Media",
                Helpers.BytesToStringLong(set.ComputeTotalFileSizeOnTape(_drive?.DefaultBlockSize ?? 0))),
            new("Created On", set.CreationTime.ToLocalTime().ToString("G")),
            new("Last Saved", set.LastSaveTime.ToLocalTime().ToString("G")),
            new("Block Size", Helpers.BytesToStringLong(set.BlockSize)),
            new("Hash Algorithm", set.HashAlgorithm.ToString()),
            new("Compression", CompressionPreset.DisplayName(set.Compression, set.CompressionLevel)),
            new("Data Format", DescribeDataFormat(set)),
            new("Incremental", YesNo(set.Incremental)),
            new("Volume", $"#{set.Volume}"),
            new("Continued from Previous Volume",
                toc.IsCurrentSetContFromPrevVolume ? "Yes, directly"
                : toc.IsCurrentSetContFromPrevVolumeInc ? "Yes, incrementally"
                : "No"),
            new("Continued on Next Volume", YesNo(toc.IsCurrentSetContOnNextVolume)),
        ];
    }

    /// <summary>
    /// Everything the calibration run header reveals, plus the checkpoint-derived run state once an Inspect has read
    ///  it (<see cref="CalibrationInfo"/>). A single "Status" row when the medium is not a calibration cartridge.
    /// </summary>
    public IReadOnlyList<ServiceProperty> DescribeCalibrationRun()
    {
        if (CalibrationHeader is not TapeCalibrationHeader cal)
            return [new("Status", "No calibration data available", ServiceReportLevel.Warning)];

        var plan = cal.Plan;
        var props = new List<ServiceProperty>
        {
            new("Status", "Calibration cartridge (no backup TOC)"),
            new("Profile Key", cal.ProfileKey),
            new("Run ID", cal.RunId.ToString("N")),
            new("Started", cal.StartedUtc.ToLocalTime().ToString("G")),
            new("Reported Capacity at BOM", Helpers.BytesToStringLong(cal.CapacityReportedAtBom)),
            new("Planned Samples",
                $"{plan.SampleCount:N0} (body {plan.BodySampleCount:N0}, tail {plan.TailSampleCount:N0})"),
            new("Planned Checkpoints", plan.NumCheckpoints.ToString("N0")),
            new("Run Block Size", Helpers.BytesToStringLong(cal.RunBlockSize)),
        };

        if (CalibrationInfo is { } info)
        {
            props.Add(new("Resumable", YesNo(info.IsResumable),
                info.IsResumable ? ServiceReportLevel.None : ServiceReportLevel.Warning));
            props.Add(new("Appears Complete", YesNo(info.AppearsComplete)));
            props.Add(new("Checkpointed", Helpers.BytesToStringLong(info.CheckpointedBytes)));
            props.Add(new("Progress", $"{info.ProgressFraction:P0}"));
            props.Add(new("EW Captured", YesNo(info.EarlyWarningCaptured)));
        }
        else
        {
            props.Add(new("Run Trail", "Not read yet — inspect the media to read its checkpoints",
                ServiceReportLevel.Info));
        }
        return props;
    }

    /// <summary>
    /// The capacity block (Design-RemainingAndEw §5.1): the driver's optimistic REPORTED figures beside our corrected
    ///  ESTIMATES, each on its own axis, then the WRITABLE space — the figure a backup is planned against — and the
    ///  provenance of the estimate.
    /// </summary>
    private void AddCapacity(List<ServiceProperty> props)
    {
        static string pair(long reported, long estimated)
            => $"{Helpers.BytesToStringLong(reported)} / {Helpers.BytesToStringLong(estimated)}";

        long estimatedCapacity = EstimatedCapacity;
        props.Add(new("Capacity Reported / Estimated", pair(Capacity, estimatedCapacity)));
        props.Add(new("Remaining Reported / Estimated", pair(ReportedContentRemaining, EstimatedContentRemaining)));
        props.Add(new("Writable", Helpers.BytesToStringLong(WritableRemaining),
            Ratio: estimatedCapacity > 0 ? (double)WritableRemaining / estimatedCapacity : null));
        props.Add(new("Estimation By", RemainingEstimationSource,
            IsEarlyWarning ? ServiceReportLevel.Warning : ServiceReportLevel.None));
    }

    /// <summary>Logs each property as a sub-entry: "Label: Value", at its level (plain facts at Info).</summary>
    protected void LogProperties(IEnumerable<ServiceProperty> properties)
    {
        foreach (var p in properties)
        {
            var level = p.Level == ServiceReportLevel.None ? ServiceReportLevel.Info : p.Level;
            _host.Report(level, $"{p.Label}: {p.Value}", isSubEntry: true);
        }
    }

    private static string YesNo(bool value) => value ? "Yes" : "No";

    #endregion
}
