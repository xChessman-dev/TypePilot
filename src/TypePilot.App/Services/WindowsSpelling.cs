using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using TypePilot.Core;

namespace TypePilot.App;

public sealed class WindowsSpelling : IDisposable
{
    private ISpellFactory? _factory;
    private readonly Dictionary<string, ISpellChecker> _checkers = [];
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
    public IReadOnlyList<Suggestion> Suggest(string word)
    {
        if (!TextEngine.IsWord(word)) return [];
        var language = word.Any(c => c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') ? "ru-RU" : "en-US";
        if (!_checkers.TryGetValue(language, out var checker)) return [];
        IEnumString? items = null;
        try
        {
            items = checker.Suggest(word);
            var words = new string[3];
            var fetched = Marshal.AllocCoTaskMem(sizeof(int));
            try
            {
                items.Next(3, words, fetched);
                return words.Take(Math.Clamp(Marshal.ReadInt32(fetched), 0, 3)).Where(w => !string.IsNullOrWhiteSpace(w)).Select(w => new Suggestion(TextEngine.MatchCase(word, w), "Словарь Windows")).ToArray();
            }
            finally { Marshal.FreeCoTaskMem(fetched); }
        }
        catch (COMException) { return []; }
        finally { if (items is not null) Marshal.ReleaseComObject(items); }
    }
    public void Dispose()
    {
        foreach (var checker in _checkers.Values) Marshal.ReleaseComObject(checker);
        _checkers.Clear();
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
        [return: MarshalAs(UnmanagedType.Interface)] object Check([MarshalAs(UnmanagedType.LPWStr)] string text);
        IEnumString Suggest([MarshalAs(UnmanagedType.LPWStr)] string word);
    }
}
