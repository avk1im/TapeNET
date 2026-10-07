using System.Runtime.CompilerServices;

namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Source-tree locations of the checked-in golden files. Anchored at the test PROJECT folder (the one holding
///  TapeLibNET.Tests.csproj), so a test file can move between folders without breaking its paths.
/// </summary>
/// <remarks>
/// Source tree, not the output folder: the generators must write where git tracks the files. Resolved through this
///  file's compile-time path, so it works wherever the repository is checked out.
/// </remarks>
internal static class GoldenPaths
{
    private const string ProjectFileName = "TapeLibNET.Tests.csproj";

    /// <summary>The test project's source folder.</summary>
    public static string ProjectDir { get; } = FindProjectDir();

    /// <summary><c>Golden/Legacy</c> in the source tree.</summary>
    public static string Legacy => Path.Combine(ProjectDir, "Golden", "Legacy");

    /// <summary><c>Golden/Legacy/Virtual</c> in the source tree.</summary>
    public static string LegacyVirtual => Path.Combine(Legacy, "Virtual");

    // Walks up from this file's folder to the one holding the .csproj — robust to moving this helper, too.
    private static string FindProjectDir([CallerFilePath] string thisFile = "")
    {
        for (var dir = Path.GetDirectoryName(thisFile); dir is not null; dir = Path.GetDirectoryName(dir))
        {
            if (File.Exists(Path.Combine(dir, ProjectFileName)))
                return dir;
        }
        throw new InvalidOperationException($"{ProjectFileName} not found above {thisFile}");
    }
}