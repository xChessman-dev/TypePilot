using System.Runtime.InteropServices;
using TypePilot.Core;

namespace TypePilot.App;

internal static class NativeEditCheck
{
    // Contract check on hidden controls owned by this process. No input is injected into user apps.
    public static void Run()
    {
        var edit = CreateWindowEx(0, "Edit", "превет ", 4, 0, 0, 200, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var password = CreateWindowEx(0, "Edit", "", 0x20, 0, 0, 200, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        var readOnly = CreateWindowEx(0, "Edit", "", 0x800, 0, 0, 200, 60, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero);
        try
        {
            if (edit == IntPtr.Zero || password == IntPtr.Zero || readOnly == IntPtr.Zero) throw new InvalidOperationException("Cannot create native test controls.");
            if (NativeMethods.ClassName(edit) != "Edit" || NativeMethods.Text(edit) != "превет ") throw new InvalidOperationException("Native edit text contract failed.");
            NativeMethods.Message(edit, NativeMethods.EmSetSel, new(7), new(7), out _);
            if (NativeMethods.Selection(edit) != (7, 7)) throw new InvalidOperationException("Native caret contract failed.");
            var change = DefaultEngine.Create().CorrectAtBoundary(NativeMethods.Text(edit)!, 7)!;
            if (!NativeMethods.Replace(edit, change.Start, change.Start + change.Original.Length, change.Replacement) || NativeMethods.Text(edit) != "привет ") throw new InvalidOperationException("Native replacement contract failed.");
            if (!NativeMethods.Replace(edit, 0, 6, "превет") || NativeMethods.Text(edit) != "превет ") throw new InvalidOperationException("Native restoration contract failed.");
            if ((NativeMethods.Style(password) & 0x20) != 0x20 || (NativeMethods.Style(readOnly) & 0x800) != 0x800) throw new InvalidOperationException("Native protected-field metadata failed.");
        }
        finally { foreach (var control in new[] { edit, password, readOnly }) if (control != IntPtr.Zero) DestroyWindow(control); }
    }
    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr CreateWindowEx(uint exStyle, string className, string title, uint style, int x, int y, int width, int height, IntPtr parent, IntPtr menu, IntPtr instance, IntPtr parameter);
    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool DestroyWindow(IntPtr window);
}
