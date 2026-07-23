using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Snaply;

public sealed partial class MainWindow : Window
{
    private const uint WdaExcludeFromCapture = 0x00000011;

    internal MainWindow(MainViewModel viewModel, ScreenCaptureService capture)
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
        capture.SetAppWindow(this, exclusionEnabled);
        ContentHost.Children.Add(new MainPage(viewModel));
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
