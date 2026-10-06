using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

internal static class SmokeChecks
{
    private static string OutputDir => Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/qa"));
    public static async Task RunUiAsync(MainWindow window, PilotViewModel vm)
    {
        Directory.CreateDirectory(OutputDir);
        try
        {
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
            window.SmokeSetEditor("Привет! Это локальный помощник набора.\n\nПишу сообщение без спешки: TypePilot поправляет опечатки, предлагает слова и помогает выразить мысль яснее.");
            vm.Status = "Готов к набору · всё остаётся на компьютере";
            vm.Result = "Пример интерфейса. Реальная генерация проверяется отдельно через --ai-smoke.";
            vm.Suggestions.Clear(); foreach (var word in new[] { "сообщение", "сообщения", "сообщить" }) vm.Suggestions.Add(new(word, "Пример подсказки"));
            foreach (var (width, height, suffix) in new[] { (1180d, 840d, "desktop"), (960d, 720d, "compact") })
            {
                window.Width = width; window.Height = height; window.SmokeShowPage("Editor");
                await window.Dispatcher.InvokeAsync(() => Capture(window, "editor-" + suffix), DispatcherPriority.ApplicationIdle);
            }
            window.SmokeShowPage("Settings"); await window.Dispatcher.InvokeAsync(() => Capture(window, "settings"), DispatcherPriority.ApplicationIdle);
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ui-check.json"), JsonSerializer.Serialize(new { passed = true, correction = true, undo = true, personalDictionary = true, windowsDictionary = spelling.Status, windowsSuggestions = suggestions.Select(s => s.Word) }));
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
        var image = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        image.Render(window);
        var encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(Path.Combine(OutputDir, name + ".png")); encoder.Save(stream);
    }
    public static async Task RunAiAsync(Application app)
    {
        Directory.CreateDirectory(OutputDir);
        try
        {
            using var ai = new AiRuntime();
            var settings = new PilotSettings();
            using var token = new CancellationTokenSource(TimeSpan.FromMinutes(2));
            var watch = System.Diagnostics.Stopwatch.StartNew();
            var outputs = new List<object>();
            foreach (var (input, style) in new[] {
                ("я кароче завтра буду где то в 18:30 тестить эту штуку, потом скажу че получилось", RewriteStyle.Clear),
                ("Я завтра проверю приложение в 18:30 и после того, как проверка будет закончена, напишу тебе, нормально ли всё работает.", RewriteStyle.Short),
                ("скинь мне файл когда сможешь, а то я без него не могу закончить работу", RewriteStyle.Polite),
                ("Превет я завтра проверю приложэение в 18:30", RewriteStyle.Grammar)
            })
            {
                var start = watch.Elapsed.TotalSeconds;
                var result = await ai.RewriteAsync(settings, input, style, token.Token);
                if (RewriteGuard.MissingDetails(input, result).Count != 0) throw new Exception("Rewrite lost important information.");
                if (input == result) throw new Exception("Rewrite left the test phrase unchanged.");
                outputs.Add(new { input, result, style = style.ToString(), elapsedSeconds = watch.Elapsed.TotalSeconds - start });
            }
            var memoryMb = ai.MemoryMb;
            await ai.UnloadAsync();
            if (ai.IsLoaded) throw new Exception("Unload failed.");
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ai-check.json"), JsonSerializer.Serialize(new { passed = true, outputs, elapsedSeconds = watch.Elapsed.TotalSeconds, memoryMb, unloaded = !ai.IsLoaded }));
            app.Shutdown();
        }
        catch (Exception ex)
        {
            await File.WriteAllTextAsync(Path.Combine(OutputDir, "ai-check.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() }));
            app.Shutdown(1);
        }
    }
}
