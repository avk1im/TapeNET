namespace TapeLoc.Cli;

// Process exit codes (docs/Design-TapeLoc.md §3). Exit code 1 (validation
//  failure) is what gates the packaging build.
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int ValidationFailed = 1;
    public const int ConfigError = 2;
    public const int ProviderError = 3;
    public const int SourceNotFound = 4;
}
