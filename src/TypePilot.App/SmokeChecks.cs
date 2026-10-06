using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

internal static class SmokeChecks
{
    private static string OutputDir
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            for (var depth = 0; current is not null && depth < 7; depth++, current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "TypePilot.slnx"))) return Path.Combine(current.FullName, "artifacts", "qa");
            return Path.Combine(AppContext.BaseDirectory, "artifacts", "qa");
        }
    }
    public static async Task RunUiAsync(MainWindow window, PilotViewModel vm, bool demo = false)
    {
        Directory.CreateDirectory(OutputDir);
        try
        {
            NativeEditCheck.Run();
            using (var fakeVm = new PilotViewModel(Path.Combine(OutputDir, "unused-test-settings.json"), new DispatcherSynchronizationContext(window.Dispatcher), new FakeRuntime()))
            {
                fakeVm.Editor = "Исходный текст";
                await fakeVm.RewriteAsync(fakeVm.Editor);
                if (!fakeVm.ApplyCommand.CanExecute(null)) throw new Exception("Valid rewrite preview was not applicable.");
                fakeVm.Editor = "Пользователь уже изменил текст";
                if (fakeVm.ApplyCommand.CanExecute(null)) throw new Exception("Stale rewrite preview became applicable.");
                fakeVm.ApplyCommand.Execute(null);
                if (fakeVm.Editor != "Пользователь уже изменил текст") throw new Exception("Stale rewrite overwrote user text.");
                var external = await fakeVm.RewriteExternalAsync("Выделенная фраза", RewriteStyle.Clear, CancellationToken.None);
                if (string.IsNullOrWhiteSpace(external) || fakeVm.Editor != "Пользователь уже изменил текст") throw new Exception("External rewrite changed the editor draft.");
            }
            window.SmokeSetEditor("Превет, это тест приложения. ");
            window.SmokeSetEditor("Превет "); window.SmokeCorrectBoundary();
            if (vm.Editor != "Привет ") throw new Exception("WPF autocorrect failed: " + vm.Editor);
            window.SmokeUndo();
            if (vm.Editor != "Превет ") throw new Exception("WPF undo failed.");
            vm.Engine.SetPersonal(["Превет"]); window.SmokeCorrectBoundary();
            if (vm.Editor != "Превет ") throw new Exception("Personal dictionary failed.");
            vm.Engine.SetPersonal([]);
            using var spelling = new WindowsSpelling();
            var suggestions = spelling.Suggest("компютер");
            foreach (var valid in new[] { "думать", "обсуждать", "hello" })
            {
                var check = spelling.Analyze(valid);
                if (check.State == SpellingState.Misspelled || check.Suggestions.Count != 0) throw new Exception("Correct word was offered replacements: " + valid);
            }
            if (spelling.Analyze("компютер").State == SpellingState.Correct) throw new Exception("Misspelling was declared correct.");
            window.SmokeSetEditor("привет , "); window.SmokeCorrectBoundary();
            if (vm.Editor != "Привет, ") throw new Exception("Typography failed: " + vm.Editor);
            window.SmokeUndo();
            if (vm.Editor != "привет , ") throw new Exception("Typography undo failed.");
            window.SmokeSetEditor("Привет! Это локальный помощник набора.\n\nПишу сообщение без спешки: TypePilot поправляет опечатки, предлагает слова и помогает выразить мысль яснее.");
            vm.Status = "Готов к набору · всё остаётся на компьютере";
            vm.Result = "Пример интерфейса. Реальная генерация проверяется отдельно через --ai-smoke.";
            if (demo)
            {
                window.SmokeSetEditor("Я завтра проверю приложение в 18:30 и после того, как проверка будет закончена, напишу тебе, нормально ли всё работает.");
                window.SmokeSetStyle(1);
                await vm.RewriteAsync(vm.Editor);
                if (string.IsNullOrWhiteSpace(vm.Result)) throw new Exception("Live preview generation failed: " + vm.AiStatus);
                vm.Status = "Текст готов · Т9 и локальная переформулировка";
            }
            vm.Suggestions.Clear(); foreach (var word in new[] { "сообщение", "сообщения", "сообщить" }) vm.Suggestions.Add(new(word, "Пример подсказки"));
            foreach (var (width, height, suffix) in new[] { (1180d, 840d, "desktop"), (960d, 720d, "compact") })
            {
                window.Width = width; window.Height = height; window.SmokeShowPage("Home");
                await window.Dispatcher.InvokeAsync(() => Capture(window, "home-" + suffix), DispatcherPriority.ApplicationIdle);
                window.Width = width; window.Height = height; window.SmokeShowPage("Editor");
                await window.Dispatcher.InvokeAsync(() => Capture(window, "editor-" + suffix), DispatcherPriority.ApplicationIdle);
            }
            window.SmokeShowPage("Settings"); await window.Dispatcher.InvokeAsync(() => Capture(window, "settings"), DispatcherPriority.ApplicationIdle);
            window.SmokeScroll(850); await window.Dispatcher.InvokeAsync(() => Capture(window, "settings-context"), DispatcherPriority.ApplicationIdle);
            var previewText = "превет я завтра проверю приложэение в 18:30";
            var previewField = new FieldSnapshot(IntPtr.Zero, IntPtr.Zero, "Тестовое приложение", "preview", previewText, 0, previewText.Length, new(100, 100, 2, 20));
            var quick = new RewriteWindow(previewField, vm, (_, _, _) => Task.FromResult(false), true);
            quick.Show(); quick.SmokePreview("Привет! Завтра проверю приложение в 18:30.");
            await window.Dispatcher.InvokeAsync(() => Capture(quick, "quick-rewrite"), DispatcherPriority.ApplicationIdle);
            quick.Close();
            var suggestionsWindow = new SuggestionWindow(_ => { }, () => { });
            suggestionsWindow.Present(new(previewField, new(0, 6, "превет", false), [new("привет", "Опечатка"), new("приветы", "Словарь Windows"), new("приветствие", "Продолжение")]));
            suggestionsWindow.Highlight(1);
            await window.Dispatcher.InvokeAsync(() => Capture(suggestionsWindow, "floating-t9"), DispatcherPriority.ApplicationIdle);
            suggestionsWindow.Close();
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ui-check.json"), JsonSerializer.Serialize(new { passed = true, correction = true, undo = true, personalDictionary = true, nativeEditContract = true, externalAppsEndToEnd = false, windowsDictionary = spelling.Status, windowsSuggestions = suggestions.Select(s => s.Word) }));
            window.Close();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ui-check.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            Application.Current.Shutdown(1);
        }
    }
    private static void Capture(Window window, string name)
    {
        window.UpdateLayout();
        var content = (FrameworkElement)window.Content;
        var image = new RenderTargetBitmap((int)content.ActualWidth, (int)content.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(OutputDir, name + ".png")); encoder.Save(stream);
    }
    public static async Task RunAiAsync(Application app)
    {
        Directory.CreateDirectory(OutputDir);
        try
        {
            using var ai = new AiRuntime(TimeSpan.FromSeconds(1));
            var settings = new PilotSettings();
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var outputs = new List<object>();
            foreach (var (input, style) in new[] {
                ("я кароче завтра буду где то в 18:30 тестить эту штуку, потом скажу че получилось", RewriteStyle.Clear),
                ("Я завтра проверю приложение в 18:30 и после того, как проверка будет закончена, напишу тебе, нормально ли всё работает.", RewriteStyle.Short),
                ("скинь мне файл когда сможешь, а то я без него не могу закончить работу", RewriteStyle.Polite),
                ("Превет я завтра проверю приложэение в 18:30", RewriteStyle.Grammar),
                ("я думаю что это работает", RewriteStyle.Typing),
                ("еслияпишусловаслитно", RewriteStyle.Typing),
                ("вчера алексей написал в telegram", RewriteStyle.Typing),
                ("hello how are you", RewriteStyle.Typing)
            })
            {
                var start = watch.Elapsed.TotalSeconds;
                var result = await ai.RewriteAsync(settings, input, style, token.Token);
                if (RewriteGuard.MissingDetails(input, result).Count != 0) throw new Exception("Rewrite lost important information.");
                if (input == result) throw new Exception("Rewrite left the " + style + " phrase unchanged: " + result);
                if (style == RewriteStyle.Typing && !ContextTyping.IsSafe(input, result)) throw new Exception("Automatic context changed letters or details.");
                if (input == "я думаю что это работает" && !result.Contains(',')) throw new Exception("Semantic comma was not inserted.");
                if (input == "еслияпишусловаслитно" && !result.Contains("я пишу слова", StringComparison.OrdinalIgnoreCase)) throw new Exception("Joined words were not split.");
                if (input == "вчера алексей написал в telegram" && (!result.Contains("Алексей") || !result.Contains("Telegram"))) throw new Exception("Proper-name case was not restored.");
                outputs.Add(new { input, result, style = style.ToString(), elapsedSeconds = watch.Elapsed.TotalSeconds - start });
            }
            var memoryMb = ai.MemoryMb;
            await Task.Delay(2500, token.Token);
            var autoUnloaded = !ai.IsLoaded;
            if (!autoUnloaded) throw new Exception("Idle unload failed.");
            using var cancelLoad = new CancellationTokenSource(50);
            try
            {
                await ai.RewriteAsync(settings, "Тест отмены загрузки", RewriteStyle.Clear, cancelLoad.Token);
                throw new Exception("Load cancellation failed.");
            }
            catch (OperationCanceledException) { }
            if (ai.IsLoaded) throw new Exception("Cancelled model remained loaded.");
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ai-check.json"), JsonSerializer.Serialize(new { passed = true, outputs, elapsedSeconds = watch.Elapsed.TotalSeconds, memoryMb, idleUnload = autoUnloaded, cancelledLoad = true, unloaded = !ai.IsLoaded }));
            app.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ai-check.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            app.Shutdown(1);
        }
    }
    private sealed class FakeRuntime : IRewriteRuntime
    {
        public event Action<string>? StatusChanged;
        public long MemoryMb => 0;
        public Task<string> RewriteAsync(PilotSettings settings, string text, RewriteStyle style, CancellationToken token) { StatusChanged?.Invoke("Тестовый ответ"); return Task.FromResult("Новый вариант текста"); }
        public Task UnloadAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
