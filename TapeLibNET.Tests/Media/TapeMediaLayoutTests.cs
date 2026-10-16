using TapeLibNET.Drive;
using TapeLibNET.Virtual;
using TapeLibNET.Media;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Media;


/// <summary>
/// Scan Media Phase 0: <see cref="TapeMediaLayout.Predict"/> and its agreement with
///  <see cref="TapeNavigator.ProduceNavigator"/>, which is now reimplemented on top of it.
/// </summary>
/// <remarks>
/// <para>
/// <b>The refactor's entire claim is that nothing changed</b>, so the decisive test is the one that runs
///  both paths over every drive profile and asserts they agree. The existing navigator suites are the
///  other witness, and must pass untouched.
/// </para>
/// <para>
/// The second claim is that the prediction needs NO MEDIA — which is what lets a UI state the expected
///  layout before a cartridge is inserted, and lets a scan that fails at its first read still report what
///  should have been there.
/// </para>
/// </remarks>
public class TapeMediaLayoutTests
{
    #region *** Test Data ***

    public static TheoryData<DriveProfile> AllProfiles =>
    [
        DriveProfile.Setmarks,
        DriveProfile.Partitions,
        DriveProfile.SeqFilemarks,
        DriveProfile.FilemarksOnly,
    ];

    #endregion

    #region *** (A) Agreement with the navigator factory ***

    /// <summary>
    /// The regression this phase exists to prevent: the predicted layout must name the navigator the
    ///  factory actually builds. Any divergence would have the scanner hopping the wrong mark type — and
    ///  on the setmark layouts that means counting the TOC's filemarks as set separators.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_NamesTheNavigatorTheFactoryBuilds(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        TapeMediaLayout layout = TapeMediaLayout.Predict(fixture.Drive);
        TapeNavigator? navigator = TapeNavigator.ProduceNavigator(fixture.Drive);

