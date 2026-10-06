namespace TypePilot.Core;

public static class SmartTyping
{
    public static TextEdit? Edit(TextEngine engine, string text, int caret, PilotSettings settings, Func<string, SpellingResult>? spelling = null)
    {
        if (caret < 1 || caret > text.Length || text.Length > 20000) return null;
        if (!char.IsWhiteSpace(text[caret - 1]) && !".,!?;:".Contains(text[caret - 1])) return null;
        var after = text;
        var cursor = caret;
        if (settings.AutoCorrect || settings.CapitalizeNames) CorrectPhrase(engine, ref after, ref cursor, settings, spelling);
        if (settings.SmartPunctuation) NormalizePunctuation(ref after, ref cursor);
        if (settings.DoubleSpacePeriod) AddPeriod(ref after, ref cursor);
        if (settings.AutoCapitalize) Capitalize(ref after, cursor);
        return TypingEdits.Difference(text, after, cursor);
    }

    private static void CorrectPhrase(TextEngine engine, ref string text, ref int caret, PilotSettings settings, Func<string, SpellingResult>? spelling)
    {
        var content = text;
        var names = settings.ProperNames.Where(TextEngine.IsWord).Distinct(StringComparer.OrdinalIgnoreCase).ToDictionary(x => x, StringComparer.OrdinalIgnoreCase);
        foreach (var match in TextEngine.WordPattern().Matches(content[..caret]).Reverse())
        {
            var start = match.Index; var end = start + match.Length;
            if (start < Math.Max(0, caret - 600) || end == caret || !PlainToken(content, start, end)) continue;
            var word = match.Value; var replacement = word;
            if (settings.AutoCorrect)
            {
                var automatic = engine.Automatic(word, settings.FixLayout);
                // A real English word is not a wrong-layout Russian word.
                var valid = spelling?.Invoke(word).State == SpellingState.Correct;
                if (automatic is not null && (automatic.Reason != "Другая раскладка" || !valid)) replacement = automatic.Word;
                else if (settings.FixLayout && !valid && !engine.IsKnown(word) && word.All(c => c is >= 'a' and <= 'z' or >= 'A' and <= 'Z'))
                {
                    var swapped = TextEngine.SwapLayout(word);
                    if (engine.IsKnown(swapped) || spelling?.Invoke(swapped).State == SpellingState.Correct) replacement = swapped;
                }
            }
            if (settings.CapitalizeNames && names.TryGetValue(replacement, out var canonical)) replacement = canonical;
            if (word == replacement) continue;
            text = text[..start] + replacement + text[end..]; caret += replacement.Length - word.Length;
        }
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
