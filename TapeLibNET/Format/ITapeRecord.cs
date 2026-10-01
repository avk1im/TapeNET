namespace TapeLibNET.Format;

/// <summary>
/// A top-level format 2.1 record that writes itself and reads itself (Design-Format-v2 §8.1).
/// Typically implemented with a <see cref="TapeSchema{T}"/> table.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface ITapeRecord<TSelf> where TSelf : ITapeRecord<TSelf>
{
    /// <summary>The record kind (§4.3).</summary>
    static abstract TapeRecordKind Kind { get; }

    /// <summary>Writes the complete record (prologue + body) through <paramref name="writer"/>.</summary>
    void WriteTo(TapeRecordWriter writer);

    /// <summary>Builds an instance from the fields of a record body.</summary>
    /// <exception cref="TapeFormatException">Unknown critical field, missing required field, bad value ...</exception>
    static abstract TSelf ReadFrom(TapeFieldReader fields);
}

/// <summary>
/// A 2.1 record that is also carried in a block frame and whose legacy ancestor may still be on tape.
/// </summary>
/// <typeparam name="TSelf">The implementing type.</typeparam>
public interface ITapeFramedRecord<TSelf> : ITapeRecord<TSelf> where TSelf : ITapeFramedRecord<TSelf>
{
    /// <summary>
    /// Reads the legacy (pre-2.1) block-frame payload; <see langword="null"/> when it is not one.
    /// Implementations are one-line forwards into <c>Legacy/</c>.
    /// </summary>
    static abstract TSelf? ReadLegacy(ReadOnlySpan<byte> block);
}
