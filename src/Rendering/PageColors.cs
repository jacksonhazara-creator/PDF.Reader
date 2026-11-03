namespace PdfReader.Rendering;

public enum PageColorMode { Normal, Dark, Sepia, Custom }

/// <summary>
/// Display-only reading theme, applied while rasterizing. Never written to the PDF;
/// printing and export always use <see cref="Normal"/>.
/// Colors are 0xAARRGGBB.
/// </summary>
public readonly record struct PageColors(PageColorMode Mode, uint Foreground, uint Background, bool KeepImagesNatural)
{
    public static readonly PageColors Normal = new(PageColorMode.Normal, 0xFF000000, 0xFFFFFFFF, true);
    public static PageColors Dark(bool keepImagesNatural = true) => new(PageColorMode.Dark, 0xFFDADADA, 0xFF1F1F1F, keepImagesNatural);
    public static PageColors Sepia(bool keepImagesNatural = true) => new(PageColorMode.Sepia, 0xFF5B4636, 0xFFF4ECD8, keepImagesNatural);
    public static PageColors Custom(uint foreground, uint background, bool keepImagesNatural = true)
        => new(PageColorMode.Custom, foreground | 0xFF000000, background | 0xFF000000, keepImagesNatural);

    public static PageColors For(PageColorMode mode, bool keepImagesNatural, uint customForeground, uint customBackground) => mode switch
    {
        PageColorMode.Dark => Dark(keepImagesNatural),
        PageColorMode.Sepia => Sepia(keepImagesNatural),
        PageColorMode.Custom => Custom(customForeground, customBackground, keepImagesNatural),
        _ => Normal,
    };

    public bool IsNormal => Mode == PageColorMode.Normal;

    /// <summary>Linear blend a→b in ARGB space, t in [0,1].</summary>
    public static uint Mix(uint a, uint b, double t)
    {
        static uint Channel(uint x, uint y, int shift, double t)
        {
            var ca = (x >> shift) & 0xFF;
            var cb = (y >> shift) & 0xFF;
            return (uint)Math.Clamp(Math.Round(ca + (cb - (double)ca) * t), 0, 255) << shift;
        }
        return 0xFF000000 | Channel(a, b, 16, t) | Channel(a, b, 8, t) | Channel(a, b, 0, t);
    }
}

/// <summary>
/// Pixel post-processing for themes when images should NOT be kept natural:
/// every pixel's luminance is mapped onto the foreground→background gradient
/// (white paper → background, black ink → foreground).
/// </summary>
public static class PageColorFilter
{
    public static void ApplyLuminanceMap(Span<uint> pixels, PageColors colors)
    {
        Span<uint> lut = stackalloc uint[256];
        for (var i = 0; i < 256; i++)
            lut[i] = PageColors.Mix(colors.Foreground, colors.Background, i / 255.0);

        for (var i = 0; i < pixels.Length; i++)
        {
            var p = pixels[i];
            // BGRA in memory => 0xAARRGGBB as uint on little-endian.
            var r = (p >> 16) & 0xFF;
            var g = (p >> 8) & 0xFF;
            var b = p & 0xFF;
            var lum = (54 * r + 183 * g + 19 * b) >> 8; // Rec. 709 weights, 0..255
            pixels[i] = lut[(int)lum];
        }
    }
}
