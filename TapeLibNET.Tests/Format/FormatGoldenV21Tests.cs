using System.Security.Cryptography;
using System.Text.Json;

using TapeLibNET.Tests.Helpers;

namespace TapeLibNET.Tests.Format;


/// <summary>
/// Freezes format 2.1 the way <c>LegacyGoldenTests</c> froze the legacy format: checked-in bytes that every future build
///  must read, and that today's writer must reproduce.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why round trips are not enough.</b> A round trip proves the writer and the reader agree with EACH OTHER. Change both
///  the same way — renumber a tag, change a default, reorder the prologue — and every round trip stays green while every
///  tape already in a drawer becomes unreadable. Goldens pin the bytes, so such a change shows up as a failure.
/// </para>
/// <para>
/// <b>The two directions mean different things.</b>
/// <list type="bullet">
///   <item><b>Read</b> (<see cref="Golden_Reads"/>) is a promise: a golden, once checked in, must read forever. It never
///    changes.</item>
///   <item><b>Write</b> (<see cref="Golden_WriterReproduces"/>) is a tripwire: the writer still produces these bytes.
///    When it fails, either the change is a bug, or it is a deliberate format evolution — then ADD a new case under a new
///    name, keep the old golden for the read direction, and bump the minor if new tags are critical.</item>
/// </list>
/// </para>
/// </remarks>
public class FormatGoldenV21Tests
{
    public static TheoryData<string> Names()
    {
        TheoryData<string> data = [];
        foreach (var c in GoldenV21.All)
            data.Add(c.Name);
        return data;
    }

    private static GoldenV21.Case Find(string name) => GoldenV21.All.Single(c => c.Name == name);

    private static string PathOf(string name) => Path.Combine(GoldenV21Generator.GoldenDir(), name);

    private static byte[] Load(string name)
    {
        string path = PathOf(name);
        Assert.True(File.Exists(path),
            $"Golden {name} is missing — run {nameof(GoldenV21Generator.GenerateMissingGoldens)} once and commit.");
        return File.ReadAllBytes(path);
    }

    /// <summary>The checked-in bytes are the ones recorded at generation — guards against an accidental overwrite.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_IsUntouched(string name)
    {
        byte[] bytes = Load(name);
        var expected = JsonSerializer.Deserialize<GoldenV21Generator.Expected>(File.ReadAllText(PathOf(name) + ".expected.json"))!;
        Assert.Equal(expected.Length, bytes.Length);
        Assert.Equal(expected.Sha256, Convert.ToHexString(SHA256.HashData(bytes)));
    }

    /// <summary>The promise: today's reader reads the frozen bytes into exactly the values they were written from.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_Reads(string name) => Find(name).AssertReads(Load(name));

    /// <summary>The tripwire: today's writer still produces the frozen bytes, byte for byte.</summary>
    [Theory]
    [MemberData(nameof(Names))]
    public void Golden_WriterReproduces(string name)
    {
        byte[] golden = Load(name);
        byte[] now = Find(name).Write();
        Assert.True(golden.AsSpan().SequenceEqual(now),
            $"{name}: the writer no longer produces the frozen bytes ({golden.Length} B frozen, {now.Length} B now). " +
            "A bug — or a deliberate format change: then add a NEW case, never regenerate this one.");
    }

}

