using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;

namespace TypePilot.App;

internal static class NativeMethods
{
    internal const uint EmGetSel = 0xB0, EmSetSel = 0xB1, EmReplaceSel = 0xC2;
    internal static readonly uint ShowMessage = RegisterWindowMessage("TypePilot.Show.v1");
    internal static void BroadcastShow() => PostMessage(new IntPtr(0xFFFF), ShowMessage, IntPtr.Zero, IntPtr.Zero);
    internal static string ProcessName(IntPtr window)
    {
        GetWindowThreadProcessId(window, out var id);
        try { using var process = Process.GetProcessById((int)id); return process.ProcessName; }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception) { return ""; }
    }
    internal static IntPtr FocusedControl()
    {
        var window = GetForegroundWindow();
        var thread = GetWindowThreadProcessId(window, out _);
        var info = new GuiThreadInfo { Size = (uint)Marshal.SizeOf<GuiThreadInfo>() };
        return GetGUIThreadInfo(thread, ref info) ? info.Focus : IntPtr.Zero;
    }
    internal static string ClassName(IntPtr window)
    { var text = new StringBuilder(128); return GetClassName(window, text, text.Capacity) > 0 ? text.ToString() : ""; }
    internal static bool Message(IntPtr window, uint message, IntPtr w, IntPtr l, out UIntPtr result) => SendMessageTimeout(window, message, w, l, 2, 100, out result) != IntPtr.Zero;
    internal static string? Text(IntPtr window)
    {
        if (!Message(window, 0xE, IntPtr.Zero, IntPtr.Zero, out var length) || length.ToUInt64() > 20000) return null;
        var size = (int)length.ToUInt64() + 1;
        var buffer = Marshal.AllocHGlobal(size * 2);
        try
        {
            if (!Message(window, 0xD, new(size), buffer, out _)) return null;
            return Marshal.PtrToStringUni(buffer);
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static (int Start, int End)? Selection(IntPtr window)
    {
        var p = Marshal.AllocHGlobal(8);
        try
        {
            if (!Message(window, EmGetSel, p, p + 4, out _)) return null;
            return (Marshal.ReadInt32(p), Marshal.ReadInt32(p, 4));
        }
        finally { Marshal.FreeHGlobal(p); }
    }
    internal static bool Replace(IntPtr window, int start, int end, string text)
    {
        if (!Message(window, EmSetSel, new(start), new(end), out _)) return false;
        var buffer = Marshal.StringToHGlobalUni(text);
        try { return Message(window, EmReplaceSel, new(1), buffer, out _); }
        finally { Marshal.FreeHGlobal(buffer); }
    }
    internal static int? Style(IntPtr window)
    {
        Marshal.SetLastPInvokeError(0);
        var style = GetWindowLong(window, -16);
        return style == 0 && Marshal.GetLastPInvokeError() != 0 ? null : style;
    }
    [StructLayout(LayoutKind.Sequential)] private struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential)] private struct GuiThreadInfo
    { public uint Size, Flags; public IntPtr Active, Focus, Capture, MenuOwner, MoveSize, Caret; public Rect CaretRect; }
    [DllImport("user32.dll")] internal static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] internal static extern uint GetWindowThreadProcessId(IntPtr window, out uint process);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetGUIThreadInfo(uint thread, ref GuiThreadInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr window, StringBuilder text, int max);
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern int GetWindowLong(IntPtr window, int index);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out UIntPtr result);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool PostMessage(IntPtr window, uint message, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool RegisterHotKey(IntPtr window, int id, uint modifiers, uint key);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnregisterHotKey(IntPtr window, int id);
    internal delegate void WinEventProc(IntPtr hook, uint eventType, IntPtr window, int objectId, int childId, uint thread, uint time);
    [DllImport("user32.dll")] internal static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr module, WinEventProc callback, uint process, uint thread, uint flags);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] internal static extern bool UnhookWinEvent(IntPtr hook);
}
