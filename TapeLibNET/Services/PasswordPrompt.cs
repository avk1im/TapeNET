namespace TapeLibNET.Services;

/// <summary>Why the service asks for a password (Design-Encryption §10.4) — drives the host's wording.</summary>
public enum PasswordPurpose
{
    /// <summary>A new encrypted set: choose its password.</summary>
    NewSet,
    /// <summary>A new encrypted set: repeat the password.</summary>
    NewSetConfirm,
    /// <summary>Open a locked set (restore, validate, verify, listing, explicit unlock).</summary>
    Unlock,
    /// <summary>An incremental backup on an encrypted set: that set's password.</summary>
    Incremental,
    /// <summary>Rename or change the password of an encrypted set: its current password.</summary>
    Rename,
    /// <summary>A password change: the new password.</summary>
    NewPassword,
    /// <summary>A password change: repeat the new password.</summary>
    NewPasswordConfirm,
}

/// <summary>One password prompt (Design-Encryption §10.4).</summary>
/// <param name="Purpose">Why the password is needed.</param>
/// <param name="SetIndex">Standard (1-based) index of the set concerned.</param>
/// <param name="AltIndex">Alternative (0, −1, …) index of the set.</param>
/// <param name="SetDisplay">The set's name for display — <c>DisplayName</c>, never a hidden description.</param>
/// <param name="Attempt">1-based attempt of this prompt.</param>
/// <param name="LastFailure">Why the previous attempt failed (wrong password, mismatch); null on the first.</param>
public readonly record struct PasswordRequest(
    PasswordPurpose Purpose,
    int SetIndex,
    int AltIndex,
    string SetDisplay,
    int Attempt,
    TapeResult? LastFailure);
