using TapeLibNET.Agents;
using TapeLibNET.Media;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Toc;

/// <summary>
/// Design-TocReserve §2: <see cref="TapeNavigator.TocReserveFor"/> per layout — no reserve with a TOC partition; on the
///  in-set layouts the floor, or both copies + extra structure + marks; the override wins; the service's prediction agrees.
/// </summary>
public class TocReserveForTests
{
    private const long MiB = 1024L * 1024;

    public static TheoryData<DriveProfile> InSetProfiles =>
        [DriveProfile.Setmarks, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly];

    private static (VirtualTapeFixture Fixture, TapeAgentBase Agent) Create(DriveProfile profile,
        long capacity = VirtualTapeFixture.DefaultContentCapacity)
    {
        var fixture = new VirtualTapeFixture(profile, contentCapacity: capacity);
        return (fixture, new TapeAgentBase(fixture.Drive, fixture.TOC));
    }

    [Fact]
    public void Partitions_NoReserve_EvenWithOverride()
    {
        var (fixture, agent) = Create(DriveProfile.Partitions);
        using (fixture) using (agent)
        {
            Assert.Equal(0L, agent.Navigator.TocReserveFor(50 * MiB));
            agent.Navigator.TocReserveOverride = 8 * MiB;
            Assert.Equal(0L, agent.Navigator.TocReserveFor(50 * MiB));
            Assert.Equal(0L, TapeNavigator.PredictTocReserve(fixture.Drive, 50 * MiB));
        }
    }

    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void InSet_SmallToc_IsTheFloor(DriveProfile profile)
    {
        var (fixture, agent) = Create(profile);     // 128 MB: the quarter cap (32 MB) is above the 4 MiB floor
        using (fixture) using (agent)
        {
            Assert.Equal(TapeNavigator.TocReserveFloor, agent.Navigator.TocReserveFor(0));
            Assert.Equal(TapeNavigator.TocReserveFloor, agent.Navigator.TocReserveFor(100 * 1024));
            Assert.Equal(TapeNavigator.TocReserveFloor, agent.Navigator.TOCCapacity);     // the alias: an empty TOC
        }
    }

    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void InSet_LargeToc_CoversBothCopiesAndMarks(DriveProfile profile)
    {
        var (fixture, agent) = Create(profile);
        using (fixture) using (agent)
        {
            long toc = 10 * MiB + 1;
            long reserve = agent.Navigator.TocReserveFor(toc);
            Assert.True(reserve >= agent.Navigator.TOCFootprint(toc) + TapeNavigator.TocMarkAllowance);
            Assert.True(reserve >= 2 * toc);
            Assert.Equal(reserve, TapeNavigator.PredictTocReserve(fixture.Drive, toc));
        }
    }

    [Fact]
    public void TocMarkLayout_AddsTheGapFile()
    {
        var (seq, seqAgent) = Create(DriveProfile.SeqFilemarks);
        var (fmk, fmkAgent) = Create(DriveProfile.FilemarksOnly);
        using (seq) using (seqAgent) using (fmk) using (fmkAgent)
        {
            long toc = 10 * MiB;
            Assert.Equal(fmkAgent.Navigator.TocReserveFor(toc) + seq.Drive.GapFileLength,
                seqAgent.Navigator.TocReserveFor(toc));
        }
    }

    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void TinyMedium_FloorCappedAtAQuarter(DriveProfile profile)
    {
        var (fixture, agent) = Create(profile, capacity: 2 * MiB);
        using (fixture) using (agent)
            Assert.Equal(Math.Max(MiB / 2, agent.Navigator.TOCFootprint(0) + TapeNavigator.TocMarkAllowance),
                agent.Navigator.TocReserveFor(0));
    }

    [Theory]
    [MemberData(nameof(InSetProfiles))]
    public void Override_Wins(DriveProfile profile)
    {
        var (fixture, agent) = Create(profile);
        using (fixture) using (agent)
        {
            agent.Navigator.TOCCapacity = 48 * 1024;                  // the alias sets the override
            Assert.Equal(48 * 1024L, agent.Navigator.TocReserveOverride);
            Assert.Equal(48 * 1024L, agent.Navigator.TocReserveFor(10 * MiB));
        }
    }

    /// <summary>The override survives a navigator renewal; a computed reserve is not frozen into one.</summary>
    [Fact]
    public void RenewNavigator_CopiesOnlyTheOverride()
    {
        var (fixture, agent) = Create(DriveProfile.Setmarks);
        using (fixture) using (agent)
        {
            Assert.True(agent.Manager.RenewNavigator());
            Assert.Null(agent.Navigator.TocReserveOverride);

            agent.Navigator.TocReserveOverride = 64 * 1024;
            Assert.True(agent.Manager.RenewNavigator());
            Assert.Equal(64 * 1024L, agent.Navigator.TocReserveOverride);
        }
    }
}
