using System.Globalization;
using System.Text.RegularExpressions;

namespace TypePilot.Core;

public sealed record Suggestion(string Word, string Reason, bool Automatic = false);
public sealed record TextEdit(string Before, string After, int Start, string Original, string Replacement, int Caret);

public sealed partial class TextEngine
{
    private readonly Dictionary<string, int> _words;
    private readonly Dictionary<string, string> _aliases;
    private readonly HashSet<string> _personal = new(StringComparer.OrdinalIgnoreCase);
    private static readonly CultureInfo Russian = CultureInfo.GetCultureInfo("ru-RU");
    public TextEngine(IEnumerable<string> words, IEnumerable<KeyValuePair<string, string>>? aliases = null)
    {
        _words = new(StringComparer.OrdinalIgnoreCase);
        var rank = 100000;
        foreach (var word in words.Where(IsWord)) _words.TryAdd(Normalize(word), rank--);
        _aliases = (aliases ?? []).ToDictionary(x => Normalize(x.Key), x => x.Value, StringComparer.OrdinalIgnoreCase);
    }
    public void SetPersonal(IEnumerable<string> words)
    {
        _personal.Clear();
        foreach (var word in words.Where(IsWord)) _personal.Add(Normalize(word));
    }
    public bool IsKnown(string word) => _words.ContainsKey(Normalize(word)) || _personal.Contains(Normalize(word));
    public static bool IsWord(string word) => word.Length is >= 2 and <= 48 && word.All(char.IsLetter);
    private static string Normalize(string word) => word.ToLowerInvariant().Replace('ё', 'е');
    public IReadOnlyList<Suggestion> Suggest(string word, int limit = 3)
    {
        if (!IsWord(word) || limit < 1 || limit > 10) return [];
        var normalized = Normalize(word);
        if (_personal.Contains(normalized)) return [];
        if (_aliases.TryGetValue(normalized, out var mapped)) return [new(MatchCase(word, mapped), "Частая опечатка", true)];
        if (IsKnown(word)) return [];
        var layout = SwapLayout(word);
        if (layout != word && IsKnown(layout)) return [new(MatchCase(word, layout), "Другая раскладка", true)];
        var maxDistance = word.Length >= 7 ? 2 : 1;
        var candidates = _personal.Concat(_words.Keys)
            .Where(w => Math.Abs(w.Length - normalized.Length) <= maxDistance)
            .Select(w => (Word: w, Distance: Distance(normalized, w)))
            .Where(x => x.Distance <= maxDistance)
            .OrderBy(x => x.Distance).ThenByDescending(x => _words.GetValueOrDefault(x.Word, 100001))
            .ThenBy(x => x.Word, StringComparer.Ordinal).Take(limit);
        // Approximate matches are suggestions only: nicknames and unfamiliar terms must not be silently changed.
        return candidates.Select(x => new Suggestion(MatchCase(word, x.Word), "Похожее слово")).ToArray();
    }
    public IReadOnlyList<Suggestion> Complete(string prefix, int limit = 3)
    {
        if (!IsWord(prefix) || limit < 1 || limit > 10) return [];
        var normalized = Normalize(prefix);
        return _personal.Concat(_words.Keys).Distinct(StringComparer.OrdinalIgnoreCase)
            .Where(w => w.StartsWith(normalized, StringComparison.OrdinalIgnoreCase) && w.Length > prefix.Length)
            .OrderByDescending(w => _words.GetValueOrDefault(w, 100001)).ThenBy(w => w.Length)
            .Take(limit).Select(w => new Suggestion(MatchCase(prefix, w), "Продолжить слово")).ToArray();
    }
    public TextEdit? CorrectAtBoundary(string text, int caret, bool fixLayout = true)
    {
        if (caret <= 0 || caret > text.Length || !char.IsWhiteSpace(text[caret - 1])) return null;
        var end = caret - 1;
        while (end > 0 && ".,!?;:".Contains(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        if (start == end || (start > 0 && !char.IsWhiteSpace(text[start - 1]) && !"(«\"".Contains(text[start - 1]))) return null;
        var word = text[start..end];
        var suggestion = Suggest(word).FirstOrDefault(s => s.Automatic && (fixLayout || s.Reason != "Другая раскладка"));
        return suggestion is null ? null : Replace(text, start, word.Length, suggestion.Word, caret);
    }
    public static TextEdit Replace(string text, int start, int length, string replacement, int caret)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(start);
        ArgumentOutOfRangeException.ThrowIfNegative(length);
        if (start + length > text.Length || caret < 0 || caret > text.Length) throw new ArgumentOutOfRangeException(nameof(length));
        return new(text, text[..start] + replacement + text[(start + length)..], start, text.Substring(start, length), replacement, Math.Clamp(caret + replacement.Length - length, 0, text.Length + replacement.Length - length));
    }
    public static bool TryUndo(TextEdit edit, string current, out string restored)
    {
        restored = current;
        if (current != edit.After) return false;
        restored = edit.Before;
        return true;
    }
    public static string SwapLayout(string value)
    {
        const string en = "qwertyuiop[]asdfghjkl;'zxcvbnm,./`";
        const string ru = "йцукенгшщзхъфывапролджэячсмитьбю.ё";
        var result = new char[value.Length];
        for (var i = 0; i < value.Length; i++)
        {
            var lower = char.ToLowerInvariant(value[i]);
            var index = en.IndexOf(lower);
            var converted = index >= 0 ? ru[index] : ru.IndexOf(lower) is var reverse && reverse >= 0 ? en[reverse] : lower;
            result[i] = char.IsUpper(value[i]) ? char.ToUpperInvariant(converted) : converted;
        }
        return new(result);
    }
    public static string MatchCase(string original, string replacement)
    {
        if (original.All(char.IsUpper)) return replacement.ToUpperInvariant();
        if (char.IsUpper(original[0])) return Russian.TextInfo.ToTitleCase(replacement.ToLowerInvariant());
        return replacement.ToLowerInvariant();
    }
    public static int Distance(string a, string b)
    {
        if (a.Length > 64 || b.Length > 64) return 65;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
            for (var j = 1; j <= b.Length; j++)
            {
                d[i, j] = Math.Min(d[i - 1, j] + 1, Math.Min(d[i, j - 1] + 1, d[i - 1, j - 1] + (a[i - 1] == b[j - 1] ? 0 : 1)));
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1]) d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
            }
        return d[a.Length, b.Length];
    }
    [GeneratedRegex(@"[\p{L}]+", RegexOptions.CultureInvariant)]
    public static partial Regex WordPattern();
}
