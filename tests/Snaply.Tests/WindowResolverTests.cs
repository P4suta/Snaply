using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Tests;

/// <summary>
/// Headless tests for the shared <see cref="WindowResolver"/> — the targeting logic the CLI and
/// MCP server both use. Driven by a fake window enumerator so the precedence rules, the
/// "never silently pick the first of several" ambiguity behaviour, and the composite-region math
/// are pinned without a real desktop.
/// </summary>
public class WindowResolverTests
{
    [Fact]
    public void Resolve_ExplicitHandle_WinsAndIsTrustedEvenIfNotListed()
    {
        var resolver = new WindowResolver(new FakeWindows(), new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(Handle: 0x9999));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x9999, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_ZeroHandle_IsInvalid()
    {
        var resolver = new WindowResolver(new FakeWindows(), new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(Handle: 0));

        WindowResolution.NotFound notFound = Assert.IsType<WindowResolution.NotFound>(result);
        Assert.Equal(ErrorCodes.InputInvalid, notFound.Error.Code);
    }

    [Fact]
    public void Resolve_NoSelector_FallsBackToForeground()
    {
        var windows = new FakeWindows { Foreground = Window(0x55, "Active", "app") };
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery());

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x55, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_ActiveWithNoForeground_IsNotFound()
    {
        var resolver = new WindowResolver(new FakeWindows(), new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(Active: true));

        Assert.IsType<WindowResolution.NotFound>(result);
    }

    [Fact]
    public void Resolve_TitleMatchingOne_Resolves()
    {
        var windows = new FakeWindows(
            Window(0x1, "Visual Studio Code", "code"),
            Window(0x2, "Notepad", "notepad"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "code"));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x1, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_TitleMatchingSeveral_IsAmbiguousWithCandidates()
    {
        var windows = new FakeWindows(
            Window(0x1, "Document 1 — Editor", "editor"),
            Window(0x2, "Document 2 — Editor", "editor"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "Editor"));

        WindowResolution.Ambiguous ambiguous = Assert.IsType<WindowResolution.Ambiguous>(result);
        Assert.Equal(2, ambiguous.Candidates.Count);
    }

    [Fact]
    public void Resolve_NoMatch_IsNotFound()
    {
        var windows = new FakeWindows(Window(0x1, "Notepad", "notepad"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "nonexistent"));

        WindowResolution.NotFound notFound = Assert.IsType<WindowResolution.NotFound>(result);
        Assert.Equal(ErrorCodes.CaptureWindow, notFound.Error.Code);
    }

    [Theory]
    [InlineData("chrome")]
    [InlineData("chrome.exe")]
    [InlineData("Chrome")]
    public void Resolve_ByProcessName_MatchesWithOrWithoutExtension(string query)
    {
        var windows = new FakeWindows(
            Window(0x1, "Gmail", "chrome"),
            Window(0x2, "Notepad", "notepad"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(ProcessName: query));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x1, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_TitleAndProcessCombined_NarrowsToOne()
    {
        var windows = new FakeWindows(
            Window(0x1, "Inbox", "chrome"),
            Window(0x2, "Inbox", "thunderbird"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "Inbox", ProcessName: "chrome"));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x1, resolved.Window.Handle);
    }

    [Fact]
    public void ResolveGroupRegion_UnionsWindowAndPopups_ClampedToHostMonitor()
    {
        // App window + a file picker overlapping it; the picker spills past the right screen edge.
        WindowInfo app = Window(0x1, "App", "app") with { Bounds = new PhysicalRect(100, 100, 800, 600) };
        WindowInfo picker = Window(0x2, "Open", "app") with { Bounds = new PhysicalRect(700, 400, 700, 500) };
        var windows = new FakeWindows { Related = [app, picker] };
        var monitors = new FakeMonitors(new PhysicalRect(0, 0, 1280, 1024));
        var resolver = new WindowResolver(windows, monitors);

        Result<PhysicalRect> region = resolver.ResolveGroupRegion(0x1);

        Assert.True(region.IsSuccess);

        // Union is (100,100)-(1400,900); clamped to the 1280×1024 monitor → right edge 1280.
        Assert.Equal(new PhysicalRect(100, 100, 1180, 800), region.Value);
    }

    [Fact]
    public void ResolveGroupRegion_NoCapturableArea_Fails()
    {
        var windows = new FakeWindows { Related = [] };
        var resolver = new WindowResolver(windows, new FakeMonitors());

        Result<PhysicalRect> region = resolver.ResolveGroupRegion(0x1);

        Assert.True(region.IsFailure);
        Assert.Equal(ErrorCodes.CaptureWindow, region.Error.Code);
    }

    [Fact]
    public void Resolve_NullQuery_Throws()
    {
        var resolver = new WindowResolver(new FakeWindows(), new FakeMonitors());

        Assert.Throws<ArgumentNullException>(() => resolver.Resolve(null!));
    }

    [Fact]
    public void Resolve_Active_WinsOverTitleAndProcessSelectors()
    {
        var windows = new FakeWindows(Window(0x1, "Editor", "editor"))
        {
            Foreground = Window(0x99, "Foreground", "shell"),
        };
        var resolver = new WindowResolver(windows, new FakeMonitors());

        // --active takes precedence over any title/process filter — the foreground wins outright.
        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "Editor", ProcessName: "editor", Active: true));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x99, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_HandleListedInEnumeration_ReturnsEnrichedWindow()
    {
        var windows = new FakeWindows(Window(0x1, "Visual Studio Code", "code"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(Handle: 0x1));

        // A listed handle is enriched from the enumeration, not returned as a bare FromHandle stub.
        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal("Visual Studio Code", resolved.Window.Title);
        Assert.Equal("code", resolved.Window.ProcessName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Resolve_WhitespaceOnlySelector_FallsBackToForeground(string selector)
    {
        var windows = new FakeWindows(Window(0x1, "Editor", "editor")) { Foreground = Window(0x55, "Active", "app") };
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: selector, ProcessName: selector));

        WindowResolution.Resolved resolved = Assert.IsType<WindowResolution.Resolved>(result);
        Assert.Equal(0x55, resolved.Window.Handle);
    }

    [Fact]
    public void Resolve_TitleAndProcess_OnlyOneMatches_IsNotFound()
    {
        // The two filters combine with AND: a window matching the title but not the process is no match.
        var windows = new FakeWindows(Window(0x1, "Inbox", "thunderbird"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        WindowResolution result = resolver.Resolve(new WindowQuery(TitleContains: "Inbox", ProcessName: "chrome"));

        Assert.IsType<WindowResolution.NotFound>(result);
    }

    [Fact]
    public void Resolve_ByProcess_EmptyEnumeratedProcessName_DoesNotMatch()
    {
        var windows = new FakeWindows(new WindowInfo(0x1, "Untitled", new PhysicalRect(0, 0, 10, 10), ProcessName: string.Empty));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        Assert.IsType<WindowResolution.NotFound>(resolver.Resolve(new WindowQuery(ProcessName: "chrome")));
    }

    [Theory]
    [InlineData("cod")] // prefix is not a full match
    [InlineData("codes")] // superset is not a full match
    public void Resolve_ByProcess_RequiresFullNameNotSubstring(string query)
    {
        var windows = new FakeWindows(Window(0x1, "Editor", "code"));
        var resolver = new WindowResolver(windows, new FakeMonitors());

        Assert.IsType<WindowResolution.NotFound>(resolver.Resolve(new WindowQuery(ProcessName: query)));
    }

    [Fact]
    public void ResolveGroupRegion_CentreInGapBetweenMonitors_ClampsToIntersectingMonitor()
    {
        // Two monitors with a gap; the union's centre falls in the gap (in neither monitor), so the
        // resolver falls back to the monitor the region actually intersects and clamps to it.
        WindowInfo win = Window(0x1, "App", "app") with { Bounds = new PhysicalRect(900, 100, 300, 200) };
        var windows = new FakeWindows { Related = [win] };
        var monitors = new FakeMonitors(new PhysicalRect(0, 0, 1000, 1000), new PhysicalRect(2000, 0, 1000, 1000));
        var resolver = new WindowResolver(windows, monitors);

        Result<PhysicalRect> region = resolver.ResolveGroupRegion(0x1);

        Assert.True(region.IsSuccess);
        Assert.Equal(new PhysicalRect(900, 100, 100, 200), region.Value);
    }

    [Fact]
    public void ResolveGroupRegion_RegionOffEveryMonitor_ReturnsUnclampedUnion()
    {
        WindowInfo win = Window(0x1, "App", "app") with { Bounds = new PhysicalRect(5000, 5000, 300, 200) };
        var windows = new FakeWindows { Related = [win] };
        var monitors = new FakeMonitors(new PhysicalRect(0, 0, 1000, 1000));
        var resolver = new WindowResolver(windows, monitors);

        Result<PhysicalRect> region = resolver.ResolveGroupRegion(0x1);

        // No monitor hosts or intersects the region: it is returned unclamped rather than lost.
        Assert.True(region.IsSuccess);
        Assert.Equal(new PhysicalRect(5000, 5000, 300, 200), region.Value);
    }

    [Fact]
    public void ResolveGroupRegion_UnionsThreeRelatedWindows()
    {
        WindowInfo app = Window(0x1, "App", "app") with { Bounds = new PhysicalRect(100, 100, 200, 200) };
        WindowInfo dialog = Window(0x2, "Dialog", "app") with { Bounds = new PhysicalRect(250, 150, 200, 200) };
        WindowInfo menu = Window(0x3, "Menu", "app") with { Bounds = new PhysicalRect(120, 300, 100, 150) };
        var windows = new FakeWindows { Related = [app, dialog, menu] };
        var monitors = new FakeMonitors(new PhysicalRect(0, 0, 4000, 4000));
        var resolver = new WindowResolver(windows, monitors);

        Result<PhysicalRect> region = resolver.ResolveGroupRegion(0x1);

        Assert.True(region.IsSuccess);
        Assert.Equal(new PhysicalRect(100, 100, 350, 350), region.Value);
    }

    private static WindowInfo Window(nint handle, string title, string process) =>
        new(handle, title, new PhysicalRect(0, 0, 800, 600), ProcessId: (int)handle, ProcessName: process);

    private sealed class FakeWindows : IWindowEnumerationService
    {
        private readonly IReadOnlyList<WindowInfo> _windows;

        public FakeWindows(params WindowInfo[] windows) => _windows = windows;

        public WindowInfo? Foreground { get; init; }

        public IReadOnlyList<WindowInfo> Related { get; init; } = [];

        public IReadOnlyList<WindowInfo> EnumerateTopLevelWindows() => _windows;

        public WindowInfo? GetForegroundWindow() => Foreground;

        public IReadOnlyList<WindowInfo> EnumerateRelatedWindows(nint target) => Related;
    }

    private sealed class FakeMonitors : IMonitorEnumerationService
    {
        private readonly IReadOnlyList<MonitorInfo> _monitors;

        // No bounds -> one large primary monitor; otherwise one monitor per rect (index 0 primary),
        // so a test can exercise the multi-monitor clamp paths.
        public FakeMonitors(params PhysicalRect[] bounds)
        {
            PhysicalRect[] rects = bounds.Length == 0 ? [new PhysicalRect(0, 0, 3840, 2160)] : bounds;
            _monitors = rects.Select((rect, index) => new MonitorInfo(index, rect, Dpi.Default, index == 0)).ToArray();
        }

        public IReadOnlyList<MonitorInfo> EnumerateMonitors() => _monitors;
    }
}
