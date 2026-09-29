using Compositor.Core.Document;
using Compositor.Core.Format;
using Compositor.Core.IO;
using Compositor.Core.IO.PSD;
using Compositor.Core.Model;
using SkiaSharp;

namespace Compositor.Cli;

/// <summary>
/// Reads and writes `.comp` packages without the editor: what a project holds, what it looks like
/// flattened, and whether reading and writing it back leaves it alone.
/// </summary>
internal static class Program
{
    private static int Main(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help")
        {
            Usage();
            return args.Length == 0 ? 2 : 0;
        }
        try
        {
            return args[0] switch
            {
                "info" => Info(args),
                "render" => Render(args),
                "save" => Save(args),
                "import" => Import(args),
                "crop" => Crop(args),
                "canvas" => Canvas(args),
                "resize" => Resize(args),
                "trim" => Trim(args),
                "merge" => Merge(args),
                "type" => Type(args),
                "shape" => Shape(args),
                "gradient" => Gradient(args),
                _ => Fail($"'{args[0]}' is not a command. Try --help."),
            };
        }
        catch (Exception error)
        {
            return Fail(error.Message);
        }
    }

    private static void Usage() => Console.WriteLine(
        """
        compositor — read and write Compositor projects

          info   <project.comp>               what the project holds
          render <project.comp> <out.png>     flatten it to a PNG
          save   <project.comp> <out.comp>    read it and write it back
          import <image> <out.comp>           start a project from one image
          crop   <in> <out> x y w h           crop the canvas to a rectangle
          canvas <in> <out> w h [anchor]      resize the canvas, moving content
          resize <in> <out> w h [dpi]         resample the image and every layer
          trim   <in> <out> [tolerance]       crop the canvas to what is drawn on it
          merge  <in> <out> <layer>           merge a layer into what lies beneath it
          type   <in> <out> <text> [size]     add a text layer
          shape  <in> <out> rectangle|ellipse|line [size]   add a shape layer
          gradient <in> <out> <layer> [linear|radial]       fill a layer with a gradient
        """);

    private static int Info(string[] args)
    {
        if (args.Length != 2) return Fail("info needs a project path.");
        using var snapshot = ProjectStore.Load(args[1]);
        var manifest = snapshot.Manifest;
        Console.WriteLine($"{args[1]}");
        Console.WriteLine($"  format v{manifest.Version}, {manifest.ColorSpace}, {manifest.Width}x{manifest.Height}, " +
            $"{manifest.Resolution ?? 72} pixels/inch");
        Console.WriteLine($"  {manifest.Layers.Count} layers, {snapshot.Images.Count} images, {snapshot.Masks.Count} masks" +
            (manifest.Guides is { Count: > 0 } guides ? $", {guides.Count} guides" : ""));
        using var document = snapshot.ToDocument();
        foreach (var entry in document.HierarchyEntries())
        {
            var layer = entry.Layer;
            var indent = new string(' ', entry.Depth * 2);
            var flags = new List<string>();
            if (!entry.Visible) flags.Add("hidden");
            if (layer.BlendMode is { } blend && blend != LayerBlendMode.Normal) flags.Add(Spell(blend));
            if (layer.Opacity is { } opacity and < 1) flags.Add($"{opacity:0.##} opacity");
            if (layer.MaskFile is not null) flags.Add(layer.MaskEnabled == false ? "mask off" : "mask");
            if (layer.MaskSourceID is not null) flags.Add("clipped");
            if (layer.MaskPlacement is not null) flags.Add("mask moved");
            var suffix = flags.Count > 0 ? "  [" + string.Join(", ", flags) + "]" : "";
            Console.WriteLine($"    {indent}{layer.Name}  {Kind(layer, snapshot)}{suffix}");
        }
        return 0;
    }

    /// <summary>What the layer is, and how big its pixels are. Adjustments have no image of their own.</summary>
    private static string Kind(ProjectLayerRecord layer, ProjectSnapshot snapshot) => layer switch
    {
        { IsGroup: true } => "folder",
        { Adjustment: { } adjustment } => Spell(adjustment.Kind),
        { Text: not null } => "text",
        { Shape: not null } => "shape",
        _ when layer.ImageFile is not null && snapshot.Images.TryGetValue(layer.ID, out var image) => $"{image.Width}x{image.Height}",
        _ => "empty",
    };

    /// <summary>The name the format spells an enum with, rather than the C# member's name.</summary>
    private static string Spell<T>(T value) where T : struct, Enum =>
        System.Text.Json.JsonSerializer.Serialize(value, ManifestJson.Options).Trim('"');

