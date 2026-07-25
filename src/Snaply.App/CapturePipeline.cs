using Microsoft.UI.Xaml;

namespace Snaply;

internal interface ICapturePipeline : IDisposable
{
    public Task<RenderedImage?> CaptureAsync(CaptureMode mode, CancellationToken cancellationToken);
}

internal sealed class CapturePipeline : ICapturePipeline
{
    private readonly ScreenCaptureService _capture;

    internal CapturePipeline(Window appWindow, bool captureExclusionEnabled)
    {
        _capture = new ScreenCaptureService(appWindow, captureExclusionEnabled);
    }

    public void Dispose() => _capture.Dispose();

    public async Task<RenderedImage?> CaptureAsync(
        CaptureMode mode,
        CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                using CapturedFrame? frame = await _capture.CaptureAsync(mode, cancellationToken);
                return frame is null
                    ? null
                    : await Task.Run(
                        () => BeautifyRenderer.RenderAsync(frame, cancellationToken),
                        cancellationToken);
            }
            catch (Exception exception) when (
                attempt == 0
                && _capture.IsGraphicsDeviceLost(exception))
            {
                _capture.ResetGraphicsDevice();
            }
        }
    }
}
