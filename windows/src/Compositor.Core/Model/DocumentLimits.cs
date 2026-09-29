namespace Compositor.Core.Model;

/// <summary>The size and memory ceilings a document is held to, in one place.</summary>
public static class DocumentLimits
{
    /// <summary>Longest side, in pixels, of any canvas, layer, mask or generated surface.</summary>
    public const int MaxSide = 30_000;

    /// <summary>Largest single surface: a canvas, an export, a filter target, an adjustment or mask render.</summary>
    public const int MaxSurfacePixels = 200_000_000;

    /// <summary>Manifest.json may not exceed this.</summary>
    public const int MaxManifestBytes = 4 * 1024 * 1024;

    /// <summary>One encoded layer image or mask may not exceed this.</summary>
    public const int MaxAssetBytes = 512 * 1024 * 1024;

    /// <summary>
    /// Total imported raster one document may hold, summed across every layer and mask; images and masks
    /// are counted separately. The Mac build takes a quarter of the machine's memory at 4 bytes a pixel,
    /// clamped between one surface and 800 MP; the GC's view of available memory stands in for
    /// `ProcessInfo.physicalMemory`.
    /// </summary>
    public static readonly int DocumentPixelBudget = Math.Min(800_000_000,
        Math.Max(MaxSurfacePixels, (int)Math.Clamp(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 16, 0, int.MaxValue)));

    public static int MaxSurfaceMegapixels => MaxSurfacePixels / 1_000_000;
    public static int DocumentBudgetMegapixels => DocumentPixelBudget / 1_000_000;
}
