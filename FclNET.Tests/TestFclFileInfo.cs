namespace FclNET.Tests;

/// <summary>
/// Simple <see cref="IFclFileInfo"/> implementation for unit tests.
/// All properties are settable so each test can construct the exact
/// file metadata it needs without touching the file system.
/// </summary>
internal sealed class TestFclFileInfo : IFclFileInfo
{
    public required string FullName { get; init; }
    public long Size { get; init; }
    public DateTime CreationTime { get; init; } = DateTime.Today;
    public DateTime LastWriteTime { get; init; } = DateTime.Today;
    public FileAttributes Attributes { get; init; } = FileAttributes.Normal;
}
