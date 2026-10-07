using TapeLibNET.Headers;
namespace TapeLibNET.Legacy;


/// <summary>
/// The shared preamble of a LEGACY header record — signature, kind byte, id, creation ticks, block size — decoded by
///  <see cref="LegacyHeaderReader"/> before the concrete kind reads its own fields (<c>ConstructBody</c>).
///  Format 2.1 carries the same facts as the shared tags 1–3 (<see cref="TapeHeaderWire"/>).
/// </summary>
/// <remarks><see cref="CreatedUtc"/> is already converted by <see cref="LegacyTime"/> when the reader builds it.</remarks>
internal readonly record struct TapeHeaderPreamble(
    TapeHeaderKind Kind, Guid Id, DateTime CreatedUtc, uint BlockSize);
