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
    private readonly bool _demo;
    private readonly DispatcherTimer _suggestTimer, _resourceTimer;
    private readonly DispatcherTimer _globalTabTimer, _editorTabTimer;
    private readonly DispatcherTimer _editorContextTimer;
    private CancellationTokenSource? _editorContextCancellation;
    private bool _editorAssisting;
    private readonly TabSelection _globalTab = new(), _editorTab = new();
    private readonly SuggestionKeys _suggestionKeys;
    private string _editorOfferText = "";
    private int _editorOfferCaret;
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _icon;
    private HwndSource? _source;
    private SuggestionWindow? _suggestions;
    private RewriteWindow? _quickRewrite;
    private bool _capturing;
    private IntPtr _handle;
    private bool _editing, _quit, _skipBoundary;
    private TextEdit? _lastEdit;
    private int _wordStart, _wordLength;
    private string _word = "";
    private TimeSpan _lastCpu;
    private DateTime _lastSample = DateTime.UtcNow;
    public MainWindow(bool smoke = false, bool demo = false)
    {
        _smoke = smoke;
        _demo = demo;
        var settings = smoke ? Path.Combine(AppContext.BaseDirectory, "data", "smoke-settings.json") : Path.Combine(AppContext.BaseDirectory, "data", "settings.json");
        _vm = new(settings, new DispatcherSynchronizationContext(Dispatcher));
        _global = new(_vm.Engine, () => _vm.Settings, Dispatcher, _spelling.Analyze, _vm.AssistTypingAsync);
        _suggestionKeys = new(Dispatcher, CycleGlobalTab, _global.Dismiss, CancelGlobalTab);
        _globalTabTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Input, (_, _) =>
        { var index = _globalTab.Index; CancelGlobalTab(); if (index >= 0) AcceptGlobal(index); }, Dispatcher); _globalTabTimer.Stop();
        _editorTabTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(600), DispatcherPriority.Input, (_, _) =>
        { var index = _editorTab.Index; CancelEditorTab(); if (index >= 0 && index < _vm.Suggestions.Count && EditorBox.IsKeyboardFocused && EditorBox.Text == _editorOfferText && EditorBox.CaretIndex == _editorOfferCaret) Accept(_vm.Suggestions[index]); }, Dispatcher); _editorTabTimer.Stop();
        _suggestTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(150), DispatcherPriority.Background, (_, _) => RefreshSuggestions(), Dispatcher); _suggestTimer.Stop();
        _resourceTimer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background, (_, _) => SampleResources(), Dispatcher); _resourceTimer.Stop();
        _editorContextTimer = new DispatcherTimer(TimeSpan.FromMilliseconds(1500), DispatcherPriority.Background, async (_, _) => await AssistEditorAsync(), Dispatcher); _editorContextTimer.Stop();
        InitializeComponent();
        DataContext = _vm;
        _global.StatusChanged += status => GlobalStatus.Text = status;
        _global.Corrected += () => _vm.Corrections++;
        _global.OfferChanged += PresentSuggestions;
        _vm.ApplyRequested += ApplyEditorText;
    }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        await _vm.LoadAsync(_smoke);
        SpellingStatus.Text = _spelling.Status;
        _vm.SettingsChanged += () => { CancelEditorContext(); UpdateIntegration(); _ = _vm.PersistFlagsAsync(); };
        if (!_smoke) { UpdateIntegration(); SetUpTray(); _resourceTimer.Start(); }
        else await SmokeChecks.RunUiAsync(this, _vm, _demo);
    }
    private void UpdateIntegration() => _global.SetEnabled(_vm.GlobalEnabled);
    private void WindowSourceInitialized(object? sender, EventArgs e)
    {
        _handle = new WindowInteropHelper(this).Handle;
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WindowMessage);
        if (!_smoke && !NativeMethods.RegisterHotKey(_handle, 1, 0x4003, 0x20)) _vm.Status = "Ctrl+Alt+Space уже занят; используй редактор вручную.";
        if (!_smoke && !NativeMethods.RegisterHotKey(_handle, 2, 0x4003, 0x08)) _vm.Status = "Ctrl+Alt+Backspace уже занят; системная отмена недоступна.";
    }
    private IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr w, IntPtr l, ref bool handled)
    {
        if ((uint)message == NativeMethods.ShowMessage) { ShowWindow(); handled = true; }
        if (message == 0x312 && w.ToInt32() == 1) { _ = CaptureSelection(); handled = true; }
        if (message == 0x312 && w.ToInt32() == 2) { UndoGlobal(); handled = true; }
        if (message == 0x312 && w.ToInt32() is >= 11 and <= 13) { AcceptGlobal(w.ToInt32() - 11); handled = true; }
        return IntPtr.Zero;
    }
    private async Task CaptureSelection()
    {
        if (_capturing) return;
        if (_quickRewrite is not null) { _quickRewrite.Show(); _quickRewrite.Activate(); return; }
        _capturing = true;
        try
        {
            _global.Dismiss();
            var field = await _global.CaptureSelectionAsync();
            for (var attempt = 0; attempt < 40 && !NativeMethods.ModifiersReleased; attempt++) await Task.Delay(20);
            _quickRewrite = new(field, _vm, _global.ApplyRewriteAsync);
            _quickRewrite.Closed += (_, _) => _quickRewrite = null;
            _quickRewrite.Show(); _quickRewrite.Activate();
        }
        catch (Exception ex) when (ex is InvalidOperationException or TimeoutException) { ShowWindow(); _vm.Status = ex.Message; }
        finally { _capturing = false; }
    }
    private void PresentSuggestions(SuggestionOffer? offer)
    {
        CancelGlobalTab(); _suggestionKeys.Stop();
        for (var index = 0; index < 3; index++) NativeMethods.UnregisterHotKey(_handle, 11 + index);
        if (offer is null) { _suggestions?.Hide(); return; }
        _suggestions ??= new(AcceptGlobal, _global.Dismiss);
        _suggestions.Present(offer);
        var tabAvailable = _vm.TabSelection && _suggestionKeys.Start(offer.Field);
        _suggestions.Highlight(-1, tabAvailable);
        for (var index = 0; index < offer.Items.Count; index++)
            if (!NativeMethods.RegisterHotKey(_handle, 11 + index, 0x4003, (uint)(0x31 + index))) _vm.Status = $"Ctrl+Alt+{index + 1} занят · выбери подсказку мышью";
    }
    private void CycleGlobalTab()
    {
        if (_global.CurrentOffer is not { } offer || !_vm.TabSelection) return;
        var index = _globalTab.Next(offer.Items.Count);
        _suggestions?.Highlight(index);
        _globalTabTimer.Stop(); _globalTabTimer.Start();
    }
    private void CancelGlobalTab() { _globalTabTimer.Stop(); _globalTab.Reset(); _suggestions?.Highlight(-1, _vm.TabSelection); }
    private void CancelEditorTab() { _editorTabTimer.Stop(); _editorTab.Reset(); }
    private async void AcceptGlobal(int index) { try { await _global.AcceptAsync(index); } catch (OperationCanceledException) { } }
    private async void UndoGlobal() { try { await _global.UndoAsync(); } catch (OperationCanceledException) { } }
    private void ShowWindow() { Show(); WindowState = WindowState.Normal; Activate(); }
    private void Navigate(object sender, RoutedEventArgs e)
    {
        if (EditorPage is null || DictionaryPage is null || SettingsPage is null || HomePage is null) return;
        var page = (sender as RadioButton)?.Tag as string;
        EditorPage.Visibility = page == "Editor" ? Visibility.Visible : Visibility.Collapsed;
        DictionaryPage.Visibility = page == "Dictionary" ? Visibility.Visible : Visibility.Collapsed;
        SettingsPage.Visibility = page == "Settings" ? Visibility.Visible : Visibility.Collapsed;
        HomePage.Visibility = page == "Home" ? Visibility.Visible : Visibility.Collapsed;
        PageScroll?.ScrollToTop();
    }
    private void StyleChanged(object sender, SelectionChangedEventArgs e)
    { if (_vm is not null && StyleBox is not null) _vm.Style = (RewriteStyle)StyleBox.SelectedIndex; }
    private void EditorChanged(object sender, TextChangedEventArgs e)
    {
        if (_editing || EditorBox is null || _vm is null) return;
        CancelEditorContext();
        CancelEditorTab();
        _vm.Editor = EditorBox.Text;
        var skip = _skipBoundary;
        var edit = skip ? null : SmartTyping.Edit(_vm.Engine, EditorBox.Text, EditorBox.CaretIndex, _vm.Settings, _spelling.Analyze);
        _skipBoundary = false;
        if (edit is not null)
        {
            ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
            _lastEdit = edit; _vm.Corrections++; _vm.Status = $"{edit.Original} → {edit.Replacement} · Backspace — вернуть";
        }
        else if (_lastEdit is not null && EditorBox.Text != _lastEdit.After) _lastEdit = null;
        UndoButton.IsEnabled = _lastEdit is not null;
        ScheduleSuggestions();
        if (!_smoke && !skip && _vm.ContextAssist)
        {
            var pendingText = EditorBox.Text;
            // WPF raises SelectionChanged after TextChanged for normal typing. Arm
            // after that caret update; otherwise it cancels every idle check.
            Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
            { if (_vm.ContextAssist && EditorBox.Text == pendingText && EditorBox.IsKeyboardFocused) _editorContextTimer.Start(); });
        }
    }
    private void EditorSelectionChanged(object sender, RoutedEventArgs e) { if (!_editing && _vm is not null) { CancelEditorContext(); CancelEditorTab(); ScheduleSuggestions(); } }
    private void CancelEditorContext() { _editorContextTimer?.Stop(); _editorContextCancellation?.Cancel(); }
    private async Task AssistEditorAsync()
    {
        _editorContextTimer.Stop();
        if (_editorAssisting || !_vm.ContextAssist || !EditorBox.IsKeyboardFocused || EditorBox.SelectionLength != 0 || _vm.Busy) return;
        var text = EditorBox.Text; var caret = EditorBox.CaretIndex;
        if (ContextTyping.Capture(text, caret) is not { } phrase) return;
        _editorAssisting = true;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30)); _editorContextCancellation = cancellation;
        try
        {
            var result = await _vm.AssistTypingAsync(phrase.Text, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_vm.ContextAssist || !EditorBox.IsKeyboardFocused || EditorBox.Text != text || EditorBox.CaretIndex != caret || EditorBox.SelectionLength != 0) return;
            if (result is null) { if (AiRuntime.IsInstalled(_vm.Settings.AiRoot)) _editorContextTimer.Start(); return; }
            if (ContextTyping.Apply(text, phrase, result) is not { } edit) return;
            ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
            _lastEdit = edit; UndoButton.IsEnabled = true; _vm.Corrections++;
            _vm.Status = "Запятые, регистр и пробелы проверены · Backspace — вернуть";
            ScheduleSuggestions();
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException or TimeoutException or System.Text.Json.JsonException)
        { _vm.Status = "Контекстная правка пропущена: " + ex.Message; }
        finally { if (_editorContextCancellation == cancellation) _editorContextCancellation = null; _editorAssisting = false; }
    }
    private void ScheduleSuggestions() { _suggestTimer.Stop(); _suggestTimer.Start(); }
    private void RefreshSuggestions()
    {
        _suggestTimer.Stop(); CancelEditorTab(); _vm.Suggestions.Clear();
        if (EditorBox.SelectionLength != 0 || !_vm.ShowSuggestions) return;
        var text = EditorBox.Text; var end = EditorBox.CaretIndex;
        var word = TypingContext.WordBeforeCaret(text, end, end);
        if (word is null) return;
        _wordStart = word.Start; _wordLength = word.Length; _word = word.Word;
        _editorOfferText = text; _editorOfferCaret = end;
        foreach (var suggestion in SuggestionPolicy.Build(_vm.Engine, word, _vm.FixLayout, _spelling.Analyze)) _vm.Suggestions.Add(suggestion);
        SuggestionHint.Text = _vm.Suggestions.Count == 0 ? "Слово оставлено как есть · исправление не требуется" : "Tab → 1 / 2 / 3 · пауза 0,6 с — принять · Escape — оставить";
    }
    private void EditorPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Tab || Keyboard.Modifiers != ModifierKeys.None) CancelEditorTab();
        if (e.Key == Key.Escape) { _suggestTimer.Stop(); _vm.Suggestions.Clear(); SuggestionHint.Text = "Подсказки скрыты · Tab перемещает фокус"; }
        if (e.Key == Key.Z && Keyboard.Modifiers.HasFlag(ModifierKeys.Control)) { _skipBoundary = true; _lastEdit = null; }
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && _lastEdit is not null && EditorBox.Text == _lastEdit.After && EditorBox.CaretIndex == _lastEdit.Caret)
        { UndoCorrection(this, new()); e.Handled = true; }
        else if (e.Key == Key.Tab && Keyboard.Modifiers == ModifierKeys.None && _vm.TabSelection && _vm.Suggestions.Count > 0)
        {
            var index = _editorTab.Next(_vm.Suggestions.Count);
            SuggestionHint.Text = $"Выбрано {index + 1}: {_vm.Suggestions[index].Word} · пауза 0,6 с — принять · Escape — отменить";
            _editorTabTimer.Stop(); _editorTabTimer.Start(); e.Handled = true;
        }
    }
    private void AcceptSuggestion(object sender, RoutedEventArgs e) { if ((sender as Button)?.Tag is Suggestion suggestion) Accept(suggestion); }
    private void Accept(Suggestion suggestion)
    {
        CancelEditorTab();
        if (EditorBox.Text != _editorOfferText || EditorBox.CaretIndex != _editorOfferCaret || _wordStart < 0 || _wordStart + _wordLength > EditorBox.Text.Length || EditorBox.Text.Substring(_wordStart, _wordLength) != _word) return;
        var edit = TextEngine.Replace(EditorBox.Text, _wordStart, _wordLength, suggestion.Word, EditorBox.CaretIndex);
        ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
        _lastEdit = edit; UndoButton.IsEnabled = true; _vm.Corrections++; _vm.Status = "Подсказка принята · можно отменить";
        EditorBox.Focus(); ScheduleSuggestions();
    }
    private void UndoCorrection(object sender, RoutedEventArgs e)
    {
        CancelEditorContext();
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
            try { EditorBox.Select(start, length); EditorBox.SelectedText = replacement; }
            finally { EditorBox.EndChange(); }
            _vm.Editor = EditorBox.Text;
            // TextBox.Text is updated at EndChange. Clamping before it used the old
            // length and placed the caret inside a longer corrected sentence.
            EditorBox.CaretIndex = Math.Clamp(caret, 0, EditorBox.Text.Length);
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
        menu.Items.Add("Включить / выключить Т9", null, (_, _) => Dispatcher.BeginInvoke(() => _vm.GlobalEnabled = !_vm.GlobalEnabled));
        menu.Items.Add("Выход", null, (_, _) => Dispatcher.BeginInvoke(() => { _quit = true; Close(); }));
        _tray = new() { Icon = _icon, Text = "TypePilot · локальный помощник", ContextMenuStrip = menu, Visible = true };
        _tray.DoubleClick += (_, _) => Dispatcher.BeginInvoke(ShowWindow);
    }
    private void MinimizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeClick(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void HideToTrayClick(object sender, RoutedEventArgs e) => Hide();
    private void OpenEditorClick(object sender, RoutedEventArgs e) { EditorNav.IsChecked = true; Navigate(EditorNav, e); }
    private void WindowClosing(object? sender, CancelEventArgs e)
    {
        if (!_quit && !_smoke) { e.Cancel = true; Hide(); return; }
        CancelEditorContext(); _suggestTimer.Stop(); _resourceTimer.Stop(); _globalTabTimer.Stop(); _editorTabTimer.Stop(); _suggestionKeys.Dispose(); _global.Dispose(); _quickRewrite?.Close(); _vm.Dispose(); _spelling.Dispose();
        _source?.RemoveHook(WindowMessage); NativeMethods.UnregisterHotKey(_handle, 1); NativeMethods.UnregisterHotKey(_handle, 2);
        _tray?.Dispose(); _icon?.Dispose();
        _suggestions?.Close(); _quickRewrite?.Close();
        for (var index = 0; index < 3; index++) NativeMethods.UnregisterHotKey(_handle, 11 + index);
    }
    internal void SmokeSetEditor(string text) => ApplyEditorText(text);
    internal void SmokeCorrectBoundary()
    {
        EditorBox.CaretIndex = EditorBox.Text.Length;
        _skipBoundary = false;
        EditorChanged(EditorBox, new TextChangedEventArgs(TextBox.TextChangedEvent, UndoAction.None));
    }
    internal void SmokeUndo() => UndoCorrection(this, new());
    internal void SmokeShowPage(string page)
    {
        var button = page == "Home" ? HomeNav : page == "Editor" ? EditorNav : page == "Dictionary" ? DictionaryNav : SettingsNav;
        button.IsChecked = true; Navigate(button, new()); UpdateLayout();
    }
    internal void SmokeSetStyle(int index) => StyleBox.SelectedIndex = index;
    internal void SmokeScroll(double offset) { PageScroll.ScrollToVerticalOffset(offset); UpdateLayout(); }
    internal int SmokeCaret => EditorBox.CaretIndex;
    internal void SmokeContext(string result)
    {
        var phrase = ContextTyping.Capture(EditorBox.Text, EditorBox.CaretIndex)!;
        var edit = ContextTyping.Apply(EditorBox.Text, phrase, result)!;
        ReplaceEditorRange(edit.Start, edit.Original.Length, edit.Replacement, edit.Caret);
    }
    internal void SmokeInsert(string text) => EditorBox.SelectedText = text;
}
