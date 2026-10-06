using System.Windows.Threading;
using System.Windows.Automation;
using TypePilot.Core;

namespace TypePilot.App;

public sealed class GlobalTyping : IDisposable
{
    private readonly TextEngine _engine;
    private readonly Func<PilotSettings> _settings;
    private readonly Dispatcher _dispatcher;
    private readonly NativeMethods.WinEventProc _callback;
    private IntPtr _hook;
    private IntPtr _pending;
    private IntPtr _lastWindow;
    private TextEdit? _lastEdit;
    private string? _undoSuppressedText;
    private readonly DispatcherTimer _debounce;
    private readonly SemaphoreSlim _capture = new(1, 1);
    public event Action<string>? StatusChanged;
    public GlobalTyping(TextEngine engine, Func<PilotSettings> settings, Dispatcher dispatcher)
    {
        _engine = engine; _settings = settings; _dispatcher = dispatcher; _callback = Changed;
        _debounce = new DispatcherTimer(TimeSpan.FromMilliseconds(100), DispatcherPriority.Background, (_, _) => ProcessPending(), dispatcher);
        _debounce.Stop();
    }
    public void SetEnabled(bool enabled)
    {
        if (_hook != IntPtr.Zero) { NativeMethods.UnhookWinEvent(_hook); _hook = IntPtr.Zero; }
        _debounce.Stop(); _lastEdit = null;
        if (enabled)
        {
            // Accessibility change events only. No keyboard hooks, injected DLLs or key logs.
            _hook = NativeMethods.SetWinEventHook(0x800E, 0x800E, IntPtr.Zero, _callback, 0, 0, 2);
            StatusChanged?.Invoke(_hook == IntPtr.Zero ? "Не удалось подключить системный режим." : "Системный режим: только совместимые Edit / RichEdit.");
        }
        else StatusChanged?.Invoke("Системный режим выключен · Т9 работает в редакторе");
    }
    private void Changed(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time)
    {
        if (!_settings().GlobalEnabled || window == IntPtr.Zero || !IsSafeNativeEdit(window)) return;
        _dispatcher.BeginInvoke(() => { _pending = window; _debounce.Stop(); _debounce.Start(); });
    }
    private void ProcessPending()
    {
        _debounce.Stop();
        var window = _pending;
        if (!_settings().GlobalEnabled || !_settings().AutoCorrect || !IsSafeNativeEdit(window)) return;
        var selection = NativeMethods.Selection(window);
        if (selection is null || selection.Value.Start != selection.Value.End) return;
        var text = NativeMethods.Text(window);
        if (text is null) return;
        if (window == _lastWindow && text == _undoSuppressedText) return;
        _undoSuppressedText = null;
        var edit = _engine.CorrectAtBoundary(text, selection.Value.End, _settings().FixLayout);
        if (edit is null) return;
        // Abort on changed focus, selection or content instead of replacing a stale word.
        if (!IsSafeNativeEdit(window) || NativeMethods.Selection(window) != selection || NativeMethods.Text(window) != text) return;
        if (NativeMethods.Replace(window, edit.Start, edit.Start + edit.Original.Length, edit.Replacement))
        {
            NativeMethods.Message(window, NativeMethods.EmSetSel, new(edit.Caret), new(edit.Caret), out _);
            _lastEdit = edit; _lastWindow = window;
            StatusChanged?.Invoke("Исправлено слово · Ctrl+Alt+Backspace — отменить");
        }
    }
    private bool IsSafeNativeEdit(IntPtr window)
    {
        if (window == IntPtr.Zero || NativeMethods.FocusedControl() != window) return false;
        var name = NativeMethods.ClassName(window);
        if (!new[] { "Edit", "RichEdit20W", "RICHEDIT50W", "RichEditD2DPT" }.Contains(name, StringComparer.OrdinalIgnoreCase)) return false;
        var style = NativeMethods.Style(window);
        if (style is null) return false;
        return FieldPolicy.Allows(new(NativeMethods.ProcessName(window), (style.Value & 0x20) != 0, (style.Value & 0x800) == 0 && (style.Value & 0x08000000) == 0, true, true), _settings().AllowedProcesses);
    }
    public bool Undo()
    {
        if (_lastEdit is null || !IsSafeNativeEdit(_lastWindow)) return false;
        var current = NativeMethods.Text(_lastWindow);
        var selection = NativeMethods.Selection(_lastWindow);
        if (current != _lastEdit.After || selection != (_lastEdit.Caret, _lastEdit.Caret)) { _lastEdit = null; return false; }
        var edit = _lastEdit; _lastEdit = null;
        _undoSuppressedText = edit.Before;
        var done = NativeMethods.Replace(_lastWindow, edit.Start, edit.Start + edit.Replacement.Length, edit.Original);
        if (done)
        {
            var caret = edit.Caret + edit.Original.Length - edit.Replacement.Length;
            NativeMethods.Message(_lastWindow, NativeMethods.EmSetSel, new(caret), new(caret), out _);
            // The restored typo is protected until the next value change from real typing.
            _debounce.Stop();
        }
        return done;
    }
    public async Task<string?> CaptureSelectionAsync()
    {
        if (!await _capture.WaitAsync(0)) throw new InvalidOperationException("Предыдущий запрос к текстовому полю ещё выполняется.");
        var settings = _settings();
        var allowed = settings.AllowedProcesses.ToArray();
        var foreground = NativeMethods.GetForegroundWindow();
        var expectedProcess = NativeMethods.ProcessName(foreground);
        var task = Task.Run(() =>
        {
            try
            {
                var field = AutomationElement.FocusedElement;
                if (field is null || NativeMethods.GetForegroundWindow() != foreground) return null;
                var password = field.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true);
                var supported = field.Current.ControlType == ControlType.Edit || field.Current.ControlType == ControlType.Document;
                if (password is not false || !supported || !allowed.Contains(expectedProcess, StringComparer.OrdinalIgnoreCase)) return null;
                if (!field.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return null;
                var ranges = ((TextPattern)raw).GetSelection();
                if (ranges.Length != 1) return null;
                var readOnly = ranges[0].GetAttributeValue(TextPattern.IsReadOnlyAttribute);
                NativeMethods.GetWindowThreadProcessId(foreground, out var expectedId);
                if (field.Current.ProcessId != expectedId || !FieldPolicy.Allows(new(expectedProcess, password is bool known ? known : null, readOnly is false && field.Current.IsEnabled, field.Current.HasKeyboardFocus, supported), allowed)) return null;
                var text = ranges[0].GetText(4001);
                if (NativeMethods.GetForegroundWindow() != foreground || string.IsNullOrWhiteSpace(text) || text.Length > 4000) return null;
                return text;
            }
            catch (System.Runtime.InteropServices.COMException) { return null; }
            catch (ElementNotAvailableException) { return null; }
            catch (InvalidOperationException) { return null; }
            finally { _capture.Release(); }
        });
        var completed = await Task.WhenAny(task, Task.Delay(2000));
        return completed == task ? await task : null;
    }
    public void Dispose()
    {
        _debounce.Stop(); if (_hook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_hook); _hook = IntPtr.Zero;
    }
}
