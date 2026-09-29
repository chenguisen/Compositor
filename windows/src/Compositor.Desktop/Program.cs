using Avalonia;
using Avalonia.Controls;
using Avalonia.Media.Imaging;
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
        Build().StartWithClassicDesktopLifetime(args);
        return 0;
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
