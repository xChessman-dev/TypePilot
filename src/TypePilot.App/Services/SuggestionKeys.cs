using System.Runtime.InteropServices;
using System.Windows.Threading;
using TypePilot.Core;

namespace TypePilot.App;

// Installed only while a verified suggestion is visible. No text keys are recorded.
internal sealed class SuggestionKeys : IDisposable
{
    private readonly Dispatcher _dispatcher;
    private readonly Action _tab, _dismiss, _cancel;
    private readonly HookProc _callback;
    private readonly NativeMethods.WinEventProc _focusCallback;
    private IntPtr _hook, _focusHook, _foregroundHook, _foreground, _focus;
    private readonly SuggestionKeyPolicy _policy = new();
    internal SuggestionKeys(Dispatcher dispatcher, Action tab, Action dismiss, Action cancel)
    {
        _dispatcher = dispatcher; _tab = tab; _dismiss = dismiss; _cancel = cancel;
        _callback = Keyboard;
        _focusCallback = (_, _, _, _, _, _, _) =>
        {
            // Focus events revoke the gate immediately, before the next polling interval.
            _foreground = IntPtr.Zero;
            _dispatcher.BeginInvoke(_dismiss);
        };
    }
    internal bool Start(FieldSnapshot field)
    {
        Stop();
        if (NativeMethods.GetForegroundWindow() != field.Foreground) return false;
        _foreground = field.Foreground;
        _focus = NativeMethods.FocusedControl();
        if (_focus == IntPtr.Zero) { Stop(); return false; }
        _hook = SetWindowsHookEx(13, _callback, GetModuleHandle(null), 0);
        if (_hook == IntPtr.Zero) { Stop(); return false; }
        _focusHook = NativeMethods.SetWinEventHook(0x8005, 0x8005, IntPtr.Zero, _focusCallback, 0, 0, 2);
        _foregroundHook = NativeMethods.SetWinEventHook(3, 3, IntPtr.Zero, _focusCallback, 0, 0, 2);
        if (_focusHook == IntPtr.Zero || _foregroundHook == IntPtr.Zero) { Stop(); return false; }
        return true;
    }
    private IntPtr Keyboard(int code, IntPtr w, IntPtr l)
    {
        if (code < 0) return CallNextHookEx(_hook, code, w, l);
        var input = Marshal.PtrToStructure<KeyboardData>(l);
        var down = w.ToInt32() is 0x100 or 0x104;
        var up = w.ToInt32() is 0x101 or 0x105;
        var target = _foreground != IntPtr.Zero && NativeMethods.GetForegroundWindow() == _foreground && NativeMethods.FocusedControl() == _focus;
        var decision = _policy.Route(input.Key, down, up, (input.Flags & 0x10) != 0, target, NativeMethods.ModifiersReleased);
        switch (decision.Action)
        {
            case SuggestionKeyAction.Next: _dispatcher.BeginInvoke(_tab); break;
            case SuggestionKeyAction.Dismiss: _dispatcher.BeginInvoke(_dismiss); break;
            case SuggestionKeyAction.CancelPending: _dispatcher.BeginInvoke(_cancel); break;
        }
        if (decision.Consume) return new(1);
        return CallNextHookEx(_hook, code, w, l);
    }
    internal void Stop()
    {
        _foreground = IntPtr.Zero;
        if (_hook != IntPtr.Zero) UnhookWindowsHookEx(_hook);
        if (_focusHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_focusHook);
        if (_foregroundHook != IntPtr.Zero) NativeMethods.UnhookWinEvent(_foregroundHook);
        _hook = _focusHook = _foregroundHook = IntPtr.Zero;
        _policy.Reset();
    }
    public void Dispose() => Stop();
    [StructLayout(LayoutKind.Sequential)] private struct KeyboardData { public uint Key, Scan, Flags, Time; public UIntPtr Extra; }
    private delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int id, HookProc callback, IntPtr module, uint thread);
    [DllImport("user32.dll")] private static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool UnhookWindowsHookEx(IntPtr hook);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string? name);
}
