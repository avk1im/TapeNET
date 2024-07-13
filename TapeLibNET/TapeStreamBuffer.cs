using System.Diagnostics;
using System.Collections;
using System.IO;


namespace TapeNET
{
    // Keeps specified number of lists of values of specified length
    class LRUDictionary<TKey, TValue> : IEnumerable<KeyValuePair<TKey, TValue?>>
        where TKey : notnull
    {
        private readonly Dictionary<TKey, LinkedList<TValue?>> m_dictionary;
        private readonly LinkedList<TKey> m_usageOrder; // head = least recently used; tail = most recently used
        private readonly int m_maxEntries; // max number of LRU-controlled entries (keys)
        private readonly int m_maxValuesPerEntry; // max number of values per key

        public LRUDictionary(int maxEntries, int maxValuesPerEntry)
        {
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxEntries, nameof(maxEntries));
            ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxValuesPerEntry, nameof(maxValuesPerEntry));

            m_maxEntries = maxEntries;
            m_maxValuesPerEntry = maxValuesPerEntry;
            m_dictionary = new(maxEntries);
            m_usageOrder = [];
        }

        // { IEnumerable<KeyValuePair<TKey, TValue?>>
        // Implementation of the generic IEnumerable<T> interface
        public IEnumerator<KeyValuePair<TKey, TValue?>> GetEnumerator()
        {
            foreach (var key in m_usageOrder)
            {
                foreach (var value in m_dictionary[key])
                {
                    yield return new KeyValuePair<TKey, TValue?>(key, value);
                }
            }
        }

        // Explicit implementation of the non-generic IEnumerable interface
        IEnumerator IEnumerable.GetEnumerator()
        {
            return GetEnumerator(); // Just call the generic version
        }
        // } IEnumerable<KeyValuePair<TKey, TValue?>>

        public void Add(TKey key, TValue? value)
        {
            if (m_dictionary.TryGetValue(key, out LinkedList<TValue?>? values))
                SetMostRecentlyUsed(key);
            else
            {
                if (m_dictionary.Count >= m_maxEntries)
                    RemoveLeastRecentlyUsed();

                values = [];
                m_dictionary.Add(key, values);
                m_usageOrder.AddLast(key); // tail = most recently used
            }

            if (values.Count >= m_maxValuesPerEntry)
                values.RemoveFirst(); // head = least recently used

            values.AddLast(value); // tail = most recently used
        }

        private void Remove(TKey key)
        {
            m_dictionary.Remove(key);
            m_usageOrder.Remove(key);
        }

        public bool TryExtractValue(TKey key, out TValue? value)
        {
            var found = m_dictionary.TryGetValue(key, out var values);
            if (found && values != null)
            {
                SetMostRecentlyUsed(key);
                value = values.LastOrDefault(); // tail = most recently used
                values.RemoveLast();

                if (values.Count == 0)
                    Remove(key);
            }
            else
                value = default;

            return found;
        }

        private void SetMostRecentlyUsed(TKey key)
        {
            if (m_dictionary.ContainsKey(key))
            {
                if (m_usageOrder.Last == null || !m_usageOrder.Last.Value.Equals(key))
                {
                    m_usageOrder.Remove(key);
                    m_usageOrder.AddLast(key); // tail = most recently used
                }
            }
        }

        private void RemoveLeastRecentlyUsed()
        {
            if (m_usageOrder.First != null) // head = least recently used
            {
                var leastUsedKey = m_usageOrder.First.Value;
                m_dictionary.Remove(leastUsedKey);
                m_usageOrder.RemoveFirst();
            }
        }
    }


    internal static class ByteBufferCache
    {
        private const int c_maxSizes = 4;
        private const int c_buffersPerSize = 2;
        private static readonly LRUDictionary<int, byte[]> s_cache = new(c_maxSizes, c_buffersPerSize);

        public static byte[] ProduceBuffer(int capacity)
        {
            if (!s_cache.TryExtractValue(capacity, out byte[]? buffer) || buffer == null)
            {
                buffer = new byte[capacity];
            }

            return buffer;
        }

        public static void RecycleBuffer(byte[] buffer)
        {
            if (buffer != null)
                s_cache.Add(buffer.Length, buffer);
        }
    }

    public class TapeByteBuffer(int capacity) : IDisposable
    {
        private readonly byte[] m_buffer = ByteBufferCache.ProduceBuffer(capacity);
        private int m_writeFrom = 0; // index of the first free element
        private int m_readFrom = 0; // index of the first occupied element
        public bool IsDisposed { get; private set; } = false;


        // implement IDisposable - do not override
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        // overridable IDisposable implementation via virtual Dispose(bool)
        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    // dispose managed resources
                    ByteBufferCache.RecycleBuffer(m_buffer);
                }
                // no unmanaged resources to dispose

                IsDisposed = true;
            }
        }
        // do not override
        ~TapeByteBuffer()
        {
            Dispose(disposing: false);
        }


        public int Capacity => capacity;
        public int ContentSize => m_writeFrom - m_readFrom;
        public bool IsEmpty => ContentSize == 0;
        public bool IsNonEmpty => !IsEmpty;
        public bool IsFull => ContentSize == Capacity;
        public int Remaining => Capacity - ContentSize;
        public void Reset()
        {
            Debug.Assert(!IsDisposed, "Attempting to reset disposed buffer");

            m_writeFrom = 0;
            m_readFrom = 0;
        }

        // For lazy content shifting to the begining of the buffer
        private int RemainingWithoutShift => capacity - m_writeFrom;
        private void ShiftContentToBegining()
        {
            if (m_readFrom > 0)
            {
                Buffer.BlockCopy(m_buffer, m_readFrom, m_buffer, 0, ContentSize); // BlockCopy can handle overlapping ranges
                m_writeFrom = ContentSize;
                m_readFrom = 0;
            }
        }
        private void MakeRoomFor(int count)
        {
            if (RemainingWithoutShift < count)
                ShiftContentToBegining();
        }

        // abstract byte sync: take count bytes from src starting at dstOffset
        protected delegate int ByteSink(byte[] src, int srcOffset, int count);
        // abstract byte source: write count bytes to dest starting at dstOffset
        protected delegate int ByteSource(byte[] dst, int dstOffset, int count);

        // Moves bytes from the beginning of the buffer (first in first out)
        //  Returns the number of bytes moved
        protected int SpillTo(ByteSink sink, int count)
        {
            int toMove = Math.Min(count, ContentSize);
            if (toMove <= 0)
                return 0;

            int moved = sink(m_buffer, m_readFrom, toMove);
            ArgumentOutOfRangeException.ThrowIfNegative(moved, nameof(moved));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(moved, toMove, nameof(moved));

            m_readFrom += moved;
            Debug.Assert(m_readFrom >= 0 && m_readFrom <= m_writeFrom);
            Debug.Assert(m_writeFrom >= 0 && m_writeFrom <= Capacity);

            return moved;
        }

        // Adds to the end of the buffer
        //  Returns the number of bytes copied
        protected int FillFrom(ByteSource source, int count)
        {
            int toMove = Math.Min(count, Remaining);
            if (toMove <= 0)
                return 0;

            MakeRoomFor(toMove);

            int moved = source(m_buffer, m_writeFrom, toMove);
            ArgumentOutOfRangeException.ThrowIfNegative(moved, nameof(moved));
            ArgumentOutOfRangeException.ThrowIfGreaterThan(moved, toMove, nameof(moved));

            m_writeFrom += moved;
            Debug.Assert(m_writeFrom >= 0 && m_writeFrom <= Capacity);
            return moved;
        }


        // Operations with byte[]
        public int SpillTo(byte[] dst, int offset, int count)
        {
            return SpillTo(
                (byte[] src, int srcOffset, int count) =>
                    { Buffer.BlockCopy(src, srcOffset, dst, offset, count); return count; },
                count);
        }

        public int FillFrom(byte[] src, int offset, int count)
        {
            return FillFrom(
                (byte[] dst, int dstOffset, int count) =>
                    { Buffer.BlockCopy(src, offset, dst, dstOffset, count); return count; },
                count);
        }


        // Zero-pad the content up to count elements, of course not exceeding Capacity
        protected void ZeroPadTo(int count)
        {
            if (count > ContentSize)
            {
                count = Math.Min(count, Capacity);
                int toPad = count - ContentSize;

                MakeRoomFor(toPad);

                Array.Clear(m_buffer, m_writeFrom, toPad);
                m_writeFrom += toPad;

                Debug.Assert(ContentSize == count);
            }
        }
    }

    // The buffer class that knows to use ReadDirect and WriteDirect methods of the TapeReadStream and TapeWriteStream
    public class TapeStreamBuffer(TapeStream owner, int capacity) : TapeByteBuffer(capacity)
    {
        /*
        // Notrhing wrong with this method, but we don't want any confusion with tape streams!
        public int FillFrom(Stream stream, int count)
        {
            return FillFrom(stream.Read, count);
        }
        */

        private int FillFrom(TapeReadStream stream, int count)
        {
            return FillFrom(stream.ReadDirect, count);
        }
        public int FillFromOwner(int count)
        {
            if (owner is TapeReadStream rstream)
                return FillFrom(rstream, count);
            else
                throw new InvalidOperationException("Attempting to fill from non-readable owner stream");
        }

        /*
        // Notrhing wrong with this method, but we don't want any confusion with tape streams!
        public int SpillTo(Stream stream, int count)
        {
            return SpillTo(stream.Write, count);
        }
        */

        private int SpillTo(TapeWriteStream stream, int count)
        {
            return SpillTo(stream.WriteDirect, count);
        }
        public int SpillToOwner(int count)
        {
            if (owner is TapeWriteStream wstream)
                return SpillTo(wstream, count);
            else
                throw new InvalidOperationException("Attempting to spill to non-writable owner stream");
        }

        private int FlushTo(TapeWriteStream stream) => SpillTo(stream, ContentSize);
        public int FlushToOwner()
        {
            if (owner is TapeWriteStream wstream)
                return FlushTo(wstream);
            else
                throw new InvalidOperationException("Attempting to Flush to non-writable owner stream");
        }

        private int SpillZeroPaddedTo(TapeWriteStream stream, int count)
        {
            ZeroPadTo(count);

            return SpillTo(stream, count);
        }
        public int SpillZeroPaddedToOwner(int count)
        {
            if (owner is TapeWriteStream wstream)
                return SpillZeroPaddedTo(wstream, count);
            else
                throw new InvalidOperationException("Attempting to spill (zero-padded) to non-writable owner stream");
        }
    } // class TapeStreamBuffer


