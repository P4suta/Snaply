using System.Globalization;
using Microsoft.UI.Xaml;
using Serilog;

namespace Snaply;

public sealed partial class App : Application, IDisposable
{
    private MainWindow? _window;
    private bool _disposed;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ConfigureLogging();

        try
        {
            _window = new MainWindow();
            _window.Closed += OnWindowClosed;
            _window.Activate();
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public void Dispose()
    {
        DisposeCore(closeWindow: true);
        GC.SuppressFinalize(this);
    }

    private void DisposeCore(bool closeWindow)
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        UnhandledException -= OnUnhandledException;
        if (_window is not null)
        {
            _window.Closed -= OnWindowClosed;
            if (closeWindow)
            {
                _window.Close();
            }

            _window.Dispose();
            _window = null;
        }

        Log.CloseAndFlush();
    }

    private static void ConfigureLogging()
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Snaply",
                "Logs");
            Directory.CreateDirectory(directory);

            Log.Logger = new LoggerConfiguration()
                .MinimumLevel.Warning()
                .WriteTo.File(
                    Path.Combine(directory, "snaply-.log"),
                    rollingInterval: RollingInterval.Day,
                    retainedFileCountLimit: 7,
                    shared: true,
                    formatProvider: CultureInfo.InvariantCulture)
                .CreateLogger();

            DateTime threshold = DateTime.UtcNow.AddDays(-7);
            foreach (string path in Directory.EnumerateFiles(directory, "snaply-*.log"))
            {
                if (File.GetLastWriteTimeUtc(path) >= threshold)
                {
                    continue;
                }

                try
                {
                    File.Delete(path);
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    Log.Warning(
                        "Log retention cleanup failed {ExceptionType} {HResult}",
                        exception.GetType().FullName,
                        exception.HResult);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Logger = new LoggerConfiguration().CreateLogger();
        }
    }

    private static void OnUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        Log.Fatal(
            "Unhandled UI failure {ExceptionType} {HResult}",
            args.Exception.GetType().FullName,
            args.Exception.HResult);
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) =>
        DisposeCore(closeWindow: false);
}
