using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Automation.Text;
using TypePilot.Core;

namespace TypePilot.App;

internal sealed record FieldSnapshot(IntPtr Foreground, IntPtr Native, string Process, string Identity,
    string Text, int Start, int End, Rect Anchor, AutomationElement? Element = null)
{
    public string SelectedText => Text[Start..End];
    public bool SameContent(FieldSnapshot other) => Identity == other.Identity && Foreground == other.Foreground &&
        TypingContext.CanApply(Text, Start, End, other.Text, other.Start, other.End);
}

// A single long-lived MTA worker owns all UIA objects. A hung provider cannot spawn more workers.
internal sealed class FieldAccess : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new(1);
    private readonly Thread _worker;
    private bool _disposed;
    public FieldAccess()
    {
        _worker = new Thread(() => { foreach (var action in _queue.GetConsumingEnumerable()) action(); }) { IsBackground = true, Name = "TypePilot accessibility" };
        _worker.SetApartmentState(ApartmentState.MTA); _worker.Start();
    }
    private Task<T> OnWorker<T>(Func<T> work, T fallback)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (_disposed) return Task.FromResult(fallback);
        try
        {
            if (!_queue.TryAdd(() =>
            {
                try { completion.TrySetResult(work()); }
                catch (Exception ex) when (ex is COMException or ElementNotAvailableException or InvalidOperationException or ArgumentException or UnauthorizedAccessException or System.ComponentModel.Win32Exception or NotSupportedException)
                { completion.TrySetResult(fallback); }
            })) completion.TrySetResult(fallback);
        }
        catch (InvalidOperationException) { completion.TrySetResult(fallback); }
        return completion.Task;
    }
    public Task<FieldSnapshot?> ReadAsync(string[] allowed, bool includeOwn = false, IntPtr onlyForeground = default) => OnWorker(() => Read(allowed, includeOwn, onlyForeground), null);
    private static FieldSnapshot? Read(string[] allowed, bool includeOwn, IntPtr onlyForeground = default)
    {
        var foreground = NativeMethods.GetForegroundWindow();
        if (onlyForeground != IntPtr.Zero && foreground != onlyForeground) return null;
        var process = NativeMethods.ProcessName(foreground);
        if (!FieldPolicy.Allows(new(process, false, true, true, true), allowed)) return null;
        NativeMethods.GetWindowThreadProcessId(foreground, out var id);
        if (!includeOwn && id == Environment.ProcessId) return null;
        var native = NativeMethods.FocusedControl();
        var className = NativeMethods.ClassName(native);
        if (new[] { "Edit", "RichEdit20W", "RICHEDIT50W", "RichEditD2DPT" }.Contains(className, StringComparer.OrdinalIgnoreCase))
        {
            var style = NativeMethods.Style(native);
            if (style is null || !FieldPolicy.Allows(new(process, (style.Value & 0x20) != 0, (style.Value & 0x800) == 0 && (style.Value & 0x08000000) == 0, true, true), allowed)) return null;
            var text = NativeMethods.Text(native); var selection = NativeMethods.Selection(native);
            if (text is null || selection is null || selection.Value.Start < 0 || selection.Value.End < selection.Value.Start || selection.Value.End > text.Length) return null;
            return NativeMethods.GetForegroundWindow() == foreground ? new(foreground, native, process, $"native:{id}:{native}", text, selection.Value.Start, selection.Value.End, NativeMethods.Anchor(native)) : null;
        }
        var field = AutomationElement.FocusedElement;
        if (field is null || field.GetCurrentPropertyValue(AutomationElement.IsPasswordProperty, true) is not false) return null;
        var current = field.Current;
        var fieldProcess = Process.GetProcessById(current.ProcessId);
        using (fieldProcess)
            if (!string.Equals(fieldProcess.ProcessName, process, StringComparison.OrdinalIgnoreCase)) return null;
        if (!FieldPolicy.Allows(new(process, false, current.IsEnabled, current.HasKeyboardFocus, current.ControlType == ControlType.Edit || current.ControlType == ControlType.Document), allowed)) return null;
        if (!field.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return null;
        var pattern = (TextPattern)raw;
        var document = pattern.DocumentRange;
        if (document.GetAttributeValue(TextPattern.IsReadOnlyAttribute) is not false) return null;
        var full = document.GetText(20001);
        if (full.Length > 20000) return null;
        var ranges = pattern.GetSelection();
        if (ranges.Length != 1) return null;
        var before = document.Clone(); before.MoveEndpointByRange(TextPatternRangeEndpoint.End, ranges[0], TextPatternRangeEndpoint.Start);
        var start = before.GetText(20001).Length; var selected = ranges[0].GetText(20001);
        var end = start + selected.Length;
        if (end > full.Length || full[start..end] != selected || NativeMethods.GetForegroundWindow() != foreground) return null;
        var bounds = ranges[0].GetBoundingRectangles();
        var anchor = bounds.Length > 0 ? new Rect(bounds[0].Left, bounds[0].Bottom, 2, 20) : NativeMethods.Anchor(native);
        if (bounds.Length == 0 && !current.BoundingRectangle.IsEmpty) anchor = new(current.BoundingRectangle.Left, current.BoundingRectangle.Bottom, 2, 20);
        return new(foreground, IntPtr.Zero, process, "uia:" + string.Join('.', field.GetRuntimeId()), full, start, end, anchor, field);
    }
    public Task<bool> ReplaceAsync(FieldSnapshot expected, int start, int length, string replacement, int caret, string[] allowed, bool restoreFocus, CancellationToken token) => OnWorker(() =>
    {
        if (token.IsCancellationRequested || replacement.Length is < 1 or > 4000 || !NativeMethods.ModifiersReleased) return false;
        if (restoreFocus)
        {
            // Foreground activation is requested only after the user presses Apply.
            if (!NativeMethods.SetForegroundWindow(expected.Foreground)) return false;
            expected.Element?.SetFocus();
        }
        var fresh = Read(allowed, true, expected.Foreground);
        if (fresh is null || !expected.SameContent(fresh) || start < 0 || length < 0 || start + length > fresh.Text.Length || token.IsCancellationRequested) return false;
        if (fresh.Native != IntPtr.Zero)
        {
            if (!NativeMethods.Replace(fresh.Native, start, start + length, replacement)) return false;
            NativeMethods.Message(fresh.Native, NativeMethods.EmSetSel, new(caret), new(caret), out _);
            return true;
        }
        if (!fresh.Element!.TryGetCurrentPattern(TextPattern.Pattern, out var raw)) return false;
        var range = ((TextPattern)raw).DocumentRange.Clone();
        range.MoveEndpointByRange(TextPatternRangeEndpoint.End, range, TextPatternRangeEndpoint.Start);
        // Select only when the provider's character units map exactly to the captured text.
        if (range.MoveEndpointByUnit(TextPatternRangeEndpoint.End, TextUnit.Character, start + length) != start + length ||
            range.MoveEndpointByUnit(TextPatternRangeEndpoint.Start, TextUnit.Character, start) != start || range.GetText(4001) != fresh.Text.Substring(start, length)) return false;
        if (token.IsCancellationRequested || NativeMethods.GetForegroundWindow() != fresh.Foreground || !NativeMethods.ModifiersReleased) return false;
        range.Select();
        var selectedFresh = Read(allowed, true, expected.Foreground);
        if (selectedFresh is null || selectedFresh.Identity != fresh.Identity || selectedFresh.Text != fresh.Text || selectedFresh.Start != start || selectedFresh.End != start + length || token.IsCancellationRequested) return false;
        if (!NativeMethods.TypeUnicode(replacement)) return false;
        Thread.Sleep(60);
        var after = Read(allowed, true, expected.Foreground);
        var expectedText = fresh.Text[..start] + replacement + fresh.Text[(start + length)..];
        if (after is null || after.Identity != fresh.Identity || after.Text != expectedText || token.IsCancellationRequested) return true;
        if (caret != after.End && after.Element!.TryGetCurrentPattern(TextPattern.Pattern, out var afterRaw))
        {
            var caretRange = ((TextPattern)afterRaw).DocumentRange.Clone();
            caretRange.MoveEndpointByRange(TextPatternRangeEndpoint.End, caretRange, TextPatternRangeEndpoint.Start);
            if (caretRange.Move(TextUnit.Character, caret) == caret) caretRange.Select();
        }
        return true;
    }, false);
    public void Dispose() { _disposed = true; _queue.CompleteAdding(); }
}
