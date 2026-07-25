using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Snaply;

internal sealed partial class MainWindow : Window, IDisposable
{
    private const uint WdaExcludeFromCapture = 0x00000011;
    private readonly MainPage _page;
    private readonly MainViewModel _viewModel;
    private bool _disposed;

    internal MainWindow()
    {
        InitializeComponent();
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        AppWindow.SetIcon("Assets/AppIcon.ico");

        nint handle = WinRT.Interop.WindowNative.GetWindowHandle(this);
        // GetDpiForWindow returns 0 on failure; fall back to 96 (100%) so the window is never sized to 0×0.
        uint dpi = GetDpiForWindow(handle);
        double scale = (dpi == 0 ? 96u : dpi) / 96d;
        AppWindow.Resize(new SizeInt32(
            checked((int)Math.Round(1100 * scale)),
            checked((int)Math.Round(720 * scale))));
        bool exclusionEnabled = SetWindowDisplayAffinity(handle, WdaExcludeFromCapture)
            && GetWindowDisplayAffinity(handle, out uint affinity)
            && affinity == WdaExcludeFromCapture;

        CapturePipeline? pipeline = null;
        MainViewModel? viewModel = null;
        MainPage? page = null;
        try
        {
            pipeline = new CapturePipeline(this, exclusionEnabled);
            viewModel = new MainViewModel(pipeline, new ImageExportService());
            pipeline = null;
            page = new MainPage(viewModel);
            ContentHost.Children.Add(page);
            Closed += OnClosed;

            _viewModel = viewModel;
            _page = page;
            viewModel = null;
            page = null;
        }
        finally
        {
            page?.Dispose();
            viewModel?.Dispose();
            pipeline?.Dispose();
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _page.Dispose();
        _viewModel.Dispose();
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        Closed -= OnClosed;
        Dispose();
    }

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowDisplayAffinity(nint window, uint affinity);

    [LibraryImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetWindowDisplayAffinity(nint window, out uint affinity);

    [LibraryImport("user32.dll")]
    private static partial uint GetDpiForWindow(nint window);
}
