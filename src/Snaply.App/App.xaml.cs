using System.Globalization;
using Microsoft.UI.Xaml;
using Serilog;

namespace Snaply;

public partial class App : Application
{
    private MainWindow? _window;

    public App()
    {
        InitializeComponent();
        UnhandledException += OnUnhandledException;
    }

    internal static Window MainWindow { get; private set; } = null!;

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        ConfigureLogging();

        var capture = new ScreenCaptureService();
        var export = new ImageExportService();
        var viewModel = new MainViewModel(capture, export);
        _window = new MainWindow(viewModel, capture);
        MainWindow = _window;
        _window.Closed += (_, _) =>
        {
            viewModel.Dispose();
            capture.Dispose();
            Log.CloseAndFlush();
        };
        _window.Activate();
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
}
