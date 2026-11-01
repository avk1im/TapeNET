using System.CommandLine;
using System.Text;
using TapeConNET.Infrastructure;
using TapeConNET.Ux;
using TapeLibNET.Compression;
using TapeLibNET.Encryption;

namespace TapeConNET.Cli;

/// <summary>
/// Password sources and encryption choices of the CLI (Design-Encryption §11.2). A password comes from a file or an
///  environment variable — never from the command line, where it would land in the shell history and the process list.
///  Absent both, the service asks through <see cref="ConsoleUxServiceHost.OnAskPassword"/> (masked; skipped unattended).
/// </summary>
/// <remarks>
/// <c>--password</c> exists only to refuse itself with advice: a user who reaches for it learns the safe form instead of
///  an "unknown option" error.
/// </remarks>
internal static class PasswordOptions
{
    #region *** Options ***

    public static readonly Option<string?> PasswordFile = new("--password-file")
    {
        Description = "Read the password from this file (UTF-8; one trailing line break is ignored).",
    };

    public static readonly Option<string?> PasswordEnv = new("--password-env")
    {
        Description = "Read the password from this environment variable.",
    };

    public static readonly Option<string?> Password = new("--password")
    {
        Description = "Not supported: a password on the command line is visible in the shell history and the process " +
                      "list. Use --password-file or --password-env.",
    };

    public static readonly Option<string?> NewPasswordFile = new("--new-password-file")
    {
        Description = "Change the set's password: read the new one from this file.",
    };

    public static readonly Option<string?> NewPasswordEnv = new("--new-password-env")
    {
        Description = "Change the set's password: read the new one from this environment variable.",
    };

    public static readonly Option<string?> NewPassword = new("--new-password")
    {
        Description = "Not supported — use --new-password-file, --new-password-env, or --change-password.",
    };

    /// <summary>Attaches the password sources; with <paramref name="withNew"/> also the new-password ones (rename).</summary>
    public static void Attach(Command cmd, bool withNew = false)
    {
        cmd.Options.Add(PasswordFile);
        cmd.Options.Add(PasswordEnv);
        cmd.Options.Add(Password);
        if (!withNew)
            return;
        cmd.Options.Add(NewPasswordFile);
        cmd.Options.Add(NewPasswordEnv);
        cmd.Options.Add(NewPassword);
    }

    #endregion

    #region *** Resolution ***

    /// <summary>The password from <c>--password-file</c> / <c>--password-env</c>; null when neither is given.</summary>
    /// <exception cref="TapeConException">UsageError: <c>--password</c>, both sources, an unreadable or empty source.</exception>
    public static char[]? Resolve(ParseResult parse)
        => ResolveCore(parse, Password, PasswordFile, PasswordEnv, "password");

    /// <summary>The new password from <c>--new-password-file</c> / <c>--new-password-env</c>; null when neither is given.</summary>
    public static char[]? ResolveNew(ParseResult parse)
        => ResolveCore(parse, NewPassword, NewPasswordFile, NewPasswordEnv, "new password");

    private static char[]? ResolveCore(ParseResult parse, Option<string?> literal, Option<string?> file,
        Option<string?> env, string what)
    {
        if (parse.GetResult(literal) is not null)
            throw new TapeConException(TapeConExitCode.UsageError,
                $"{literal.Name} is not supported: a {what} on the command line is visible in the shell history and " +
                $"the process list. Use {file.Name} <path> or {env.Name} <VARIABLE> instead — or omit both to be asked.");

        string? path = parse.GetValue(file);
        string? variable = parse.GetValue(env);
        if (path is not null && variable is not null)
            throw new TapeConException(TapeConExitCode.UsageError, $"{file.Name} and {env.Name} are mutually exclusive.");

        string? text;
        if (path is not null)
        {
            try
            {
                text = StripOneLineBreak(File.ReadAllText(path, new UTF8Encoding(false, throwOnInvalidBytes: true)));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DecoderFallbackException)
            {
                throw new TapeConException(TapeConExitCode.UsageError, $"Couldn't read the {what} file >{path}<: {ex.Message}");
            }
            if (text.Length == 0)
                throw new TapeConException(TapeConExitCode.UsageError, $"The {what} file >{path}< is empty.");
        }
        else if (variable is not null)
        {
            text = Environment.GetEnvironmentVariable(variable);
            if (string.IsNullOrEmpty(text))
                throw new TapeConException(TapeConExitCode.UsageError,
                    $"The environment variable {variable} is not set, or empty.");
        }
        else
            return null;

        return text.ToCharArray();      // the string itself cannot be cleared — hygiene, not protection (§2.2)
    }

    /// <summary>Removes one trailing <c>\r\n</c> or <c>\n</c> — nothing else: a password may end in a space.</summary>
    internal static string StripOneLineBreak(string text)
        => text.EndsWith("\r\n", StringComparison.Ordinal) ? text[..^2]
         : text.EndsWith('\n') ? text[..^1]
         : text;

    #endregion

    #region *** Interactive new password (rename-set --change-password) ***

    /// <summary>Asks for a new password twice, masked; null when the user declined or the entries differ three times.</summary>
    /// <exception cref="TapeConException">UsageError when the console cannot ask (non-interactive / quiet).</exception>
    public static char[]? AskNew(IConsoleUx ux, string setDisplay)
    {
        if (ux.NonInteractive || ux.QuietMode)
            throw new TapeConException(TapeConExitCode.UsageError,
                "--change-password needs an interactive console — use --new-password-file or --new-password-env.");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            string? first = ux.AskSecret($"New password for {setDisplay}");
            if (string.IsNullOrEmpty(first))
                return null;
            string? again = ux.AskSecret("Repeat the new password");
            if (first == again)
                return first.ToCharArray();
            ux.Log(WarningLevel.Warning, "The passwords do not match — try again.");
        }
        return null;
    }

    #endregion
}

/// <summary>
/// The CLI's encryption and compression words (Design-Encryption §11.2). "hardware" and "drive" are synonyms in both, so
///  <c>--compression drive</c> and <c>--encryption hardware</c> read as naturally as their twins.
/// </summary>
internal static class SchemeWords
{
    /// <summary>The encryption choices, for help texts.</summary>
    public const string EncryptionChoices = "none, software, hardware | drive";

    /// <summary>Parses an encryption word.</summary>
    public static bool TryParseEncryption(string? word, out TapeEncryption scheme)
    {
        scheme = (word ?? "").Trim().ToLowerInvariant() switch
        {
            "none" or "off" or "no" => TapeEncryption.None,
            "software" or "sw" or "aes" or "on" or "yes" => TapeEncryption.Software,
            "hardware" or "hw" or "drive" => TapeEncryption.Hardware,
            _ => (TapeEncryption)(-1),
        };
        return (int)scheme >= 0;
    }

    /// <summary>
    /// Maps the compression synonyms onto the words <see cref="CompressionPreset.TryParse"/> knows: "drive" and "hw" →
    ///  "hardware". Everything else passes through unchanged.
    /// </summary>
    public static string NormalizeCompression(string? spec)
        => (spec ?? "").Trim().ToLowerInvariant() is "drive" or "hw" ? "hardware" : spec ?? "";
}
