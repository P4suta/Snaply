using System.Diagnostics.CodeAnalysis;

namespace Snaply.Application;

/// <summary>
/// Central resolver for Snaply's per-user data directory and its subfolders under
/// <c>%LOCALAPPDATA%\Snaply</c>. This is an unpackaged app, so we use the file system
/// rather than <c>ApplicationData</c>. Settings, rolling logs, and crash dumps all live
/// here so the location is consistent and easy to point a user at. Shared by the WinUI
/// app and the CLI so both write to the same place.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Environment/filesystem path resolver; infrastructure, not unit-tested.")]
public static class AppPaths
{
    /// <summary>The per-user root: <c>%LOCALAPPDATA%\Snaply</c>.</summary>
    public static string Root { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "Snaply");

    /// <summary>The settings document: <c>%LOCALAPPDATA%\Snaply\settings.json</c>.</summary>
    public static string SettingsFile => Path.Combine(Root, "settings.json");

    /// <summary>The rolling-log directory: <c>%LOCALAPPDATA%\Snaply\logs</c>.</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The crash-dump directory: <c>%LOCALAPPDATA%\Snaply\crashes</c>.</summary>
    public static string CrashesDirectory => Path.Combine(Root, "crashes");

    /// <summary>Ensures <paramref name="directory"/> exists and returns it.</summary>
    /// <param name="directory">The directory to create if missing.</param>
    /// <returns>The same directory path, now guaranteed to exist.</returns>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
