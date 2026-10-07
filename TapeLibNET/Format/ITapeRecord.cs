namespace TapeLibNET.Format;

/// <summary>
/// A top-level format 2.1 record that writes itself and reads itself (Design-Format-v2 §8.1).
/// Typically implemented with a <see cref="TapeSchema{T}"/> table.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface ITapeRecord<TSelf> where TSelf : ITapeRecord<TSelf>
{
    /// <summary>The record kind (§4.3); an instance member so polymorphic bases answer per subtype.</summary>
    TapeRecordKind RecordKind { get; }

    /// <summary>Writes the fields of the record body; the prologue is owned by <see cref="TapeRecordWriter"/>.</summary>
    void WriteBody(TapeFieldWriter fields);

    /// <summary>Whether this type can be built from a record of <paramref name="kind"/>.</summary>
    static abstract bool Accepts(TapeRecordKind kind);

    /// <summary>Builds an instance from the fields of a record body; <c>fields.Record.Kind</c> selects the subtype.</summary>
    /// <exception cref="TapeFormatException">Unknown critical field, missing required field, bad value ...</exception>
    static abstract TSelf ReadBody(TapeFieldReader fields);
}

/// <summary>
/// A 2.1 record that is also carried in a block frame and whose legacy ancestor may still be on tape.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface ITapeFramedRecord<TSelf> : ITapeRecord<TSelf>
    where TSelf : class, ITapeFramedRecord<TSelf>
{
    /// <summary>
    /// Parses a legacy frame ([int32 len][payload][crc32]) at the start of the block. Never throws.
    /// Implementations are one-line forwards into <c>Legacy/</c>.
    /// </summary>
    static abstract TapeFrameStatus TryReadLegacy(ReadOnlySpan<byte> block, out TSelf? record);
}
