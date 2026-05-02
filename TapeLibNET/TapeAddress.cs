using System.Globalization;

namespace TapeLibNET
{
    /// <summary>
    /// Immutable tape position: a block number and a byte offset within that block.
    /// <para>Phase 1: all addresses have <see cref="Offset"/> == 0 (one file per block boundary).
    ///  Phase 2 (packing) will produce non-zero offsets when multiple files share a block.</para>
    /// </summary>
    public readonly record struct TapeAddress(long Block, uint Offset)
    {
        /// <summary>Address at the very start of tape: block 0, offset 0.</summary>
        public static readonly TapeAddress Zero = new(0, 0);

        /// <summary>Sentinel meaning "not set / unknown".</summary>
        public static readonly TapeAddress Invalid = new(-1, 0);

        /// <summary>True when this address is not the <see cref="Invalid"/> sentinel.</summary>
        public bool IsValid => Block >= 0;

        /// <summary>True when the file starts exactly at a block boundary (offset == 0).
        ///  Always true for Phase 1 addresses.</summary>
        public bool IsAligned => Offset == 0;

        #region *** Comparison / Equality ***
        /// <summary>
        /// Compares the current TapeAddress instance with another TapeAddress and returns an integer that indicates
        /// their relative position in the sort order.
        /// </summary>
        /// <remarks>The comparison is performed first on the Block property, and if equal, then on the
        /// Offset property. This method is typically used to support sorting and ordering of TapeAddress
        /// instances. Used by the comparison operator definitions.</remarks>
        /// <param name="other">The TapeAddress to compare with the current instance.</param>
        /// <returns>A value less than zero if this instance precedes <paramref name="other"/> in the sort order; zero if they
        /// are equal; or a value greater than zero if this instance follows <paramref name="other"/>.</returns>
        public int CompareTo(TapeAddress other)
        {
            var blockCmp = Block.CompareTo(other.Block);
            if (blockCmp != 0)
                return blockCmp;

            return Offset.CompareTo(other.Offset);
        }
        public static bool operator <(TapeAddress left, TapeAddress right)
            => left.CompareTo(right) < 0;
        public static bool operator >(TapeAddress left, TapeAddress right)
            => left.CompareTo(right) > 0;
        public static bool operator <=(TapeAddress left, TapeAddress right)
            => left.CompareTo(right) <= 0;
        public static bool operator >=(TapeAddress left, TapeAddress right)
            => left.CompareTo(right) >= 0;
        #endregion

        /// <summary>
        /// Returns <c>"block"</c> when the offset is zero (Phase 1 / aligned),
        ///  or <c>"block:offset"</c> when a non-zero offset is present (Phase 2 packed files).
        /// </summary>
        public override string ToString() =>
            Offset == 0
                ? Block.ToString(CultureInfo.InvariantCulture)
                : $"{Block}:{Offset}";

        /// <summary>
        /// Parses a <see cref="TapeAddress"/> from a string produced by <see cref="ToString"/>.
        /// Accepted formats: <c>"block"</c> or <c>"block:offset"</c>.
        /// </summary>
        /// <exception cref="FormatException">Thrown when the input cannot be parsed.</exception>
        public static TapeAddress Parse(string s)
        {
            if (string.IsNullOrWhiteSpace(s))
                throw new FormatException($"Cannot parse TapeAddress from empty string.");

            int colon = s.IndexOf(':');
            if (colon < 0)
            {
                if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long block))
                    return new TapeAddress(block, 0);
            }
            else
            {
                var blockPart  = s[..colon];
                var offsetPart = s[(colon + 1)..];
                if (long.TryParse(blockPart,  NumberStyles.Integer, CultureInfo.InvariantCulture, out long block) &&
                    uint.TryParse(offsetPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out uint offset))
                    return new TapeAddress(block, offset);
            }

            throw new FormatException($"Cannot parse TapeAddress from \"{s}\".");
        }
    }
}
