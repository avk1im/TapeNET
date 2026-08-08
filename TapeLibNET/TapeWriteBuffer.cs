using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TapeLibNET;

/// <summary>
/// A pooled, system-page-aligned write buffer backed by a Pinned-Object-Heap (POH) array.
/// <para>
/// The SPTD write path can DMA directly from a page-aligned buffer, skipping the intermediate copy into
/// native scratch. A POH array never moves, so its data address is stable for life; we over-allocate by
/// one page and expose a page-aligned WINDOW of <see cref="Capacity"/> bytes. The window's internal start
/// offset is a private detail — the high layer addresses the window with 0-based positions and never sees it.
/// </para>
/// <para>
/// This is the storable rental handle (it survives the packer's buffer rotation and the worker-thread
/// handoff), which is why <c>Rent</c> returns this object rather than a <see cref="Span{T}"/> — a ref struct
/// cannot be stored in a field. The SCSI layer never references this type: the write glue passes the plain
/// backing array + window offset (both <c>internal</c>), and the SPTD path auto-detects page alignment from
/// the pinned pointer, so no "isAligned" flag has to cross the boundary.
/// </para>
/// </summary>
public sealed class TapeWriteBuffer
{
    private readonly byte[] _array;
    private readonly int _offset;   // page-aligned window start within _array

    internal TapeWriteBuffer(byte[] array, int offset, int capacity, TapeWriteBufferPool owner)
    {
        _array = array;
        _offset = offset;
        Capacity = capacity;
        Owner = owner;
    }

    /// <summary>Usable capacity (bytes) of the window. Also the pool bucket key.</summary>
    public int Capacity { get; }

    /// <summary>True: the window start is page-aligned (guaranteed by the pool).</summary>
    public static bool IsPageAligned => true;

    internal TapeWriteBufferPool Owner { get; }

    // Backing store + window offset, for the write glue only (drive.WriteDirect is byte[]-based, and the
    //  fixed-buffer pin in the SPTD path needs the real array). Internal, so it stays out of the public
    //  surface — no offset "laundry" leaks to callers.
    internal byte[] Array => _array;
    internal int Offset => _offset;

    // -----------------------------------------------------------------------
    //  Window operations — all positions are 0-based within the usable window.
    //  These replace the packer's direct Buffer.BlockCopy / Array.Clear calls so
    //  the private offset stays encapsulated.
    // -----------------------------------------------------------------------

    /// <summary>Copies <paramref name="src"/> into the window starting at <paramref name="destPos"/>.</summary>
    public void CopyFrom(ReadOnlySpan<byte> src, int destPos)
        => src.CopyTo(_array.AsSpan(_offset + destPos, Capacity - destPos));

    /// <summary>Zero-fills <paramref name="length"/> bytes of the window starting at <paramref name="start"/>.</summary>
    public void Clear(int start, int length)
        => System.Array.Clear(_array, _offset + start, length);

    /// <summary>
    /// Copies a region of THIS window into the START of <paramref name="dest"/>'s window — used to carry
    /// the trailing sub-block bytes over when the packer rotates in a fresh fill buffer.
    /// </summary>
    public void CopyRegionTo(TapeWriteBuffer dest, int srcStart, int length)
        => _array.AsSpan(_offset + srcStart, length).CopyTo(dest._array.AsSpan(dest._offset, length));

    /// <summary>
    /// The first <paramref name="length"/> bytes of the (page-aligned) window, as a span. Transient — do not
    /// store; the backing array is POH so it never moves during the write. Provided for span-based consumers;
    /// the byte[]-based write path uses the internal <see cref="Array"/>/<see cref="Offset"/> instead.
    /// </summary>
    public Span<byte> Data(int length) => _array.AsSpan(_offset, length);

    /// <summary>Returns this buffer to its owning pool for reuse.</summary>
    public void Return() => Owner.Return(this);
}

