namespace TypePilot.Core;

public sealed record WordContext(int Start, int Length, string Word, bool AtBoundary);

public static class TypingContext
{
    public static WordContext? WordBeforeCaret(string text, int start, int end)
    {
        if (start != end || end < 1 || end > text.Length || text.Length > 20000) return null;
        var boundary = char.IsWhiteSpace(text[end - 1]);
        var wordEnd = end;
        if (boundary)
        {
            wordEnd--;
            while (wordEnd > 0 && ".,!?;:".Contains(text[wordEnd - 1])) wordEnd--;
        }
        var wordStart = wordEnd;
        while (wordStart > 0 && char.IsLetter(text[wordStart - 1])) wordStart--;
        if (wordStart == wordEnd || (wordStart > 0 && !char.IsWhiteSpace(text[wordStart - 1]) && !"(«\"".Contains(text[wordStart - 1]))) return null;
        var word = text[wordStart..wordEnd];
        return TextEngine.IsWord(word) ? new(wordStart, word.Length, word, boundary) : null;
    }
    public static bool CanApply(string original, int start, int end, string current, int currentStart, int currentEnd) =>
        original == current && start == currentStart && end == currentEnd && start >= 0 && end >= start && end <= original.Length;
}
