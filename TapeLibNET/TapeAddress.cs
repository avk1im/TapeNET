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
