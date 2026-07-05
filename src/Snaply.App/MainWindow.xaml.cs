using System.Runtime.InteropServices;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Windows.Graphics;

namespace Snaply;

/// <summary>
/// The application window. This hosts a Frame that displays pages. Add your
/// UI and logic to MainPage.xaml / MainPage.xaml.cs instead of here so you
/// can use Page features such as navigation events and the Loaded lifecycle.
/// </summary>
public sealed partial class MainWindow : Window
{
    [DllImport("user32.dll")]
    private static extern uint GetDpiForWindow(IntPtr hWnd);

    /// <summary>Sets up the title bar, icon, initial size and navigates to the main page.</summary>
    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");

        SizeToEditor();

        // Navigate the root frame to the main page on startup.
        RootFrame.Navigate(typeof(MainPage));
    }

    /// <summary>
    /// Sizes the window for an editor layout. AppWindow.Resize takes physical
    /// pixels, so the DIP dimensions (from the design tokens) are scaled by the
    /// monitor DPI. Read via GetDpiForWindow because XamlRoot.RasterizationScale
    /// is null in the constructor.
    /// </summary>
    private void SizeToEditor()
    {
        double widthDip = ReadTokenDouble("WindowWidth", 1200);
        double heightDip = ReadTokenDouble("WindowHeight", 800);

        IntPtr hwnd = Win32Interop.GetWindowFromWindowId(AppWindow.Id);
        double scale = GetDpiForWindow(hwnd) / 96.0;

        AppWindow.Resize(new SizeInt32((int)(widthDip * scale), (int)(heightDip * scale)));
    }

    private static double ReadTokenDouble(string key, double fallback) =>
        Application.Current.Resources.TryGetValue(key, out object? value) && value is double d
            ? d
            : fallback;
}
