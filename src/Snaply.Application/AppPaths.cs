using System.Diagnostics.CodeAnalysis;

namespace Snaply.Application;

/// <summary>
/// Central resolver for Snaply's data directory and its subfolders. Snaply is portable: everything
/// lives in a <c>Snaply</c> folder <em>beside the running executable</em> (<see cref="AppContext.BaseDirectory"/>),
/// never in the user's profile — capturing, logging, and crash dumps leave the user's environment
/// untouched. Shared by the WinUI app and the CLI so both write to the same app-local place.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Environment/filesystem path resolver; infrastructure, not unit-tested.")]
public static class AppPaths
{
    /// <summary>The app-local data root: a <c>Snaply</c> folder beside the executable (portable, not the user profile).</summary>
    public static string Root { get; } = Path.Combine(AppContext.BaseDirectory, "Snaply");

    /// <summary>The rolling-log directory: <c>&lt;app&gt;\Snaply\logs</c>.</summary>
    public static string LogsDirectory => Path.Combine(Root, "logs");

    /// <summary>The crash-dump directory: <c>&lt;app&gt;\Snaply\crashes</c>.</summary>
    public static string CrashesDirectory => Path.Combine(Root, "crashes");

    /// <summary>The auto-save directory for captured screenshots: <c>&lt;app&gt;\Snaply\Captures</c>.</summary>
    public static string CapturesDirectory => Path.Combine(Root, "Captures");

    /// <summary>Ensures <paramref name="directory"/> exists and returns it.</summary>
    /// <param name="directory">The directory to create if missing.</param>
    /// <returns>The same directory path, now guaranteed to exist.</returns>
    public static string Ensure(string directory)
    {
        Directory.CreateDirectory(directory);
        return directory;
    }
}
