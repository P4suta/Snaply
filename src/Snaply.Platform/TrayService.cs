using System.Runtime.InteropServices;
using H.NotifyIcon.Core;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// System-tray presence over H.NotifyIcon's core (non-XAML) API. Shows an icon with
/// a context menu (Capture region / Capture full screen / Exit).
/// <para>
/// Threading: <see cref="Show(TrayMenuLabels)"/> creates a message window and should be called on the
/// app's UI (DispatcherQueue) thread; the menu events are raised on that same thread.
/// </para>
/// </summary>
public sealed class TrayService : ITrayService
{
    private const int IdiApplication = 32512;

    private TrayIconWithContextMenu? _trayIcon;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<HotkeyAction>? CaptureRequested;

    /// <inheritdoc/>
    public event EventHandler? ExitRequested;

    /// <inheritdoc/>
    public void Show(TrayMenuLabels labels)
    {
        ArgumentNullException.ThrowIfNull(labels);

        if (_disposed || _trayIcon is not null)
        {
            return;
        }

        var menu = new PopupMenu();
        menu.Items.Add(new PopupMenuItem(labels.CaptureRegion, (_, _) => CaptureRequested?.Invoke(this, HotkeyAction.CaptureRegion)));
        menu.Items.Add(new PopupMenuItem(labels.CaptureFullScreen, (_, _) => CaptureRequested?.Invoke(this, HotkeyAction.CaptureFullScreen)));
        menu.Items.Add(new PopupMenuSeparator());
        menu.Items.Add(new PopupMenuItem(labels.Exit, (_, _) => ExitRequested?.Invoke(this, EventArgs.Empty)));

        _trayIcon = new TrayIconWithContextMenu
        {
            ToolTip = labels.ToolTip,
            Icon = LoadDefaultIcon(),
            ContextMenu = menu,
        };
        _trayIcon.Create();
    }

    private static IntPtr LoadDefaultIcon()
    {
        // A generic application HICON so the tray entry is visible without shipping an asset.
        return LoadIcon(IntPtr.Zero, IdiApplication);
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _trayIcon?.Dispose();
        _trayIcon = null;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr LoadIcon(IntPtr hInstance, int lpIconName);
}
