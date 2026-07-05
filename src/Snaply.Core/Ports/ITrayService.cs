namespace Snaply.Core.Ports;

/// <summary>
/// The system-tray presence and its menu. Implemented in the Platform layer over
/// H.NotifyIcon; the app normally lives here rather than in a visible window.
/// </summary>
public interface ITrayService : IDisposable
{
    /// <summary>Raised when the user picks a capture action from the tray menu.</summary>
    event EventHandler<HotkeyAction>? CaptureRequested;

    /// <summary>Raised when the user picks Exit.</summary>
    event EventHandler? ExitRequested;

    /// <summary>Show the tray icon with the supplied localized menu captions.</summary>
    /// <param name="labels">The localized tray menu items and icon tooltip.</param>
    void Show(TrayMenuLabels labels);
}
