using System.Text;
using System.Text.Json;
using Compositor.Core.Format;
using Compositor.Core.IO;
using SkiaSharp;

namespace Compositor.Core.Tests;

/// <summary>Saving and loading a whole package, and what a save leaves behind.</summary>
public class ProjectRoundTripTests : ProjectTestBase
{
    [Fact]
    public void AWholeProjectSurvivesARoundTrip()
    {
        using var original = KitchenSink();
        var before = Serialized(original.Manifest);
        var package = PathIn("Kitchen.comp");
        ProjectStore.Save(original, package);

        // The manifest on disk is exactly what the writer produces, and saving is stable.
        Assert.Equal(before, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName))));

        using var loaded = ProjectStore.Load(package);
        Assert.Equal(before, Serialized(loaded.Manifest));
        Assert.Equal(before, Encoding.UTF8.GetString(File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName))));

        // Nothing was dropped: every record, in order, with every field.
        Assert.Equal(original.Manifest.Layers.Select(layer => layer.ID), loaded.Manifest.Layers.Select(layer => layer.ID));
        Assert.Equal(original.Manifest.Layers.Count, loaded.Manifest.Layers.Count);
        Assert.Equal(original.Manifest.DocumentID, loaded.Manifest.DocumentID);
        Assert.Equal(640, loaded.Manifest.Width);
        Assert.Equal(480, loaded.Manifest.Height);
        Assert.Equal(300, loaded.Manifest.Resolution);
        Assert.Equal(ChildID, loaded.Manifest.ActiveLayerID);

        var background = loaded.Manifest.Layers[0];
        Assert.Equal(LayerBlendMode.Multiply, background.BlendMode);
        Assert.Equal(0.9, background.Opacity);
        Assert.Equal(8, background.Effects!.Stroke!.Size);
        Assert.False(background.Effects.Stroke.Enabled);
        Assert.False(background.Effects.Stroke.IsEnabled);
        Assert.Null(background.Effects.Shadow!.Enabled);
        Assert.Equal(45, background.Effects.Shadow.Angle);
        Assert.Equal(0.65, background.Effects.ColorOverlay!.Opacity);
        Assert.Equal(135, background.Effects.InnerShadow!.Angle);
        Assert.Equal(0.5, background.Effects.OuterGlow!.Blue);
        Assert.Equal(0.4, background.Effects.InnerGlow!.Opacity);

        var clipped = loaded.Manifest.Layers[1];
        Assert.Equal("Paint & sky 🌤", clipped.Name);
        Assert.Equal(LayerBlendMode.ColorDodge, clipped.BlendMode);
        Assert.False(clipped.MaskEnabled);
        Assert.False(clipped.MaskLinked);
        Assert.NotNull(clipped.MaskPlacement);
        Assert.Equal(10.5, clipped.MaskPlacement!.Origin.X);
        Assert.Equal(BackgroundID, clipped.MaskSourceID);
        Assert.Equal("Hello 🌤 world", clipped.Text!.Content);
        Assert.Equal(TextAlignment.Center, clipped.Text.Alignment);
        Assert.Equal(new JsonSize(200, 100), clipped.Text.BoxSize);
        Assert.Equal("Menlo", Assert.Single(clipped.Text.FontRuns!).FontName);
        Assert.Equal(5, Assert.Single(clipped.Text.ColorRuns!).Length);

        var folder = loaded.Manifest.Layers[2];
        Assert.True(folder.IsGroup);
        Assert.Equal(0.5, folder.Opacity);
        Assert.Equal(LayerBlendMode.Normal, folder.BlendMode);
        Assert.Null(folder.ImageFile);
        Assert.NotNull(folder.MaskFile);

        var child = loaded.Manifest.Layers[3];
        Assert.Equal(FolderID, child.ParentID);
        Assert.False(child.IsVisible);
        Assert.Equal(ShapeKind.Line, child.Shape!.Kind);
        Assert.Equal(3, child.Shape.LineWidth);
        Assert.Equal(new JsonPoint(0.9, 0.8), child.Shape.End);

        var adjustment = loaded.Manifest.Layers[4].Adjustment!;
        Assert.Equal(AdjustmentKind.Curves, adjustment.Kind);
        Assert.Equal(15, adjustment.Hue);
        Assert.Equal(LevelsChannel.Green, adjustment.Levels.Channel);
        Assert.Equal(10, adjustment.Levels.Ranges[0].Black);
        Assert.Equal(3, adjustment.Curves.Channels[1].Count);
        Assert.Equal(147, adjustment.Curves.Channels[1][1].Y);
        Assert.Equal(1.5, adjustment.Exposure!.Exposure);
        Assert.True(adjustment.GradientMap.Reversed);
        Assert.Equal(2, adjustment.Grain.Size);
        Assert.True(adjustment.BlackWhite.Tint);
        Assert.False(adjustment.ColorBalance.PreserveLuminosity);
        Assert.Equal(12.5, adjustment.GaussianRadius);
        Assert.Equal(-30, adjustment.ResolvedMotionAngle);
        Assert.Equal(40, adjustment.ResolvedMotionDistance);
        Assert.Equal(22, adjustment.ResolvedNoiseAmount);
        Assert.True(adjustment.ResolvedNoiseGaussian);
        Assert.False(adjustment.ResolvedNoiseMonochromatic);
        Assert.Equal(4_000_000_000u, adjustment.ResolvedNoiseSeed);
        Assert.Null(loaded.Manifest.Layers[1].Adjustment);
        Assert.Null(loaded.Manifest.Layers[2].Text);

        Assert.Equal(2, loaded.Manifest.Guides!.Count);
        Assert.Equal(100.5, loaded.Manifest.Guides[0].Position);
        Assert.Equal(GuideAxis.Horizontal, loaded.Manifest.Guides[0].Axis);
        Assert.Equal(-20.25, loaded.Manifest.Guides[1].Position);
    }

    [Fact]
    public void EveryAssetComesBackWithItsPixels()
    {
        using var original = KitchenSink();
        var package = PathIn("Pixels.comp");
        ProjectStore.Save(original, package);

        using var loaded = ProjectStore.Load(package);

        var background = loaded.Images[BackgroundID];
        Assert.Equal(12, background.Width);
        Assert.Equal(8, background.Height);
        var pixel = background.Image.GetPixel(3, 4);
        Assert.Equal(10, pixel.Red);
        Assert.Equal(20, pixel.Green);
        Assert.Equal(30, pixel.Blue);
        Assert.Equal(200, pixel.Alpha);
        Assert.Equal(12, background.Thumbnail.Width);
        Assert.Equal(8, background.Thumbnail.Height);

        var mask = loaded.Masks[ClippedID];
        Assert.Equal(SKColorType.Gray8, mask.Image.ColorType);
        Assert.Equal(SKAlphaType.Opaque, mask.Image.AlphaType);
        Assert.Equal(6, mask.Width);
        Assert.Equal(4, mask.Height);
        Assert.Equal(128, mask.Image.GetPixel(0, 0).Red);

        var folderMask = loaded.Masks[FolderID];
        Assert.Equal(255, folderMask.Image.GetPixel(7, 7).Red);
        Assert.True(Model.LayerMask.IsValid(folderMask.Image));
        Assert.False(Model.LayerMask.IsValid(background.Image));
        Assert.Equal(3, loaded.Images.Count);

        // The runtime document keeps the mask, the link and the group.
        var document = loaded.ToDocument();
        Assert.Equal(5, document.Layers.Count);
        var runtimeClipped = document.Layers[1];
        Assert.NotNull(runtimeClipped.Mask);
        Assert.False(runtimeClipped.Mask!.IsEnabled);
        Assert.False(runtimeClipped.Mask.IsLinked);
        Assert.Equal(10.5, runtimeClipped.Mask.Placement!.Value.X);
        Assert.Equal(BackgroundID, runtimeClipped.MaskSourceID);
        Assert.NotNull(runtimeClipped.Text);
        Assert.NotNull(runtimeClipped.LiveText);
        Assert.Equal("Menlo", runtimeClipped.Text!.Style.FontRuns![0].FontName);
        Assert.True(document.Layers[2].IsGroup);
        Assert.Equal(FolderID, document.Layers[3].ParentID);
        Assert.Equal(new[] { BackgroundID, ClippedID, AdjustmentID },
            document.RenderLayers().Select(layer => layer.ID));
        Assert.Equal(0.5, document.EffectiveOpacities()[ChildID]);

        // The pixels a layer holds are the ones the file holds, so a shape layer stays a shape layer.
        Assert.NotNull(document.Layers[3].LiveShape);
        Assert.Null(document.Layers[0].LiveShape);
        Assert.Equal("Paint & sky 🌤", loaded.Images[ClippedID].Name);
    }

    /// <summary>
    /// The package directory is rebuilt on every save: an asset a layer no longer names is gone, and a
    /// Quick Look preview is dropped rather than left stale.
    /// </summary>
    [Fact]
    public void SaveReplacesThePackageAndDropsWhatIsNoLongerReferenced()
    {
        using var original = KitchenSink();
        var package = PathIn("Replace.comp");
        ProjectStore.Save(original, package, Encoding.UTF8.GetBytes("jpeg bytes"));

        Assert.True(File.Exists(Path.Combine(package, ProjectStore.QuickLookFolderName, ProjectStore.QuickLookPreviewName)));
        var images = Directory.GetFiles(Path.Combine(package, ProjectStore.ImagesName)).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(5, images.Count);
        Assert.Contains($"{BackgroundID:D}".ToUpperInvariant() + ".png", images);
        Assert.Contains($"{ClippedID:D}".ToUpperInvariant() + ".mask.png", images);

        using var withoutFolderMask = KitchenSink();
        var manifest = withoutFolderMask.Manifest;
        manifest.Layers.RemoveAll(layer => layer.ID == ChildID);
        manifest.ActiveLayerID = ClippedID;
        foreach (var layer in manifest.Layers)
        {
            if (layer.MaskFile == Format.LayerMask.ExpectedFile(FolderID))
            {
                layer.MaskFile = null;
                layer.MaskEnabled = null;
            }
        }
        withoutFolderMask.Images.Remove(ChildID);
        withoutFolderMask.Masks.Remove(FolderID);
        ProjectStore.Save(withoutFolderMask, package);

        Assert.False(Directory.Exists(Path.Combine(package, ProjectStore.QuickLookFolderName)));
        images = Directory.GetFiles(Path.Combine(package, ProjectStore.ImagesName)).Select(Path.GetFileName).ToHashSet();
        Assert.Equal(3, images.Count);
        Assert.DoesNotContain($"{ChildID:D}".ToUpperInvariant() + ".png", images);
        Assert.DoesNotContain($"{FolderID:D}".ToUpperInvariant() + ".mask.png", images);

        using var loaded = ProjectStore.Load(package);
        Assert.Equal(4, loaded.Manifest.Layers.Count);
        Assert.Single(loaded.Masks);
    }

    /// <summary>A rejected save leaves the previous package exactly as it was, and no staging behind.</summary>
    [Fact]
    public void AFailedSaveLeavesThePreviousPackageAlone()
    {
        using var original = KitchenSink();
        var package = PathIn("Safe.comp");
        ProjectStore.Save(original, package);
        var saved = File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName));

        using var broken = KitchenSink();
        broken.Manifest.Version = 99;
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Save(broken, package));
        Assert.Equal(ProjectError.Version, error.Error);

        using var missingAsset = KitchenSink();
        missingAsset.Images.Remove(BackgroundID);
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Save(missingAsset, package)).Error);

        using var badMask = KitchenSink();
        badMask.Masks[ClippedID] = Model.ImportedImage.Create(Picture(6, 4, SKColors.White), "not a mask");
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Save(badMask, package)).Error);

        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName)));
        Assert.Empty(Directory.GetDirectories(Root, ".*"));
        using var stillGood = ProjectStore.Load(package);
        Assert.Equal(5, stillGood.Manifest.Layers.Count);

        // A regular file where the package should be is a failed save, not a clobbered one.
        var blocker = PathIn("not-a-directory");
        File.WriteAllBytes(blocker, [1]);
        Assert.ThrowsAny<Exception>(() => ProjectStore.Save(original, Path.Combine(blocker, "CannotSave.comp")));
        Assert.Equal(saved, File.ReadAllBytes(Path.Combine(package, ProjectStore.ManifestName)));
    }

    [Fact]
    public void SaveAndLoadHandleADocumentWithNoLayersAndNoAssets()
    {
        var manifest = Manifest(11);
        manifest.ActiveLayerID = null;
        using var snapshot = new ProjectSnapshot(manifest);
        var package = PathIn("Empty.comp");
        ProjectStore.Save(snapshot, package);

        Assert.Empty(Directory.GetFiles(Path.Combine(package, ProjectStore.ImagesName)));
        using var loaded = ProjectStore.Load(package);
        Assert.Empty(loaded.Manifest.Layers);
        Assert.Empty(loaded.Images);
        Assert.Null(loaded.Manifest.ActiveLayerID);
    }

    /// <summary>Leading a save to a target whose parent does not exist yet is a save, not an error.</summary>
    [Fact]
    public void SaveCreatesTheParentFolder()
    {
        using var snapshot = KitchenSink();
        var package = PathIn("nested/deeper/Project.comp");
        ProjectStore.Save(snapshot, package);
        using var loaded = ProjectStore.Load(package);
        Assert.Equal(5, loaded.Manifest.Layers.Count);
    }

    [Fact]
    public void AMissingOrDamagedAssetIsRejected()
    {
        using var snapshot = KitchenSink();
        var package = PathIn("Damaged.comp");
        ProjectStore.Save(snapshot, package);

        var image = Path.Combine(package, ProjectStore.ImagesName, $"{BackgroundID:D}".ToUpperInvariant() + ".png");
        File.Delete(image);
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);

        File.WriteAllBytes(image, Encoding.UTF8.GetBytes("not a png"));
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);

        File.Copy(Path.Combine(package, ProjectStore.ImagesName, $"{ChildID:D}".ToUpperInvariant() + ".png"), image, true);
        using var wrongSize = ProjectStore.Load(package);
        Assert.Equal(5, wrongSize.Images[BackgroundID].Width);
    }

    [Fact]
    public void AMaskMustBeEightBitGrayscaleWithoutAlpha()
    {
        using var snapshot = KitchenSink();
        var package = PathIn("Masks.comp");
        ProjectStore.Save(snapshot, package);
        var mask = Path.Combine(package, ProjectStore.ImagesName, $"{ClippedID:D}".ToUpperInvariant() + ".mask.png");

        // A color image is not a mask, however it is named.
        File.WriteAllBytes(mask, PngCodec.Encode(Picture(6, 4, SKColors.White)));
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);

        // Nor is a grayscale image with alpha (PNG color type 4), which Skia cannot encode for us.
        File.WriteAllBytes(mask, GrayWithAlphaPng(6, 4));
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);

        File.WriteAllBytes(mask, PngCodec.Encode(Gray(6, 4, 200)));
        using var loaded = ProjectStore.Load(package);
        Assert.Equal(200, loaded.Masks[ClippedID].Image.GetPixel(0, 0).Red);

        File.Delete(mask);
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);
    }

    [Fact]
    public void AnImageOutsideThePackageIsRejected()
    {
        // The name has to be the layer's own, which keeps `..` out of it.
        var escaped = ImageLayer(BackgroundID, "Background", "../../outside.png");
        var manifest = Manifest(11, escaped);
        ExpectInvalid(manifest);

        using var snapshot = KitchenSink();
        var package = PathIn("Escape.comp");
        ProjectStore.Save(snapshot, package);
        var manifestFile = Path.Combine(package, ProjectStore.ManifestName);
        var text = File.ReadAllText(manifestFile).Replace(
            $"{BackgroundID:D}".ToUpperInvariant() + ".png",
            "../../../outside.png");
        File.WriteAllText(manifestFile, text);
        Assert.Equal(ProjectError.Invalid, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);
    }

    [Fact]
    public void AnAnimationOrSixteenBitImageIsRejected()
    {
        using var snapshot = KitchenSink();
        var package = PathIn("Depth.comp");
        ProjectStore.Save(snapshot, package);
        var image = Path.Combine(package, ProjectStore.ImagesName, $"{BackgroundID:D}".ToUpperInvariant() + ".png");

        // A 16-bit PNG, as the Mac build's depth check rejects.
        var wide = new SKBitmap(new SKImageInfo(12, 8, SKColorType.Rgba16161616, SKAlphaType.Unpremul));
        wide.Erase(new SKColor(1, 2, 3, 4));
        File.WriteAllBytes(image, PngCodec.Encode(wide));
        Assert.Equal(ProjectError.MissingImage, Assert.Throws<ProjectException>(() => ProjectStore.Load(package)).Error);

        // The same picture at 8 bits per channel is fine.
        File.WriteAllBytes(image, PngCodec.Encode(Picture(12, 8, new SKColor(9, 9, 9))));
        using var loaded = ProjectStore.Load(package);
        Assert.Equal(9, loaded.Images[BackgroundID].Image.GetPixel(0, 0).Red);
    }

    [Fact]
    public void TheManifestItselfIsCappedAtFourMegabytes()
    {
        using var snapshot = KitchenSink();
        var layers = snapshot.Manifest.Layers;
        var name = new string('n', 16_000);
        for (var index = 0; index < 320; index++)
        {
            layers.Add(new ProjectLayerRecord
            {
                ID = Guid.NewGuid(),
                Name = $"{name}{index}",
                IsVisible = true,
                Transform = Concrete(),
            });
        }
        var error = Assert.Throws<ProjectException>(() => ProjectStore.Save(snapshot, PathIn("Huge.comp")));
        Assert.Equal(ProjectError.TooLarge, error.Error);
        Assert.False(Directory.Exists(PathIn("Huge.comp")));
    }

    [Fact]
    public void ACanvasOrAnImageBeyondTheSideLimitIsRejected()
    {
        using var snapshot = KitchenSink();
        snapshot.Manifest.Width = Model.DocumentLimits.MaxSide + 1;
        Assert.Equal(ProjectError.TooLarge, Assert.Throws<ProjectException>(() => ProjectStore.Save(snapshot, PathIn("Wide.comp"))).Error);
        snapshot.Manifest.Width = 640;
        snapshot.Manifest.Height = 0;
        Assert.Equal(ProjectError.TooLarge, Assert.Throws<ProjectException>(() => ProjectStore.Save(snapshot, PathIn("Wide.comp"))).Error);

        // A real asset past the side limit: one pixel tall and just too wide.
        snapshot.Manifest.Height = 480;
        snapshot.Images[BackgroundID].Dispose();
        snapshot.Images[BackgroundID] = Asset(Model.DocumentLimits.MaxSide + 1, 1, SKColors.Red);
        Assert.Equal(ProjectError.TooLarge, Assert.Throws<ProjectException>(() => ProjectStore.Save(snapshot, PathIn("Wide.comp"))).Error);
    }
}
