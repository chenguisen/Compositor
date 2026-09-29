using System.Reflection;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Compositor.Core.IO;

namespace Compositor.Desktop;

/// <summary>
/// Help ▸ Check for Updates: reads the app's update feed and says whether there is anything newer.
/// <para>
/// The feed is the one the Mac build publishes, which points at a Mac download, so what this can honestly offer
/// is the news — a new version exists, and where to read what changed — rather than an installer for this
/// machine. Making the Windows build from this repository is what actually updates it.
/// </para>
/// </summary>
internal sealed class UpdateDialog : Window
{
    private static readonly IBrush Ink = new SolidColorBrush(Color.FromRgb(0xE6, 0xE8, 0xEB));

    private UpdateDialog(string title, string message, string? page)
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var close = new Button { Content = "Close", IsCancel = true, IsDefault = true };
        close.Click += (_, _) => Close();
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            HorizontalAlignment = HorizontalAlignment.Right,
        };
        if (page is not null)
        {
            var open = new Button { Content = "What changed…" };
            open.Click += (_, _) => Open(page);
            buttons.Children.Add(open);
        }
        buttons.Children.Add(close);
        Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap, Foreground = Ink },
                buttons,
            },
        };
        Opened += (_, _) => close.Focus();
    }

    /// <summary>The release page opened in whatever the machine uses for links, and nothing when it cannot be.</summary>
    private static void Open(string page)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(page) { UseShellExecute = true });
        }
        catch (Exception error) when (error is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            // A machine with no browser set up is not worth failing the dialog over: the address is in the box.
        }
    }

    public static async Task Ask(Window owner, string title, string message, string? page = null)
    {
        await new UpdateDialog(title, message, page).ShowDialog(owner);
    }

    /// <summary>The version this build is: the one in the project file, read back off the assembly.</summary>
    public static AppVersion Running
    {
        get
        {
            var version = Assembly.GetExecutingAssembly().GetName().Version;
            return version is null ? new AppVersion(0, 0, 0, "") : new AppVersion(version.Major, version.Minor, version.Build, "");
        }
    }
}
