using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

internal static class FieldIntegrationCheck
{
    public static async Task RunAsync(Application app)
    {
        // All reads and input go exclusively to this process's synthetic fixture, never user apps.
        var output = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../artifacts/qa"));
        Directory.CreateDirectory(output);
        var box = new TextBox { Text = "превет ", AcceptsReturn = true, Height = 90, Margin = new(16) };
        var password = new PasswordBox { Password = "synthetic-secret", Margin = new(16) };
        var panel = new StackPanel(); panel.Children.Add(box); panel.Children.Add(password);
        var window = new Window { Title = "TypePilot integration fixture", Width = 520, Height = 320, Content = panel };
        using var fields = new FieldAccess();
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        using var vm = new PilotViewModel(Path.Combine(output, "unused-fixture.json"), new DispatcherSynchronizationContext(app.Dispatcher), new TestRuntime());
        try
        {
            window.Show(); window.Activate(); box.Focus(); box.Select(7, 0);
            await Task.Delay(200, timeout.Token);
            var fixtureHandle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            // Windows may deny a background process foreground activation. Give an interactive
            // runner time to activate this owned fixture; never inspect any other window's field.
            for (var attempt = 0; attempt < 200 && NativeMethods.GetForegroundWindow() != fixtureHandle; attempt++)
                await Task.Delay(100, timeout.Token);
            if (NativeMethods.GetForegroundWindow() != fixtureHandle)
            {
                await File.WriteAllTextAsync(Path.Combine(output, "field-check.json"), JsonSerializer.Serialize(new { passed = false, skipped = true, reason = "Windows did not allow fixture activation. No other field was read or changed.", fixtureOnly = true }));
                window.Close(); app.Shutdown(); return;
            }
            var original = await fields.ReadAsync(["TypePilot"], true, fixtureHandle).WaitAsync(timeout.Token) ?? throw new Exception("UIA fixture is not readable.");
            NativeMethods.GetWindowThreadProcessId(original.Foreground, out var fixtureOwner);
            Require(original.Foreground == fixtureHandle && fixtureOwner == Environment.ProcessId, $"Input is restricted to the owned fixture window (captured={original.Foreground}, expected={fixtureHandle}, owner={fixtureOwner}, self={Environment.ProcessId})");
            Require(original.Text == "превет " && original.Start == 7 && original.End == 7, "UIA caret snapshot");
            Require(await fields.ReplaceAsync(original, 0, 6, "привет", 7, ["TypePilot"], false, timeout.Token), "UIA correction input");
            await Task.Delay(100, timeout.Token);
            Require(box.Text == "привет " && box.CaretIndex == 7, "UIA correction and caret preserve suffix");
            var after = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            Require(after.Identity == original.Identity, "Undo is restricted to the same fixture field");
            Require(await fields.ReplaceAsync(after, 0, 6, "превет", 7, ["TypePilot"], false, timeout.Token), "UIA undo input");
            await Task.Delay(100, timeout.Token); Require(box.Text == "превет ", "UIA undo");
            box.Text = "Привет , "; box.CaretIndex = box.Text.Length;
            var typographySource = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            var typography = SmartTyping.Edit(vm.Engine, typographySource.Text, typographySource.End, vm.Settings)!;
            Require(await fields.ReplaceAsync(typographySource, typography.Start, typography.Original.Length, typography.Replacement, typography.Caret, ["TypePilot"], false, timeout.Token), "UIA punctuation correction");
            await Task.Delay(100, timeout.Token); Require(box.Text == "Привет, ", "UIA punctuation range");
            var typographyAfter = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            Require(await fields.ReplaceAsync(typographyAfter, typography.Start, typography.Replacement.Length, typography.Original, typographySource.End, ["TypePilot"], false, timeout.Token), "UIA punctuation undo");
            await Task.Delay(100, timeout.Token); Require(box.Text == "Привет , ", "UIA punctuation undo preserves original");
            using (var keys = new SuggestionKeys(app.Dispatcher, () => { }, () => { }, () => { }))
                Require(keys.Start((await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!), "Optional Tab/focus hooks install and unload in owned fixture");
            var stale = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            box.Text = "Новое сообщение"; box.CaretIndex = box.Text.Length;
            Require(!await fields.ReplaceAsync(stale, 0, 6, "НЕ ВСТАВЛЯТЬ", 7, ["TypePilot"], false, timeout.Token), "Stale source refused");
            box.IsReadOnly = true;
            Require(await fields.ReadAsync(["TypePilot"], true, fixtureHandle) is null, "UIA read-only refused");
            box.IsReadOnly = false; password.Focus();
            Require(await fields.ReadAsync(["TypePilot"], true, fixtureHandle) is null, "UIA password refused");
            box.Focus(); box.Text = "Фраза до | хвост"; box.Select(0, 8);
            var selected = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            Require(selected.Identity == original.Identity, "Selection belongs to the owned fixture");
            Require(selected.SelectedText == "Фраза до", "UIA selection snapshot");
            var suggestionWindow = new SuggestionWindow(_ => { }, () => { });
            suggestionWindow.Present(new(selected, new(0, 5, "Фраза", false), [new("Фразы", "Тест")]));
            await Task.Delay(80, timeout.Token);
            Require((await fields.ReadAsync(["TypePilot"], true, fixtureHandle))?.Identity == selected.Identity, "Suggestions keep original field focused");
            suggestionWindow.Close();
            var quick = new RewriteWindow(selected, vm, (field, result, token) => fields.ReplaceAsync(field, field.Start, field.End - field.Start, result, field.Start + result.Length, ["TypePilot"], true, token), true);
            quick.Show(); quick.SmokePreview("Готовая фраза");
            Require(await quick.ApplyAsync(), "Quick rewrite apply");
            await Task.Delay(100, timeout.Token);
            Require(box.Text == "Готовая фраза | хвост", "Only selected range replaced");
            box.Select(0, 13);
            selected = (await fields.ReadAsync(["TypePilot"], true, fixtureHandle))!;
            Require(selected.Identity == original.Identity, "Stale-result fixture identity");
            var staleQuick = new RewriteWindow(selected, vm, (field, result, token) => fields.ReplaceAsync(field, field.Start, field.End - field.Start, result, field.Start + result.Length, ["TypePilot"], true, token), true);
            staleQuick.Show(); staleQuick.SmokePreview("УСТАРЕВШИЙ РЕЗУЛЬТАТ");
            box.Text = "Пользователь изменил исходник";
            Require(!await staleQuick.ApplyAsync(), "Quick rewrite stale result refused");
            Require(box.Text == "Пользователь изменил исходник", "Changed source kept intact"); staleQuick.Close();
            await File.WriteAllTextAsync(Path.Combine(output, "field-check.json"), JsonSerializer.Serialize(new { passed = true, uiaRead = true, uiaWrite = true, undo = true, typographyUndo = true, tabHookInstallation = true, caret = true, noFocusSteal = true, passwordsRefused = true, staleRefused = true, selectedRangeApply = true, fixtureOnly = true, browserEndToEnd = false }));
            window.Close(); app.Shutdown();
        }
        catch (Exception ex)
        { await File.WriteAllTextAsync(Path.Combine(output, "field-check.json"), JsonSerializer.Serialize(new { passed = false, error = ex.ToString() })); app.Shutdown(1); }
    }
    private static void Require(bool condition, string name) { if (!condition) throw new Exception(name); }
    private sealed class TestRuntime : IRewriteRuntime
    {
        public event Action<string>? StatusChanged;
        public long MemoryMb => 0;
        public Task<string> RewriteAsync(PilotSettings settings, string text, RewriteStyle style, CancellationToken token) { StatusChanged?.Invoke("Тест"); return Task.FromResult("Тестовый результат"); }
        public Task UnloadAsync() => Task.CompletedTask;
        public void Dispose() { }
    }
}
