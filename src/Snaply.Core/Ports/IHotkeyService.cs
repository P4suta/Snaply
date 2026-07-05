namespace Snaply.Core.Ports;

/// <summary>A registered global hotkey action.</summary>
public enum HotkeyAction
{
    /// <summary>Capture a user-selected region.</summary>
    CaptureRegion,

    /// <summary>Capture the full screen.</summary>
    CaptureFullScreen,
}

/// <summary>
/// System-wide hotkeys that trigger a capture even when the app has no focus.
/// Implemented in the Platform layer over the Win32 RegisterHotKey message loop.
/// </summary>
public interface IHotkeyService : IDisposable
{
    /// <summary>Raised on the UI thread when a registered hotkey is pressed.</summary>
    event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Register a chord (e.g. PrintScreen) for an action.</summary>
    /// <param name="action">The action to trigger.</param>
    /// <param name="chord">The key chord, e.g. <c>PrintScreen</c> or <c>Ctrl+Shift+4</c>.</param>
    /// <returns>Success, or a failure if the chord could not be registered.</returns>
    Result Register(HotkeyAction action, string chord);
}
