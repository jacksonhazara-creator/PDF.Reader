using System.Reflection;

namespace PdfReader.App.Services;

public static class AppInfo
{
    /// <summary>Public GitHub repository used for update checks (anonymous, no token).</summary>
    /// <remarks>GitHub user names cannot contain "_"; adjust if your account name differs.</remarks>
    public const string RepositoryUrl = "https://github.com/jacksonhazara-creator/PDF.Reader";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";
}
