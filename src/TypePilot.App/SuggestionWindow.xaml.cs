using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace TypePilot.App;

public partial class SuggestionWindow : Window
{
    private readonly Action<int> _accept;
    private readonly Action _dismiss;
    internal SuggestionWindow(Action<int> accept, Action dismiss)
    {
        _accept = accept; _dismiss = dismiss; InitializeComponent();
        SourceInitialized += (_, _) =>
        {
            var source = (HwndSource)PresentationSource.FromVisual(this);
            SetWindowLongPtr(source.Handle, -20, new(GetWindowLongPtr(source.Handle, -20).ToInt64() | 0x08000000 | 0x80));
            source.AddHook((IntPtr h, int message, IntPtr w, IntPtr l, ref bool handled) =>
            { if (message == 0x21) { handled = true; return new(3); } return IntPtr.Zero; });
        };
    }
    internal void Present(SuggestionOffer offer)
    {
        Choices.Children.Clear();
        for (var i = 0; i < offer.Items.Count; i++)
        {
            var index = i;
            var button = new Button { Content = new TextBlock { Text = offer.Items[i].Word, MaxWidth = 156, TextWrapping = TextWrapping.NoWrap, TextTrimming = TextTrimming.CharacterEllipsis }, ToolTip = offer.Items[i].Word + " · " + offer.Items[i].Reason + $" · Ctrl+Alt+{i + 1}", Margin = new(0, 0, i < offer.Items.Count - 1 ? 8 : 0, 0), Padding = new(12, 8, 12, 8), Focusable = false };
            button.Click += (_, _) => _accept(index); Choices.Children.Add(button);
        }
        Show(); UpdateLayout(); FloatingPlacement.Near(this, offer.Field.Anchor);
    }
    private void DismissClick(object sender, RoutedEventArgs e) => _dismiss();
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr value);
}
