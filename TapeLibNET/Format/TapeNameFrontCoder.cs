namespace TapeLibNET.Format;

/// <summary>
/// Front coding of file names within one TOC batch (Design-Format-v2 §5.1): each name stores the number of UTF-16
///  chars it shares with the previous name plus the differing tail. Stateful; <see cref="Reset"/> at every batch
///  start so batches stay self-contained. One implementation serves writer and reader.
/// </summary>
public sealed class TapeNameFrontCoder
{
    private string m_previous = "";

    /// <summary>Forgets the previous name: the next entry has no shared prefix.</summary>
    public void Reset() => m_previous = "";

    /// <summary>Encodes <paramref name="name"/> against the previous name and remembers it.</summary>
    /// <param name="shared">UTF-16 chars shared with the previous name; never splits a surrogate pair.</param>
    /// <returns>The differing tail (<c>name[shared..]</c>).</returns>
    public ReadOnlySpan<char> Encode(string name, out int shared)
    {
        ArgumentNullException.ThrowIfNull(name);

        int limit = Math.Min(name.Length, m_previous.Length);
        int n = 0;
        while (n < limit && name[n] == m_previous[n])
            n++;

        // Never end the shared part inside a surrogate pair (the split must stay valid UTF-16 on both sides).
        if (n > 0 && n < name.Length && char.IsHighSurrogate(name[n - 1]) && char.IsLowSurrogate(name[n]))
            n--;

        m_previous = name;
        shared = n;
        return name.AsSpan(n);
    }

    /// <summary>Rebuilds the full name from <paramref name="shared"/> and <paramref name="suffix"/> and remembers it.</summary>
    /// <exception cref="TapeFormatException"><paramref name="shared"/> exceeds the previous name, or splits a surrogate pair.</exception>
    public string Decode(int shared, string suffix)
    {
        ArgumentNullException.ThrowIfNull(suffix);

        if (shared < 0 || shared > m_previous.Length)
            throw TapeFormatException.Bad($"shared prefix of {shared} chars exceeds the previous name ({m_previous.Length})");
        if (shared > 0 && shared < m_previous.Length && char.IsHighSurrogate(m_previous[shared - 1]) && char.IsLowSurrogate(m_previous[shared]))
            throw TapeFormatException.Bad("shared prefix splits a surrogate pair");

        string name = shared == 0 ? suffix : string.Concat(m_previous.AsSpan(0, shared), suffix);
        m_previous = name;
        return name;
    }
}
