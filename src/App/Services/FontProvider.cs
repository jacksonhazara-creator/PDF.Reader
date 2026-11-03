namespace PdfReader.App.Services;

/// <summary>Fonts that ship with Windows, embedded into the PDF when used (e.g. for typed signatures).</summary>
public static class FontProvider
{
    private static readonly Lazy<byte[]?> s_handwriting = new(() => Load("segoesc.ttf") ?? Load("Inkfree.ttf") ?? Load("segoepr.ttf"));

    /// <summary>A handwriting-style font (Segoe Script / Ink Free), or null to fall back to Helvetica Oblique.</summary>
    public static byte[]? Handwriting => s_handwriting.Value;

    private static byte[]? Load(string fileName)
    {
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Fonts), fileName);
            return File.Exists(path) ? File.ReadAllBytes(path) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
