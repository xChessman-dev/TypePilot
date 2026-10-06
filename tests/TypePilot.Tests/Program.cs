using TypePilot.Core;
using TypePilot.Tests;
using System.Net;
using System.Text;
using System.Text.Json;

var engine = DefaultEngine.Create();
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
