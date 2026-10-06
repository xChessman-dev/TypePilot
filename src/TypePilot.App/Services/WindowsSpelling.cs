using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TypePilot.Core;

namespace TypePilot.App;

public sealed class WindowsSpelling : IDisposable
{
    private ISpellFactory? _factory;
    private readonly Dictionary<string, ISpellChecker> _checkers = [];
    private readonly Dictionary<string, SpellingResult> _cache = new(StringComparer.Ordinal);
    public WindowsSpelling()
    {
        try
        {
            var type = Type.GetTypeFromCLSID(new("7AB36653-1796-484B-BDFA-E74F1DB7C1DC"));
            _factory = (ISpellFactory)Activator.CreateInstance(type!)!;
            foreach (var language in new[] { "ru-RU", "en-US" })
                if (_factory.IsSupported(language)) _checkers[language] = _factory.CreateSpellChecker(language);
        }
        catch (COMException) { Dispose(); }
    }
    public string Status => _checkers.Count == 0 ? "Словарь TypePilot · системный словарь недоступен" : "Словари Windows: " + string.Join(", ", _checkers.Keys);
    public IReadOnlyList<Suggestion> Suggest(string word) => Analyze(word).Suggestions;
    public SpellingResult Analyze(string word)
    {
        if (!TextEngine.IsWord(word)) return SpellingResult.Unavailable;
        if (_cache.TryGetValue(word, out var cached)) return cached;
        var result = CheckWord(word);
        if (_cache.Count >= 512) _cache.Clear();
        _cache[word] = result;
        return result;
    }
    private SpellingResult CheckWord(string word)
    {
        var language = word.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') ? "ru-RU" : "en-US";
        if (!_checkers.TryGetValue(language, out var checker)) return SpellingResult.Unavailable;
        IEnumString? items = null;
        IEnumSpellingError? errors = null;
        ISpellingError? error = null;
        try
        {
            errors = checker.Check(word);
            var status = errors.Next(out error);
            if (status == 1) return new(SpellingState.Correct, []);
            if (status < 0) Marshal.ThrowExceptionForHR(status);
            if (error is null) return SpellingResult.Unavailable;
            if (error.GetCorrectiveAction() == 0) return new(SpellingState.Correct, []);
            items = checker.Suggest(word);
            var words = new string[3];
            var fetched = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                items.Next(3, words, fetched);
                return new(SpellingState.Misspelled, words.Take(Math.Clamp(Marshal.ReadInt32(fetched), 0, 3)).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => new Suggestion(TextEngine.MatchCase(word, w), "Словарь Windows")).ToArray());
            }
            finally { Marshal.FreeCoTaskMem(fetched); }
        }
        catch (COMException) { return SpellingResult.Unavailable; }
        finally
        {
            if (items is not null) Marshal.ReleaseComObject(items);
            if (error is not null) Marshal.ReleaseComObject(error);
            if (errors is not null) Marshal.ReleaseComObject(errors);
        }
    }
    public void Dispose()
    {
        foreach (var checker in _checkers.Values) Marshal.ReleaseComObject(checker);
        _checkers.Clear();
        _cache.Clear();
        if (_factory is not null) Marshal.ReleaseComObject(_factory);
        _factory = null;
    }
    [ComImport, Guid("8E018A9D-2415-4677-BF08-794EA61F94BB"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellFactory
    {
        IEnumString SupportedLanguages { get; }
        [return: MarshalAs(UnmanagedType.Bool)] bool IsSupported([MarshalAs(UnmanagedType.LPWStr)] string language);
        ISpellChecker CreateSpellChecker([MarshalAs(UnmanagedType.LPWStr)] string language);
    }
    [ComImport, Guid("B6FD0B71-E2BC-4653-8D05-F197E412770B"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellChecker
    {
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetLanguageTag();
        IEnumSpellingError Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }
    [ComImport, Guid("803E3BD4-2828-4410-8290-418D1D73C762"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IEnumSpellingError
    {
        [PreserveSig] int Next(out ISpellingError? error);
    }
    [ComImport, Guid("B7C82D61-FBE8-4B47-9B27-6C0D2E0DE0A3"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface ISpellingError
    {
        uint GetStartIndex();
        uint GetLength();
        int GetCorrectiveAction();
        [return: MarshalAs(UnmanagedType.LPWStr)] string GetReplacement();
    }
}
