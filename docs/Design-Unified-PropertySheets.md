# Unified Property Sheets — TapeConNET and TapeWinNET share one source

**Idea:** the library decides **what** is shown about a drive, a medium, a set or a calibration run; each app decides
only **how**. `TapeServiceBase` returns a list of `ServiceProperty(Label, Value, Level, Ratio)`; the CLI logs each
as `Label: Value`, the WPF view model adds each as a `PropertyItem`. A new fact is one line in one file and appears in
both apps.

**Why not override the virtual `Log*` hooks in WPF:** that would make the view model a log consumer and parse text
back into rows. Data out, rendering in the app is the clean split; the virtual hooks stay for CLI-specific tweaks.

---

## 1. New file

`TapeLibNET/Services/TapeServiceBase.Properties.cs` — `ServiceProperty`, `DeviceModel`, `DescribeDrive`,
`DescribeMedia`, `DescribeSet`, `DescribeCalibrationRun`, the shared capacity block, `LogProperties`.

Labels are **Title Case** (the WPF pane's style), so the WPF UI does not change; CLI lines change from
`Device name: …` to `Device Name: …`. Times are local in both apps (the WPF calibration pane showed UTC before).

**Merged along the way** — both apps now show everything either showed:

| Fact | Was only in |
|---|---|
| Compression (set) | WPF |
| Set Index, Total File Size | WPF |
| TOC Placement with file path / recovered flag | WPF |
| Run trail (resumable, progress, …) | WPF |
| EW Captured (the old TODO) | neither — `TapeCalibrationMediaInfo.EarlyWarningCaptured` |
| Data Format, TOC Format, Written By (Phase 7) | — |

---

## 2. `TapeServiceBase.List.cs` — three hooks, whole methods

```csharp
    /// <summary>
    /// Logs drive hardware properties (device identity, capabilities, block sizes). Called by
    ///  <see cref="ListContentsAsync"/> when <see cref="ListDepth.Drive"/> is set. Content from
    ///  <see cref="DescribeDrive"/> — the same rows TapeWinNET shows in its drive pane.
    /// </summary>
    protected virtual void LogDriveInfo()
    {
        if (_drive is null) return;
        LogProperties(DescribeDrive());
    }
```

```csharp
    /// <summary>
    /// Logs full media information during <see cref="ListContentsAsync"/>. Content from
    ///  <see cref="DescribeMedia"/> — the same rows TapeWinNET shows in its media pane.
    /// </summary>
    protected virtual void LogMediaInfoFull()
    {
        if (_drive is null || _toc is null) return;
        LogProperties(DescribeMedia());
    }
```

```csharp
    /// <summary>
    /// Logs per-set detail during <see cref="ListContentsAsync"/>. Content from <see cref="DescribeSet"/> — the same
    ///  rows TapeWinNET shows in its backup-set pane.
    /// </summary>
    protected virtual void LogCurrentSetInfo()
    {
        if (_drive is null || _toc is not TapeTOC toc) return;
        LogProperties(DescribeSet(toc.CurrentSetIndex));
    }
```

`LogBackupSetsTable` stays: a fixed-width text table is CLI-specific (WPF has its `BackupSetListItem` grid).
`LogMediaInfo` (the short post-load summary) and `LogTOCInfo` stay too — they are load-time summaries, not sheets.

## 3. `TapeServiceBase.cs` — `LogCalibrationInfo`, whole method

```csharp
    /// <summary>
    /// Logs everything the loaded calibration run header reveals — and the run trail once an Inspect has read it.
    ///  Content from <see cref="DescribeCalibrationRun"/>, the same rows as TapeWinNET's calibration pane.
    /// </summary>
    protected virtual void LogCalibrationInfo()
    {
        if (_loadedHeader is not TapeCalibrationHeader)
            return;
        LogProperties(DescribeCalibrationRun());
    }
```

---

## 4. `MainViewModel.cs`

### 4.1 Add — the one mapping from library to pane

```csharp
    /// <summary>
    /// Renders a library property as a pane row. The writable-capacity row carries a fill ratio, which WPF turns into
    ///  its own traffic-light highlight; every other row keeps the level the library assigned.
    /// </summary>
    private static PropertyItem ToPropertyItem(ServiceProperty p)
        => new(p.Label, p.Value,
            highlightLevel: p.Ratio is { } ratio ? WarningLevelHelper.Translate(ratio) : p.Level);

    /// <summary>Appends <paramref name="properties"/> to <paramref name="target"/>.</summary>
    private static void ShowProperties(ObservableCollection<PropertyItem> target, IEnumerable<ServiceProperty> properties)
    {
        foreach (var p in properties)
            target.Add(ToPropertyItem(p));
    }
```

`highlightLevel: … : p.Level` compiles as is if TapeWinNET's `WarningLevel` aliases `ServiceReportLevel` (as
TapeConNET's does). If it is a separate enum, map it there — one switch, in this one place.

### 4.2 Delete `AddCapacityProperties`

Replaced by the library's capacity block.

### 4.3 `LoadDriveInfo` — whole method

```csharp
    private void LoadDriveInfo()
    {
        PropertyList.Clear();
        FileList = [];
        BackupSetList.Clear();
        ContentType = ContentPaneType.DriveInfo;
        PropertiesHeader = "Drive Properties";
        TableHeader = ""; // Not visible for drive
        UsageBar.Clear();

        ShowProperties(PropertyList, _tapeService.DescribeDrive());

        StatusMessage = "Drive information displayed";

        // Append remote connection info section when a remote host is active (§2.6)
        if (IsRemoteConnected)
            AppendRemoteConnectionInfo();
    }
```

### 4.4 `LoadCalibrationInfo` — whole method

```csharp
    /// <summary>
    /// Populates the calibration panes for a Calibration Cartridge tree node: the drive / media context in the upper
    ///  Properties pane, the run header and run trail in the lower <see cref="CalibrationPropertyList"/> pane (in place
    ///  of the backup-set / file table). Both from the service's property sheets — the same rows the CLI lists.
    /// </summary>
    private void LoadCalibrationInfo()
    {
        PropertyList.Clear();
        CalibrationPropertyList.Clear();
        BackupSetList.Clear();
        FileList = [];
        ContentType = ContentPaneType.CalibrationInfo;
        PropertiesHeader = "Media Properties";
        TableHeader = "Calibration Details";
        UsageBar.Clear();

        if (_tapeService.CalibrationHeader is null)
        {
            ShowProperties(PropertyList, _tapeService.DescribeCalibrationRun());   // the "no data" status row
            StatusMessage = "No calibration data available";
            return;
        }

        // Upper pane: the drive as context — a calibration cartridge is media without a backup TOC.
        ShowProperties(PropertyList, _tapeService.DescribeDrive(includeCapabilities: false));

        // Lower pane: the run header, and the run trail once "Inspect Media" has read it.
        ShowProperties(CalibrationPropertyList, _tapeService.DescribeCalibrationRun());

        StatusMessage = "Calibration cartridge (no backup TOC)";
    }
```

### 4.5 `LoadMediaInfo` — whole method

```csharp
    private void LoadMediaInfo()
    {
        PropertyList.Clear();
        BackupSetList.Clear();
        FileList = [];
        ContentType = ContentPaneType.MediaInfo;
        PropertiesHeader = "Media Properties";

        ShowProperties(PropertyList, _tapeService.DescribeMedia());

        if (_tapeService.TOC is not { } toc)
        {
            TableHeader = "Backup Sets";
            StatusMessage = "No media information available";
            return;
        }

        // Populate backup sets table (newest-first, with checked-state sync)
        _tocView ??= new TOCView(toc);
        TableHeader = $"Backup Sets ({toc.Count})";
        foreach (var item in _tocView.BuildBackupSetItemList())
            BackupSetList.Add(item);

        // Refresh the header "select all" checkbox — items may carry partial
        //  (null) checked state from per-file selections in a previous visit.
        OnPropertyChanged(nameof(AreAllBackupSetsChecked));

        var mediaName = toc.Description ?? "Volume #" + toc.Volume;
        StatusMessage = _tapeService.TOCIsFrom is TOCSource.File
            ? $"\u26a0 TOC: {Path.GetFileName(_tapeService.TOCFilePath)} | Media: {mediaName} - {toc.Count} backup set(s)"
            : $"Media: {mediaName} - {toc.Count} backup set(s)";
        if (_tapeService.TOCIsFrom is TOCSource.Recovered)
            StatusMessage += " (using recovered TOC)";

        // Build the media usage bar from the current-volume sets
        UsageBar.Rebuild();
    }
```

### 4.6 `LoadBackupSetInfo` — whole method

```csharp
    private void LoadBackupSetInfo(int setIndex)
    {
        if (_tapeService.TOC is not { } toc || _tocView is null)
            return;

        PropertyList.Clear();
        BackupSetList.Clear();
        ClearFileFilter();
        FileList = [];
        ContentType = ContentPaneType.BackupSetInfo;
        TableHeader = "Files"; // Reset early to avoid showing stale backup-set header
        UsageBar.Clear();

        try
        {
            int altIndex = toc.SetIndexToAlt(setIndex);
            PropertiesHeader = $"Backup Set #{setIndex} | {altIndex} Properties";

            // Also makes setIndex the TOC's current set — the view below relies on that.
            ShowProperties(PropertyList, _tapeService.DescribeSet(setIndex));

            // Get or create the BackupSetView (handles incremental file resolution,
            //  caching, and checked-state migration)
            var setView = _tocView.GetOrCreate(setIndex, ShowIncrementalSets);
            _currentSetView = setView;

            // Build the display list (creates FileListItem proxies as needed)
            FileList = setView.BuildFileItemList(ShowFullPathname);
            NotifyFilterPropertiesChanged();

            StatusMessage = $"Set #{setIndex} | #{altIndex}: {FileTotalCount} file(s)";

            // Queue a pending filter restore if the user previously filtered this set
            PendingFilterRestore = setView.SavedFilterState;
        }
        catch (Exception ex)
        {
            LogErr($"Error loading backup set info: {ex.Message}");
            StatusMessage = "Error loading backup set information";
        }
    }
```

`using TapeLibNET.Compression;` may become unused in `MainViewModel.cs` — IDE0005 will say.

---

## 5. Tests to touch

| Where | Change |
|---|---|
| `ServiceMixedMediaTests.TocFormat_IsReported_BeforeAndAfterTheUpgrade` | `"TOC format: 2.1"` → `"TOC Format: 2.1"` (the listing now comes from `DescribeMedia`) |
| Any CLI test matching a lower-case list label (`"Device name:"`, `"Block size:"`, …) | Title Case |

Find them:

```powershell
git grep -n -E "ContainsMessage\(\"[A-Z][a-z]+ [a-z]+[^\"]*:" -- TapeLibNET.Tests TapeConNET.Tests
```

**Worth adding** — pins the shared source, so the apps cannot drift apart again:

```csharp
    /// <summary>Every property the media sheet holds reaches the CLI listing, one line each.</summary>
    [Fact]
    public async Task List_Media_LogsEveryDescribedProperty()
    {
        using var media = new TempVirtualMedia(withInitiator: false, ContentCapacity, InitiatorCapacity);
        var (svc, host) = await OpenAndFormatAsync(media);
        using (svc)
        {
            var list = await svc.ListContentsAsync(new ListRequest(Depth: ListDepth.Media));
            Assert.True(list.Success, list.Message);

            foreach (var p in svc.DescribeMedia())
                Assert.True(host.ContainsMessage($"{p.Label}: {p.Value}"), $"missing '{p.Label}'\n{host.DumpReports()}");
        }
    }
```

(in `TapeLibNET.Tests/Services/`, e.g. a new `ServicePropertySheetTests.cs`).

---

## 6. Design note — where the line runs

| Library (`Describe*`) | App |
|---|---|
| which facts, their order, labels, formatting, emphasis | headers, tables, status bar, usage bar, remote-connection block |
| `Ratio` for capacity | how a ratio becomes a colour (WPF) — or nothing (CLI) |
| pure, no I/O, no lock | when to refresh |

Rule for the future: a fact both apps might show → a row in `Describe*`. A fact only one app can show (remote
connection, set-table columns) → stays in that app.
