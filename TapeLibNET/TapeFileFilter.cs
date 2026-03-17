using System.Text.RegularExpressions;

namespace TapeLibNET
{
    /// <summary>
    /// Abstraction for filtering tape files by their descriptor.
    /// Implementations decide the matching strategy (wildcards, FCL expressions, etc.).
    /// </summary>
    public interface ITapeFileFilter
    {
        /// <summary>
        /// Returns <c>true</c> if the file described by <paramref name="fileDescr"/> passes the filter.
        /// </summary>
        bool Matches(in TapeFileDescriptor fileDescr);
    }

    /// <summary>
    /// Built-in <see cref="ITapeFileFilter"/> that matches files against DOS-style
    /// wildcard patterns (<c>*</c>, <c>?</c>). Patterns are pre-compiled to regex
    /// in the constructor for efficient repeated evaluation.
    /// </summary>
    /// <remarks>
    /// Plain file names (no wildcards) use a <see cref="HashSet{T}"/>-based O(1) lookup
    /// for maximum performance.
    /// </remarks>
    public sealed class WildcardFileFilter : ITapeFileFilter
    {
        private readonly List<Regex>? _regexPatterns;       // wildcard path, compiled regex
        private readonly HashSet<string>? _plainNames;       // non-wildcard path, O(1) lookup

        /// <summary>
        /// Creates a wildcard filter from a list of patterns.
        /// Patterns may contain <c>*</c> and <c>?</c> wildcards; a trailing <c>\</c>
        /// selects all files in the directory.
        /// </summary>
        /// <param name="patterns">DOS-style wildcard or plain file-name patterns.</param>
        public WildcardFileFilter(List<string> patterns)
        {
            if (TapeSetTOC.PatternsHaveWildcards(patterns))
            {
                // Pre-compile to Regex objects for repeated evaluation
                _regexPatterns = [.. TapeSetTOC.FromFilePatternsToRegexPatterns(patterns)
                    .Select(p => new Regex(p, RegexOptions.IgnoreCase | RegexOptions.Compiled))
                    ];
            }
            else
            {
                // Fast path: plain file names without wildcards
                _plainNames = new HashSet<string>(patterns, StringComparer.OrdinalIgnoreCase);
            }
        }

        /// <inheritdoc />
        public bool Matches(in TapeFileDescriptor fileDescr)
        {
            var fullName = fileDescr.FullName;

            if (_regexPatterns is not null)
                return _regexPatterns.Exists(r => r.IsMatch(fullName));

            return _plainNames is not null && _plainNames.Contains(fullName);
        }
    }
}