/// <summary>
/// Pool of page-aligned <see cref="TapeWriteBuffer"/> instances, bucketed by (page-rounded) capacity.
/// Supports several live rentals of the same size (e.g. the packer's double-buffering), so each bucket is a
/// stack of free buffers rather than a single slot. Rent/Return are cheap and thread-safe.
/// <para>
/// Backed by POH arrays (<see cref="GC.AllocateArray{T}(int, bool)"/>): real <see cref="byte"/>[] that never
/// move and are GC-reclaimed — no manual free, no leak on exception paths. Keep the pooled count small; POH
/// is not compacted.
/// </para>
/// </summary>
public sealed class TapeWriteBufferPool : IDisposable
{
    private readonly object _lock = new();
    private readonly Dictionary<int, Stack<TapeWriteBuffer>> _free = [];
    private readonly ILogger _logger;
    private bool _disposed;

    /// <summary>System page size used for alignment (defaults to <see cref="Environment.SystemPageSize"/>).</summary>
    public int PageSize { get; }

    public TapeWriteBufferPool(ILogger? logger = null, int? pageSize = null)
    {
        PageSize = pageSize ?? Environment.SystemPageSize;
        if (PageSize <= 0 || (PageSize & (PageSize - 1)) != 0)
            throw new ArgumentException("Page size must be a positive power of two.", nameof(pageSize));

        _logger = logger ?? NullLogger.Instance;
    }

    /// <summary>
    /// Rents a page-aligned buffer whose <see cref="TapeWriteBuffer.Capacity"/> is at least
    /// <paramref name="minimumCapacity"/> (rounded up to a whole page). Reuses a free buffer of the same
    /// bucket if available, else allocates a new POH-pinned array.
    /// </summary>
    public TapeWriteBuffer Rent(int minimumCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(minimumCapacity);
        int bucket = RoundUpToPage(minimumCapacity);

        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_free.TryGetValue(bucket, out Stack<TapeWriteBuffer>? stack) && stack.Count > 0)
                return stack.Pop();
        }

        // Allocate outside the lock: POH allocation can be relatively expensive.
        return Allocate(bucket);
    }

    /// <summary>Returns a previously rented buffer for reuse. Buffers from a disposed pool are dropped (GC-reclaimed).</summary>
    public void Return(TapeWriteBuffer buffer)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (!ReferenceEquals(buffer.Owner, this))
            throw new ArgumentException("Buffer was not rented from this pool.", nameof(buffer));

        lock (_lock)
        {
            if (_disposed)
                return; // let the GC reclaim the POH array

            if (!_free.TryGetValue(buffer.Capacity, out Stack<TapeWriteBuffer>? stack))
                _free[buffer.Capacity] = stack = new Stack<TapeWriteBuffer>();
            stack.Push(buffer);
        }
    }

    private TapeWriteBuffer Allocate(int bucket)
    {
        // Over-allocate by one page so a page-aligned window of `bucket` bytes always fits regardless of
        //  where the POH placed the array's first element.
        byte[] array = GC.AllocateArray<byte>(bucket + PageSize, pinned: true);
        int offset = ComputeAlignedOffset(array, PageSize);
        _logger.LogTrace("TapeWriteBufferPool: allocated POH buffer bucket={Bucket} offset={Offset}", bucket, offset);
        return new TapeWriteBuffer(array, offset, bucket, this);
    }

    // Reads the (stable, since POH) data address and returns the offset to the next page boundary.
    private static unsafe int ComputeAlignedOffset(byte[] array, int pageSize)
    {
        ref byte r0 = ref MemoryMarshal.GetArrayDataReference(array);
        nint addr = (nint)Unsafe.AsPointer(ref r0);
        int misalign = (int)(addr & (pageSize - 1));
        return misalign == 0 ? 0 : pageSize - misalign;
    }

    private int RoundUpToPage(int n) => (n + PageSize - 1) & ~(PageSize - 1);

    public void Dispose()
    {
        lock (_lock)
        {
            _disposed = true;
            _free.Clear(); // POH arrays are reclaimed by the GC once unreferenced
        }
    }
}
