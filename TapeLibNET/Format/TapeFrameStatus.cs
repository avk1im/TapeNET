using TapeLibNET.Headers;

namespace TapeLibNET.Format;


/// <summary>Why a <see cref="TapeFramer"/>'s unpack did or did not yield a record.</summary>
public enum TapeFrameStatus
{
    /// <summary>Framed, CRC intact, payload parsed.</summary>
    Ok,
    /// <summary>No plausible frame: no magic / implausible length, or the block ends inside the frame.</summary>
    NotFramed,
    /// <summary>A plausible frame whose CRC disagrees — a torn or corrupt record.</summary>
    CrcMismatch,
    /// <summary>CRC intact, but the record would not parse — unknown kind, newer version, unknown critical field.</summary>
    Unparseable,
}
