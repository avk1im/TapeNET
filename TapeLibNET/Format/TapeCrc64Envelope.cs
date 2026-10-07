using System.IO.Hashing;

using TapeLibNET.Streams;

namespace TapeLibNET.Format;


/// <summary>
/// The CRC-64 envelope of a stream of records: <c>Record* ‖ CRC-64(all bytes above)</c> (Design-Format-v2 §4.5, §5.7).
/// </summary>
/// <remarks>
/// The reader never reads ahead of what the records consume, so the trailer is read from exactly where the body ends.
/// </remarks>
public static class TapeCrc64Envelope
{
    /// <summary>Size of the trailer.</summary>
    public const int CrcLength = TapeFrame.CrcLength;

    /// <summary>Runs <paramref name="write"/> against a hashing wrapper of <paramref name="stream"/>, then appends the CRC-64.</summary>
    public static void Write(Stream stream, Action<Stream> write)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(write);

        var crc = new Crc64();
        using (var hashing = new HashingStream(stream, crc))
            write(hashing);

        byte[] trailer = crc.GetCurrentHash();
        stream.Write(trailer, 0, trailer.Length);
    }

    /// <summary>
    /// Runs <paramref name="read"/> against a hashing wrapper of <paramref name="stream"/>, then reads and verifies the
    ///  CRC-64 trailer.
    /// </summary>
    /// <exception cref="TapeFormatException">
    /// Anything <paramref name="read"/> throws; <see cref="FormatErrorKind.Truncated"/> if the trailer is cut off;
    /// <see cref="FormatErrorKind.CrcMismatch"/> if it disagrees.
    /// </exception>
    public static T Read<T>(Stream stream, Func<Stream, T> read)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentNullException.ThrowIfNull(read);

        var crc = new Crc64();
        T result;
        using (var hashing = new HashingStream(stream, crc))
            result = read(hashing);

        byte[] stored = new byte[CrcLength];
        int got = stream.ReadAtLeast(stored, CrcLength, throwOnEndOfStream: false);
        if (got < CrcLength)
            throw new TapeFormatException(FormatErrorKind.Truncated, "stream ended inside the CRC-64 trailer");
        if (!crc.GetCurrentHash().AsSpan().SequenceEqual(stored))
            throw new TapeFormatException(FormatErrorKind.CrcMismatch, "CRC-64 over the records does not match");

        return result;
    }
}
