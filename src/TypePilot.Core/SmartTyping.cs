namespace TypePilot.Core;

public static class SmartTyping
{
    public static TextEdit? Edit(TextEngine engine, string text, int caret, PilotSettings settings)
    {
        if (caret < 1 || caret > text.Length || text.Length > 20000) return null;
        if (!char.IsWhiteSpace(text[caret - 1]) && !".,!?;:".Contains(text[caret - 1])) return null;
        var after = text;
        var cursor = caret;
        if (settings.AutoCorrect && engine.CorrectAtBoundary(after, cursor, settings.FixLayout) is { } correction)
        { after = correction.After; cursor = correction.Caret; }
        if (settings.SmartPunctuation) NormalizePunctuation(ref after, ref cursor);
        if (settings.DoubleSpacePeriod) AddPeriod(ref after, ref cursor);
        if (settings.AutoCapitalize) Capitalize(ref after, cursor);
        if (after == text) return null;
        var start = 0;
        while (start < text.Length && start < after.Length && text[start] == after[start]) start++;
        var oldEnd = text.Length; var newEnd = after.Length;
        while (oldEnd > start && newEnd > start && text[oldEnd - 1] == after[newEnd - 1]) { oldEnd--; newEnd--; }
        // UIA literal input cannot insert an empty string. Include one unchanged character
        // so deletion/insertion and their undo use the same bounded, verified write path.
        if ((oldEnd == start || newEnd == start) && start > 0) start--;
        return new(text, after, start, text[start..oldEnd], after[start..newEnd], cursor);
    }

    private static (int Start, int End)? LastWord(string text, int caret)
    {
        var end = caret;
        while (end > 0 && (char.IsWhiteSpace(text[end - 1]) || ".,!?;:»\"'”)]".Contains(text[end - 1]))) end--;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        if (start == end || !PlainToken(text, start, end)) return null;
        return (start, end);
    }
    private static bool PlainToken(string text, int start, int end)
    {
        var tokenStart = start; var tokenEnd = end;
        while (tokenStart > 0 && !char.IsWhiteSpace(text[tokenStart - 1])) tokenStart--;
        while (tokenEnd < text.Length && !char.IsWhiteSpace(text[tokenEnd])) tokenEnd++;
        var token = text[tokenStart..tokenEnd].Trim('(', ')', '«', '»', '"', '\'', '“', '”', ',', '.', '!', '?', ';', ':');
        return !token.Any(c => char.IsDigit(c) || "@/#_\\=<>`".Contains(c)) &&
            !token.Contains(":", StringComparison.Ordinal) && !token.Contains(".", StringComparison.OrdinalIgnoreCase) &&
            (start == 0 || char.IsWhiteSpace(text[start - 1]) || "(«\"'“".Contains(text[start - 1]));
    }
    private static void Capitalize(ref string text, int caret)
    {
        if (LastWord(text, caret) is not { } word) return;
        var startOfSentence = 0;
        for (var before = word.Start - 1; before >= Math.Max(0, word.Start - 512); before--)
        {
            if (text[before] is '\n' or '\r' or '!' or '?') { startOfSentence = before + 1; break; }
            if (text[before] != '.') continue;
            var previousStart = FindWordStart(text, before);
            var previous = text[previousStart..before].ToLowerInvariant();
            if (previous.Length > 1 && !new[] { "ул", "им", "рис", "стр", "руб", "др", "тд", "тп" }.Contains(previous)) { startOfSentence = before + 1; break; }
        }
        if (word.Start - startOfSentence > 512) return;
        while (startOfSentence < word.End && (char.IsWhiteSpace(text[startOfSentence]) || "(«\"'“»”)]".Contains(text[startOfSentence]))) startOfSentence++;
        var firstEnd = startOfSentence;
        while (firstEnd < text.Length && char.IsLetter(text[firstEnd])) firstEnd++;
        if (firstEnd > startOfSentence && char.IsLower(text[startOfSentence]) && PlainToken(text, startOfSentence, firstEnd))
        {
            text = text[..startOfSentence] + char.ToUpperInvariant(text[startOfSentence]) + text[(startOfSentence + 1)..];
        }
    }
    private static void NormalizePunctuation(ref string text, ref int caret)
    {
        var end = caret;
        while (end > 0 && text[end - 1] == ' ') end--;
        // Remove spaces directly before a newly typed punctuation mark, not line breaks or numeric separators.
        if (end > 0 && ",.!?;:".Contains(text[end - 1]))
        {
            var mark = end - 1; var start = mark;
            while (start > 0 && text[start - 1] == ' ') start--;
            if (start < mark && start > 0 && char.IsLetter(text[start - 1]) && PlainToken(text, FindWordStart(text, start), start))
            { text = text.Remove(start, mark - start); caret -= mark - start; }
        }
        // Supply a missing space after , ; ! ? when the following word is completed.
        for (var mark = Math.Max(1, caret - 512); mark + 1 < caret; mark++)
        {
            if (!",;!?".Contains(text[mark]) || !char.IsLetter(text[mark - 1]) || !char.IsLetter(text[mark + 1])) continue;
            var previousStart = FindWordStart(text, mark);
            if (PlainToken(text, previousStart, mark)) { text = text.Insert(mark + 1, " "); caret++; mark++; }
        }
    }
    private static int FindWordStart(string text, int end)
    { while (end > 0 && char.IsLetter(text[end - 1])) end--; return end; }
    private static void AddPeriod(ref string text, ref int caret)
    {
        if (caret != text.Length || caret < 3 || !text.EndsWith("  ", StringComparison.Ordinal) || (caret > 2 && char.IsWhiteSpace(text[caret - 3]))) return;
        var word = LastWord(text, caret);
        if (word is null || word.Value.End != caret - 2) return;
        text = text[..(caret - 2)] + ". ";
    }
}
