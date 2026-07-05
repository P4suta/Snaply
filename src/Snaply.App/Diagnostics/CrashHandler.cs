using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;

namespace Snaply.Diagnostics;

/// <summary>
/// Last-resort crash observability. Writes a self-contained crash dump (exception plus
/// environment context) to <c>%LOCALAPPDATA%\Snaply\crashes</c>. Depends only on the BCL so it
/// works even when DI / logging is not (yet) available — e.g. an exception during startup.
/// </summary>
public static class CrashHandler
{
    /// <summary>
    /// Writes a crash dump for <paramref name="exception"/> and returns its path, or null if the
    /// dump could not be written (never throws — this is the bottom of the safety net).
    /// </summary>
    /// <param name="exception">The exception to record.</param>
    /// <param name="source">Where the exception surfaced (e.g. "UI", "AppDomain", "TaskScheduler").</param>
    /// <returns>The dump file path, or null on failure.</returns>
    public static string? WriteDump(Exception exception, string source)
    {
        try
        {
            string directory = AppPaths.Ensure(AppPaths.CrashesDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss", CultureInfo.InvariantCulture);
            string unique = Guid.NewGuid().ToString("N")[..8];
            string path = Path.Combine(directory, $"crash-{stamp}-{unique}.log");
            File.WriteAllText(path, BuildReport(exception, source));
            return path;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string BuildReport(Exception exception, string source)
    {
        string version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        var sb = new StringBuilder();
        sb.AppendLine("Snaply crash report");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Time:       {DateTimeOffset.Now:O}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Source:     {source}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Version:    {version}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"OS:         {Environment.OSVersion}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Runtime:    {RuntimeInformation.FrameworkDescription}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"Arch:       OS={RuntimeInformation.OSArchitecture}, Process={RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine(CultureInfo.InvariantCulture, $"UICulture:  {CultureInfo.CurrentUICulture.Name}");
        sb.AppendLine();
        sb.AppendLine("Exception:");
        sb.AppendLine(exception.ToString());
        return sb.ToString();
    }
}
