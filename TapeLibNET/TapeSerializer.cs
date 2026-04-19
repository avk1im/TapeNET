using System.Runtime.CompilerServices;
using System.Text;

namespace TapeLibNET
{
    /// <summary>
    /// Contract for types that can be written to / read from tape via <see cref="TapeSerializer"/> and <see cref="TapeDeserializer"/>.
    /// </summary>
    public interface ITapeSerializable
    {
        /// <summary>Writes this instance to the given <paramref name="serializer"/>.</summary>
        void SerializeTo(TapeSerializer serializer);
        /// <summary>Reconstructs an instance from the given <paramref name="deserializer"/>, or returns <see langword="null"/> on failure.</summary>
        abstract static ITapeSerializable? ConstructFrom(TapeDeserializer deserializer);
    }

    /// <summary>
    /// Minimal binary serializer — no reflection, no JSON.
    /// <para>Works with any <see cref="Stream"/>; employs no buffering of its own
    ///  since <see cref="TapeStream"/> already buffers. Strings are UTF-8 with a 32-bit length prefix.</para>
    /// </summary>
    /// <param name="wstream">Target stream to write serialized data to.</param>
    public class TapeSerializer(Stream wstream)
    {
        internal static readonly byte[] Signature = [(byte)'T', (byte)'F'];
        public const ushort Version = 0x0100;

        // The following works for any type, yet makes no sense for reference types (handles the reference only)
        private static byte[] GetBytesUnmanaged<TUnmanaged>(TUnmanaged v) where TUnmanaged: unmanaged
        {
            byte[] bytes = new byte[Unsafe.SizeOf<TUnmanaged>()];
            Unsafe.As<byte, TUnmanaged>(ref bytes[0]) = v;
            return bytes;
        }

        public void Serialize(byte[] bytes) => wstream.Write(bytes, 0, bytes.Length);
        public void SerializeWithLength(byte[] bytes)
        {
            Serialize(bytes.Length);
            wstream.Write(bytes, 0, bytes.Length);
        }
        public void SerializeNullableWithLength(byte[]? bytes)
        {
            if (bytes == null)
                Serialize(-1);
            else
                SerializeWithLength(bytes);
        }
        public void Serialize<TUnmanaged>(TUnmanaged value) where TUnmanaged: unmanaged
            => Serialize(GetBytesUnmanaged(value));
        public void Serialize(string str) => SerializeWithLength(Encoding.UTF8.GetBytes(str));

        public void Serialize(FileAttributes attr) => Serialize((uint)attr);
        public void Serialize(DateTime dt) => Serialize(dt.Ticks);
        public void Serialize(TapeFileDescriptor fileDescr)
        {
            // serialize all settable public properties
            Serialize(fileDescr.FullName);
            Serialize(fileDescr.Length);
            Serialize(fileDescr.Attributes);
            Serialize(fileDescr.CreationTime);
            Serialize(fileDescr.LastWriteTime);
            Serialize(fileDescr.LastAccessTime);
        }

        public void SerializeSignature(ushort version = Version)
        {
            Serialize(Signature);
            Serialize(version);
        }

        public void Serialize(ITapeSerializable serializable) => serializable.SerializeTo(this);

        public void Serialize<TList, TValue>(TList list)
            where TList : IEnumerable<TValue>
            where TValue : ITapeSerializable
        {
            Serialize(list.Count());
            foreach (var item in list)
                item.SerializeTo(this);
        }

    } // class TapeSerializer


    /// <summary>
    /// Counterpart to <see cref="TapeSerializer"/> — reads primitives, strings, and
    ///  <see cref="ITapeSerializable"/> objects from a stream.
    /// </summary>
    /// <param name="rstream">Source stream to read serialized data from.</param>
    public class TapeDeserializer(Stream rstream)
    {
        public byte[]? DeserializeBytes(int length)
        {
            var bytes = new byte[length];
            if (rstream.Read(bytes, 0, length) != length)
                return null;
            else
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
        public string DeserializeString()
        {
            var bytes = DeserializeBytesWithLength();
            return (bytes != null) ? Encoding.UTF8.GetString(bytes) : throw new FormatException("Error deserializing string");
        }

        public FileAttributes DeserializeFileAttributes() => (FileAttributes)DeserializeUInt32();
        public DateTime DeserializeDateTime() => new(DeserializeInt64());
        public TapeFileDescriptor DeserializeFileDescriptor()
        {
            var fileDescr = new TapeFileDescriptor(DeserializeString())
            {
                Length = DeserializeInt64(),
                Attributes = DeserializeFileAttributes(),
                CreationTime = DeserializeDateTime(),
                LastWriteTime = DeserializeDateTime(),
                LastAccessTime = DeserializeDateTime(),
            };
            return fileDescr;
        }

        public bool ValidateSignature()
        {
            var signature = DeserializeBytes(TapeSerializer.Signature.Length);
            if (signature == null || !signature.SequenceEqual(TapeSerializer.Signature))
                return false; // signature does not match

            var version = DeserializeUInt16();
            if (version != TapeSerializer.Version)
                return false; // version mismatch

            return true;
        }
        public bool ValidateSignature(out ushort version)
        {
            var signature = DeserializeBytes(TapeSerializer.Signature.Length);
            if (signature == null || !signature.SequenceEqual(TapeSerializer.Signature))
            {
                version = 0;
                return false; // signature does not match
            }

            version = DeserializeUInt16();
            return true;
        }

        public TClass? Deserialize<TClass>() where TClass : class, ITapeSerializable
            => TClass.ConstructFrom(this) as TClass;

        public TList Deserialize<TList, TValue>()
            where TList : List<TValue>, new()
            where TValue : ITapeSerializable
        {
            var list = new TList();
            var count = DeserializeInt32();

            for (int i = 0; i < count; i++)
            {
                var item = (TValue?)TValue.ConstructFrom(this);
                if (item != null)
                    list.Add(item);
            }

            return list;
        }

    } // class TapeDeserializer

} // namespace TapeNET
