using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

internal sealed record SuggestionOffer(FieldSnapshot Field, WordContext Word, IReadOnlyList<Suggestion> Items);

internal sealed class GlobalTyping : IDisposable
{
    private readonly TextEngine _engine;
    private readonly Func<PilotSettings> _settings;
    private readonly Func<string, SpellingResult> _spelling;
    private readonly Func<string, CancellationToken, Task<string?>>? _assist;
    private readonly FieldAccess _fields = new();
    private readonly DispatcherTimer _poll;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _working, _disposed, _assisting;
    private CancellationTokenSource? _contextCancellation;
    private string _pending = "", _localDone = "", _contextDone = "";
    private long _quietSince;
    private string _dismissed = "", _observed = "", _undoSuppressed = "";
    private FieldSnapshot? _lastAfter;
    private TextEdit? _lastEdit;
    private FieldSnapshot? _previousField;
    public SuggestionOffer? CurrentOffer { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<SuggestionOffer?>? OfferChanged;
    public event Action? Corrected;
    public GlobalTyping(TextEngine engine, Func<PilotSettings> settings, Dispatcher dispatcher, Func<string, SpellingResult>? spelling = null, Func<string, CancellationToken, Task<string?>>? assist = null)
    {
        _engine = engine; _settings = settings;
        _spelling = spelling ?? (_ => SpellingResult.Unavailable);
        _assist = assist;
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _poll.Stop();
    }
    public void SetEnabled(bool enabled)
    {
        CancelContext(); _pending = ""; _previousField = null; _observed = ""; HideOffer();
        if (enabled) _poll.Start(); else { _poll.Stop(); HideOffer(); }
        StatusChanged?.Invoke(enabled ? "Т9 в фоне · проверяемые поля Windows и браузера" : "Фоновый Т9 на паузе · горячая клавиша ИИ доступна");
    }
    private string[] Allowed => _settings().AllowedProcesses.ToArray();
    private static string Fingerprint(FieldSnapshot field) => $"{field.Identity}:{field.Start}:{field.End}:{field.Text}";
    private async Task PollAsync()
    {
        if (_working || _disposed || !_settings().GlobalEnabled) return;
        _working = true;
        try
        {
            var field = await _fields.ReadAsync(Allowed).WaitAsync(TimeSpan.FromSeconds(2));
            if (_disposed || !_settings().GlobalEnabled) { HideOffer(); return; }
            if (field is null) { CancelContext(); _pending = ""; _observed = ""; _previousField = null; HideOffer(); return; }
            var fingerprint = Fingerprint(field);
            var changedText = _previousField is { } previous && previous.Identity == field.Identity && previous.Foreground == field.Foreground && previous.Text != field.Text;
            var changed = _previousField is null || !_previousField.SameContent(field);
            _previousField = field;
            if (changed)
            {
                CancelContext();
                _pending = changedText && field.Start == field.End ? fingerprint : "";
                _quietSince = Environment.TickCount64; _contextDone = "";
            }
            if (fingerprint == _undoSuppressed) { HideOffer(); return; }
            // Merely focusing an existing draft is not permission to rewrite it.
            if (_pending == fingerprint && fingerprint != _dismissed && Environment.TickCount64 - _quietSince >= 500 && NativeMethods.InputIdle(500))
            {
                if (_localDone != fingerprint)
                {
                    _localDone = fingerprint;
                    var edit = SmartTyping.Edit(_engine, field.Text, field.End, _settings(), _spelling);
                    if (edit is not null && NativeMethods.ModifiersReleased && await _fields.ReplaceAsync(field, edit.Start, edit.Original.Length, edit.Replacement, edit.Caret, Allowed, false, _lifetime.Token, true))
                    {
                        await RememberAsync(field, edit, true);
                        StatusChanged?.Invoke("Текст поправлен · Ctrl+Alt+Backspace — отменить"); Corrected?.Invoke(); HideOffer(); return;
                    }
                }
                if (_settings().ContextAssist && !_assisting && _assist is not null && _contextDone != fingerprint && Environment.TickCount64 - _quietSince >= 1500 && NativeMethods.InputIdle(1500) && ContextTyping.Capture(field.Text, field.End) is { } phrase)
                { _contextDone = fingerprint; _ = AssistAsync(field, phrase); }
            }
            if (_observed == fingerprint) return;
            _observed = fingerprint;
            var word = TypingContext.WordBeforeCaret(field.Text, field.Start, field.End);
            if (word is null) { HideOffer(); return; }
            if (!_settings().ShowSuggestions || fingerprint == _dismissed) { HideOffer(); return; }
            var items = SuggestionPolicy.Build(_engine, word, _settings().FixLayout, _spelling);
            CurrentOffer = items.Count == 0 ? null : new(field, word, items);
            OfferChanged?.Invoke(CurrentOffer);
        }
        catch (OperationCanceledException) { HideOffer(); }
        catch (TimeoutException) { HideOffer(); StatusChanged?.Invoke("Поле не отвечает · Т9 не меняет текст"); }
        finally { _working = false; }
    }
    private void CancelContext() => _contextCancellation?.Cancel();
    private async Task AssistAsync(FieldSnapshot field, TypingPhrase phrase)
    {
        _assisting = true;
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cancellation.CancelAfter(TimeSpan.FromSeconds(30)); _contextCancellation = cancellation;
        try
        {
            var result = await _assist!(phrase.Text, cancellation.Token);
            if (cancellation.IsCancellationRequested || !_settings().ContextAssist || !_settings().GlobalEnabled || _disposed) return;
            if (result is null) { if (AiRuntime.IsInstalled(_settings().AiRoot)) { _contextDone = ""; _quietSince = Environment.TickCount64; } return; }
            var edit = ContextTyping.Apply(field.Text, phrase, result);
            if (edit is null) return;
            if (await _fields.ReplaceAsync(field, edit.Start, edit.Original.Length, edit.Replacement, edit.Caret, Allowed, false, cancellation.Token, true))
            {
                await RememberAsync(field, edit, false);
                StatusChanged?.Invoke("Запятые, регистр и пробелы проверены · Ctrl+Alt+Backspace — отменить"); Corrected?.Invoke(); HideOffer();
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or ArgumentException or TimeoutException or System.Text.Json.JsonException)
        { StatusChanged?.Invoke("Контекстная правка пропущена: " + ex.Message); }
        finally { if (_contextCancellation == cancellation) _contextCancellation = null; _assisting = false; }
    }
    private async Task RememberAsync(FieldSnapshot field, TextEdit edit, bool allowContext)
    {
        await Task.Delay(70, _lifetime.Token);
        var after = await _fields.ReadAsync(Allowed);
        if (after is null || after.Identity != field.Identity || after.Text != edit.After || after.Start != edit.Caret || after.End != edit.Caret) return;
        _lastAfter = after; _lastEdit = edit; _previousField = after;
        _pending = allowContext ? Fingerprint(after) : "";
        _quietSince = Environment.TickCount64; _localDone = Fingerprint(after); _observed = "";
    }
    private void HideOffer() { if (CurrentOffer is null) return; CurrentOffer = null; OfferChanged?.Invoke(null); }
    public void Dismiss() { if (CurrentOffer is not null) _dismissed = Fingerprint(CurrentOffer.Field); HideOffer(); }
    public async Task AcceptAsync(int index)
    {
        var offer = CurrentOffer;
        CancelContext(); _pending = "";
        Dismiss();
        if (offer is null || index < 0 || index >= offer.Items.Count || _disposed) return;
        for (var attempt = 0; attempt < 30 && !NativeMethods.ModifiersReleased; attempt++) await Task.Delay(20);
        var edit = TextEngine.Replace(offer.Field.Text, offer.Word.Start, offer.Word.Length, offer.Items[index].Word, offer.Field.End);
        if (await _fields.ReplaceAsync(offer.Field, edit.Start, edit.Original.Length, edit.Replacement, edit.Caret, Allowed, false, _lifetime.Token))
        {
            await RememberAsync(offer.Field, edit, false);
            StatusChanged?.Invoke("Подсказка принята · Ctrl+Alt+Backspace — отменить"); Corrected?.Invoke();
        }
        else StatusChanged?.Invoke("Поле или текст изменились — подсказка не вставлена");
    }
    public async Task UndoAsync()
    {
        if (_lastEdit is null || _lastAfter is null) return;
        CancelContext(); _pending = "";
        var edit = _lastEdit; var after = _lastAfter; _lastEdit = null;
        for (var attempt = 0; attempt < 30 && !NativeMethods.ModifiersReleased; attempt++) await Task.Delay(20);
        if (await _fields.ReplaceAsync(after, edit.Start, edit.Replacement.Length, edit.Original, edit.Caret + edit.Original.Length - edit.Replacement.Length, Allowed, false, _lifetime.Token))
        {
            await Task.Delay(70, _lifetime.Token);
            var restored = await _fields.ReadAsync(Allowed);
            if (restored is not null) _undoSuppressed = Fingerprint(restored);
            StatusChanged?.Invoke("Исходное слово возвращено"); HideOffer();
        }
        else StatusChanged?.Invoke("Текст уже изменился — безопасная отмена недоступна");
    }
    public async Task<FieldSnapshot?> CaptureSelectionAsync()
    {
        var field = await _fields.ReadAsync(Allowed).WaitAsync(TimeSpan.FromSeconds(2));
        return field is not null && !string.IsNullOrWhiteSpace(field.SelectedText) && field.SelectedText.Length <= 4000 ? field : null;
    }
    public Task<bool> ApplyRewriteAsync(FieldSnapshot field, string result, CancellationToken token) =>
        _fields.ReplaceAsync(field, field.Start, field.End - field.Start, result, field.Start + result.Length, Allowed, true, token);
    public void Dispose() { _disposed = true; _poll.Stop(); CancelContext(); _lifetime.Cancel(); _fields.Dispose(); }
}
