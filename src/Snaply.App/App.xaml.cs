using System.Globalization;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Snaply.Bootstrap;
using Snaply.Diagnostics;
using Snaply.Services;

namespace Snaply;

/// <summary>
/// Provides application-specific behavior to supplement the default Application class.
/// Also installs the global exception safety net (UI / AppDomain / TaskScheduler) so no
/// failure goes unlogged or crashes the app silently.
/// </summary>
public partial class App : Microsoft.UI.Xaml.Application
{
    private ILogger<App>? _logger;
    private bool _exiting;

    /// <summary>
    /// The main application window. Use <c>App.Window</c> from any class that needs
    /// the window reference (for dialogs, pickers, interop, etc.).
    /// </summary>
    public static Window Window { get; private set; } = null!;

    /// <summary>The composition root; resolve app services and view models from here.</summary>
    public static IServiceProvider Services { get; private set; } = null!;

    /// <summary>
    /// The UI thread dispatcher. Use <c>App.DispatcherQueue</c> to marshal calls
    /// to the UI thread. Fully qualified to avoid CS0104 ambiguity with
    /// <see cref="Windows.System.DispatcherQueue"/>.
    /// </summary>
    public static Microsoft.UI.Dispatching.DispatcherQueue DispatcherQueue { get; private set; } = null!;

    /// <summary>
    /// The native window handle (HWND). Use for file pickers,
    /// <c>DataTransferManager</c>, and any WinRT interop that requires
    /// <c>InitializeWithWindow</c>.
    /// </summary>
    public static nint WindowHandle =>
        WinRT.Interop.WindowNative.GetWindowHandle(Window);

    /// <summary>The window id, for the modern <c>Microsoft.Windows.Storage.Pickers</c> APIs.</summary>
    public static Microsoft.UI.WindowId WindowId =>
        Microsoft.UI.Win32Interop.GetWindowIdFromWindow(WindowHandle);

    /// <summary>
    /// Initializes the singleton application object and installs the UI-thread exception hook.
    /// </summary>
    public App()
    {
        InitializeComponent();
        UnhandledException += OnUiUnhandledException;
    }

    /// <summary>
    /// Invoked when the application is launched.
    /// </summary>
    /// <param name="args">Details about the launch request and process.</param>
    protected override void OnLaunched(Microsoft.UI.Xaml.LaunchActivatedEventArgs args)
    {
        // Install the rest of the safety net as early as possible.
        AppDomain.CurrentDomain.UnhandledException += OnDomainUnhandledException;
        TaskScheduler.UnobservedTaskException += OnUnobservedTaskException;

        try
        {
            Services = ServiceRegistration.Build();
            DispatcherQueue = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            _logger = Services.GetRequiredService<ILogger<App>>();

            // Snaply follows the OS display language: MRT/x:Uid resolve against the user's Windows
            // language list, so there is no override to apply — just record the effective culture.
            AppLog.Started(_logger, AppVersion, CultureInfo.CurrentUICulture.Name);

            Window = new MainWindow();
            Window.Closed += OnWindowClosed;
            Window.Activate();
        }
        catch (Exception ex)
        {
            // Startup is the one place we cannot recover from: record everything, then rethrow.
            HandleFatal(ex, "Startup");
            throw;
        }
    }

    // Computed once (reflection) so logging call sites pass a cheap field, not an expensive expression.
    private static readonly string AppVersion =
        Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    // --- Global exception safety net -----------------------------------------
    private void OnUiUnhandledException(object sender, Microsoft.UI.Xaml.UnhandledExceptionEventArgs e)
    {
        HandleFatal(e.Exception, "UI");

        // The failure has been logged, dumped, and surfaced; keep the app alive rather
        // than letting a single UI-thread exception tear the whole process down.
        e.Handled = true;
    }

    private void OnDomainUnhandledException(object sender, System.UnhandledExceptionEventArgs e)
    {
        if (e.ExceptionObject is Exception exception)
        {
            HandleFatal(exception, "AppDomain");
        }
    }

    private void OnUnobservedTaskException(object? sender, UnobservedTaskExceptionEventArgs e)
    {
        if (_logger is not null)
        {
            AppLog.UnobservedTaskException(_logger, e.Exception);
        }

        _ = CrashHandler.WriteDump(e.Exception, "TaskScheduler");
        e.SetObserved();
    }

    private void HandleFatal(Exception exception, string source)
    {
        if (_logger is not null)
        {
            AppLog.UnhandledException(_logger, source, exception);
        }

        string? dumpPath = CrashHandler.WriteDump(exception, source);
        if (_logger is not null && dumpPath is not null)
        {
            AppLog.CrashDumpWritten(_logger, dumpPath);
        }

        TryShowCrashDialog();
    }

    private void TryShowCrashDialog()
    {
        if (Services is null || Window?.Content is not FrameworkElement root || DispatcherQueue is null)
        {
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => _ = ShowCrashDialogAsync(root));
    }

    private async Task ShowCrashDialogAsync(FrameworkElement root)
    {
        try
        {
            IUiStrings strings = Services.GetRequiredService<IUiStrings>();
            var dialog = new ContentDialog
            {
                XamlRoot = root.XamlRoot,
                Title = strings.Get("CrashTitle"),
                Content = strings.Get("CrashBody"),
                PrimaryButtonText = strings.Get("CrashOpenLogs"),
                SecondaryButtonText = strings.Get("CrashRestart"),
                CloseButtonText = strings.Get("CrashClose"),
                DefaultButton = ContentDialogButton.Close,
            };

            ContentDialogResult result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Primary)
            {
                if (!ShellActions.OpenFolder(AppPaths.LogsDirectory) && _logger is not null)
                {
                    AppLog.OpenLogsFailed(_logger);
                }
            }
            else if (result == ContentDialogResult.Secondary)
            {
                _ = Microsoft.Windows.AppLifecycle.AppInstance.Restart(string.Empty);
            }
        }
        catch (Exception ex)
        {
            if (_logger is not null)
            {
                AppLog.UnhandledException(_logger, "CrashDialog", ex);
            }
        }
    }

    private void OnWindowClosed(object sender, WindowEventArgs args) => ExitApp();

    private void ExitApp()
    {
        if (_exiting)
        {
            return;
        }

        _exiting = true;

        // The container owns every singleton (including the native capture device), so
        // disposing it disposes them all.
        (Services as IDisposable)?.Dispose();

        Exit();
    }
}
