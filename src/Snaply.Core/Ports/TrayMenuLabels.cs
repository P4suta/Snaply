namespace Snaply.Core.Ports;

/// <summary>
/// Localized captions for the system-tray menu. Supplied by the App layer (which owns
/// the localized resources) and passed to <see cref="ITrayService.Show(TrayMenuLabels)"/>,
/// so the Platform adapter renders the tray without any knowledge of localization.
/// </summary>
/// <param name="CaptureRegion">Caption for the "capture a region" menu item.</param>
/// <param name="CaptureFullScreen">Caption for the "capture the full screen" menu item.</param>
/// <param name="Exit">Caption for the "exit the app" menu item.</param>
/// <param name="ToolTip">Hover tooltip shown on the tray icon.</param>
public sealed record TrayMenuLabels(
    string CaptureRegion,
    string CaptureFullScreen,
    string Exit,
    string ToolTip);
