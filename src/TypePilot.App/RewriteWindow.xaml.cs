using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using TypePilot.Core;

namespace TypePilot.App;

public partial class RewriteWindow : Window
{
    private readonly FieldSnapshot? _target;
    private readonly PilotViewModel _vm;
    private readonly Func<FieldSnapshot, string, CancellationToken, Task<bool>> _apply;
    private readonly CancellationTokenSource _lifetime = new();
    private bool _busy;
    private readonly bool _preview;
    internal RewriteWindow(FieldSnapshot? target, PilotViewModel vm, Func<FieldSnapshot, string, CancellationToken, Task<bool>> apply, bool preview = false)
    {
        _target = target; _vm = vm; _apply = apply; _preview = preview; InitializeComponent();
        OriginalBox.Text = target?.SelectedText ?? "";
        StyleChoice.SelectedIndex = (int)vm.Style;
        _vm.PropertyChanged += ModelChanged;
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { e.Handled = true; Close(); } };
    }
    private void ModelChanged(object? sender, PropertyChangedEventArgs e) { if (_busy && e.PropertyName == nameof(PilotViewModel.AiStatus)) Feedback.Text = _vm.AiStatus; }
    private async void WindowLoaded(object sender, RoutedEventArgs e)
    {
        if (_preview) return;
        if (_target is not null) { FloatingPlacement.Near(this, _target.Anchor); SourceLabel.Text = $"{_target.Process} · выбранный фрагмент · только локально"; await GenerateAsync(); }
        else { Feedback.Text = "Выдели текст в поддерживаемом поле и нажми Ctrl+Alt+Space. Если поле недоступно, используй редактор TypePilot."; RetryButton.IsEnabled = false; }
    }
    private async Task GenerateAsync()
    {
        if (_target is null || _busy) return;
        _busy = true; UpdateButtons(); ResultBox.Text = ""; Feedback.Text = "Готовим локальный ИИ…";
        try
        {
            ResultBox.Text = await _vm.RewriteExternalAsync(_target.SelectedText, (RewriteStyle)StyleChoice.SelectedIndex, _lifetime.Token);
            Feedback.Text = "Проверь смысл. «Заменить в поле» вернёт фразу в исходное приложение.";
        }
        catch (OperationCanceledException) { Feedback.Text = "Запрос отменён"; }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or HttpRequestException or TimeoutException or ArgumentException or System.Text.Json.JsonException) { Feedback.Text = "ИИ: " + ex.Message; }
        finally { _busy = false; UpdateButtons(); }
    }
    private void UpdateButtons()
    {
        if (ApplyButton is null) return;
        ApplyButton.IsEnabled = !_busy && _target is not null && !string.IsNullOrWhiteSpace(ResultBox.Text);
        CopyButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(ResultBox.Text);
        RetryButton.IsEnabled = !_busy && _target is not null;
        StyleChoice.IsEnabled = !_busy;
    }
    private void ResultChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateButtons();
    private async void RetryClick(object sender, RoutedEventArgs e) => await GenerateAsync();
    private async void ApplyClick(object sender, RoutedEventArgs e)
        => await ApplyAsync();
    internal async Task<bool> ApplyAsync()
    {
        if (_target is null || _busy || string.IsNullOrWhiteSpace(ResultBox.Text)) return false;
        _busy = true; UpdateButtons(); Hide();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token); deadline.CancelAfter(2000);
        try
        {
            if (await _apply(_target, ResultBox.Text, deadline.Token).WaitAsync(deadline.Token)) { Close(); return true; }
            Feedback.Text = "Поле, выделение или текст изменились. Замена отменена: скопируй результат вручную.";
        }
        catch (OperationCanceledException) { Feedback.Text = "Поле не ответило вовремя. Автоматическая замена отменена."; }
        finally { _busy = false; if (!_lifetime.IsCancellationRequested) { Show(); Activate(); UpdateButtons(); } }
        return false;
    }
    private void CopyClick(object sender, RoutedEventArgs e)
    { try { Clipboard.SetText(ResultBox.Text); Feedback.Text = "Скопировано · вставь вручную через Ctrl+V"; } catch (ExternalException) { Feedback.Text = "Буфер обмена занят. Попробуй ещё раз."; } }
    private void CloseClick(object sender, RoutedEventArgs e) => Close();
    private void WindowClosing(object? sender, CancelEventArgs e) { _lifetime.Cancel(); _vm.PropertyChanged -= ModelChanged; }
    internal void SmokePreview(string result) { ResultBox.Text = result; Feedback.Text = "Предпросмотр интерфейса · искусственный тестовый текст"; }
}
