namespace TapeLibNET.Format;

/// <summary>
/// Kind registry (Design-Format-v2 §4.3). High byte = family: 01 TOC, 02 content, 03 headers, 0F host-side.
/// The top bit (<see cref="TapeFormat.SkippableKindBit"/>) is never set on a kind of this registry.
/// </summary>
public enum TapeRecordKind : ushort
{
    /// <summary>TOC header - TOC stream.</summary>
    TocHeader = 0x0101,

    /// <summary>TOC set - TOC stream.</summary>
    TocSet = 0x0102,

    /// <summary>TOC file batch - TOC stream.</summary>
    TocFileBatch = 0x0103,

    /// <summary>TOC end - TOC stream.</summary>
    TocEnd = 0x0104,

    /// <summary>File header - inline frame before each file body.</summary>
    FileHeader = 0x0201,

    /// <summary>Media header - block frame.</summary>
    MediaHeader = 0x0301,

    /// <summary>Set header - block frame.</summary>
    SetHeader = 0x0302,

    /// <summary>Calibration run header - block frame.</summary>
    CalibrationRunHeader = 0x0303,

    /// <summary>Calibration checkpoint - block frame.</summary>
    CalibrationCheckpoint = 0x0304,

    /// <summary>Virtual media state - host metadata file, not on tape.</summary>
    VirtualMediaState = 0x0F01,
}
