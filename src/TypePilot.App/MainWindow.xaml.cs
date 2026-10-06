using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

public partial class MainWindow : Window
{
    private readonly PilotViewModel _vm;
    private readonly WindowsSpelling _spelling = new();
    private readonly GlobalTyping _global;
    private readonly bool _smoke;
    private readonly DispatcherTimer _suggestTimer, _resourceTimer;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private HwndSource? _source;
    private IntPtr _handle;
    private bool _editing, _quit, _skipBoundary;
    private TextEdit? _lastEdit;
    private int _wordStart, _wordLength;
    private string _word = "";
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;
    public MainWindow(bool smoke = false)
    {
        _smoke = smoke;
        var settings = smoke ? Path.Combine(AppContext.BaseDirectory, "data", "smoke-settings.json") : Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        _vm = new(settings, new DispatcherSynchronizationContext(Dispatcher));
        _global = new(_vm.Engine, () => _vm.Settings, Dispatcher);
        _suggestTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshSuggestions(), Dispatcher); _suggestTimer.Stop();
        _resourceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleResources(), Dispatcher); _resourceTimer.Stop();
        InitializeComponent();
        DataContext = _vm;
        _global.StatusChanged += status => GlobalStatus.Text = status;
        _vm.ApplyRequested += ApplyEditorText;
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.LoadAsync(_smoke);
        SpellingStatus.Text = _spelling.Status;
        _vm.SettingsChanged += () => { UpdateIntegration(); _ = _vm.PersistFlagsAsync(); };
        if (!_smoke) { UpdateIntegration(); SetUpTray(); _resourceTimer.Start(); }
        else await SmokeChecks.RunUiAsync(this, _vm);
    }
    private void UpdateIntegration() => _global.SetEnabled(_vm.GlobalEnabled);
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessage);
        if (!_smoke && !NativeMethods.RegisterHotKey(_handle, 1, 0x4003, 0x20)) _vm.Status = "Ctrl+Alt+Space уже занят; используй редактор вручную.";
        if (!_smoke) NativeMethods.RegisterHotKey(_handle, 2, 0x4003, 0x08);
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if ((uint)message == NativeMethods.ShowMessage) { ShowWindow(); handled = true; }
        if (message == 0x312 && w.ToInt32() == 1) { _ = CaptureSelection(); handled = true; }
        if (message == 0x312 && w.ToInt32() == 2) { _global.Undo(); handled = true; }
        return IntPtr.Zero;
    }
    private async Task CaptureSelection()
    {
        try
        {
            var text = await _global.CaptureSelectionAsync();
            ShowWindow(); EditorNav.IsChecked = true;
            if (text is null) { _vm.Status = "Не удалось безопасно прочитать выделение. Скопируй текст и вставь его в редактор."; return; }
            if (_vm.Editor.Length > 0 && MessageBox.Show(this, "Заменить текущий текст редактора выделенным фрагментом?", "TypePilot", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
            ApplyEditorText(text); _vm.Status = "Выделенный текст загружен · ИИ запускается только кнопкой";
        }
        catch (InvalidOperationException ex) { ShowWindow(); _vm.Status = ex.Message; }
    }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (EditorPage is null || DictionaryPage is null || SettingsPage is null) return;
        var page = (sender as RadioButton)?.Tag as string;
        EditorPage.Visibility = page == "Editor" ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = page == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
    }
    private void StyleChanged(object sender, SelectionChangedEventArgs e)
    { if (_vm is not null && StyleBox is not null) _vm.Style = (RewriteStyle)StyleBox.SelectedIndex; }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing || EditorBox is null || _vm is null) return;
        _vm.Editor = EditorBox.Text;
        var edit = _skipBoundary || !_vm.AutoCorrect ? null : _vm.Engine.CorrectAtBoundary(EditorBox.Text, EditorBox.CaretIndex, _vm.FixLayout);
        _skipBoundary = false;
        if (edit is not null)
        {
            ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
            _lastEdit = edit; _vm.Corrections++; _vm.Status = $"{edit.Original} → {edit.Replacement} · Backspace — вернуть";
        }
        else if (_lastEdit is not null && EditorBox.Text != _lastEdit.After) _lastEdit = null;
        UndoButton.IsEnabled = _lastEdit is not null;
        ScheduleSuggestions();
    }
    private void EditorSelectionChanged(object sender, RoutedEventArgs e) { if (!_editing && _vm is not null) ScheduleSuggestions(); }
    private void ScheduleSuggestions() { _suggestTimer.Stop(); _suggestTimer.Start(); }
    private void RefreshSuggestions()
    {
        _suggestTimer.Stop(); _vm.Suggestions.Clear();
        if (EditorBox.SelectionLength != 0) return;
        var text = EditorBox.Text; var end = EditorBox.CaretIndex;
        // Suggestions target only the word immediately before the caret, not a stale token.
        while (end > 0 && char.IsWhiteSpace(text[end - 1])) end--;
        var start = end;
        while (start > 0 && char.IsLetter(text[start - 1])) start--;
        if (start == end || end - start > 48 || (start > 0 && (char.IsDigit(text[start - 1]) || "@/_-".Contains(text[start - 1])))) return;
        _wordStart = start; _wordLength = end - start; _word = text[start..end];
        var suggestions = _vm.Engine.Suggest(_word).Concat(_vm.Engine.IsKnown(_word) ? [] : _spelling.Suggest(_word)).Concat(_vm.Engine.Complete(_word));
        foreach (var suggestion in suggestions.DistinctBy(s => s.Word.ToLowerInvariant()).Take(3)) _vm.Suggestions.Add(suggestion);
        SuggestionHint.Text = _vm.Suggestions.Count == 0 ? "Нет уверенного исправления · слово оставлено как есть" : "Tab — первая подсказка. Неизвестные слова сами не заменяются.";
    }
    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _skipBoundary = true; _lastEdit = null; }
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && _lastEdit is not null && EditorBox.Text == _lastEdit.After && EditorBox.CaretIndex == _lastEdit.Caret)
        { UndoCorrection(this, new()); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && _vm.Suggestions.Count > 0)
        { Accept(_vm.Suggestions[0]); e.Handled = true; }
    }
    private void AcceptSuggestion(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is Suggestion suggestion) Accept(suggestion); }
    private void Accept(Suggestion suggestion)
    {
        if (_wordStart < 0 || _wordStart + _wordLength > EditorBox.Text.Length || EditorBox.Text.Substring(_wordStart, _wordLength) != _word) return;
        var edit = TextEngine.Replace(EditorBox.Text, _wordStart, _wordLength, suggestion.Word, EditorBox.CaretIndex);
        ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
        _lastEdit = edit; UndoButton.IsEnabled = true; _vm.Corrections++; _vm.Status = "Подсказка принята · можно отменить";
        EditorBox.Focus(); ScheduleSuggestions();
    }
    private void UndoCorrection(object sender, RoutedEventArgs e)
    {
        if (_lastEdit is null || !TextEngine.TryUndo(_lastEdit, EditorBox.Text, out _)) { _lastEdit = null; UndoButton.IsEnabled = false; return; }
        var edit = _lastEdit; _lastEdit = null;
        ReplaceEditorRange(edit.Start, edit.Replacement.Length, edit.Original, edit.Caret + edit.Original.Length - edit.Replacement.Length);
        UndoButton.IsEnabled = false; _vm.Status = "Исправление отменено · исходное слово возвращено"; EditorBox.Focus(); ScheduleSuggestions();
    }
    private void ReplaceEditorRange(int start, int length, string replacement, int caret)
    {
        _editing = true;
        try
        {
            EditorBox.BeginChange();
            try { EditorBox.Select(start, length); EditorBox.SelectedText = replacement; EditorBox.CaretIndex = Math.Clamp(caret, 0, EditorBox.Text.Length); }
            finally { EditorBox.EndChange(); }
            _vm.Editor = EditorBox.Text;
        }
        finally { _editing = false; }
    }
    private void ApplyEditorText(string text) { ReplaceEditorRange(0, EditorBox.Text.Length, text, text.Length); _lastEdit = null; UndoButton.IsEnabled = false; ScheduleSuggestions(); }
    private void ImportDictionary(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Словарь TXT|*.txt", Title = "Импорт личного словаря" };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            if (new FileInfo(dialog.FileName).Length > 200000) throw new InvalidDataException("Файл больше 200 КБ.");
            var imported = SettingsStore.ParseDictionary(File.ReadAllText(dialog.FileName));
            var merged = SettingsStore.ParseDictionary(_vm.DictionaryText + Environment.NewLine + string.Join(Environment.NewLine, imported));
            _vm.DictionaryText = string.Join(Environment.NewLine, merged); _vm.Status = "Импорт готов · нажми «Сохранить словарь»";
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _vm.Status = "Импорт отклонён: " + ex.Message; }
    }
    private void ExportDictionary(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { Filter = "Словарь TXT|*.txt", FileName = "typepilot-words.txt" };
        if (dialog.ShowDialog(this) != true) return;
        try { File.WriteAllLines(dialog.FileName, SettingsStore.ParseDictionary(_vm.DictionaryText)); _vm.Status = "Личный словарь экспортирован"; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { _vm.Status = "Не экспортировано: " + ex.Message; }
    }
    private void SampleResources()
    {
        using var process = Process.GetCurrentProcess();
        var now = DateTime.UtcNow; var cpu = process.TotalProcessorTime;
        var percentage = (cpu - _lastCpu).TotalMilliseconds / Math.Max(1, (now - _lastSample).TotalMilliseconds) / Environment.ProcessorCount * 100;
        if (_lastCpu != TimeSpan.Zero) ResourceStatus.Text = $"Приложение: {percentage:F1}% CPU · {process.WorkingSet64 / 1048576} МБ RAM · ИИ: {_vm.AiMemoryMb} МБ RAM";
        _lastCpu = cpu; _lastSample = now;
    }
    private void SetUpTray()
    {
        using var stream = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/TypePilot.ico"))!.Stream;
        _icon = new System.Drawing.Icon(stream);
        var menu = new System.Windows.Forms.ContextMenuStrip();
        menu.Items.Add("Открыть TypePilot", null, (_, _) => Dispatcher.BeginInvoke(ShowWindow));
        menu.Items.Add("Отключить системный Т9", null, (_, _) => Dispatcher.BeginInvoke(() => _vm.GlobalEnabled = false));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(() => { _quit = true; Close(); }));
        _tray = new() { Icon = _icon, Text = "TypePilot · локальный помощник", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
    }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_quit && !_smoke) { e.Cancel = true; Hide(); return; }
        _suggestTimer.Stop(); _resourceTimer.Stop(); _global.Dispose(); _vm.Dispose(); _spelling.Dispose();
        _source?.RemoveHook(WindowMessage); NativeMethods.UnregisterHotKey(_handle, 1); NativeMethods.UnregisterHotKey(_handle, 2);
        _tray?.Dispose(); _icon?.Dispose();
    }
    internal void SmokeSetEditor(string text) => ApplyEditorText(text);
    internal void SmokeCorrectBoundary()
    {
        _skipBoundary = false;
        EditorChanged(EditorBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }
    internal void SmokeUndo() => UndoCorrection(this, new());
    internal void SmokeShowPage(string page)
    {
        var button = new RadioButton { Tag = page };
        Navigate(button, new()); UpdateLayout();
    }
}
