using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using TypePilot.Core;

namespace TypePilot.App;

public sealed class PilotViewModel : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;
    public event Action? SettingsChanged;
    public event Action<string>? ApplyRequested;
    private readonly SettingsStore _store;
    private readonly IRewriteRuntime _ai;
    private readonly SynchronizationContext _context;
    private CancellationTokenSource? _rewriteCancellation;
    private string _editor = "", _result = "", _status = "Готов к набору", _aiStatus = "ИИ спит · не занимает видеопамять", _dictionary = "", _allowed = "";
    private string _rewriteOriginal = "";
    private bool _busy;
    private int _corrections;
    public TextEngine Engine { get; } = DefaultEngine.Create();
    public PilotSettings Settings { get; private set; } = new();
    public ObservableCollection<Suggestion> Suggestions { get; } = [];
    public string Editor { get => _editor; set { if (Set(ref _editor, value)) { Notify(nameof(CharacterCount)); RewriteCommand.Refresh(); ApplyCommand.Refresh(); } } }
    public string Result { get => _result; set { if (Set(ref _result, value)) { CopyResultCommand.Refresh(); ApplyCommand.Refresh(); } } }
    public string Status { get => _status; set => Set(ref _status, value); }
    public string AiStatus { get => _aiStatus; set => Set(ref _aiStatus, value); }
    public string DictionaryText { get => _dictionary; set => Set(ref _dictionary, value); }
    public string AllowedText { get => _allowed; set => Set(ref _allowed, value); }
    public int CharacterCount => Editor.Length;
    public int Corrections { get => _corrections; set => Set(ref _corrections, value); }
    public bool Busy { get => _busy; set { if (Set(ref _busy, value)) RefreshCommands(); } }
    public bool AutoCorrect { get => Settings.AutoCorrect; set { Settings.AutoCorrect = value; Notify(); SettingsChanged?.Invoke(); } }
    public bool FixLayout { get => Settings.FixLayout; set { Settings.FixLayout = value; Notify(); SettingsChanged?.Invoke(); } }
    public bool GlobalEnabled { get => Settings.GlobalEnabled; set { Settings.GlobalEnabled = value; Notify(); SettingsChanged?.Invoke(); } }
    public RewriteStyle Style { get; set; } = RewriteStyle.Clear;
    public string AiRoot { get => Settings.AiRoot; set { Settings.AiRoot = value; Notify(); } }
    public long AiMemoryMb => _ai.MemoryMb;
    public ActionCommand RewriteCommand { get; }
    public ActionCommand CancelCommand { get; }
    public ActionCommand ApplyCommand { get; }
    public ActionCommand CopyResultCommand { get; }
    public ActionCommand SaveCommand { get; }
    public ActionCommand UnloadCommand { get; }
    public ActionCommand ClearCommand { get; }
    public ActionCommand CopyEditorCommand { get; }
    public PilotViewModel(string settingsPath, SynchronizationContext? context = null, IRewriteRuntime? runtime = null)
    {
        _store = new(settingsPath);
        _ai = runtime ?? new AiRuntime();
        _context = context ?? SynchronizationContext.Current ?? new SynchronizationContext();
        _ai.StatusChanged += status => _context.Post(_ => AiStatus = status, null);
        RewriteCommand = new(() => _ = RewriteAsync(Editor), () => !Busy && !string.IsNullOrWhiteSpace(Editor) && Editor.Length <= 4000);
        CancelCommand = new(() => _rewriteCancellation?.Cancel(), () => Busy);
        ApplyCommand = new(ApplyResult, () => !Busy && Result.Length > 0 && Editor == _rewriteOriginal);
        CopyResultCommand = new(() => Copy(Result), () => Result.Length > 0 && !Busy);
        SaveCommand = new(() => _ = SaveAsync(), () => !Busy);
        UnloadCommand = new(() => _ = UnloadAsync(), () => !Busy);
        ClearCommand = new(() => { Editor = ""; Result = ""; Status = "Редактор очищен"; }, () => !Busy);
        CopyEditorCommand = new(() => Copy(Editor), () => !Busy);
    }
    public async Task LoadAsync(bool smoke)
    {
        try { Settings = smoke ? new() : await _store.LoadAsync(); }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException) { Status = "Настройки не прочитаны; исходный файл сохранён. Используются значения по умолчанию."; }
        Settings.AllowedProcesses ??= [];
        Settings.PersonalWords ??= [];
        Engine.SetPersonal(Settings.PersonalWords);
        DictionaryText = string.Join(Environment.NewLine, Settings.PersonalWords);
        AllowedText = string.Join(Environment.NewLine, Settings.AllowedProcesses);
        foreach (var property in new[] { nameof(AutoCorrect), nameof(FixLayout), nameof(GlobalEnabled), nameof(AiRoot) }) Notify(property);
        AiStatus = AiRuntime.IsInstalled(Settings.AiRoot) ? "Qwen3 4B установлена · сейчас выгружена" : "Модель не установлена · Т9 готов без неё";
    }
    public async Task SaveAsync()
    {
        try
        {
            var words = SettingsStore.ParseDictionary(DictionaryText);
            var allowed = AllowedText.Split(['\n', '\r'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (allowed.Length > 40 || allowed.Any(p => p.Length > 80 || p.Any(c => !char.IsLetterOrDigit(c) && c != '-' && c != '_' && c != '.'))) throw new InvalidDataException("По одному имени процесса без .exe и пути в строке, максимум 40 приложений.");
            Settings.PersonalWords = words.ToList(); Settings.AllowedProcesses = allowed.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            await _store.SaveAsync(Settings);
            Engine.SetPersonal(words); DictionaryText = string.Join(Environment.NewLine, words);
            Status = "Настройки и личный словарь сохранены"; SettingsChanged?.Invoke();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidDataException) { Status = "Не сохранено: " + ex.Message; }
    }
    public async Task PersistFlagsAsync()
    {
        try { await _store.SaveAsync(Settings); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Status = "Настройки не сохранены: " + ex.Message; }
    }
    public async Task RewriteAsync(string text)
    {
        if (Busy || string.IsNullOrWhiteSpace(text) || text.Length > 4000) return;
        Busy = true; _rewriteOriginal = Editor; Result = "";
        _rewriteCancellation?.Dispose(); _rewriteCancellation = new();
        try { Result = await _ai.RewriteAsync(Settings, text, Style, _rewriteCancellation.Token); AiStatus = "Результат готов · проверь смысл перед применением"; }
        catch (OperationCanceledException) { AiStatus = "Запрос отменён · исходный текст сохранён"; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or InvalidDataException or ArgumentException or TimeoutException or System.Text.Json.JsonException) { AiStatus = "ИИ: " + ex.Message; }
        finally { Busy = false; }
    }
    private void ApplyResult()
    {
        if (Editor != _rewriteOriginal) { Status = "Текст изменился: результат не применён"; return; }
        ApplyRequested?.Invoke(Result); Status = "Результат перенесён в редактор · Ctrl+Z — отменить";
    }
    private void Copy(string text)
    {
        try { System.Windows.Clipboard.SetText(text); Status = "Текст скопирован · вставь через Ctrl+V"; }
        catch (System.Runtime.InteropServices.ExternalException) { Status = "Буфер обмена занят. Попробуй ещё раз."; }
    }
    private async Task UnloadAsync()
    { try { await _ai.UnloadAsync(); } catch (InvalidOperationException ex) { AiStatus = ex.Message; } }
    private void RefreshCommands()
    { foreach (var command in new[] { RewriteCommand, CancelCommand, ApplyCommand, CopyResultCommand, SaveCommand, UnloadCommand, ClearCommand, CopyEditorCommand }) command.Refresh(); }
    private bool Set<T>(ref T field, T value, [CallerMemberName] string? property = null)
    { if (EqualityComparer<T>.Default.Equals(field, value)) return false; field = value; Notify(property); return true; }
    private void Notify([CallerMemberName] string? property = null) => PropertyChanged?.Invoke(this, new(property));
    public void Dispose() { _rewriteCancellation?.Cancel(); _ai.Dispose(); _rewriteCancellation?.Dispose(); }
}
