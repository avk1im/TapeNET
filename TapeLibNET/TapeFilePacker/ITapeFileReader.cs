namespace TapeLibNET.TapeFilePacker;

/// <summary>
/// Common surface implemented by <see cref="TapeFilePipelinedReader"/>. <see cref="TapeStreamManager"/>
///  holds this interface so the implementation can be swapped without touching any
///  call site outside the packer subsystem.
/// </summary>
internal interface ITapeFileReader : IDisposable
{
    /// <summary>Block size in bytes (mirrors the backend's block size).</summary>
    int BlockSize { get; }

    /// <summary>True while a file is open between <see cref="BeginRead"/> and <see cref="EndRead"/>.</summary>
    bool IsFileOpen { get; }

    /// <summary>
    /// Open a logical read slot for one file at <paramref name="addr"/> spanning
    ///  <paramref name="length"/> bytes. Returns a <see cref="TapeReadStreamFacade"/> for
    ///  sequential reads. At most one file may be open at a time.
    /// </summary>
    TapeReadStreamFacade BeginRead(TapeAddress addr, long length);

    /// <summary>
    /// Close the currently open read slot. Implementations may retain cached blocks for
    ///  the next caller. No-op if no slot is open.
    /// </summary>
    void EndRead();
}
