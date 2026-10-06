namespace TypePilot.Core;

public static class TypingEdits
{
    public static TextEdit? Difference(string before, string after, int caret)
    {
        if (before == after || caret < 0 || caret > after.Length) return null;
        var start = 0;
        while (start < before.Length && start < after.Length && before[start] == after[start]) start++;
        var oldEnd = before.Length; var newEnd = after.Length;
        while (oldEnd > start && newEnd > start && before[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        if ((oldEnd == start || newEnd == start) && start > 0) start--;
        return new(before, after, start, before[start..oldEnd], after[start..newEnd], caret);
    }

    // Include the unchanged tail up to the original caret. Literal input then ends at
    // the correct position naturally, without a delayed UIA caret move racing new input.
    public static TextEdit? NaturalCaret(TextEdit edit, int originalCaret)
    {
        var end = edit.Start + edit.Original.Length;
        var delta = edit.Replacement.Length - edit.Original.Length;
        if (edit.Caret == edit.Start + edit.Replacement.Length) return edit.Replacement.Length is >= 1 and <= 4000 ? edit : null;
        if (originalCaret < end || originalCaret > edit.Before.Length || edit.Caret != originalCaret + delta) return null;
        var tail = edit.Before[end..originalCaret];
        return edit.Replacement.Length + tail.Length is >= 1 and <= 4000
            ? edit with { Original = edit.Original + tail, Replacement = edit.Replacement + tail } : null;
    }
}

public sealed record TypingPhrase(int Start, string Text, int Caret);

public static class ContextTyping
{
    public static TypingPhrase? Capture(string text, int caret)
    {
        if (text.Length > 20000 || caret < 1 || caret > text.Length) return null;
        // Never finish a sentence while the user is editing its middle.
        if (caret < text.Length && text[caret] is not '\r' and not '\n') return null;
        var start = text.LastIndexOf('\n', caret - 1) + 1;
        var paragraph = text[start..caret];
        var content = paragraph.TrimEnd();
        if (content.Length is < 8 or > 600 || content.Any(c => char.IsControl(c)) || content.Count(char.IsLetter) < 8) return null;
        return new(start, content, caret);
    }

    public static TextEdit? Apply(string text, TypingPhrase phrase, string result)
    {
        if (phrase.Start < 0 || phrase.Start + phrase.Text.Length > text.Length ||
            text.Substring(phrase.Start, phrase.Text.Length) != phrase.Text || !IsSafe(phrase.Text, result)) return null;
        var after = text[..phrase.Start] + result + text[(phrase.Start + phrase.Text.Length)..];
        return TypingEdits.Difference(text, after, phrase.Caret + result.Length - phrase.Text.Length);
    }

    public static bool IsSafe(string original, string result)
    {
        if (string.IsNullOrWhiteSpace(result) || result.Length > original.Length + 200 || result.Any(char.IsControl)) return false;
        // Automatic context editing may change punctuation, case and spaces only.
        // No translation, changed negation, paraphrases or invented words are allowed.
        static string Letters(string value) => new(value.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant).ToArray());
        static string Symbols(string value) => new(value.Where(c => !char.IsLetterOrDigit(c) && !char.IsWhiteSpace(c) && !".,!?;:—–-()«»\"'…“”".Contains(c)).ToArray());
        static string SignedNumbers(string value) => string.Join('|', System.Text.RegularExpressions.Regex.Matches(value, @"[+-]\d+(?:[.,:/-]\d+)*").Select(m => m.Value));
        return Letters(original) == Letters(result) && Symbols(original) == Symbols(result) &&
            SignedNumbers(original) == SignedNumbers(result) &&
            RewriteGuard.MissingDetails(original, result).Count == 0;
    }
}
