using Snaply.Application;
using Snaply.Core;
using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Tests;

/// <summary>
/// Headless tests for the shared <see cref="CapturePipeline"/> use case, driven by fake
/// Core ports — no display/GPU needed, the same platform-independence goal that keeps the
/// domain unit-testable. Proves the capture→beautify orchestration and the auto-dimension
/// resolution the CLI, MCP server and WinUI app all rely on.
/// </summary>
public class CapturePipelineTests
{
    [Fact]
    public async Task Rerender_BeforeAnyCapture_FailsWithNoCapture()
    {
        var pipeline = new CapturePipeline(new FakeCapture(), new RecordingRenderer());

        Result<CapturedImage> result = await pipeline.RerenderAsync(BeautifySpec.Default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.PipelineNoCapture, result.Error.Code);
    }

    [Fact]
    public async Task CaptureFullScreen_Success_RendersAndMarksHasCapture()
    {
        CapturedImage raw = SolidImage(1920, 1080);
        var renderer = new RecordingRenderer();
        var pipeline = new CapturePipeline(new FakeCapture(Result<CapturedImage>.Ok(raw)), renderer);

        Result<CapturedImage> result = await pipeline.CaptureFullScreenAsync(BeautifySpec.Default);

        Assert.True(result.IsSuccess);
        Assert.True(pipeline.HasCapture);
        Assert.Same(raw, renderer.LastSource);
    }

    [Fact]
    public async Task Capture_Failure_PropagatesAndSkipsRenderer()
    {
        var renderer = new RecordingRenderer();
        var failure = Result<CapturedImage>.Fail(ErrorCodes.CaptureMonitor, "boom");
        var pipeline = new CapturePipeline(new FakeCapture(failure), renderer);

        Result<CapturedImage> result = await pipeline.CaptureFullScreenAsync(BeautifySpec.Default);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.CaptureMonitor, result.Error.Code);
        Assert.Null(renderer.LastSpec); // renderer never invoked on capture failure
    }

    [Fact]
    public async Task Beautify_AppliesAutoDimensionsAndResolvesAutoBackground()
    {
        CapturedImage raw = SolidImage(2560, 1440);
        var renderer = new RecordingRenderer();
        var pipeline = new CapturePipeline(new FakeCapture(), renderer);

        // Background.Auto should be resolved to a concrete gradient before the renderer sees it,
        // and padding/corner-radius should be the size-derived suggestions, not the spec's literals.
        BeautifySpec spec = BeautifySpec.Default with { Background = new Background.Auto() };
        await pipeline.BeautifyAsync(raw, spec);

        Assert.NotNull(renderer.LastSpec);
        Assert.Equal(BeautifyDefaults.SuggestPadding(raw.Size), renderer.LastSpec!.Padding);
        Assert.Equal(BeautifyDefaults.SuggestCornerRadius(raw.Size), renderer.LastSpec.CornerRadius);
        Assert.IsType<Background.LinearGradient>(renderer.LastSpec.Background);
    }

    [Theory]
    [InlineData("monitor")]
    [InlineData("region")]
    [InlineData("window")]
    public async Task EveryCaptureTarget_RendersAndCachesForRerender(string target)
    {
        CapturedImage raw = SolidImage(1280, 720);
        var renderer = new RecordingRenderer();
        var pipeline = new CapturePipeline(new FakeCapture(Result<CapturedImage>.Ok(raw)), renderer);

        Result<CapturedImage> result = target switch
        {
            "monitor" => await pipeline.CaptureMonitorAsync(1, BeautifySpec.Default),
            "region" => await pipeline.CaptureRegionAsync(new PhysicalRect(0, 0, 640, 480), BeautifySpec.Default),
            _ => await pipeline.CaptureWindowAsync(0x1234, BeautifySpec.Default),
        };

        Assert.True(result.IsSuccess);
        Assert.Same(raw, renderer.LastSource);
        Assert.True(pipeline.HasCapture);
    }

    [Fact]
    public async Task Rerender_AfterCapture_ReusesTheLastRawImage()
    {
        CapturedImage raw = SolidImage(1600, 900);
        var renderer = new RecordingRenderer();
        var pipeline = new CapturePipeline(new FakeCapture(Result<CapturedImage>.Ok(raw)), renderer);

        await pipeline.CaptureFullScreenAsync(BeautifySpec.Default);
        Result<CapturedImage> rerendered = await pipeline.RerenderAsync(BeautifySpec.Default with { Aspect = AspectPreset.Wide });

        Assert.True(rerendered.IsSuccess);
        Assert.Same(raw, renderer.LastSource); // re-rendered from the cached capture, not a new grab
        Assert.Equal(AspectPreset.Wide, renderer.LastSpec!.Aspect);
    }

    [Fact]
    public async Task Beautify_HonoursExplicitPaddingAndCornerRadius()
    {
        CapturedImage raw = SolidImage(2560, 1440);
        var renderer = new RecordingRenderer();
        var pipeline = new CapturePipeline(new FakeCapture(), renderer);

        // A caller that turned off auto (the CLI --padding / MCP padding arg) must have its
        // explicit values reach the renderer, not the size-derived suggestions.
        BeautifySpec spec = BeautifySpec.Default with
        {
            Padding = Padding.Uniform(200),
            AutoPadding = false,
            CornerRadius = 50,
            AutoCornerRadius = false,
        };
        await pipeline.BeautifyAsync(raw, spec);

        Assert.NotNull(renderer.LastSpec);
        Assert.Equal(Padding.Uniform(200), renderer.LastSpec!.Padding);
        Assert.Equal(50, renderer.LastSpec.CornerRadius);
    }

    private static CapturedImage SolidImage(int width, int height)
    {
        byte[] bgra = new byte[width * height * 4];
        Array.Fill(bgra, (byte)128);
        return new CapturedImage(new PhysicalSize(width, height), bgra, new Dpi(144));
    }

    private sealed class FakeCapture : IScreenCaptureService
    {
        private readonly Result<CapturedImage> _result;

        public FakeCapture(Result<CapturedImage>? result = null) =>
            _result = result ?? Result<CapturedImage>.Ok(new CapturedImage(new PhysicalSize(8, 8), new byte[8 * 8 * 4], Dpi.Default));

        public Task<Result<CapturedImage>> CaptureRegionAsync(PhysicalRect region, CancellationToken cancellationToken = default) => Task.FromResult(_result);

        public Task<Result<CapturedImage>> CaptureMonitorAsync(int monitorIndex, CancellationToken cancellationToken = default) => Task.FromResult(_result);

        public Task<Result<CapturedImage>> CaptureWindowAsync(nint windowHandle, CancellationToken cancellationToken = default) => Task.FromResult(_result);
    }

    private sealed class RecordingRenderer : IBeautifyRenderer
    {
        public BeautifySpec? LastSpec { get; private set; }

        public CapturedImage? LastSource { get; private set; }

        public Task<Result<CapturedImage>> RenderAsync(CapturedImage source, BeautifySpec spec, CancellationToken cancellationToken = default)
        {
            LastSource = source;
            LastSpec = spec;
            return Task.FromResult(Result<CapturedImage>.Ok(source));
        }
    }
}
