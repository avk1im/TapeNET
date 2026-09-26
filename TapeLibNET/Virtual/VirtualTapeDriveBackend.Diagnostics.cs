using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using System.Diagnostics;
using System.Reflection;
using Windows.Win32.Foundation;


namespace TapeLibNET.Virtual;

public partial class VirtualTapeDriveBackend
{
#if DEBUG
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

    /// <summary>Wires the injectors into the currently loaded media. Mirrors <c>ApplyEwProfileToMedia</c>.</summary>
    private void ApplyFaultInjectorsToMedia()
    {
        if (m_contentMedia is not null)
        {
            m_contentMedia.WriteFaults = ContentWriteFaults;
            m_contentMedia.ReadFaults = ContentReadFaults;
        }
    }
#endif

}
