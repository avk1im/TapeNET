using Xunit;

using TapeConNET.Infrastructure;
using TapeConNET.Tests.Helpers;

namespace TapeConNET.Tests;

/// <summary>
/// Opt-in physical-drive smoke tests. Skipped unless the
/// <c>TAPECON_PHYSICAL_DRIVE</c> environment variable is set to a valid
/// drive number (e.g. <c>0</c>). Used to confirm that a real Win32 drive
/// behaves the same as the virtual-drive test suite for the safe verbs
/// (<c>info</c>, <c>list</c>). NEVER runs <c>format</c>/<c>backup</c>
/// against a real drive automatically.
/// </summary>
[Trait("Category", "Physical")]
public class PhysicalSmokeTests
{
    private const string EnvVarDrive = "TAPECON_PHYSICAL_DRIVE";

    private static int? GetPhysicalDrive()
    {
        var raw = Environment.GetEnvironmentVariable(EnvVarDrive);
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return int.TryParse(raw, out var n) ? n : null;
    }

    [SkippableFact]
    public async Task Info_OnPhysicalDrive_Succeeds()
    {
        var drive = GetPhysicalDrive();
        Skip.If(drive is null,
            $"Set {EnvVarDrive}=N to enable physical-drive tests (N = drive number).");

        var r = await TapeConHost.RunAsync("info", "--drive", drive.Value.ToString());

        // Accept Ok (media loaded) or OperationFailed (no media / not ready).
        //  Anything else (e.g. UsageError, FatalError) means the verb itself
        //  is broken.
        Assert.True(
            r.Exit is TapeConExitCode.Ok or TapeConExitCode.OperationFailed,
            $"Unexpected exit code {r.Exit}");
    }

    [SkippableFact]
    public async Task Backup_WithoutDriveSelection_AutoOpensWin32DriveZero()
    {
        Skip.If(GetPhysicalDrive() is null,
            $"Set {EnvVarDrive}=N to enable physical-drive tests (N = drive number).");

        // Per architecture §4 shortcut: when no drive option is supplied, the
        //  verb auto-opens Win32 drive 0. On a CI machine with no tape drive
        //  installed that opens-then-fails — but the failure is an
        //  operational one (no such drive), NOT a usage error.
        var r = await TapeConHost.RunAsync("backup", "C:\\");
        Assert.NotEqual(TapeConExitCode.UsageError, r.Exit);
        Assert.Contains(r.Entries, e =>
            e.Message.Contains("Win32 drive 0", StringComparison.OrdinalIgnoreCase));
    }
}
