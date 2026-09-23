using System.Text.RegularExpressions;

namespace EveDeck.Utilities;

// Scrubs identifying strings out of the "Copy Diagnostics" report before it reaches the clipboard.
// That text is meant to be pasted into a public GitHub issue or Discord, and EveDeck is used across
// the EVE community: a character or system name in a bug report is an intel leak, not just PII.
//
// Fails closed where it can. Structural fields (window titles, seat labels, paths) are replaced
// outright by the caller; this pass then catches the same names wherever else they surfaced, such
// as free-text error lines. Names are matched whole-word and case-insensitively, longest first, so
// "Bob Alt" is replaced before "Bob" gets a chance to leave " Alt" behind.
internal static class DiagnosticsRedactor
{
    public static string Redact(
        string text,
        IEnumerable<string> characterNames,
        IEnumerable<string> systemNames,
        IEnumerable<string> userNames)
    {
        var rules = new List<(string Value, string Replacement)>();
        rules.AddRange(Distinct(characterNames).Select(n => (n, "<character>")));
        rules.AddRange(Distinct(systemNames).Select(n => (n, "<system>")));
        rules.AddRange(Distinct(userNames).Select(n => (n, "<user>")));

        foreach (var (value, replacement) in rules.OrderByDescending(r => r.Value.Length))
        {
            // \w lookarounds instead of \b: EVE names can start or end with ' or -, where \b
            // would refuse to match at all.
            var pattern = $@"(?<!\w){Regex.Escape(value)}(?!\w)";
            text = Regex.Replace(text, pattern, replacement, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
        }
        return text;
    }

    // Replaces the user-profile prefix of a path, which carries the Windows account name.
    public static string RedactPath(string path)
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return !string.IsNullOrEmpty(profile) && path.StartsWith(profile, StringComparison.OrdinalIgnoreCase)
            ? "%USERPROFILE%" + path[profile.Length..]
            : path;
    }

    private static IEnumerable<string> Distinct(IEnumerable<string> values) =>
        values.Select(v => v?.Trim() ?? "")
              .Where(v => v.Length > 0)
              .Distinct(StringComparer.OrdinalIgnoreCase);
}
