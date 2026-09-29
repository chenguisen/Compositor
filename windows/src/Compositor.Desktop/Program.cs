using Avalonia;
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
        Build().StartWithClassicDesktopLifetime(args);
        return 0;
    }

    public static AppBuilder Build() =>
        AppBuilder.Configure<DesktopApp>().UsePlatformDetect().WithInterFont().LogToTrace();

    private static int Render(string project, string output, bool showGrid)
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
        using var target = new RenderTargetBitmap(new PixelSize(640, 480));
        target.Render(view);
        target.Save(output, new PngBitmapEncoderOptions());
        Console.WriteLine($"wrote {output} for {document.Width}x{document.Height} document at {view.Zoom * 100:0}%");
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
