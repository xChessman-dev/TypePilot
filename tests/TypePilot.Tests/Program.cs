using TypePilot.Core;
using TypePilot.Tests;
using System.Net;
using System.Text;
using System.Text.Json;

try
{
var engine = DefaultEngine.Create();
var smartSettings = new PilotSettings();
foreach (var (before, expected) in new[] {
    ("привет ", "Привет "), ("я дома ", "Я дома "), ("привет мир ", "Привет мир "),
    ("Привет! как дела ", "Привет! Как дела "), ("Привет. как дела ", "Привет. Как дела "),
    ("Привет\nкак дела ", "Привет\nКак дела "), ("«привет» ", "«Привет» "),
    ("превет ", "Привет "), ("привет , ", "Привет, "), ("Привет,как дела ", "Привет, как дела "),
    ("привет  ", "Привет. "), ("Привет!  ", "Привет!  ") })
    Checks.Equal(expected, SmartTyping.Edit(engine, before, before.Length, smartSettings)?.After ?? before, "Smart typing: " + before);
foreach (var text in new[] { "https://example.com  ", "user@example.com  ", "foo_bar  ", "18:30  ", "1.5  ", "@привет ", "#привет ", "C:\\привет  ", "http://превет ", "  ", "привет" })
    Checks.True(SmartTyping.Edit(engine, text, text.Length, smartSettings) is null, "Protected token: " + text);
Checks.True(SmartTyping.Edit(engine, "Привет  мир", 8, smartSettings) is null, "Double-space inside a draft is untouched");
Checks.True(SmartTyping.Edit(engine, "привет ", 7, new() { AutoCapitalize = false }) is null, "Capitalization toggle");
Checks.True(SmartTyping.Edit(engine, "Привет  ", 8, new() { DoubleSpacePeriod = false }) is null, "Double-space toggle");
Checks.True(SmartTyping.Edit(engine, "Привет , ", 9, new() { SmartPunctuation = false }) is null, "Spacing toggle");
Checks.True(SmartTyping.Edit(engine, "Привет, тут г. москва ", 21, smartSettings) is null, "Abbreviation doesn't start a sentence");
var capitalizeEdit = SmartTyping.Edit(engine, "привет  ", 8, smartSettings)!;
Checks.True(TextEngine.TryUndo(capitalizeEdit, capitalizeEdit.After, out var originalSmart) && originalSmart == "привет  ", "Combined typography undo");
foreach (var text in new[] { "Привет , ", "Привет,как " })
{
    var spacingEdit = SmartTyping.Edit(engine, text, text.Length, smartSettings)!;
    Checks.True(spacingEdit.Original.Length > 0 && spacingEdit.Replacement.Length > 0, "Typography and undo use nonempty UIA input");
    Checks.Equal(spacingEdit.After, TextEngine.Replace(text, spacingEdit.Start, spacingEdit.Original.Length, spacingEdit.Replacement, spacingEdit.Caret - spacingEdit.Replacement.Length + spacingEdit.Original.Length).After, "Typography minimal-range contract");
}
Checks.True(SuggestionPolicy.Build(engine, new(0, 6, "думать", false), true, _ => throw new Exception("Unneeded check")).Count == 0, "Known complete word has no noisy completions");
Checks.True(SuggestionPolicy.Build(engine, new(0, 3, "мяу", false), true, _ => throw new Exception("Unneeded check")).Count == 0, "Meow stays meow");
Checks.True(SuggestionPolicy.Build(engine, new(0, 9, "обсуждать", false), true, _ => new(SpellingState.Correct, [new("обсуждают", "fake")])).Count == 0, "Windows-valid word suppresses fuzzy suggestions");
Checks.True(SuggestionPolicy.Build(engine, new(0, 2, "хз", true), true, _ => new(SpellingState.Misspelled, [new("он", "fake")])).Count == 0, "Short slang has no replacements");
Checks.True(SuggestionPolicy.Build(engine, new(0, 7, "мирофон", true), true, _ => SpellingResult.Unavailable).Count == 0, "Unavailable dictionary doesn't invent corrections");
Checks.Equal("микрофон", SuggestionPolicy.Build(engine, new(0, 7, "мирофон", true), true, _ => new(SpellingState.Misspelled, [new("микрофон", "Windows")]))[0].Word, "Confirmed spelling suggestions are prioritized");
var tab = new TabSelection();
Checks.Equal(0, tab.Next(3), "First Tab selects option 1");
Checks.Equal(1, tab.Next(3), "Second Tab selects option 2");
Checks.Equal(2, tab.Next(3), "Third Tab selects option 3");
Checks.Equal(0, tab.Next(3), "Fourth Tab wraps");
tab.Reset(); Checks.Equal(0, tab.Next(2), "New offer resets Tab");
Checks.Equal(-1, tab.Next(0), "No offer means no Tab interception");
var keys = new SuggestionKeyPolicy();
Checks.True(!keys.Route(9, true, false, false, false, true).Consume, "Tab outside target window passes through");
Checks.True(!keys.Route(9, true, false, false, true, false).Consume, "Shift/Control/Alt+Tab passes through");
Checks.True(!keys.Route(9, true, false, true, true, true).Consume, "Injected Tab passes through");
Checks.Equal(new SuggestionKeyDecision(true, SuggestionKeyAction.Next), keys.Route(9, true, false, false, true, true), "Physical Tab selects a choice");
Checks.Equal(new SuggestionKeyDecision(true, SuggestionKeyAction.None), keys.Route(9, true, false, false, true, true), "Held Tab doesn't advance repeatedly");
Checks.True(keys.Route(9, false, true, false, true, true).Consume, "Consumed Tab has a matching key-up");
Checks.Equal(SuggestionKeyAction.Next, keys.Route(9, true, false, false, true, true).Action, "Released and pressed Tab advances again");
Checks.Equal(new SuggestionKeyDecision(false, SuggestionKeyAction.CancelPending), keys.Route(65, true, false, false, true, true), "Typing cancels deferred selection without swallowing input");
Checks.Equal(new SuggestionKeyDecision(true, SuggestionKeyAction.Dismiss), keys.Route(27, true, false, false, true, true), "Escape cancels an offer");
keys.Reset(); Checks.True(!keys.Route(9, false, true, false, true, true).Consume, "Removing offer clears interception state");
Checks.True(new PilotSettings().GlobalEnabled, "Background T9 enabled by default");
Checks.Equal("прил", TypingContext.WordBeforeCaret("Пишу прил", 9, 9)!.Word, "Word at caret");
Checks.True(TypingContext.WordBeforeCaret("foo@превет", 10, 10) is null, "Email token is not offered");
Checks.True(TypingContext.WordBeforeCaret("hello", 0, 5) is null, "Selected text not offered as a word");
Checks.True(TypingContext.WordBeforeCaret("hello", 100, 100) is null, "Invalid caret ignored");
Checks.True(TypingContext.WordBeforeCaret("превет! ", 8, 8)!.AtBoundary, "Punctuated boundary token");
Checks.True(TypingContext.CanApply("abc", 1, 2, "abc", 1, 2), "Same source selection is applicable");
Checks.True(!TypingContext.CanApply("abc", 1, 2, "abcd", 1, 2), "Changed source refuses replacement");
Checks.True(!TypingContext.CanApply("abc", 1, 2, "abc", 2, 3), "Moved selection refuses replacement");
Checks.Equal("привет", engine.Suggest("превет")[0].Word, "Common typo");
Checks.Equal("Привет", engine.Suggest("Првиет")[0].Word, "Title case");
Checks.Equal("ПРИВЕТ", engine.Suggest("ПРЕВЕТ")[0].Word, "Upper case");
Checks.Equal("привет", engine.Suggest("ghbdtn")[0].Word, "Keyboard layout");
Checks.Equal("hello", TextEngine.SwapLayout("руддщ"), "Reverse keyboard layout");
Checks.True(engine.Suggest("спасибо").Count == 0, "Valid word is untouched");
Checks.True(engine.Suggest("a").Count == 0, "Short token is untouched");
Checks.True(engine.Suggest("user123").Count == 0, "Identifiers are untouched");
Checks.True(engine.Suggest("a@b.com").Count == 0, "Email is untouched");
Checks.True(engine.Suggest(new string('а', 200)).Count == 0, "Oversized token is ignored");
Checks.True(engine.Complete("прил").Any(x => x.Word == "приложение"), "Completion");
Checks.True(engine.Suggest("мирофон").All(x => !x.Automatic), "Fuzzy candidates require confirmation");
Checks.Equal(1, TextEngine.Distance("првиет", "привет"), "Transposition");
var edit = engine.CorrectAtBoundary("Ну превет! ", 11)!;
Checks.Equal("Ну привет! ", edit.After, "Correction preserves punctuation");
Checks.True(TextEngine.TryUndo(edit, edit.After, out var undone) && undone == edit.Before, "Safe undo");
Checks.True(!TextEngine.TryUndo(edit, edit.After + "новое", out _), "Stale undo is refused");
Checks.True(engine.CorrectAtBoundary("превет", 6) is null, "No correction before boundary");
Checks.True(engine.CorrectAtBoundary("http://превет ", 14) is null, "URL is not corrected");
Checks.True(engine.CorrectAtBoundary("ghbdtn ", 7, false) is null, "Layout toggle");
Checks.Equal("привет ", engine.CorrectAtBoundary("ghbdtn ", 7)!.After, "Layout at boundary");
engine.SetPersonal(["превет", "Микпилот"]);
Checks.True(engine.Suggest("превет").Count == 0, "Personal words override autocorrect");
Checks.True(engine.IsKnown("микпилот"), "Personal dictionary ignores case");
Checks.True(engine.CorrectAtBoundary("foo@превет ", 11) is null, "Email suffix is untouched");
Checks.True(engine.CorrectAtBoundary("my_превет ", 10) is null, "Code identifier suffix is untouched");
Checks.Equal("Ещё", TextEngine.MatchCase("Еше", "ещё"), "Cyrillic ё case");
Checks.Throws<ArgumentOutOfRangeException>(() => TextEngine.Replace("text", -1, 1, "a", 1), "Negative edit range refused");
Checks.Throws<ArgumentOutOfRangeException>(() => TextEngine.Replace("text", 1, 8, "a", 1), "Out of range edit refused");
Checks.True(RewriteGuard.MissingDetails("завтра в 18:30", "В 18:30").Contains("завтра"), "Temporal detail guard");
Checks.True(RewriteGuard.MissingDetails("завтра в 18:30", "Завтра в 19:30").Contains("18:30"), "Time detail guard");
Checks.True(RewriteGuard.MissingDetails("цена 2", "цена 20").Contains("2"), "Number matching is exact");
Checks.True(RewriteGuard.MissingDetails("ссылка https://example.com/test", "ссылка https://example.com/other").Count == 1, "Link detail guard");
Checks.True(RewriteGuard.MissingDetails("Вчера в 18:30", "ВЧЕРА в 18:30").Count == 0, "Guard ignores marker case");
Checks.Throws<InvalidDataException>(() => SettingsStore.ParseDictionary("hello\n@secret"), "Invalid import is atomic");
Checks.Equal(2, SettingsStore.ParseDictionary("hello\nHello\nПривет\n").Count, "Import deduplication");
Checks.True(!FieldPolicy.Allows(new("notepad", true, true, true, true), ["notepad"]), "Passwords refused");
Checks.True(!FieldPolicy.Allows(new("notepad", null, true, true, true), ["notepad"]), "Unknown password state refused");
Checks.True(!FieldPolicy.Allows(new("Code", false, true, true, true), ["Code"]), "Code editor hard exclusion");
Checks.True(!FieldPolicy.Allows(new("VALORANT", false, true, true, true), ["VALORANT"]), "Game hard exclusion");
Checks.True(!FieldPolicy.Allows(new("notepad", false, false, true, true), ["notepad"]), "Read-only field refused");
Checks.True(!FieldPolicy.Allows(new("notepad", false, true, false, true), ["notepad"]), "Unfocused field refused");
Checks.True(!FieldPolicy.Allows(new("brave", false, true, true, true), ["notepad"]), "Explicit allowlist");
Checks.True(FieldPolicy.Allows(new("notepad", false, true, true, true), ["NOTEPAD"]), "Supported edit allowed");
foreach (var uri in new[] { "https://127.0.0.1:1234", "http://example.com", "http://localhost:1234", "http://127.0.0.1:1234/private", "http://127.0.0.1:1234?url=evil", "http://user@127.0.0.1:1234" })
    Checks.Throws<ArgumentException>(() => new RewriteClient(new(uri)), "Remote/ambiguous endpoint refused: " + uri);
var handler = new FakeHandler();
var client = new RewriteClient(new("http://127.0.0.1:17864"), new HttpClient(handler));
Checks.Equal("Проверю завтра.", await client.RewriteAsync("Завтра я всё проверю", RewriteStyle.Short, CancellationToken.None), "Rewrite response");
using (var json = JsonDocument.Parse(handler.Body!))
{
    var root = json.RootElement;
    Checks.Equal("Завтра я всё проверю", root.GetProperty("messages")[1].GetProperty("content").GetString(), "Only requested text sent");
    Checks.True(!root.GetProperty("chat_template_kwargs").GetProperty("enable_thinking").GetBoolean(), "Thinking disabled");
    Checks.True(!root.GetProperty("stream").GetBoolean(), "Bounded response");
}
using (var cancelled = new CancellationTokenSource())
{
    cancelled.Cancel();
    try { await client.RewriteAsync("Привет", RewriteStyle.Clear, cancelled.Token); throw new Exception("Cancellation failed"); }
    catch (OperationCanceledException) { Checks.True(true, "Rewrite cancellation"); }
}
var retryHandler = new SequenceHandler("В 18:30 проверю.", "Завтра в 18:30 проверю.");
var retryClient = new RewriteClient(new("http://127.0.0.1:17864"), new HttpClient(retryHandler));
Checks.Equal("Завтра в 18:30 проверю.", await retryClient.RewriteAsync("Завтра в 18:30 я проверю", RewriteStyle.Short, CancellationToken.None), "Lost detail retried");
Checks.Equal(2, retryHandler.Calls, "Only one retry");
var badHandler = new SequenceHandler("Проверю.", "Проверю.");
try
{
    await new RewriteClient(new("http://127.0.0.1:17864"), new HttpClient(badHandler)).RewriteAsync("Завтра в 18:30 я проверю", RewriteStyle.Short, CancellationToken.None);
    throw new Exception("Lost details were accepted.");
}
catch (InvalidDataException) { Checks.Equal(2, badHandler.Calls, "Lost details refused after bounded retry"); }
foreach (var (payload, raw) in new[] { ("<think>reasoning</think>text", false), ("", false), ("{}", true), (new string('a', 110000), false) })
{
    try
    {
        await new RewriteClient(new("http://127.0.0.1:17864"), new HttpClient(new SequenceHandler([payload], raw))).RewriteAsync("Привет", RewriteStyle.Clear, CancellationToken.None);
        throw new Exception("Invalid response accepted.");
    }
    catch (InvalidDataException) { Checks.True(true, "Unsafe/empty/malformed/oversized response refused"); }
}
var temp = Path.Combine(Path.GetTempPath(), "typepilot-test-" + Guid.NewGuid().ToString("N"), "settings.json");
try
{
    var store = new SettingsStore(temp);
    Checks.True((await store.LoadAsync()).AutoCorrect, "Settings defaults");
    await store.SaveAsync(new() { PersonalWords = ["слово"], GlobalEnabled = false });
    Checks.Equal("слово", (await store.LoadAsync()).PersonalWords[0], "Settings round trip");
    Checks.True(!Directory.EnumerateFiles(Path.GetDirectoryName(temp)!, "*.tmp").Any(), "No temporary leftovers");
}
finally { if (File.Exists(temp)) File.Delete(temp); Directory.Delete(Path.GetDirectoryName(temp)!); }
Console.WriteLine($"{Checks.Passed} checks passed.");
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    Environment.ExitCode = 1;
}

sealed class FakeHandler : HttpMessageHandler
{
    public string? Body { get; private set; }
    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Body = await request.Content!.ReadAsStringAsync(cancellationToken);
        return new(HttpStatusCode.OK) { Content = new StringContent("{\"choices\":[{\"message\":{\"content\":\"Проверю завтра.\"}}]}", Encoding.UTF8, "application/json") };
    }
}
sealed class SequenceHandler : HttpMessageHandler
{
    private readonly string[] _results;
    private readonly bool _raw;
    public int Calls { get; private set; }
    public SequenceHandler(params string[] results) { _results = results; }
    public SequenceHandler(string[] results, bool raw) { _results = results; _raw = raw; }
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var value = _results[Math.Min(Calls++, _results.Length - 1)];
        var json = _raw ? value : JsonSerializer.Serialize(new { choices = new[] { new { message = new { content = value } } } });
        return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
    }
}
