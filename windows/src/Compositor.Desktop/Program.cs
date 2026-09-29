using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.VisualTree;
using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Desktop;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // Draws the canvas control straight to a PNG, so the interface can be checked without a window.
        // `--grid` turns the layout grid on for the render, which is how that drawing is checked.
        if (args is ["--render", var project, var output]) return Render(project, output, showGrid: false);
        if (args is ["--render", var gridProject, var gridOutput, "--grid"]) return Render(gridProject, gridOutput, showGrid: true);
        // `--shape` draws the shape tool's drag preview, which needs a pointer to make.
        if (args is ["--render", var shapeProject, var shapeOutput, "--shape"])
        {
            return Render(shapeProject, shapeOutput, showGrid: false, shape: true);
        }
        // `--preview` opens a preview of the top layer inverted, so the canvas drawing one can be checked.
        if (args is ["--render", var previewProject, var previewOutput, "--preview"])
        {
            return Render(previewProject, previewOutput, showGrid: false, preview: true);
        }
        // `--pixel-grid` is the same render with the pixel grid asked for, and `--zoom-in` with the view taken
        // past 800% where the grid draws at all: the two together are how it is checked without a pointer.
        if (args is ["--render", var gridProject2, var gridOutput2, "--pixel-grid"])
        {
            return Render(gridProject2, gridOutput2, showGrid: false, pixelGrid: true);
        }
        if (args is ["--render", var zoomProject, var zoomOutput, "--zoom-in"])
        {
            return Render(zoomProject, zoomOutput, showGrid: false, zoomIn: true);
        }
        if (args is ["--render", var zoomGridProject, var zoomGridOutput, "--zoom-in", "--pixel-grid"])
        {
            return Render(zoomGridProject, zoomGridOutput, showGrid: false, zoomIn: true, pixelGrid: true);
        }
        // `--rulers` draws a ruler strip straight to a PNG, which is how its ticks are checked without a
        // pointer: the strip is measured and arranged the way the window does, at a known zoom.
        if (args is ["--rulers", var rulerOutput, var rulerScale, var rulerOrigin])
        {
            return Rulers(rulerOutput, double.Parse(rulerScale), double.Parse(rulerOrigin));
        }
        // `--window` builds the whole window and draws it, which is the only way to check the parts that are
        // not the canvas — the menus, the tab strip and the panel — without a display to click them on.
        if (args is ["--window", var windowOutput]) return Window(windowOutput);
        // `--tabs` drives the tab strip without a pointer: two projects into tabs, one brought back in front,
        // one closed, and the last closed as well. It draws the window afterwards so the strip can be looked at.
        if (args is ["--tabs", var tabsOutput]) return Tabs(tabsOutput);
        // `--updates` reads the app's real update feed and says what it makes of it, which is the whole check
        // short of the dialog: off the network it prints that the feed could not be reached instead.
        if (args is ["--updates"]) return Updates();
        if (args.Length >= 1 && args[0] == "--theme-probe") return ThemeProbe(args[1..]);
        // `--dialogs` draws a few dialog bodies under the real theme, which is the only way to look at their
        // colours: what a control's text and fill resolve to is the theme's business, not the dialog's.
        if (args is ["--dialogs", var dialogOutput]) return Dialogs(dialogOutput);
        Build().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    /// <summary>
    /// Prints the colours the Fluent theme resolves to: every key, or only those whose names hold one of the
    /// given words. The colours a control is drawn with are the theme's rather than the app's, so this is how
    /// they are read — it is what found the hundred keys that follow Fluent's own blue, which <see cref="Skin"/>
    /// moves onto the Mac's. What it says first is whether that took: no key should still hold Fluent's blue.
    /// </summary>
    private static int ThemeProbe(string[] names)
    {
        Build().SetupWithoutStarting();
        var theme = Application.Current!.Styles.OfType<FluentTheme>().First();
        if (theme.Resources is not ResourceDictionary resources) return 0;
        foreach (var variant in resources.ThemeDictionaries)
        {
            if (variant.Key != ThemeVariant.Dark || variant.Value is not IResourceDictionary dark) continue;
            var flaunted = 0;
            foreach (var key in dark.Keys)
            {
                dark.TryGetResource(key, null, out var held);
                if (HoldsFluentBlue(held)) flaunted++;
            }
            Console.WriteLine($"variant {Application.Current!.RequestedThemeVariant}; {dark.Count} resources, " +
                $"{flaunted} still in Fluent's own blue");
            foreach (var key in dark.Keys.OrderBy(k => k.ToString(), StringComparer.Ordinal))
            {
                var name = key.ToString()!;
                if (names.Length > 0 && !names.All(part => name.Contains(part, StringComparison.OrdinalIgnoreCase))) continue;
                dark.TryGetResource(key, null, out var value);
                Console.WriteLine($"  {name} = {value}");
            }
        }
        return 0;
    }

    /// <summary>Whether a resource still holds one of the three shades Fluent draws its accent in.</summary>
    private static bool HoldsFluentBlue(object? value)
    {
        var colour = value switch
        {
            Color one => one,
            ISolidColorBrush brush => brush.Color,
            _ => (Color?)null,
        };
        return colour is { } held && (held == Color.FromRgb(0x00, 0x78, 0xD4)
            || held == Color.FromRgb(0x23, 0xA0, 0xFF) || held == Color.FromRgb(0x00, 0x58, 0x9B));
    }

    /// <summary>
    /// A few dialogs' bodies drawn one under another under the real theme. A dialog's colours are mostly the
    /// theme's — the text, a field's fill, a slider's track and thumb, a tick box — so this is how they are
    /// looked at without a pointer: each is composed the way its window composes it, its own background behind
    /// its content, and the background it resolved to is printed beside it.
    /// </summary>
    private static int Dialogs(string output)
    {
        Build().SetupWithoutStarting();
        var folder = Path.GetDirectoryName(Path.GetFullPath(output))!;
        foreach (var (name, file, width, body) in new (string, string, double, Control)[]
                 {
                     ("New Project", "new-project.png", 400, new NewDocumentDialog().TakeBody()),
                     ("Grid Settings", "grid-settings.png", 380, new GridSettingsDialog(new LayoutGrid()).TakeBody()),
                     ("Controls", "controls.png", 420, Controls()),
                 })
        {
            Draw(name, Path.Combine(folder, file), width, body);
        }
        // The two panels whose body is a scroll view cannot be drawn this way: a control's template is applied
        // when it reaches a live window and a bitmap is not one, so a ScrollViewer has no presenter to lay its
        // content out in. What they hold is the same controls the sheet above shows.
        Console.WriteLine("the Brightness/Contrast and Camera Raw bodies are scroll views, which a bitmap "
            + "does not lay out: draw them on a screen to look at them");
        return 0;
    }

    /// <summary>
    /// The controls a dialog is made of, side by side under the app's own window: this is where a slider's
    /// track and thumb, a tick box's mark, a pop-up's field and a selected row show what the theme draws them.
    /// </summary>
    private static Control Controls()
    {
        var selected = new ListBoxItem { Content = "A selected row", IsSelected = true };
        return new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = "A label" },
                new TextBlock { Text = "A second line", Foreground = Skin.SecondaryBrush },
                new Slider { Minimum = 0, Maximum = 100, Value = 40 },
                new CheckBox { Content = "A tick box", IsChecked = true },
                new RadioButton { Content = "A radio button", IsChecked = true },
                new ComboBox
                {
                    ItemsSource = new[] { "A pop-up", "Another choice" },
                    SelectedIndex = 0,
                },
                new ListBox { ItemsSource = new object[] { "A row", selected }, Height = 72 },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 8,
                    Children = { new Button { Content = "Cancel" }, new Button { Content = "OK" } },
                },
            },
        };
    }

    /// <summary>One body drawn on a window of the app's own make, at the width its dialog asks for.</summary>
    private static void Draw(string name, string path, double width, Control body)
    {
        var holder = new CheckWindow { Width = width };
        var shown = new Border { Background = holder.Background, Child = body };
        holder.Content = shown;
        shown.Measure(new Size(width, double.PositiveInfinity));
        shown.Arrange(new Rect(0, 0, width, Math.Max(80, shown.DesiredSize.Height)));
        shown.UpdateLayout();
        var tall = (int)Math.Ceiling(shown.Bounds.Height);
        using var target = new RenderTargetBitmap(new PixelSize((int)width, tall));
        target.Render(shown);
        target.Save(path, new PngBitmapEncoderOptions());
        Console.WriteLine($"{name}: {body.GetType().Name} drawn {width}x{tall} on {holder.Background} to {path}");
    }

    /// <summary>A window of the app's own make, to hold a dialog's body while it is drawn.</summary>
    private sealed class CheckWindow : DialogWindow;

    /// <summary>
    /// Help ▸ Check for Updates without the dialog: the feed is read from where the Mac build reads it, parsed,
    /// and compared with this build's version.
    /// </summary>
    private static int Updates()
    {
        Build().SetupWithoutStarting();
        var running = UpdateDialog.Running;
        string? feed;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            feed = http.GetStringAsync(UpdateFeed.Address).GetAwaiter().GetResult();
        }
        catch (Exception error) when (error is HttpRequestException or TaskCanceledException or UriFormatException)
        {
            feed = null;
        }
        if (feed is null)
        {
            Console.WriteLine($"the update feed could not be reached; this build is {running}");
            return 0;
        }
        if (UpdateFeed.Newest(feed) is not { } release)
        {
            Console.WriteLine($"the update feed held no release; this build is {running}");
            return 0;
        }
        Console.WriteLine($"this build is {running}; the feed lists {release.Version} ({release.Title})" +
            (UpdateFeed.IsNewer(release, running) ? " — newer" : " — nothing to do") +
            $", {(release.Download is null ? "no download" : $"{release.Bytes} bytes")}" +
            (release.Page is { } page ? $", {page}" : ""));
        return 0;
    }

    /// <summary>
    /// The tab strip driven without a pointer. Two projects are written and opened into tabs, one is brought
    /// back in front, one is closed, the last is closed as well, and the window is drawn at the end, so what
    /// the strip looks like with an empty tab in it is in the PNG beside the report.
    /// </summary>
    private static int Tabs(string output)
    {
        Build().SetupWithoutStarting();
        var folder = Path.Combine(Path.GetTempPath(), "compositor-tabs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var first = Path.Combine(folder, "first.comp");
            var second = Path.Combine(folder, "second.comp");
            using (var document = Demo()) ProjectStore.Save(ProjectSnapshot.FromDocument(document), first);
            using (var document = LayerPlacement.NewDocument(100, 80))
            {
                ProjectStore.Save(ProjectSnapshot.FromDocument(document!), second);
            }
            var window = new MainWindow();
            Console.WriteLine(window.SelfCheck(first, second));
            var content = (Control)window.Content!;
            content.Measure(new Size(1280, 820));
            content.Arrange(new Rect(0, 0, 1280, 820));
            content.UpdateLayout();
            using var target = new RenderTargetBitmap(new PixelSize(1280, 820));
            target.Render(content);
            target.Save(output, new PngBitmapEncoderOptions());
            Console.WriteLine($"wrote {output}: the tab strip driven and drawn");
            return 0;
        }
        finally
        {
            try
            {
                if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The window itself, built and drawn to a PNG. It is a smoke test rather than a picture of anything: what
    /// it proves is that the window's construction runs — every menu row, the tab strip and the layer panel.
    /// </summary>
    private static int Window(string output)
    {
        Build().SetupWithoutStarting();
        var window = new MainWindow();
        // A window's own content is not laid out without a platform window, so what is measured, arranged and
        // drawn is that content — which is the whole interface: the menus, the tab strip and the panel.
        var content = (Control)window.Content!;
        content.Measure(new Size(1280, 820));
        content.Arrange(new Rect(0, 0, 1280, 820));
        content.UpdateLayout();
        using var target = new RenderTargetBitmap(new PixelSize(1280, 820));
        target.Render(content);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output}: the window built and drew");
        return 0;
    }

    /// <summary>
    /// A horizontal ruler from <paramref name="origin"/> along the document at <paramref name="scale"/> points
    /// to the pixel, drawn to a PNG. It is the same control the window puts along the top of the canvas.
    /// </summary>
    private static int Rulers(string output, double scale, double origin)
    {
        Build().SetupWithoutStarting();
        var strip = new RulerStrip { Axis = GuideAxis.Horizontal, Scale = scale, Origin = origin };
        strip.Measure(new Size(600, 18));
        strip.Arrange(new Rect(0, 0, 600, 18));
        using var target = new RenderTargetBitmap(new PixelSize(600, 18));
        target.Render(strip);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output} for a ruler at {scale} points to the pixel from {origin}, " +
            $"numbered every {RulerScale.MajorStep(scale)} pixels");
        return 0;
    }

    public static AppBuilder Build() =>
        AppBuilder.Configure<DesktopApp>().UsePlatformDetect().WithInterFont().LogToTrace();

    private static int Render(string project, string output, bool showGrid, bool preview = false, bool shape = false,
        bool zoomIn = false, bool pixelGrid = false)
    {
        Build().SetupWithoutStarting();
        using var document = project == "--demo" ? Demo() : ProjectStore.Load(project).ToDocument();

        // Walk the same path the Edit menu does, and report what the history made of it.
        var history = new DocumentHistory();
        var layer = document.Layers[^1];
        var before = layer.Transform.X;
        history.Begin("Flip Canvas Horizontal", document, layer.ID);
        LayerEdits.FlipCanvas(document, horizontally: true);
        history.End(document, layer.ID);
        var after = layer.Transform.X;
        var undone = history.Undo();
        var back = undone?.Document?.Layers[^1].Transform.X;
        var redone = history.Redo();
        var again = redone?.Document?.Layers[^1].Transform.X;
        Console.WriteLine($"edit: layer X {before} → {after} → undo {back} → redo {again}; " +
            $"modified {history.IsModified}, can undo {history.CanUndo}, can redo {history.CanRedo}");

        // Save the edited document the way the File menu does, and read it back.
        var saved = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(output))!, "saved.comp");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), saved);
        history.MarkSaved();
        using (var reloaded = ProjectStore.Load(saved).ToDocument())
        {
            Console.WriteLine($"saved and read back: layer X {reloaded.Layers[^1].Transform.X}, " +
                $"flipped {reloaded.Layers[^1].Transform.FlipX}, modified {history.IsModified}");
        }
        Directory.Delete(saved, recursive: true);

        var view = new CanvasView();
        view.Measure(new Size(640, 480));
        view.Arrange(new Rect(0, 0, 640, 480));
        view.Document = document;
        if (showGrid) view.Grid = new LayoutGrid();
        if (pixelGrid) view.PixelGrid = true;
        // Ten steps of a quarter is about 930%, which is past the 800% the pixel grid starts at.
        if (zoomIn)
        {
            for (var step = 0; step < 10; step++) view.ZoomBy(1.25);
        }
        if (shape)
        {
            // The shape tool's drag preview: an ellipse in a colour of its own, over a box of the canvas.
            var box = SKRectI.Create(80, 60, 240, 160);
            view.ShapeKind = Compositor.Core.Format.ShapeKind.Ellipse;
            view.ShapePreviewFor = dragged => (new Compositor.Core.Format.LayerShapeStyle
            {
                Kind = Compositor.Core.Format.ShapeKind.Ellipse, Red = 1, Green = 0.2, Blue = 0.1,
            }, dragged);
            view.PreviewShape(box);
            Console.WriteLine($"showing a shape preview over {box}");
        }
        // A preview of the top layer, as a filter panel would show one: the canvas draws it in the document's
        // place while the document is left as it was.
        FilterPreview? shown = null;
        if (preview && document.Layers.Count > 0)
        {
            var id = document.Layers[^1].ID;
            shown = FilterPreview.Begin(document, id);
            if (shown is not null)
            {
                shown.Show((target, layer) => FilterEdits.ApplyAdjustment(target, layer,
                    new LayerAdjustment { Kind = AdjustmentKind.Invert }));
                view.PreviewDocument = shown.Document;
            }
        }
        using var target = new RenderTargetBitmap(new PixelSize(640, 480));
        target.Render(view);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output} for {document.Width}x{document.Height} document at {view.Zoom * 100:0}%" +
            (shown is null ? "" : ", showing a preview"));
        // Stop drawing the preview before it is put away, as the window does.
        view.PreviewDocument = null;
        shown?.Dispose();
        return 0;
    }

    /// <summary>A small document for the self check: a backdrop, a masked patch with a stroke, and a folder.</summary>
    private static CanvasDocument Demo()
    {
        var document = new CanvasDocument(Guid.NewGuid(), 240, 160);
        document.Layers.Add(Solid(new SKColor(40, 70, 120), 0, 0, 240, 160));
        var mask = new SKBitmap(new SKImageInfo(1, 1, SKColorType.Gray8, SKAlphaType.Opaque));
        mask.Erase(new SKColor(210, 210, 210));
        var folder = Solid(SKColors.White, 0, 0, 1, 1, opacity: 0.6);
        folder.IsGroup = true;
        folder.Asset!.Dispose();
        folder.Asset = null;
        var patch = Solid(new SKColor(230, 90, 60), 40, 30, 120, 80);
        patch.Mask = Compositor.Core.Model.LayerMask.AssetFrom(mask);
        patch.Effects = new LayerEffects
        {
            Stroke = new StrokeEffect { Size = 3, Red = 1, Green = 1, Blue = 1, Opacity = 1 },
        };
        patch.ParentID = folder.ID;
        document.Layers.Add(folder);
        document.Layers.Add(patch);
        return document;
    }

    private static ImageLayer Solid(SKColor colour, double x, double y, int width, int height,
        double opacity = 1, LayerBlendMode blend = LayerBlendMode.Normal)
    {
        var bitmap = new SKBitmap(Bitmaps.ColorInfo(width, height));
        bitmap.Erase(colour);
        return new ImageLayer(Guid.NewGuid(), ImportedImage.Create(bitmap, "Layer"),
            new Compositor.Core.Model.LayerTransform(x, y, width, height, 0, false, false, LayerSampling.HighQuality), "Layer")
        {
            Opacity = opacity,
            BlendMode = blend,
        };
    }
}