    private static int Render(string[] args)
    {
        if (args.Length != 3) return Fail("render needs a project path and an output path.");
        if (!ImageWriter.Knows(args[2])) return Fail("The output has to end in .png, .jpg or .jpeg.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        // PNG is written a band of tiles at a time, so any canvas size works; JPEG has to be made whole.
        if (!ImageWriter.Write(document, args[2]))
        {
            return Fail("That canvas is too big to write as one picture; use PNG.");
        }
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    private static int Save(string[] args)
    {
        if (args.Length != 3) return Fail("save needs a project path and an output path.");
        using var snapshot = ProjectStore.Load(args[1]);
        ProjectStore.Save(snapshot, args[2]);
        Console.WriteLine($"wrote {args[2]}");
        return 0;
    }

    private static int Import(string[] args)
    {
        if (args.Length != 3) return Fail("import needs an image path and an output path.");
        var name = Path.GetFileName(args[1]);
        var extension = Path.GetExtension(args[1]);
        if (extension.Equals(".psd", StringComparison.OrdinalIgnoreCase)
            || extension.Equals(".psb", StringComparison.OrdinalIgnoreCase))
        {
            var photoshop = PsdImporter.Read(args[1]);
            ProjectStore.Save(photoshop.Snapshot(), args[2]);
            Console.WriteLine($"wrote {args[2]} ({photoshop.Manifest.Width}x{photoshop.Manifest.Height}, " +
                $"{photoshop.Manifest.Layers.Count} layers from {name})");
            foreach (var note in photoshop.Notes) Console.WriteLine($"  kept as pixels: {note.Layer} — {note.What}");
            return 0;
        }
        var image = ImageImporter.Decode(args[1]);
        using var document = ImageImporter.NewDocument(image);
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height} from {name})");
        return 0;
    }

    private static int Crop(string[] args)
    {
        if (args.Length != 7) return Fail("crop needs an input, an output and x y width height.");
        if (!int.TryParse(args[3], out var x) || !int.TryParse(args[4], out var y)
            || !int.TryParse(args[5], out var width) || !int.TryParse(args[6], out var height))
        {
            return Fail("crop's x, y, width and height must be whole numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!CanvasEdits.Crop(document, SKRectI.Create(x, y, width, height))) return Fail("That crop leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    private static int Canvas(string[] args)
    {
        if (args.Length is not (5 or 6)) return Fail("canvas needs an input, an output, width and height, and an anchor if you want one.");
        if (!int.TryParse(args[3], out var width) || !int.TryParse(args[4], out var height))
        {
            return Fail("canvas's width and height must be whole numbers.");
        }
        var anchor = CanvasEdits.CentreAnchor;
        if (args.Length == 6 && !int.TryParse(args[5], out anchor)) return Fail("canvas's anchor must be 0 to 8.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!CanvasEdits.Resize(document, width, height, anchor)) return Fail("That canvas size leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height}, anchor {anchor})");
        return 0;
    }

    private static int Resize(string[] args)
    {
        if (args.Length is not (5 or 6)) return Fail("resize needs an input, an output and width and height, and a resolution if you want one.");
        if (!int.TryParse(args[3], out var width) || !int.TryParse(args[4], out var height))
        {
            return Fail("resize's width and height must be whole numbers.");
        }
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var resolution = document.Resolution;
        if (args.Length == 6 && !double.TryParse(args[5], out resolution)) return Fail("resize's resolution must be a number.");
        if (!ImageEdits.Resize(document, width, height, resolution)) return Fail("That image size leaves the document as it was.");
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height} at {document.Resolution:0} pixels per inch)");
        return 0;
    }

