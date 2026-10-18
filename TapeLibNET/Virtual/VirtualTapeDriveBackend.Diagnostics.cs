#if TEST_HOOKS
using Windows.Win32.Foundation;
#endif

namespace TapeLibNET.Virtual;


public partial class VirtualTapeDriveBackend
{
#if TEST_HOOKS

    /// <summary>
    /// Every data byte of the loaded cartridge — content partition, then initiator partition — marks skipped. For
    ///  "no plaintext on tape" assertions (Design-Encryption §15.4).
    /// </summary>
    internal byte[] DumpData() => [.. m_contentMedia?.DumpData() ?? [], .. m_initiatorMedia?.DumpData() ?? []];

    /// <summary>
    /// Block-level write faults injected into the CONTENT medium. Owned here rather than by the medium so
    ///  the settings survive <see cref="LoadMedia"/>, which builds a fresh medium on every load and every
    ///  volume swap.
    /// </summary>
    public VirtualMediaFaultInjector ContentWriteFaults { get; } =
        new() { ErrorWin32 = WIN32_ERROR.ERROR_WRITE_FAULT };

    /// <summary>Block-level read faults injected into the CONTENT medium.</summary>
    public VirtualMediaFaultInjector ContentReadFaults { get; } =
        new() { ErrorWin32 = WIN32_ERROR.ERROR_READ_FAULT };

    /// <summary>
    /// Block-level write faults injected into the INITIATOR (TOC) medium — the counterpart of
    ///  <see cref="ContentWriteFaults"/> for partitioned media.
    /// </summary>
    public VirtualMediaFaultInjector InitiatorWriteFaults { get; } =
        new() { ErrorWin32 = WIN32_ERROR.ERROR_WRITE_FAULT };

    /// <summary>
    /// Block-level read faults injected into the INITIATOR (TOC) medium — e.g. a damaged first TOC copy on a
    ///  partitioned cartridge.
    /// </summary>
    public VirtualMediaFaultInjector InitiatorReadFaults { get; } =
        new() { ErrorWin32 = WIN32_ERROR.ERROR_READ_FAULT };

    /// <summary>
    /// Wires the injectors into the currently loaded media. Mirrors <c>ApplyEwProfileToMedia</c>. Called on every load
    ///  AND after <see cref="FormatMedia"/>, which builds a fresh initiator medium.
    /// </summary>
    private void ApplyFaultInjectorsToMedia()
    {
        if (m_contentMedia is not null)
        {
            m_contentMedia.WriteFaults = ContentWriteFaults;
            m_contentMedia.ReadFaults = ContentReadFaults;
        }
        if (m_initiatorMedia is not null)
        {
            m_initiatorMedia.WriteFaults = InitiatorWriteFaults;
            m_initiatorMedia.ReadFaults = InitiatorReadFaults;
        }
    }
#endif
}
