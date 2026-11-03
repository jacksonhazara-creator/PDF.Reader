using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using PdfReader.Core.Settings;
using PdfReader.Rendering;

namespace PdfReader.App.Services;

public static class BitmapFactory
{
    /// <summary>Copies PDFium output into an Avalonia bitmap and frees the native buffer. Safe off the UI thread.</summary>
    public static Bitmap Create(PixelBuffer pixels)
    {
        try
        {
            return new Bitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels.Data,
                new PixelSize(pixels.Width, pixels.Height), new Vector(96, 96), pixels.Stride);
        }
        finally
        {
            pixels.Dispose();
        }
    }
}

/// <summary>First-page thumbnails for the start screen's recent files.</summary>
public static class RecentThumbnails
{
    private const double Width = 160;

    public static async Task EnsureAsync(PdfDocument document, string path)
    {
        try
        {
            var file = AppPaths.ThumbnailPathFor(path);
            if (File.Exists(file) && File.GetLastWriteTimeUtc(file) >= File.GetLastWriteTimeUtc(path))
                return;
            if (document.PageCount == 0)
                return;
            var scale = Width * 2 / document.PageSizes[0].Width;
            var pixels = await document.RenderPageAsync(0, scale, PageColors.Normal, WorkPriority.Background);
            await Task.Run(() =>
            {
                using var bitmap = BitmapFactory.Create(pixels);
                Directory.CreateDirectory(AppPaths.ThumbnailDirectory);
                bitmap.Save(file, PngBitmapEncoderOptions.Default);
            });
        }
        catch (Exception)
        {
            // A missing thumbnail only means a placeholder on the start screen.
        }
    }
}