    private static int Trim(string[] args)
    {
        if (args.Length is not (3 or 4)) return Fail("trim needs an input and an output, and a tolerance if you want one.");
        byte tolerance = 0;
        if (args.Length == 4 && !byte.TryParse(args[3], out tolerance)) return Fail("trim's tolerance must be 0 to 255.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        if (!TrimEdits.Trim(document, new TrimOptions(Tolerance: tolerance)))
        {
            return Fail("There was nothing to trim: the canvas is already as tight as it goes, or is empty.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({document.Width}x{document.Height})");
        return 0;
    }

    /// <summary>The layer is named rather than numbered, because a number would move under the merge.</summary>
    private static int Merge(string[] args)
    {
        if (args.Length != 4) return Fail("merge needs an input, an output and the name of the layer to merge.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers
            .Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var id = matches[0].ID;
        if (LayerMerge.Plan(document, [id], id) is not { } plan) return Fail($"'{args[3]}' has nothing to merge with.");
        if (LayerMerge.Merge(document, [id], id) is not { } made)
        {
            return Fail("The merge was refused: that canvas is too big to composite at once, or the result would not load back.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var merged = document.Layers.First(layer => layer.ID == made);
        Console.WriteLine($"wrote {args[2]} ({plan.Action}: '{merged.Name}' is now " +
            $"{merged.Asset!.Width}x{merged.Asset.Height} at {merged.Transform.X:0.##},{merged.Transform.Y:0.##}, " +
            $"{document.Layers.Count} layers left)");
        return 0;
    }

    /// <summary>Sets a string of text as a new layer, drawn from the style the way the Type tool does.</summary>
    private static int Type(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("type needs a project, an output, the text, and a size if you want one.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var style = new LayerTextStyle
        {
            Content = args[3],
            FontName = "Arial",
            FontSize = 72,
            Red = 0,
            Green = 0,
            Blue = 0,
        };
        if (args.Length == 5)
        {
            if (!double.TryParse(args[4], out var size)) return Fail("type's size must be a number.");
            style.FontSize = size;
        }
        if (!style.IsValid) return Fail("That text or size is not one a text layer may hold.");
        var origin = new SKPoint((float)(document.Width / 4.0), (float)(document.Height / 4.0));
        if (TextEdits.Add(document, style, origin) is not { } id)
        {
            return Fail("That text could not be drawn: its box is too big for one surface, or the project holds too many layers.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var layer = document.Layers.First(candidate => candidate.ID == id);
        Console.WriteLine($"wrote {args[2]} (text '{layer.Name}' {layer.Asset!.Width}x{layer.Asset.Height} " +
            $"at {origin.X:0},{origin.Y:0}, {document.Layers.Count} layers)");
        return 0;
    }

    /// <summary>Adds a shape layer, drawn the way the Shape tool draws one.</summary>
    private static int Shape(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("shape needs a project, an output, a shape name, and a size if you want one.");
        var kind = args[3].ToLowerInvariant() switch
        {
            "rectangle" => ShapeKind.Rectangle,
            "ellipse" => ShapeKind.Ellipse,
            "line" => ShapeKind.Line,
            _ => (ShapeKind?)null,
        };
        if (kind is not { } shape) return Fail($"'{args[3]}' is not a shape; try rectangle, ellipse or line.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var style = new LayerShapeStyle { Kind = shape, Red = 0.9, Green = 0.2, Blue = 0.1, CornerRadius = 8 };
        var side = 160;
        if (args.Length == 5 && !int.TryParse(args[4], out side)) return Fail("shape's size must be a whole number.");
        var box = SKRectI.Create(document.Width / 4, document.Height / 4, side, (int)(side * 0.6));
        if (shape == ShapeKind.Line)
        {
            style.LineWidth = 6;
            style.Start = new JsonPoint(0, 0);
            style.End = new JsonPoint(1, 1);
        }
        if (ShapeEdits.Add(document, style, box, null) is not { } id)
        {
            return Fail("That shape could not be drawn: its box is too big for one surface, or the project holds too many layers.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        var layer = document.Layers.First(candidate => candidate.ID == id);
        Console.WriteLine($"wrote {args[2]} ({shape} '{layer.Name}' {layer.Asset!.Width}x{layer.Asset.Height} " +
            $"at {box.Left},{box.Top}, {document.Layers.Count} layers)");
        return 0;
    }

    /// <summary>Fills a layer's pixels with a gradient, the way the Gradient tool does.</summary>
    private static int Gradient(string[] args)
    {
        if (args.Length is not (4 or 5)) return Fail("gradient needs a project, an output, the layer to fill, and a shape if you want one.");
        var shape = args.Length == 5
            ? args[4].ToLowerInvariant() switch
            {
                "linear" => GradientShape.Linear,
                "radial" => GradientShape.Radial,
                _ => (GradientShape?)null,
            }
            : GradientShape.Linear;
        if (shape is not { } fill) return Fail($"'{args[4]}' is not a gradient shape; try linear or radial.");
        using var snapshot = ProjectStore.Load(args[1]);
        using var document = snapshot.ToDocument();
        var matches = document.Layers.Where(layer => string.Equals(layer.Name, args[3], StringComparison.OrdinalIgnoreCase)).ToList();
        if (matches.Count == 0) return Fail($"No layer in that project is called '{args[3]}'.");
        if (matches.Count > 1) return Fail($"{matches.Count} layers are called '{args[3]}'; rename one of them first.");
        var id = matches[0].ID;
        var start = new SKPoint(0, document.Height / 2f);
        var end = new SKPoint(document.Width, document.Height / 2f);
        var from = new SKColor(255, 255, 255, 255);
        if (!GradientEdits.Fill(document, id, mask: false, start, end, from, new SKColor(255, 255, 255, 0), 1, fill))
        {
            return Fail("That layer holds no pixels to fill, or the drag was too short.");
        }
        ProjectStore.Save(ProjectSnapshot.FromDocument(document), args[2]);
        Console.WriteLine($"wrote {args[2]} ({fill} gradient over '{matches[0].Name}', from {start.X},{start.Y} to {end.X},{end.Y})");
        return 0;
    }

    private static int Fail(string message)    {
        Console.Error.WriteLine($"compositor: {message}");
        return 1;
    }
}
