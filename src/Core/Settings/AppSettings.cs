using PdfReader.Rendering;

namespace PdfReader.Core.Settings;

public enum AppTheme { System, Light, Dark }
public enum UpdateChannel { Stable, Beta }
public enum ZoomMode { FitWidth, FitPage, ActualSize, Custom }
public enum ToolbarDock { Left, Right, Top, Floating }

/// <summary>User preferences (settings.json). Defaults are chosen so nobody ever has to open Settings.</summary>
public sealed class AppSettings
{
    // General
    /// <summary>"system", "en" or "de".</summary>
    public string Language { get; set; } = "system";
    public bool RestoreSession { get; set; } = true;
    /// <summary>Shows developer tools such as the self-diagnosis in Settings → General.</summary>
    public bool DeveloperMode { get; set; }
    public ZoomMode DefaultZoom { get; set; } = ZoomMode.FitWidth;

    // Appearance
    public AppTheme Theme { get; set; } = AppTheme.System;
    public PageColorMode PageColorMode { get; set; } = PageColorMode.Normal;
    public bool KeepImagesNatural { get; set; } = true;
    /// <summary>#RRGGBB</summary>
    public string CustomForeground { get; set; } = "#E8E2D0";
    /// <summary>#RRGGBB</summary>
    public string CustomBackground { get; set; } = "#26303A";

    // Marker & comments (remembered between sessions)
    /// <summary>#RRGGBB</summary>
    public string MarkerColor { get; set; } = "#FFE600";
    /// <summary>0 = opaque … 0.9 = almost invisible.</summary>
    public double MarkerTransparency { get; set; } = 0.55;
    /// <summary>0.2 = pale … 1 = full color.</summary>
    public double MarkerStrength { get; set; } = 1.0;
    /// <summary>Pen width in points when drawing.</summary>
    public double MarkerThickness { get; set; } = 8;
    public CommentShape CommentShape { get; set; } = CommentShape.Square;
    /// <summary>#RRGGBB</summary>
    public string CommentColor { get; set; } = "#FFB020";
    public bool CommentOnLastWord { get; set; } = true;

    // Draw & text tools
    /// <summary>#RRGGBB</summary>
    public string DrawColor { get; set; } = "#D9363E";
    public double DrawThickness { get; set; } = 2.5;
    /// <summary>0 = opaque … 0.9.</summary>
    public double DrawTransparency { get; set; }
    public double TextSize { get; set; } = 14;
    public string StampLabel { get; set; } = "Approved";

    // Layout
    public ToolbarDock ToolbarDock { get; set; } = ToolbarDock.Left;
    public double ToolbarX { get; set; } = 90;
    public double ToolbarY { get; set; } = 60;
    /// <summary>Toolbar width when docked left/right or floating; below ~110 it shows icons only.</summary>
    public double ToolbarWidth { get; set; } = 160;
    public double RightPanelWidth { get; set; } = 300;
    public bool RightPanelOpen { get; set; } = true;
    /// <summary>Pages, Bookmarks or Comments.</summary>
    public string RightPanelTab { get; set; } = "Pages";

    // Updates
    public bool CheckForUpdatesOnStartup { get; set; } = true;
    public UpdateChannel UpdateChannel { get; set; } = UpdateChannel.Stable;

    /// <summary>
    /// Marker color as 0xAARRGGBB: strength fades the color toward white (pale ↔ vivid),
    /// transparency sets the alpha (how much of the page shows through).
    /// </summary>
    public uint ResolveMarkerArgb()
    {
        var baseColor = ColorParser.ParseOrDefault(MarkerColor, 0xFFFFE600);
        var rgb = PageColors.Mix(0xFFFFFFFF, baseColor, Math.Clamp(MarkerStrength, 0.2, 1)) & 0xFFFFFF;
        var alpha = (uint)Math.Round(255 * (1 - Math.Clamp(MarkerTransparency, 0, 0.9)));
        return (alpha << 24) | rgb;
    }

    public uint ResolveDrawArgb()
    {
        var rgb = ColorParser.ParseOrDefault(DrawColor, 0xFFD9363E) & 0xFFFFFF;
        var alpha = (uint)Math.Round(255 * (1 - Math.Clamp(DrawTransparency, 0, 0.9)));
        return (alpha << 24) | rgb;
    }

    public PageColors ResolvePageColors(PageColorMode? overrideMode = null) => PageColors.For(
        overrideMode ?? PageColorMode,
        KeepImagesNatural,
        ColorParser.ParseOrDefault(CustomForeground, 0xFFE8E2D0),
        ColorParser.ParseOrDefault(CustomBackground, 0xFF26303A));
}

public static class ColorParser
{
    /// <summary>Parses #RGB, #RRGGBB or #AARRGGBB into 0xAARRGGBB (alpha forced opaque).</summary>
    public static bool TryParse(string? text, out uint argb)
    {
        argb = 0;
        if (string.IsNullOrWhiteSpace(text))
            return false;
        var s = text.Trim().TrimStart('#');
        if (s.Length == 3)
            s = string.Concat(s[0], s[0], s[1], s[1], s[2], s[2]);
        if (s.Length is not (6 or 8) || !uint.TryParse(s, System.Globalization.NumberStyles.HexNumber, null, out var value))
            return false;
        argb = 0xFF000000 | (value & 0x00FFFFFF);
        return true;
    }

    public static uint ParseOrDefault(string? text, uint fallback) => TryParse(text, out var v) ? v : fallback;

    public static string ToHex(uint argb) => $"#{argb & 0xFFFFFF:X6}";
}
