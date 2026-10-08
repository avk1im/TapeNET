using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Legacy;


/// <summary>
/// Regenerates the Phase 0 legacy goldens into <c>Golden/Legacy/</c> (source tree). Skipped by default:
/// goldens are frozen; regenerate only deliberately, then review the diff.
/// </summary>
public class GoldenGenerator
{
    private static string GoldenDir() => GoldenPaths.Legacy;

    [Fact(Skip = "Manual: regenerates the frozen legacy goldens. Remove Skip temporarily to run.")]
    public void GenerateLegacyGoldens()
    {
        var dir = GoldenDir();
        Directory.CreateDirectory(dir);

        foreach (var (name, bytes) in GoldenData.All())
        {
            File.WriteAllBytes(Path.Combine(dir, name), bytes);
            File.WriteAllText(Path.Combine(dir, Path.ChangeExtension(name, ".expected.json")), GoldenData.Describe(name, bytes));
        }
    }
}
