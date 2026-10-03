using System.Runtime.CompilerServices;
using System.Text;

namespace TapeLibNET.Legacy;

/// <summary>
/// Frozen, read-only reader of the pre-2.1 binary layout (formerly <c>TapeDeserializer</c>):
///  primitives, UTF-8 strings with a 32-bit length prefix, and <see cref="ITapeSerializable"/> objects.
/// Differences from the original: reads are exact (short reads from a stream are retried, not mistaken
///  for end of data), and a list item that fails to construct is an error.
/// </summary>
/// <param name="rstream">Source stream to read serialized data from.</param>
public class LegacyDeserializer(Stream rstream)
{
    /// <summary>Reads exactly <paramref name="length"/> bytes; <see langword="null"/> if the stream ends first.</summary>
    public byte[]? DeserializeBytes(int length)
    {
        if (length < 0)
            return null;

        var bytes = new byte[length];
        int total = 0;
        while (total < length)
        {
            int n = rstream.Read(bytes, total, length - total);
            if (n <= 0)
                return null;
            total += n;
        }
        return bytes;
    }
    public byte[] DeserializeBytesWithLength()
    {
        var length = DeserializeInt32();
        return DeserializeBytes(length) ?? throw new FormatException("Error deserializing byte array");
    }
    public byte[]? DeserializeNullableBytesWithLength()
    {
        var length = DeserializeInt32();
        return (length < 0) ? null : DeserializeBytes(length);
    }
    public byte[] DeserializeBytes<TUnmanaged>() where TUnmanaged : unmanaged
    {
        byte[]? bytes = DeserializeBytes(Unsafe.SizeOf<TUnmanaged>());
        return bytes ?? throw new FormatException($"Error deserializing unmanaged type {typeof(TUnmanaged)}");
    }
    public bool DeserializeBoolean() => BitConverter.ToBoolean(DeserializeBytes<bool>(), 0);
    public char DeserializeChar() => BitConverter.ToChar(DeserializeBytes<char>(), 0);
    public double DeserializeDouble() => BitConverter.ToDouble(DeserializeBytes<double>(), 0);
    public short DeserializeInt16() => BitConverter.ToInt16(DeserializeBytes<short>(), 0);
    public int DeserializeInt32() => BitConverter.ToInt32(DeserializeBytes<int>(), 0);
    public long DeserializeInt64() => BitConverter.ToInt64(DeserializeBytes<long>(), 0);
    public float DeserializeSingle() => BitConverter.ToSingle(DeserializeBytes<float>(), 0);
    public ushort DeserializeUInt16() => BitConverter.ToUInt16(DeserializeBytes<ushort>(), 0);
    public uint DeserializeUInt32() => BitConverter.ToUInt32(DeserializeBytes<uint>(), 0);
    public ulong DeserializeUInt64() => BitConverter.ToUInt64(DeserializeBytes<ulong>(), 0);
    public string DeserializeString() => Encoding.UTF8.GetString(DeserializeBytesWithLength());

    public FileAttributes DeserializeFileAttributes() => (FileAttributes)DeserializeUInt32();
    public DateTime DeserializeDateTime() => new(DeserializeInt64());
    /// <summary>Deserializes a <see cref="Guid"/> — a fixed 16-byte block.</summary>
    /// <exception cref="FormatException">Thrown if fewer than 16 bytes remain.</exception>
    public Guid DeserializeGuid()
    {
        var bytes = DeserializeBytes(16);
        return (bytes != null) ? new Guid(bytes) : throw new FormatException("Error deserializing Guid");
    }
    public TapeAddress DeserializeTapeAddress() => new(DeserializeInt64(), DeserializeUInt32());
    public TapeFileDescriptor DeserializeFileDescriptor()
        => new(DeserializeString())
        {
            Length = DeserializeInt64(),
            Attributes = DeserializeFileAttributes(),
            CreationTime = DeserializeDateTime(),
            LastWriteTime = DeserializeDateTime(),
            LastAccessTime = DeserializeDateTime(),
        };

    public bool ValidateSignature()
    {
        var signature = DeserializeBytes(LegacyFormat.Signature.Length);
        if (signature == null || !signature.AsSpan().SequenceEqual(LegacyFormat.Signature))
            return false; // signature does not match

        var version = DeserializeUInt16();
        return version == LegacyFormat.Version; // version mismatch => false
    }
    public bool ValidateSignature(out ushort version)
    {
        var signature = DeserializeBytes(LegacyFormat.Signature.Length);
        if (signature == null || !signature.AsSpan().SequenceEqual(LegacyFormat.Signature))
        {
            version = 0;
            return false; // signature does not match
        }

        version = DeserializeUInt16();
        return true;
    }

    public TClass? Deserialize<TClass>() where TClass : class, ITapeSerializable
        => TClass.ConstructFrom(this) as TClass;

    /// <exception cref="FormatException">An item failed to construct.</exception>
    public TList Deserialize<TList, TValue>()
        where TList : List<TValue>, new()
        where TValue : ITapeSerializable
    {
        var list = new TList();
        var count = DeserializeInt32();

        for (int i = 0; i < count; i++)
        {
            var item = (TValue?)TValue.ConstructFrom(this)
                ?? throw new FormatException($"Error deserializing {typeof(TValue).Name} list item {i} of {count}");
            list.Add(item);
        }

        return list;
    }
}
