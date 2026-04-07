using Microsoft.Extensions.Logging;
using TapeLibNET.Tests.Helpers;
using Xunit.Abstractions;

namespace TapeLibNET.Tests.Physical;

// =============================================================================
// xUnit Collection Definitions for Physical Tape Tests
// =============================================================================
//
// Physical tape tests must run sequentially because they share a real tape drive.
// xUnit's [Collection] mechanism enforces this: all tests in the same collection
// run one at a time and share the same ICollectionFixture<T> instance.
//
// We define one collection per drive. For now a single "PhysicalDrive" collection
// is sufficient — if testing multiple drives simultaneously, add more collections.
//
// Usage in test classes:
//   [Collection(PhysicalDriveCollectionDefinition.Name)]
//   [Trait("Category", "Physical")]
//   [TestCaseOrderer("TapeLibNET.Tests.Helpers.PriorityOrderer", "TapeLibNET.Tests")]
//   public class MyPhysicalTests(PhysicalDriveFixtureWrapper fixture, ITestOutputHelper output)
// =============================================================================

/// <summary>
/// Shared fixture wrapper for physical drive tests. Implements
/// <see cref="IAsyncLifetime"/> to perform one-time drive discovery, open,
/// format, and teardown for the entire test collection.
/// <para>
/// This is the <c>ICollectionFixture&lt;T&gt;</c> type — xUnit creates one
/// instance per collection and injects it into all test class constructors.
/// </para>
/// <para>
/// Trace logging: The fixture creates a <see cref="RedirectableTestOutput"/>-backed
/// <see cref="ILoggerFactory"/> so all TapeLibNET trace output appears in xUnit
/// results. Each test method calls <see cref="SetOutput"/> to redirect to its
/// own <see cref="ITestOutputHelper"/>.
/// </para>
/// </summary>
public sealed class PhysicalDriveFixtureWrapper : IAsyncLifetime
{
    /// <summary>Redirectable output sink — created once, pointed at each test's helper.</summary>
    private readonly RedirectableTestOutput _outputSink = new();

    /// <summary>
    /// The underlying fixture, or null if no physical drive was found.
    /// Test classes should call <see cref="GetFixtureOrSkip"/> to access it.
    /// </summary>
    public PhysicalTapeFixture? Fixture { get; private set; }

    /// <summary>Drive number used (for diagnostics).</summary>
    public uint? DriveNumber { get; private set; }

    /// <summary>Reason if no fixture was created (for skip messages).</summary>
    public string? SkipReason { get; private set; }

    /// <summary>
    /// Redirects all TapeLibNET trace output to the given test's output helper.
    /// Call at the start of every test method.
    /// </summary>
    public void SetOutput(ITestOutputHelper output) => _outputSink.SetOutput(output);

    public Task InitializeAsync()
    {
        var drives = PhysicalTapeFixture.DiscoverDrives();

        if (drives.Count == 0)
        {
            SkipReason = "No physical tape drives found. " +
                $"Set {PhysicalTestEnv.DriveNumbers} or connect a tape drive.";
            return Task.CompletedTask;
        }

        // Use the first available drive
        DriveNumber = drives[0];

        try
        {
            // Create logger factory backed by the redirectable output sink —
            //  each test method calls SetOutput() to point it at its own ITestOutputHelper
            var loggerFactory = new XunitLoggerFactory(_outputSink, LogLevel.Trace);

            Fixture = new PhysicalTapeFixture(
                DriveNumber.Value,
                format: true,
                loggerFactory: loggerFactory,
                mediaDescription: "Physical Test Session");
        }
        catch (Exception ex)
        {
            SkipReason = $"Failed to initialize physical drive #{DriveNumber}: {ex.Message}";
        }

        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        _outputSink.SetOutput(null);
        Fixture?.Dispose();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns the fixture, or skips the test if no drive is available.
    /// Call this at the start of every physical test method.
    /// </summary>
    public PhysicalTapeFixture GetFixtureOrSkip()
    {
        Skip.If(Fixture == null, SkipReason ?? "No physical tape fixture available");
        return Fixture!;
    }
}

/// <summary>
/// xUnit collection definition that groups all physical drive tests for sequential execution.
/// </summary>
[CollectionDefinition(Name)]
public class PhysicalDriveCollectionDefinition : ICollectionFixture<PhysicalDriveFixtureWrapper>
{
    /// <summary>Collection name constant — use in <c>[Collection(...)]</c> attributes.</summary>
    public const string Name = "PhysicalDrive";
}