#if OLD_STUFF
    public class TapeStreamBufferORG(TapeStream owner, int capacity) : IDisposable
    {
        private static readonly ByteBufferCache s_bufferCache = [];
        private readonly byte[] m_buffer = s_bufferCache.ProduceNewBuffer(capacity);
        private int m_topFree = 0; // index of the first free element
        protected bool IsDisposed { get; private set; } = false;


        // implement IDisposable - do not override
        public void Dispose()
        {
            Dispose(disposing: true);
            GC.SuppressFinalize(this);
        }

        // overridable IDisposable implementation via virtual Dispose(bool)
        protected virtual void Dispose(bool disposing)
        {
            if (!IsDisposed)
            {
                if (disposing)
                {
                    // dispose managed resources
                    s_bufferCache.RecycleOldBuffer(m_buffer);
                }
                // no unmanaged resources to dispose

                IsDisposed = true;
            }
        }
        // do not override
        ~TapeStreamBufferORG()
        {
            Dispose(disposing: false);
        }


        public int Capacity => capacity;
        public int ContentSize => m_topFree;
        public bool IsEmpty => ContentSize == 0;
        public bool IsNonEmpty => !IsEmpty;
        public bool IsFull => ContentSize == Capacity;
        public int Remaining => Capacity - ContentSize;
        public void Reset() => m_topFree = 0;


        // Adds to the end of the buffer
        //  Returns the number of bytes copied
        public int AddFrom(byte[] source, int offset, int count)
        {
            int toCopy = Math.Min(count, Remaining);
            if (toCopy <= 0)
                return 0;

            Buffer.BlockCopy(source, offset, m_buffer, m_topFree, toCopy);

            m_topFree += toCopy;
            Debug.Assert(m_topFree <= capacity); // same as ContentSize <= Capacity
            return toCopy;
        }

        // Moves bytes from the end of the buffer (last in first out)
        //  Returns the number of bytes moved
        public int SpillToLIFO(byte[] dest, int offset, int count)
        {
            int toMove = Math.Min(count, ContentSize);
            if (toMove <= 0)
                return 0;

            int moveFrom = m_topFree - count;
            Debug.Assert(moveFrom >= 0);

            Buffer.BlockCopy(m_buffer, moveFrom, dest, offset, toMove);

            m_topFree = moveFrom;
            Debug.Assert(m_topFree >= 0);
            return toMove;
        }

        // Moves bytes from the beginning of the buffer (first in first out)
        //  Returns the number of bytes moved
        public int SpillToFIFO(byte[] dest, int offset, int count)
        {
            int toMove = Math.Min(count, ContentSize);
            if (toMove <= 0)
                return 0;

            Buffer.BlockCopy(m_buffer, 0, dest, offset, toMove);

            // shift the remaining content, if any, to the begining
            if (m_topFree > toMove)
                Buffer.BlockCopy(m_buffer, toMove, m_buffer, 0, m_topFree - toMove); // BlockCopy can handle overlapping ranges

            m_topFree -= toMove;
            Debug.Assert(m_topFree >= 0);
            return toMove;
        }


        private delegate int ByteSink(int srcOffset, int count);
        private delegate int ByteSource(int srcOffset, int count);

        // Moves bytes from the beginning of the buffer (first in first out)
        //  Returns the number of bytes moved
        private int SpillTo(ByteSink sink, int count)
        {
            int toMove = Math.Min(count, ContentSize);
            if (toMove <= 0)
                return 0;

            int moved = sink(0, toMove);

            // shift the remaining content, if any, to the begining
            if (m_topFree > moved)
                Buffer.BlockCopy(m_buffer, moved, m_buffer, 0, m_topFree - moved); // BlockCopy can handle overlapping ranges

            m_topFree -= moved;
            Debug.Assert(m_topFree >= 0 && m_topFree <= Capacity);
            return moved;
        }

        private int FillFrom(ByteSource source, int count)
        {
            int toMove = Math.Min(count, Remaining);
            if (toMove <= 0)
                return 0;

            int moved = source(0, toMove);

            m_topFree += moved;
            Debug.Assert(m_topFree >= 0 && m_topFree <= Capacity);
            return moved;
        }

        /*
        // Notrhing wrong with this method, but we needn't work with generic streams [yet?] and don't want any confusion with tape streams!
        public int SpillTo(Stream stream, int count)
        {
            return SpillTo((int srcOffset, int count) => { stream.Write(m_buffer, srcOffset, count); return count; }, count);
        }
        */

        private int SpillTo(TapeWriteStream stream, int count)
        {
            return SpillTo((int srcOffset, int count) => stream.WriteDirect(m_buffer, srcOffset, count), count);
        }
        public int SpillToOwner(int count)
        {
            if (owner is TapeWriteStream wstream)
                return SpillTo(wstream, count);
            else
                throw new InvalidOperationException("Attempting to spill to non-writable owner stream");
        }

        private int FillFrom(TapeReadStream stream, int count)
        {
            return FillFrom((int srcOffset, int count) => stream.ReadDirect(m_buffer, srcOffset, count), count);
        }
        public int FillFromOwner(int count)
        {
            if (owner is TapeReadStream rstream)
                return FillFrom(rstream, count);
            else
                throw new InvalidOperationException("Attempting to fill from non-readable owner stream");
        }

        private int SpillZeroPaddedTo(TapeWriteStream stream, int count)
        {
            if (count > ContentSize)
            {
                // if count is beyond ContentSize, then zero-pad the content up to count, of course not exceeding Capacity
                count = Math.Min(count, Capacity);
                Debug.Assert(count > m_topFree);

                Array.Clear(m_buffer, m_topFree, count - m_topFree);
                m_topFree = count;
            }

            return SpillTo(stream, count);
        }
        public int SpillZeroPaddedToOwner(int count)
        {
            if (owner is TapeWriteStream wstream)
                return SpillZeroPaddedTo(wstream, count);
            else
                throw new InvalidOperationException("Attempting to spill (zero-padded) to non-writable owner stream");
        }

        public int SpillTo(byte[] dest, int offset, int count)
        {
            return SpillTo((int srcOffset, int count) => { Buffer.BlockCopy(m_buffer, srcOffset, dest, offset, count); return count; }, count);
        }

        private int FlushTo(TapeWriteStream stream) => SpillTo(stream, ContentSize);
        public int FlushToOwner()
        {
            if (owner is TapeWriteStream wstream)
                return FlushTo(wstream);
            else
                throw new InvalidOperationException("Attempting to Flush to non-writable owner stream");
        }


    } // class Buffer.ORG
#endif
}
