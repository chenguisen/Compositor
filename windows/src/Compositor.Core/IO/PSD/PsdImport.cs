using Compositor.Core.Format;
using Compositor.Core.Model;

namespace Compositor.Core.IO.PSD;

/// <summary>Something the Photoshop file held that Compositor could not keep, for the conversion report.</summary>
public sealed record PsdConversionNote(string Layer, string What);

/// <summary>
/// A Photoshop file read into a document: the manifest, the pixels its layers and masks name, and what
/// could not be kept. The pixels belong to the caller from here on.
/// </summary>
public sealed class PsdImport
{
    public required ProjectManifest Manifest { get; init; }
    public required Dictionary<Guid, ImportedImage> Images { get; init; }
    public required Dictionary<Guid, ImportedImage> Masks { get; init; }
    public required IReadOnlyList<PsdConversionNote> Notes { get; init; }

    /// <summary>The manifest with the pixels it names, ready to save or to open for editing.</summary>
    public ProjectSnapshot Snapshot() => new(Manifest, Images, Masks);
}

/// <summary>
/// Reads Photoshop <c>.psd</c> and <c>.psb</c> files from Adobe's *Photoshop File Formats Specification*
/// (2019 HTML edition: File Header, Color Mode Data, Image Resources, Layer and Mask Information, Image
/// Data). Only the 8-bit RGB files the Mac build accepts are read; anything else is refused rather than
/// misread.
/// </summary>
public static class PsdImporter
{
    /// <summary>
    /// Reads a PSD or PSB into a document that can be saved as a <c>.comp</c>.
    /// <paramref name="remainingPixels"/> is what the document has left of its pixel budget; the default
    /// reads against the whole budget, as the Mac build does.
    /// </summary>
    public static PsdImport Read(string path, int remainingPixels = -1)
    {
        var budget = remainingPixels < 0 ? DocumentLimits.DocumentPixelBudget : remainingPixels;
        var bytes = ReadFile(path);
        var document = PsdReader.Read(bytes, budget, Path.GetFileNameWithoutExtension(path));
        return PsdDocumentBuilder.Build(document, budget);
    }

    private static byte[] ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            // One encoded layer image or mask may not exceed this either, so a whole file larger than the
            // cap is one no import could hold.
            if (info.Length > DocumentLimits.MaxAssetBytes) throw new ImportException(ImportError.TooLarge);
            return File.ReadAllBytes(path);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            throw new ImportException(ImportError.Unreadable, $"Could not read {path}: {error.Message}");
        }
    }
}
