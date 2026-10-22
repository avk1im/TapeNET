using Windows.Win32.Foundation;

using TapeLibNET.Agents;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Media;

/// <summary>
/// Design-TocLocation R2 / TL-3: when end-of-content cannot be found by marks, the TOC-in-set navigators settle at
///  begin-of-content ONLY on positive blank evidence. Content without marks is refused — the old fallback read the media
///  header as a TOC and let an append overwrite set 0.
/// </summary>
public class TapeNavigatorEndOfContentTests
{
    public static TheoryData<DriveProfile, bool> InSetProfilesAndHeaders
    {
        get
        {
            TheoryData<DriveProfile, bool> data = [];
            foreach (var profile in new[] { DriveProfile.Setmarks, DriveProfile.SeqFilemarks, DriveProfile.FilemarksOnly })
            {
                data.Add(profile, false);
                data.Add(profile, true);
            }
            return data;
        }
    }

    /// <summary>Content written with NO marks at all: end-of-content is unknowable — refuse, never "blank".</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void ContentWithoutMarks_IsRefused(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        Assert.True(agent.Navigator.MoveToBeginOfContent());

        var block = new byte[fixture.Drive.BlockSize];
        new Random(3).NextBytes(block);
        for (int i = 0; i < 3; i++)
            Assert.Equal(block.Length, fixture.Drive.WriteDirect(block, 0, block.Length));
        agent.Navigator.ResetContentSet();

        Assert.False(agent.Navigator.MoveToEndOfContent(), "content without marks must not be taken for blank media");
        Assert.Equal(WIN32_ERROR.ERROR_INVALID_DATA, agent.Navigator.LastErrorWin32);
    }

    /// <summary>Blank media (headed or not): end-of-content IS begin-of-content, at its exact block.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void BlankMedia_SettlesAtBeginOfContent(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();

        Assert.True(agent.Navigator.MoveToEndOfContent(), agent.Navigator.LastErrorMessage);
        Assert.Equal(fixture.FirstContentBlock, fixture.Drive.CurrentBlock);
    }

    /// <summary>A healthy tape is unaffected: append after one set, then reload — two sets.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void HealthyTape_AppendStillWorks(DriveProfile profile, bool headed)
    {
        using var tree1 = new TempFileTree(seed: 1);
        tree1.AddFiles("h1", count: 3, minSize: 512, maxSize: 4 * 1024);
        using var tree2 = new TempFileTree(seed: 2);
        tree2.AddFiles("h2", count: 2, minSize: 512, maxSize: 4 * 1024);
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed, withSetHeaders: headed);

        fixture.BackupFiles(tree1.Files, description: "One");
        fixture.LoadTOC();
        fixture.BackupFiles(tree2.Files, description: "Two");
        fixture.LoadTOC();

        Assert.Equal(2, fixture.TOC.Count);
        Assert.Equal(tree1.Files.Count, fixture.TOC[1].Count);
    }

    /// <summary>A tape holding only a TOC (zero content sets): end-of-content is begin-of-content, and the TOC reloads.</summary>
    [Theory]
    [MemberData(nameof(InSetProfilesAndHeaders))]
    public void TocOnly_ZeroSets_EndOfContentIsBeginOfContent(DriveProfile profile, bool headed)
    {
        using var fixture = new VirtualTapeFixture(profile, withMediaHeader: headed);
        fixture.SaveAndReloadTOC();

        using var agent = new TapeAgentBase(fixture.Drive, fixture.TOC);
        agent.EnsureMediaHeaderResolved();
        agent.Navigator.ResetContentSet();
        Assert.True(agent.Navigator.MoveToEndOfContent(), agent.Navigator.LastErrorMessage);
        Assert.Equal(fixture.FirstContentBlock, fixture.Drive.CurrentBlock);
    }
}
