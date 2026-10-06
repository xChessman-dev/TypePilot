namespace TypePilot.Core;

public enum SpellingState { Unavailable, Correct, Misspelled }
public sealed record SpellingResult(SpellingState State, IReadOnlyList<Suggestion> Suggestions)
{
    public static SpellingResult Unavailable { get; } = new(SpellingState.Unavailable, []);
}

public static class SuggestionPolicy
{
    public static IReadOnlyList<Suggestion> Build(TextEngine engine, WordContext word, bool fixLayout, Func<string, SpellingResult> spelling)
    {
        if (engine.IsKnown(word.Word)) return [];
        var local = engine.Suggest(word.Word).Where(s => fixLayout || s.Reason != "Другая раскладка").ToArray();
        // Deterministic aliases/layout fixes are separate from approximate dictionary matches.
        if (local.Any(s => s.Automatic)) return local;
        var result = spelling(word.Word);
        if (result.State == SpellingState.Correct) return [];
        // Very short unknown words are usually abbreviations, slang, or an unfinished token.
        if (word.Word.Length < 4) return word.AtBoundary ? [] : engine.Complete(word.Word);
        var candidates = result.State == SpellingState.Misspelled ? result.Suggestions.Concat(local) : [];
        return candidates.Concat(word.AtBoundary ? [] : engine.Complete(word.Word))
            .Where(s => !string.Equals(s.Word, word.Word, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(s => s.Word.ToLowerInvariant()).Take(3).ToArray();
    }
}

public sealed class TabSelection
{
    public int Index { get; private set; } = -1;
    public void Reset() => Index = -1;
    public int Next(int count)
    {
        if (count is < 1 or > 3) { Reset(); return -1; }
        return Index = (Index + 1) % count;
    }
}
