using TapeLibNET.Headers;

namespace TapeLibNET.Agents;

/// <summary>Action chosen by <see cref="ITapeSetNotifiable.OnSetAnomaly"/> when a set does not verify.</summary>
public enum SetAnomalyAction
{
    /// <summary>Stop the operation. The safe default for a notifiable that has not been taught otherwise.</summary>
    Abort,
    /// <summary>Authorize the remaining recovery stages — or, where none remain, the terminal action.</summary>
    Proceed,
}

/// <summary>Which recovery stage produced — or failed at — a <see cref="TapeSetAnomaly"/>.</summary>
/// <remarks>
/// A drift settled by the relative delta indicts a MARK; one that needed the re-navigation indicts the
///  TAIL. Different facts about the cartridge, so the summary must be able to tell them apart (SH-16).
/// </remarks>
public enum TapeSetAnomalyStage
{
    /// <summary>Detected on the first verification; no recovery attempted yet.</summary>
    Detected,
    /// <summary>Produced or settled by the relative-delta correction (SH-10).</summary>
    Delta,
    /// <summary>Produced or settled by the re-navigation from begin-of-content (SH-14, Step 5).</summary>
    Renavigated,
}

/// <summary>
/// A set that cannot be used as the TOC describes it — the payload of both set-level callbacks.
/// </summary>
/// <remarks>
/// Carries DESCRIPTIONS, not merely indices: a prompt must name WHICH set, which is the same reason
///  <see cref="TapeSetHeader.Description"/> was admitted to the record at all.
///  <see cref="ActualDescription"/> comes from <see cref="TapeSetHeader.DisplayName"/> and is therefore
///  never empty — it synthesizes a name from the indices when the header carries none.
/// </remarks>
public readonly record struct TapeSetAnomaly(
    TapeSetHeaderVerdict Verdict,
    TapeSetAnomalyStage Stage,
    int SetIndex,
    int ExpectedVolumeSetIndex,
    int ActualVolumeSetIndex,
    string ExpectedDescription,
    string ActualDescription,
    int ExpectedVolume,
    int ActualVolume,
    bool IsDestructive,
    bool CanAttemptRecovery,
    TapeResult Diagnosis)
{
    /// <summary>
    /// A one-line summary suitable for a log entry or the head of a prompt.
    /// </summary>
    /// <remarks>
    /// Descriptions in <c>&gt;…&lt;</c>, as everywhere else in TapeNET. The ALT index is deliberately
    ///  absent: deriving it needs the TOC, which this record does not carry (and must not, being a
    ///  by-value payload). The service-side handler adds it — see
    ///  <see cref="Services.ServiceOperationProgressHandler.DescribeSet"/>.
    /// </remarks>
    public override string ToString() =>
        $"Set #{SetIndex} anomaly ({Verdict}, {Stage}): expected on-volume set {ExpectedVolumeSetIndex} " +
        $">{ExpectedDescription}<, found {ActualVolumeSetIndex} >{ActualDescription}<";
}