using Avalonia.Controls;
using Avalonia.Controls.Documents;

namespace Compositor.Desktop;

/// <summary>
/// What every dialog of this app is: a window on the editor's own chrome. Fluent draws a window's background
/// itself — white in its light palette, black in its dark one — and the Mac has one appearance and paints its
/// dialogs the same 0.14 grey as the editor behind them (ContentView's <c>Color(white: 0.14)</c>), so they all
/// start from here rather than each remembering to say so.
/// </summary>
internal abstract class DialogWindow : Window
{
    protected DialogWindow()
    {
        Background = Skin.ChromeBrush;
        // A plain TextBlock has no foreground of its own, so on a dark dialog it would draw black on near
        // black. Setting the inherited one leaves every control that says what its own text is — a button, a
        // field, a menu row — exactly as the theme drew it.
        TextElement.SetForeground(this, Skin.LabelBrush);
    }
}
