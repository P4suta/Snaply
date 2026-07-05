using System.Diagnostics;

namespace Snaply.Diagnostics;

/// <summary>Small shell helpers for opening OS locations. Best-effort; never throws.</summary>
public static class ShellActions
{
    /// <summary>Opens <paramref name="folder"/> in the file explorer (creating it if missing).</summary>
    /// <param name="folder">The folder to reveal.</param>
    /// <returns><c>true</c> if the open was launched; <c>false</c> on failure.</returns>
    public static bool OpenFolder(string folder)
    {
        try
        {
            AppPaths.Ensure(folder);
            _ = Process.Start(new ProcessStartInfo(folder) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException or UnauthorizedAccessException)
        {
            return false;
        }
    }
}
