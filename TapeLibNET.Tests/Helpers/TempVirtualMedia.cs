namespace TapeLibNET.Tests.Helpers;

/// <summary>
/// Owns a temporary on-disk virtual-tape file (and optional initiator file)
/// used to drive <c>tapecon --virtual PATH</c> end-to-end tests across
/// multiple in-process invocations. The media file persists across
/// <see cref="TapeConHost"/> calls so backup → restore round-trips work.
/// </summary>
public sealed class TempVirtualMedia : IDisposable
{
    /// <summary>Default content capacity: 64 MiB — large enough for the test trees.</summary>
    public const long DefaultContentCapacity = 64L * 1024 * 1024;

    /// <summary>Default initiator partition capacity: 4 MiB.</summary>
    public const long DefaultInitiatorCapacity = 4L * 1024 * 1024;

    public string Root { get; }
    public string ContentPath { get; }
    public string? InitiatorPath { get; }
    public long ContentCapacity { get; }
    public long InitiatorCapacity { get; }
    public bool HasInitiator => InitiatorPath is not null;

    public TempVirtualMedia(
        bool withInitiator = false,
        long contentCapacity = DefaultContentCapacity,
        long initiatorCapacity = DefaultInitiatorCapacity)
    {
        var basePath = Environment.GetEnvironmentVariable(TapeLibNET.Tests.Helpers.TempFileTree.EnvVarBasePath);
        if (string.IsNullOrEmpty(basePath))
            basePath = Path.GetTempPath();

        Root = Path.Combine(basePath, $"TapeConNET_VMD_{Guid.NewGuid():N}");
        Directory.CreateDirectory(Root);

        ContentPath = Path.Combine(Root, "media.vtape");
        InitiatorPath = withInitiator ? Path.Combine(Root, "media.vinit") : null;
        ContentCapacity = contentCapacity;
        InitiatorCapacity = initiatorCapacity;
    }

    /// <summary>
    /// Returns the global drive options to pass to <c>tapecon</c> for this
    /// virtual media. Always begins with <c>--virtual</c> + path; appends
    /// <c>--initiator</c> when the fixture was built with a separate TOC
    /// partition; appends <c>--capacity</c> / <c>--init-capacity</c> so the
    /// initial format uses the fixture's sizes.
    /// </summary>
    public string[] DriveArgs()
    {
        var args = new List<string>(8)
        {
            "--virtual", ContentPath,
            "--capacity", ContentCapacity.ToString(),
        };
        if (InitiatorPath is not null)
        {
            args.Add("--initiator");
            args.Add(InitiatorPath);
            args.Add("--init-capacity");
            args.Add(InitiatorCapacity.ToString());
        }
        return [.. args];
    }

    /// <summary>
    /// Convenience: returns <c>tapecon</c> args for a verb invocation —
    /// verb name first, then the drive selection, then the verb's own args.
    /// </summary>
    public string[] Verb(string verb, params string[] verbArgs)
    {
        var drive = DriveArgs();
        var all = new string[1 + drive.Length + verbArgs.Length];
        all[0] = verb;
        drive.CopyTo(all, 1);
        verbArgs.CopyTo(all, 1 + drive.Length);
        return all;
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }
        catch
        {
            // best-effort cleanup; tests must not fail on disposal
        }
    }
}
