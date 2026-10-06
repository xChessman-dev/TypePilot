using System.Text.RegularExpressions;

namespace TypePilot.Core;

public static partial class RewriteGuard
{
    public static IReadOnlyList<string> MissingDetails(string original, string result)
    {
        var missing = new List<string>();
        foreach (Match match in NumericDetails().Matches(original))
            if (!NumericDetails().Matches(result).Any(found => found.Value == match.Value)) missing.Add(match.Value);
        foreach (var marker in new[] { "сегодня", "завтра", "вчера", "послезавтра", "позавчера" })
            if (ContainsWord(original, marker) && !ContainsWord(result, marker)) missing.Add(marker);
        foreach (Match match in Links().Matches(original))
            if (!result.Contains(match.Value, StringComparison.Ordinal)) missing.Add(match.Value);
        return missing.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }
    private static bool ContainsWord(string text, string word) => TextEngine.WordPattern().Matches(text).Any(m => string.Equals(m.Value, word, StringComparison.OrdinalIgnoreCase));
    [GeneratedRegex(@"\d+(?:[.,:/-]\d+)*", RegexOptions.CultureInvariant)] private static partial Regex NumericDetails();
    [GeneratedRegex(@"https?://[^\s]+|[\w.+-]+@[\w.-]+\.[a-zA-Z]{2,}", RegexOptions.CultureInvariant)] private static partial Regex Links();
}
