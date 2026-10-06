using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

internal sealed record SuggestionOffer(FieldSnapshot Field, WordContext Word, IReadOnlyList<Suggestion> Items);

internal sealed class GlobalTyping : IDisposable
{
    private readonly TextEngine _engine;
    private readonly Func<PilotSettings> _settings;
    private readonly Func<string, IReadOnlyList<Suggestion>> _spelling;
    private readonly FieldAccess _fields = new();
    private readonly DispatcherTimer _poll;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _working, _disposed;
    private string _dismissed = "", _observed = "", _undoSuppressed = "";
    private FieldSnapshot? _lastAfter;
    private TextEdit? _lastEdit;
    public SuggestionOffer? CurrentOffer { get; private set; }
    public event Action<string>? StatusChanged;
    public event Action<SuggestionOffer?>? OfferChanged;
    public event Action? Corrected;
    public GlobalTyping(TextEngine engine, Func<PilotSettings> settings, Dispatcher dispatcher, Func<string, IReadOnlyList<Suggestion>>? spelling = null)
    {
        _engine = engine; _settings = settings;
        _spelling = spelling ?? (_ => []);
        _poll = new DispatcherTimer(TimeSpan.FromMilliseconds(280), DispatcherPriority.Background, async (_, _) => await PollAsync(), dispatcher);
        _poll.Stop();
    }
    public void SetEnabled(bool enabled)
    {
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
            if (field is null) { _observed = ""; HideOffer(); return; }
            var fingerprint = Fingerprint(field);
            if (_observed == fingerprint) return;
            _observed = fingerprint;
            var word = TypingContext.WordBeforeCaret(field.Text, field.Start, field.End);
            if (word is null || fingerprint == _undoSuppressed) { HideOffer(); return; }
            if (_settings().AutoCorrect && word.AtBoundary && fingerprint != _dismissed)
            {
                var edit = _engine.CorrectAtBoundary(field.Text, field.End, _settings().FixLayout);
                if (edit is not null && NativeMethods.ModifiersReleased && await _fields.ReplaceAsync(field, edit.Start, edit.Original.Length, edit.Replacement, edit.Caret, Allowed, false, _lifetime.Token))
                {
                    await Task.Delay(70, _lifetime.Token);
                    var after = await _fields.ReadAsync(Allowed);
                    if (after is not null && after.Identity == field.Identity && after.Text == edit.After) { _lastAfter = after; _lastEdit = edit; }
                    StatusChanged?.Invoke("Опечатка исправлена · Ctrl+Alt+Backspace — отменить"); Corrected?.Invoke(); HideOffer(); return;
                }
            }
            if (!_settings().ShowSuggestions || fingerprint == _dismissed) { HideOffer(); return; }
            var items = _engine.Suggest(word.Word).Concat(_engine.IsKnown(word.Word) ? [] : _spelling(word.Word)).Concat(word.AtBoundary ? [] : _engine.Complete(word.Word))
                .Where(s => _settings().FixLayout || s.Reason != "Другая раскладка")
                .DistinctBy(s => s.Word.ToLowerInvariant()).Take(3).ToArray();
            CurrentOffer = items.Length == 0 ? null : new(field, word, items);
            OfferChanged?.Invoke(CurrentOffer);
        }
        catch (OperationCanceledException) { HideOffer(); }
        catch (TimeoutException) { HideOffer(); StatusChanged?.Invoke("Поле не отвечает · Т9 не меняет текст"); }
        finally { _working = false; }
    }
    private void HideOffer() { if (CurrentOffer is null) return; CurrentOffer = null; OfferChanged?.Invoke(null); }
    public void Dismiss() { if (CurrentOffer is not null) _dismissed = Fingerprint(CurrentOffer.Field); HideOffer(); }
    public async Task AcceptAsync(int index)
    {
        var offer = CurrentOffer;
        Dismiss();
        if (offer is null || index < 0 || index >= offer.Items.Count || _disposed) return;
        for (var attempt = 0; attempt < 30 && !NativeMethods.ModifiersReleased; attempt++) await Task.Delay(20);
        var edit = TextEngine.Replace(offer.Field.Text, offer.Word.Start, offer.Word.Length, offer.Items[index].Word, offer.Field.End);
        if (await _fields.ReplaceAsync(offer.Field, edit.Start, edit.Original.Length, edit.Replacement, edit.Caret, Allowed, false, _lifetime.Token))
        {
            await Task.Delay(70, _lifetime.Token);
            var after = await _fields.ReadAsync(Allowed);
            if (after is not null && after.Identity == offer.Field.Identity && after.Text == edit.After) { _lastAfter = after; _lastEdit = edit; }
            StatusChanged?.Invoke("Подсказка принята · Ctrl+Alt+Backspace — отменить"); Corrected?.Invoke();
        }
        else StatusChanged?.Invoke("Поле или текст изменились — подсказка не вставлена");
    }
    public async Task UndoAsync()
    {
        if (_lastEdit is null || _lastAfter is null) return;
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
    public void Dispose() { _disposed = true; _poll.Stop(); _lifetime.Cancel(); _fields.Dispose(); }
}
