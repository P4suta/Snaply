using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Application;

/// <summary>
/// How the caller wants to identify a window to capture. A concrete <see cref="Handle"/> wins
/// outright; otherwise <see cref="Active"/> targets the foreground window; otherwise the
/// <see cref="TitleContains"/> / <see cref="ProcessName"/> filters are applied together (AND).
/// With nothing set the foreground window is used, so "capture the window" always means
/// "capture what's in front".
/// </summary>
/// <param name="Handle">An explicit HWND (from list_windows), or null.</param>
/// <param name="TitleContains">Match windows whose title contains this text (case-insensitive), or null.</param>
/// <param name="ProcessName">Match windows whose process name equals this (case-insensitive, extension optional), or null.</param>
/// <param name="Active">Target the current foreground window.</param>
public sealed record WindowQuery(
    nint? Handle = null,
    string? TitleContains = null,
    string? ProcessName = null,
    bool Active = false);

/// <summary>The outcome of resolving a <see cref="WindowQuery"/> to a single window.</summary>
public abstract record WindowResolution
{
    private WindowResolution()
    {
    }

    /// <summary>Exactly one window matched.</summary>
    /// <param name="Window">The resolved window.</param>
    public sealed record Resolved(WindowInfo Window) : WindowResolution;

    /// <summary>
    /// More than one window matched a title/process selector. The caller must pick a specific
    /// <see cref="WindowInfo.Handle"/> rather than have one chosen silently.
    /// </summary>
    /// <param name="Candidates">The matching windows, front-to-back.</param>
    public sealed record Ambiguous(IReadOnlyList<WindowInfo> Candidates) : WindowResolution;

    /// <summary>No window matched, or the selector was invalid.</summary>
    /// <param name="Error">Why resolution failed.</param>
    public sealed record NotFound(Error Error) : WindowResolution;
}

/// <summary>
/// Resolves a <see cref="WindowQuery"/> to a single window, and computes the composite region of
/// a window plus its popups/dialogs for "capture window with popups". Shared by the CLI and the
/// MCP server so both target windows identically — a handle is trusted, a title/process selector
/// that matches several windows is reported as <see cref="WindowResolution.Ambiguous"/> (never
/// silently narrowed to the first hit), and an empty selector falls back to the foreground window.
/// </summary>
public sealed class WindowResolver
{
    private readonly IWindowEnumerationService _windows;
    private readonly IMonitorEnumerationService _monitors;

    /// <summary>Creates the resolver over the window and monitor enumeration ports.</summary>
    /// <param name="windows">The window enumerator.</param>
    /// <param name="monitors">The monitor enumerator (used to clamp composite regions).</param>
    public WindowResolver(IWindowEnumerationService windows, IMonitorEnumerationService monitors)
    {
        _windows = windows;
        _monitors = monitors;
    }

    /// <summary>Resolves <paramref name="query"/> to a single window (or an ambiguity / failure).</summary>
    /// <param name="query">How to identify the window.</param>
    /// <returns>The resolution: resolved, ambiguous, or not found.</returns>
    public WindowResolution Resolve(WindowQuery query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (query.Handle is { } handle)
        {
            if (handle == 0)
            {
                return new WindowResolution.NotFound(new Error(ErrorCodes.InputInvalid, "Window handle must be non-zero."));
            }

            // Trust the caller's handle; enrich from the enumeration if it happens to be listed.
            WindowInfo? listed = _windows.EnumerateTopLevelWindows().FirstOrDefault(w => w.Handle == handle);
            return new WindowResolution.Resolved(listed ?? WindowInfo.FromHandle(handle));
        }

        bool hasTitle = !string.IsNullOrWhiteSpace(query.TitleContains);
        bool hasProcess = !string.IsNullOrWhiteSpace(query.ProcessName);

        // No selector, or an explicit --active: capture whatever is in front.
        if (query.Active || (!hasTitle && !hasProcess))
        {
            WindowInfo? foreground = _windows.GetForegroundWindow();
            return foreground is null
                ? new WindowResolution.NotFound(new Error(ErrorCodes.CaptureWindow, "There is no foreground window to capture."))
                : new WindowResolution.Resolved(foreground);
        }

        string wanted = query.ProcessName?.Trim() ?? string.Empty;
        List<WindowInfo> matches = _windows.EnumerateTopLevelWindows()
            .Where(w => (!hasTitle || w.Title.Contains(query.TitleContains!, StringComparison.OrdinalIgnoreCase))
                     && (!hasProcess || ProcessMatches(w.ProcessName, wanted)))
            .ToList();

        return matches.Count switch
        {
            0 => new WindowResolution.NotFound(new Error(ErrorCodes.CaptureWindow, $"No window matched {Describe(query)}.")),
            1 => new WindowResolution.Resolved(matches[0]),
            _ => new WindowResolution.Ambiguous(matches),
        };
    }

    /// <summary>
    /// The composite region covering the window identified by <paramref name="target"/> and all of
    /// its popups/dialogs, clamped to the monitor that will capture it. Feed this to a region
    /// capture to grab a window together with its file picker / dialog / menu as one image.
    /// </summary>
    /// <param name="target">The owner window (or one of its popups).</param>
    /// <returns>The clamped composite region, or a failure when nothing capturable was found.</returns>
    public Result<PhysicalRect> ResolveGroupRegion(nint target)
    {
        IReadOnlyList<WindowInfo> group = _windows.EnumerateRelatedWindows(target);
        PhysicalRect union = PhysicalRect.Bounds(group.Select(w => w.Bounds));
        if (union.IsEmpty)
        {
            return Result<PhysicalRect>.Fail(ErrorCodes.CaptureWindow, "The window and its popups have no capturable area (is it minimized?).");
        }

        // The region capture samples a single monitor and crops; clamp the union to the monitor
        // that owns its centre so popups spilling past a screen edge don't drag in dead pixels.
        PhysicalRect clamped = ClampToMonitor(union);
        return Result<PhysicalRect>.Ok(clamped.IsEmpty ? union : clamped);
    }

    private PhysicalRect ClampToMonitor(PhysicalRect region)
    {
        int centreX = region.X + (region.Width / 2);
        int centreY = region.Y + (region.Height / 2);
        IReadOnlyList<MonitorInfo> monitors = _monitors.EnumerateMonitors();

        MonitorInfo? host = monitors.FirstOrDefault(m => m.Bounds.Contains(centreX, centreY))
            ?? monitors.FirstOrDefault(m => !m.Bounds.Intersect(region).IsEmpty);

        return host is null ? region : region.Intersect(host.Bounds);
    }

    // Match "chrome", "chrome.exe" or "Chrome" against an enumerated process name (no extension).
    private static bool ProcessMatches(string actual, string wanted)
    {
        if (actual.Length == 0)
        {
            return false;
        }

        ReadOnlySpan<char> trimmed = wanted.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
            ? wanted.AsSpan(0, wanted.Length - 4)
            : wanted;
        return trimmed.Equals(actual, StringComparison.OrdinalIgnoreCase);
    }

    private static string Describe(WindowQuery query)
    {
        if (!string.IsNullOrWhiteSpace(query.TitleContains) && !string.IsNullOrWhiteSpace(query.ProcessName))
        {
            return $"title ~ '{query.TitleContains}' and process '{query.ProcessName}'";
        }

        return !string.IsNullOrWhiteSpace(query.ProcessName)
            ? $"process '{query.ProcessName}'"
            : $"title ~ '{query.TitleContains}'";
    }
}
