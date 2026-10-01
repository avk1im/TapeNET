namespace TapeLibNET.Format;

/// <summary>
/// Delta coding of calibration samples (Design-Format-v2 §5.5): <c>varuint count</c>, then per sample
///  <c>varuint ΔActualWritten</c> (monotone) and <c>varint ΔReportedRemaining</c>, the first from <c>(0, 0)</c>.
/// A few bytes per sample instead of 16.
/// </summary>
public static class TapeSampleCoder
{
    /// <summary>Encodes <paramref name="samples"/>; <c>ActualWritten</c> must be non-decreasing and non-negative.</summary>
    /// <exception cref="ArgumentException">A sample breaks monotonicity.</exception>
    public static byte[] Encode(IReadOnlyList<(long ActualWritten, long ReportedRemaining)> samples)
    {
        ArgumentNullException.ThrowIfNull(samples);

        var buffer = new byte[TapeFormat.MaxVarUIntBytes + samples.Count * 2 * TapeFormat.MaxVarUIntBytes];
        int pos = TapePrimitives.WriteVarUInt(buffer, (ulong)samples.Count);

        long prevActual = 0, prevReported = 0;
        foreach (var (actual, reported) in samples)
        {
            if (actual < prevActual)
                throw new ArgumentException("ActualWritten must be non-decreasing and non-negative", nameof(samples));

            pos += TapePrimitives.WriteVarUInt(buffer.AsSpan(pos), (ulong)(actual - prevActual));
            pos += TapePrimitives.WriteVarInt(buffer.AsSpan(pos), unchecked(reported - prevReported));
            prevActual = actual;
            prevReported = reported;
        }
        return buffer.AsSpan(0, pos).ToArray();
    }

    /// <summary>Decodes the output of <see cref="Encode"/>; the value must be consumed entirely.</summary>
    /// <exception cref="TapeFormatException">Truncated, overlong, overflowing or trailing data.</exception>
    public static List<(long ActualWritten, long ReportedRemaining)> Decode(ReadOnlySpan<byte> data)
    {
        ulong count = TapePrimitives.ReadVarUInt(data, out int pos);

        // Each sample needs at least 2 bytes: a lying count must not drive allocation.
        if (count > (ulong)(data.Length - pos) / 2)
            throw new TapeFormatException(FormatErrorKind.Truncated, "sample count exceeds the data available");

        var samples = new List<(long, long)>((int)count);
        long actual = 0, reported = 0;
        try
        {
            for (ulong i = 0; i < count; i++)
            {
                ulong dActual = TapePrimitives.ReadVarUInt(data[pos..], out int n);
                pos += n;
                long dReported = TapePrimitives.ReadVarInt(data[pos..], out n);
                pos += n;

                if (dActual > long.MaxValue)
                    throw TapeFormatException.Bad("sample delta overflows");
                actual = checked(actual + (long)dActual);
                reported = unchecked(reported + dReported);
                samples.Add((actual, reported));
            }
        }
        catch (OverflowException ex)
        {
            throw new TapeFormatException(FormatErrorKind.BadValue, "sample values overflow", ex);
        }

        if (pos != data.Length)
            throw new TapeFormatException(FormatErrorKind.Underrun, "sample data has trailing bytes");
        return samples;
    }
}
