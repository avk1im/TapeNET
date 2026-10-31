using System.Security.Cryptography;

namespace TapeLibNET.Encryption;

/// <summary>
/// The one indirection between the encryption code and its two environmental inputs: randomness and the PBKDF2 work
///  factor at creation time. Everything else in <c>TapeLibNET.Encryption</c> is a pure function of its arguments.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why an indirection at all.</b> Goldens must be byte-reproducible (Design-Encryption §15.7), and thousands of
///  tests cannot afford 600,000 PBKDF2 iterations per encrypted set. Both are solved by SCOPED overrides, available only
///  under <c>TEST_HOOKS</c> — a product build has no way to weaken either.
/// </para>
/// <para>
/// <b>Scoped by <see cref="AsyncLocal{T}"/>.</b> xUnit runs test classes in parallel; a static override would leak from
///  one test into another. An <see cref="AsyncLocal{T}"/> follows the logical flow of the test that set it, including
///  into the tasks it starts, and nowhere else.
/// </para>
/// <para>
/// <b>Readers never consult <see cref="CreationIterations"/>.</b> Unwrapping uses whatever count the envelope recorded,
///  so a set written under a test override still opens in a product build, and vice versa (Design-Encryption §4.1).
/// </para>
/// </remarks>
internal static class TapeCrypto
{
    #region *** Constants ***

    /// <summary>PBKDF2-HMAC-SHA256 iterations used when a NEW envelope is created (Design-Encryption §4.1).</summary>
    public const int DefaultIterations = 600_000;

    /// <summary>
    /// Upper bound a READER accepts from an envelope. A tampered envelope must not be able to stall an unlock for hours;
    ///  the bound is far above any count this build or a foreseeable successor writes.
    /// </summary>
    public const int MaxIterations = 50_000_000;

    #endregion

#if TEST_HOOKS
    #region *** Test overrides ***

    private static readonly AsyncLocal<Random?> s_random = new();
    private static readonly AsyncLocal<int?> s_iterations = new();

    /// <summary>
    /// Makes <see cref="Fill"/> deterministic for the current logical flow until the returned scope is disposed.
    /// </summary>
    /// <remarks>
    /// Backed by <see cref="Random(int)"/>, which is stable across runs, machines and .NET versions for a given seed —
    ///  the same property <c>GoldenV21</c> already relies on. NEVER cryptographically strong; test use only.
    /// </remarks>
    public static IDisposable UseDeterministicRandom(int seed)
    {
        Random? previous = s_random.Value;
        s_random.Value = new Random(seed);
        return new Scope(() => s_random.Value = previous);
    }

    /// <summary>Overrides <see cref="CreationIterations"/> for the current logical flow until the scope is disposed.</summary>
    public static IDisposable UseIterations(int iterations)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(iterations, 1);
        int? previous = s_iterations.Value;
        s_iterations.Value = iterations;
        return new Scope(() => s_iterations.Value = previous);
    }

    private sealed class Scope(Action restore) : IDisposable
    {
        private Action? m_restore = restore;

        public void Dispose()
        {
            m_restore?.Invoke();
            m_restore = null;
        }
    }

    #endregion
#endif

    #region *** Public API ***

    /// <summary>
    /// Fills <paramref name="data"/> with cryptographically strong random bytes — or, inside a
    ///  <c>UseDeterministicRandom</c> scope (TEST_HOOKS), with the scope's reproducible sequence.
    /// </summary>
    public static void Fill(Span<byte> data)
    {
#if TEST_HOOKS
        if (s_random.Value is { } random)
        {
            random.NextBytes(data);
            return;
        }
#endif
        RandomNumberGenerator.Fill(data);
    }

    /// <summary>
    /// PBKDF2 iterations for a NEW envelope: <see cref="DefaultIterations"/>, or the <c>UseIterations</c> scope's value
    ///  (TEST_HOOKS).
    /// </summary>
    public static int CreationIterations
    {
        get
        {
#if TEST_HOOKS
            if (s_iterations.Value is { } iterations)
                return iterations;
#endif
            return DefaultIterations;
        }
    }

    #endregion
}