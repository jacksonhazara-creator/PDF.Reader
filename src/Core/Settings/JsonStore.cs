using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;

namespace PdfReader.Core.Settings;

[JsonSourceGenerationOptions(WriteIndented = true, UseStringEnumConverter = true, PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    ReadCommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true)]
[JsonSerializable(typeof(AppSettings))]
[JsonSerializable(typeof(AppState))]
internal sealed partial class CoreJsonContext : JsonSerializerContext;

/// <summary>
/// Loads and saves the app's JSON files under %LOCALAPPDATA%\PDF.Reader. Writes are atomic
/// (temp file + replace) so a crash can never leave a half-written settings file, and a corrupt
/// file falls back to defaults instead of breaking startup.
/// </summary>
public sealed class JsonStore
{
    public JsonStore(string? rootDirectory = null)
    {
        RootDirectory = rootDirectory ?? AppPaths.DataDirectory;
    }

    public string RootDirectory { get; }
    private string SettingsPath => Path.Combine(RootDirectory, "settings.json");
    private string StatePath => Path.Combine(RootDirectory, "state.json");

    public AppSettings LoadSettings() => Load(SettingsPath, CoreJsonContext.Default.AppSettings) ?? new AppSettings();
    public AppState LoadState() => Load(StatePath, CoreJsonContext.Default.AppState) ?? new AppState();

    public void SaveSettings(AppSettings settings) => Save(SettingsPath, settings, CoreJsonContext.Default.AppSettings);

    public void SaveState(AppState state)
    {
        state.Trim();
        Save(StatePath, state, CoreJsonContext.Default.AppState);
    }

    private static T? Load<T>(string path, JsonTypeInfo<T> typeInfo) where T : class
    {
        try
        {
            if (!File.Exists(path))
                return null;
            using var stream = File.OpenRead(path);
            return JsonSerializer.Deserialize(stream, typeInfo);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException or NotSupportedException)
        {
            return null;
        }
    }

    private static void Save<T>(string path, T value, JsonTypeInfo<T> typeInfo)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + ".tmp";
            using (var stream = File.Create(temp))
                JsonSerializer.Serialize(stream, value, typeInfo);
            File.Move(temp, path, overwrite: true);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Settings are a convenience; failing to save must never crash the reader.
        }
    }
}

public static class AppPaths
{
    public static string DataDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "PDF.Reader");

    public static string ThumbnailDirectory => Path.Combine(DataDirectory, "Thumbnails");

    /// <summary>Stable, filesystem-safe cache key for a document path.</summary>
    public static string ThumbnailPathFor(string documentPath)
    {
        var hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(documentPath.ToUpperInvariant()));
        return Path.Combine(ThumbnailDirectory, Convert.ToHexString(hash, 0, 16) + ".png");
    }
}