        Assert.NotNull(navigator);
        Assert.Equal(navigator!.GetType().Name, layout.NavigatorKind);
    }

    /// <summary>
    /// <see cref="TapeMediaLayout.UseSmks"/> must match the navigator's own setting, not merely the
    ///  drive's capability: the two differ on the filemark layouts, and the scanner hops by THIS value.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_UseSmks_MatchesTheNavigatorsOwnSetting(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        TapeMediaLayout layout = TapeMediaLayout.Predict(fixture.Drive);
        TapeNavigator navigator = TapeNavigator.ProduceNavigator(fixture.Drive)!;

        Assert.Equal(navigator.UseSmks, layout.UseSmks);
    }

    /// <summary>
    /// The <c>useTOCMark: false</c> branch: a sequential-filemark drive falls back to the plain filemark
    ///  layout. Rarely exercised in production, but the parameter exists and both paths must still agree.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_WithoutTocMark_StillAgreesWithTheFactory(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        TapeMediaLayout layout = TapeMediaLayout.Predict(fixture.Drive, useTOCMark: false);
        TapeNavigator navigator = TapeNavigator.ProduceNavigator(fixture.Drive, useTOCMark: false)!;

        Assert.Equal(navigator.GetType().Name, layout.NavigatorKind);
        Assert.Equal(navigator.UseSmks, layout.UseSmks);
        Assert.False(layout.HasTocMark);
    }

    #endregion

    #region *** (B) Per-profile shape ***

    /// <summary>
    /// The four profiles' layouts spelled out, so a future capability change that silently re-routes a
    ///  profile is caught here rather than in the scanner's walk.
    /// </summary>
    [Theory]
    [InlineData(DriveProfile.Setmarks,      nameof(TapeNavigatorTOCInSetWithSmks),            true,  false, false)]
    [InlineData(DriveProfile.Partitions,    nameof(TapeNavigatorTOCInPartition),              true,  true,  false)]
    [InlineData(DriveProfile.SeqFilemarks,  nameof(TapeNavigatorTOCInSetWithFmksAndTOCMark),  false, false, true)]
    [InlineData(DriveProfile.FilemarksOnly, nameof(TapeNavigatorTOCInSetWithFmks),            false, false, false)]
    public void Predict_PerProfile_HasTheExpectedShape(
        DriveProfile profile, string navigatorKind, bool useSmks, bool tocInPartition, bool hasTocMark)
    {
        using var fixture = new VirtualTapeFixture(profile);

        TapeMediaLayout layout = TapeMediaLayout.Predict(fixture.Drive);

        Assert.Equal(navigatorKind, layout.NavigatorKind);
        Assert.Equal(useSmks, layout.UseSmks);
        Assert.Equal(tocInPartition, layout.TocInPartition);
        Assert.Equal(hasTocMark, layout.HasTocMark);
        Assert.True(layout.MediaLoaded);
    }

    /// <summary>
    /// The fact that makes identification — not mark-hopping — the scanner's core (§2): on the filemark
    ///  layouts the set separator and the TOC delimiter are the SAME mark, so hopping alone can never tell
    ///  content from TOC.
    /// </summary>
    [Theory]
    [InlineData(DriveProfile.Setmarks,      false)]   // setmarks vs. filemarks — distinguishable by type
    [InlineData(DriveProfile.Partitions,    false)]   // the TOC is not even in this partition
    [InlineData(DriveProfile.SeqFilemarks,  true)]
    [InlineData(DriveProfile.FilemarksOnly, true)]
    public void Predict_SeparatorAmbiguousWithToc_OnlyOnTheFilemarkLayouts(
        DriveProfile profile, bool expected)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.Equal(expected, TapeMediaLayout.Predict(fixture.Drive).SeparatorAmbiguousWithToc);
    }

    #endregion

    #region *** (C) Purity, and prediction without media ***

    /// <summary>
    /// Prediction must work with NO cartridge — §2's whole diagnostic value rests on it. The partitioning
    ///  it cannot observe falls back to the single-partition FLOOR, and <see cref="TapeMediaLayout.MediaLoaded"/>
    ///  says so, rather than letting a caller mistake the floor for a finding.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_WithNoMediaLoaded_SucceedsAndFlagsTheFloor(DriveProfile profile)
    {
        var backend = VirtualTapeDriveBackend.CreateMemoryBacked(
            TestLoggerFactory.Default,
            VirtualTapeFixture.ProfileToCapabilities(profile),
            contentCapacity: VirtualTapeFixture.DefaultContentCapacity,
            initiatorPartitionCapacity: 0);

        using var drive = new TapeDrive(TestLoggerFactory.Default, backend);
        Assert.True(drive.ReopenDrive(0), "Failed to open virtual drive");

        // Deliberately NO ReloadMedia: this is the drive-open-but-empty state a UI sits in.
        Assert.False(drive.IsMediaLoaded);

        TapeMediaLayout layout = TapeMediaLayout.Predict(drive);

        Assert.False(layout.MediaLoaded);
        Assert.False(layout.TocInPartition);                            // the floor, never a guess
        Assert.False(string.IsNullOrWhiteSpace(layout.NavigatorKind));  // still a usable answer

        // The capability-driven half is fully knowable without a cartridge, so it must already be right.
        Assert.Equal(drive.SupportsSetmarks, layout.UseSmks);
    }

    /// <summary>
    /// Pure: prediction moves nothing and writes nothing. Asserted on the drive's own position and byte
    ///  counter, because the scan's read-only promise (SM-1) begins here.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_MovesNothingAndWritesNothing(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.True(fixture.Drive.Rewind());
        long blockBefore = fixture.Drive.CurrentBlock;
        long bytesBefore = fixture.Drive.ByteCounter;

        TapeMediaLayout.Predict(fixture.Drive);

        Assert.Equal(blockBefore, fixture.Drive.CurrentBlock);
        Assert.Equal(bytesBefore, fixture.Drive.ByteCounter);
    }

    /// <summary>Repeatable: the same drive yields an equal value every time (it is a record struct).</summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void Predict_IsRepeatable(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        Assert.Equal(TapeMediaLayout.Predict(fixture.Drive), TapeMediaLayout.Predict(fixture.Drive));
    }

    [Fact]
    public void Predict_NullDrive_Throws()
        => Assert.Throws<ArgumentNullException>(() => TapeMediaLayout.Predict(null!));

    #endregion

    #region *** (D) Display ***

    /// <summary>
    /// <see cref="TapeMediaLayout.ToString"/> reaches the scan map and the viewer, so it must name the
    ///  layout and the separator, and must flag a media-less prediction as provisional.
    /// </summary>
    [Theory]
    [MemberData(nameof(AllProfiles))]
    public void ToString_NamesTheLayoutAndTheSeparator(DriveProfile profile)
    {
        using var fixture = new VirtualTapeFixture(profile);

        TapeMediaLayout layout = TapeMediaLayout.Predict(fixture.Drive);
        string text = layout.ToString();

        Assert.Contains(layout.NavigatorKind, text, StringComparison.Ordinal);
        Assert.Contains(layout.UseSmks ? "setmark" : "filemark", text, StringComparison.Ordinal);
        Assert.DoesNotContain("without media", text, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_WithoutMedia_SaysSo()
    {
        var layout = new TapeMediaLayout(
            nameof(TapeNavigatorTOCInSetWithFmks), UseSmks: false,
            TocInPartition: false, HasTocMark: false, MediaLoaded: false);

        Assert.Contains("without media", layout.ToString(), StringComparison.Ordinal);
    }

    #endregion
}
