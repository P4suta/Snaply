using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;

namespace Snaply.Launcher;

/// <summary>
/// The tiny executable a user double-clicks at the root of the distributable bundle. The real
/// WinUI app and its self-contained runtime live one level down in <c>app\</c>; this launcher
/// sits alone at the top so "which file do I run" is obvious, starts <c>app\Snaply.App.exe</c>
/// (forwarding its arguments), and exits — the GUI app outlives it, so only one process remains.
/// </summary>
internal static partial class Program
{
    private const string AppSubdirectory = "app";
    private const string AppExecutable = "Snaply.App.exe";
    private const uint MbIconError = 0x10;

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int MessageBoxW(nint hWnd, string text, string caption, uint type);

    /// <summary>Locate and launch the real app next to this launcher.</summary>
    /// <param name="args">Command-line arguments to forward to the app.</param>
    /// <returns><c>0</c> on success; <c>1</c> if the app executable could not be found.</returns>
    [STAThread]
    private static int Main(string[] args)
    {
        string root = AppContext.BaseDirectory;
        string appDirectory = Path.Combine(root, AppSubdirectory);
        string appExecutable = Path.Combine(appDirectory, AppExecutable);

        if (!File.Exists(appExecutable))
        {
            _ = MessageBoxW(
                0,
                $"Could not find {AppSubdirectory}\\{AppExecutable}.\n\nPlease re-extract the download and keep the '{AppSubdirectory}' folder next to this launcher.",
                "Snaply",
                MbIconError);
            return 1;
        }

        // WorkingDirectory = app\ so the .NET apphost resolves its own DLLs / *.deps.json.
        var startInfo = new ProcessStartInfo
        {
            FileName = appExecutable,
            WorkingDirectory = appDirectory,
            UseShellExecute = false,
        };
        foreach (string arg in args)
        {
            startInfo.ArgumentList.Add(arg);
        }

        // Spawn and return immediately — do not wait; the GUI app is the surviving process.
        try
        {
            _ = Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            LogError(ex);
            _ = MessageBoxW(
                0,
                $"Could not start Snaply.\n\n{ex.Message}",
                "Snaply",
                MbIconError);
            return 1;
        }

        return 0;
    }

    // Best-effort text log next to the app's other logs; the launcher has no external
    // dependencies (Native AOT), so this stays plain BCL and culture-invariant.
    private static void LogError(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Snaply",
                "logs");
            Directory.CreateDirectory(directory);
            string timestamp = DateTime.Now.ToString("O", CultureInfo.InvariantCulture);
            File.AppendAllText(
                Path.Combine(directory, "launcher.log"),
                $"[{timestamp}] Failed to start app: {exception}{Environment.NewLine}");
        }
        catch (Exception)
        {
            // Nothing more the launcher can do if even the log write fails.
            return;
        }
    }
}
