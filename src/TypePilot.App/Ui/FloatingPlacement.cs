using System.Windows;
using System.Windows.Interop;

namespace TypePilot.App;

internal static class FloatingPlacement
{
    public static void Near(Window window, Rect anchor)
    {
        var source = PresentationSource.FromVisual(window);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? System.Windows.Media.Matrix.Identity;
        var screen = System.Windows.Forms.Screen.FromPoint(new((int)anchor.X, (int)anchor.Y));
        var bounds = screen.WorkingArea;
        var topLeft = toDip.Transform(new Point(bounds.Left, bounds.Top));
        var bottomRight = toDip.Transform(new Point(bounds.Right, bounds.Bottom));
        var point = toDip.Transform(new Point(anchor.X, anchor.Y));
        var width = window.ActualWidth > 0 ? window.ActualWidth : window.Width;
        var height = window.ActualHeight > 0 ? window.ActualHeight : window.Height;
        window.Left = Math.Clamp(point.X, topLeft.X + 8, Math.Max(topLeft.X + 8, bottomRight.X - width - 8));
        window.Top = point.Y + height + 12 <= bottomRight.Y ? point.Y + 8 : Math.Max(topLeft.Y + 8, point.Y - height - 24);
    }
}
