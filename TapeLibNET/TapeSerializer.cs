using System.Runtime.CompilerServices;
using System.Text;

using TapeLibNET.Legacy; // signature and version constants


namespace TapeLibNET
{

    /// <summary>
    /// Contract for types that can be written to / read from tape via <see cref="TapeSerializer"/> and <see cref="Legacy.LegacyDeserializer"/>.
    /// </summary>
    //[Obsolete("Legacy writer — product uses removed by Phases 4–6; moves to TapeLibNET.Tests (Design-Format-v2 §7.3)")]
    public interface ITapeSerializable
    {
        /// <summary>Writes this instance to the given <paramref name="serializer"/>.</summary>
        void SerializeTo(TapeSerializer serializer);
        /// <summary>Reconstructs an instance from the given <paramref name="deserializer"/>, or returns <see langword="null"/> on failure.</summary>
        abstract static ITapeSerializable? ConstructFrom(Legacy.LegacyDeserializer deserializer);
    }

    /// <summary>
    /// Minimal binary serializer — no reflection, no JSON.
    /// <para>Works with any <see cref="Stream"/>; employs no buffering of its own
    ///  since <see cref="TapeStream"/> already buffers. Strings are UTF-8 with a 32-bit length prefix.</para>
    /// </summary>
    /// <param name="wstream">Target stream to write serialized data to.</param>
    //[Obsolete("Legacy writer — product uses removed by Phases 4–6; moves to TapeLibNET.Tests (Design-Format-v2 §7.3)")]
    public class TapeSerializer(Stream wstream)
    {
        // Signature moved to LegacyFormat.Signature, Version moved to LegacyFormat.Version
        //  internal static readonly byte[] Signature = [(byte)'T', (byte)'F'];
        //  /// <summary>Current on-tape format version. Bumped from 0x0100 for TapeAddress (Block+Offset).</summary>
        //  public const ushort Version = 0x0101;

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
        public void Serialize(TapeAddress addr) { Serialize(addr.Block); Serialize(addr.Offset); }
        public void Serialize(DateTime dt) => Serialize(dt.Ticks);
        /// <summary>
        /// Serializes a <see cref="Guid"/> as its canonical 16-byte representation.
        /// <remarks>
        ///  Uses <see cref="Guid.ToByteArray"/> (not the generic unmanaged path) so the on-tape
        ///  layout stays defined and stable across runtimes.
        /// </remarks>
        /// </summary>
        public void Serialize(Guid guid) => Serialize(guid.ToByteArray());
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

        public void SerializeSignature(ushort version = LegacyFormat.Version)
        {
            Serialize(LegacyFormat.Signature.ToArray());
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

} // namespace TapeNET
