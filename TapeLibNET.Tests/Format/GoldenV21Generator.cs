using System.Security.Cryptography;
using System.Text.Json;
using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Format;


/// <summary>
/// Regenerates V21 goldens into <c>Golden/V21/</c> (source tree). Skipped by default:
///  goldens are frozen; regenerate only deliberately the missing ones, then review the diff.
/// </summary>
public class GoldenV21Generator
{
    public static string GoldenDir() => GoldenPaths.V21;

    private static string PathOf(string name) => Path.Combine(GoldenDir(), name);

    public sealed record Expected(int Length, string Sha256, string Describes);

    /// <summary>
    /// Writes every golden that does not exist yet, with its <c>.expected.json</c>. NEVER overwrites one: a checked-in
    ///  golden is frozen.
    /// <para>
    /// To run: Remove the Skip, run once, restore the Skip, commit the new files.
    /// </para>
    /// </summary>
    [Fact(Skip = "Generator — run deliberately to add NEW goldens; it never overwrites existing ones.")]
    [Trait("Category", "Golden")]
    public void GenerateMissingGoldens()
    {
        Directory.CreateDirectory(GoldenDir());
        var options = new JsonSerializerOptions { WriteIndented = true };
        foreach (var c in GoldenV21.All)
        {
            string path = PathOf(c.Name);
            if (File.Exists(path))
                continue;                                   // frozen — never regenerated
            byte[] bytes = c.Write();
            c.AssertReads(bytes);                           // a golden the reader refuses is never written
            File.WriteAllBytes(path, bytes);
            File.WriteAllText(path + ".expected.json", JsonSerializer.Serialize(
                new Expected(bytes.Length, Convert.ToHexString(SHA256.HashData(bytes)), c.Describes), options));
        }
    }
}
