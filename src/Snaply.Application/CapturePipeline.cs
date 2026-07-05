using System.Security.Cryptography;
using Snaply.Core;
using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Application;

/// <summary>
/// Orchestrates the capture -> beautify flow and remembers the last raw capture so
/// live beautify tweaks can re-render without re-grabbing the screen. Talks only to
/// Core ports and Core models; it holds no WinUI / platform types, so the ViewModel
/// (and the CLI / MCP hosts) above it stay platform-free too.
/// </summary>
public sealed class CapturePipeline
{
    private readonly IScreenCaptureService _capture;
    private readonly IBeautifyRenderer _renderer;

    // The most recent unbeautified capture; re-rendered whenever the spec changes.
    private CapturedImage? _lastRaw;

    // A fresh salt per capture nudges the auto gradient so repeats aren't identical; it is held
    // for the capture's lifetime so background/shadow/aspect re-renders don't reshuffle the colour.
    private uint _backgroundSalt;

    /// <summary>Wires the pipeline to a capture service and a beautify renderer.</summary>
    /// <param name="capture">The screen capture service.</param>
    /// <param name="renderer">The beautify renderer.</param>
    public CapturePipeline(IScreenCaptureService capture, IBeautifyRenderer renderer)
    {
        _capture = capture;
        _renderer = renderer;
    }

    /// <summary>True once at least one capture has succeeded (re-render is possible).</summary>
    public bool HasCapture => _lastRaw is not null;

    /// <summary>Capture the primary monitor, then beautify with <paramref name="spec"/>.</summary>
    /// <param name="spec">How the capture should be beautified.</param>
    /// <param name="cancellationToken">Cancels the capture and render.</param>
    /// <returns>The beautified capture, or a failure.</returns>
    public async Task<Result<CapturedImage>> CaptureFullScreenAsync(BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        Result<CapturedImage> capture = await _capture.CaptureMonitorAsync(0, cancellationToken).ConfigureAwait(true);
        return await BeautifyCaptureAsync(capture, spec, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Capture a specific monitor by index, then beautify with <paramref name="spec"/>.</summary>
    /// <param name="monitorIndex">Zero-based monitor index (0 == primary).</param>
    /// <param name="spec">How the capture should be beautified.</param>
    /// <param name="cancellationToken">Cancels the capture and render.</param>
    /// <returns>The beautified capture, or a failure.</returns>
    public async Task<Result<CapturedImage>> CaptureMonitorAsync(int monitorIndex, BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        Result<CapturedImage> capture = await _capture.CaptureMonitorAsync(monitorIndex, cancellationToken).ConfigureAwait(true);
        return await BeautifyCaptureAsync(capture, spec, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Capture a physical-pixel region of the virtual desktop, then beautify it.</summary>
    /// <param name="region">The region to capture, in physical pixels.</param>
    /// <param name="spec">How the capture should be beautified.</param>
    /// <param name="cancellationToken">Cancels the capture and render.</param>
    /// <returns>The beautified capture, or a failure.</returns>
    public async Task<Result<CapturedImage>> CaptureRegionAsync(PhysicalRect region, BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        Result<CapturedImage> capture = await _capture.CaptureRegionAsync(region, cancellationToken).ConfigureAwait(true);
        return await BeautifyCaptureAsync(capture, spec, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>Capture a single top-level window, then beautify it.</summary>
    /// <param name="windowHandle">The native window handle (HWND) to capture.</param>
    /// <param name="spec">How the capture should be beautified.</param>
    /// <param name="cancellationToken">Cancels the capture and render.</param>
    /// <returns>The beautified capture, or a failure.</returns>
    public async Task<Result<CapturedImage>> CaptureWindowAsync(nint windowHandle, BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        Result<CapturedImage> capture = await _capture.CaptureWindowAsync(windowHandle, cancellationToken).ConfigureAwait(true);
        return await BeautifyCaptureAsync(capture, spec, cancellationToken).ConfigureAwait(true);
    }

    /// <summary>
    /// Beautify an externally supplied raw image (e.g. a PNG loaded from disk by the CLI's
    /// <c>beautify</c> verb) with <paramref name="spec"/>. Unlike the capture methods this does
    /// not touch the screen and does not become the "last raw" capture for live re-render.
    /// </summary>
    /// <param name="raw">The raw (unbeautified) image to beautify.</param>
    /// <param name="spec">How the image should be beautified.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The beautified image, or a failure.</returns>
    public Task<Result<CapturedImage>> BeautifyAsync(CapturedImage raw, BeautifySpec spec, CancellationToken cancellationToken = default) =>
        _renderer.RenderAsync(raw, WithAutoDimensions(spec, raw, NextSalt()), cancellationToken);

    /// <summary>Re-beautify the last raw capture with a new spec (live control edits).</summary>
    /// <param name="spec">The new beautify spec to apply.</param>
    /// <param name="cancellationToken">Cancels the render.</param>
    /// <returns>The re-beautified capture, or a failure if nothing was captured yet.</returns>
    public Task<Result<CapturedImage>> RerenderAsync(BeautifySpec spec, CancellationToken cancellationToken = default)
    {
        if (_lastRaw is null)
        {
            return Task.FromResult(Result<CapturedImage>.Fail(ErrorCodes.PipelineNoCapture, "Nothing has been captured yet."));
        }

        return _renderer.RenderAsync(_lastRaw, WithAutoDimensions(spec, _lastRaw, _backgroundSalt), cancellationToken);
    }

    // Padding and corner radius are chosen automatically from the raw capture size, and an
    // Auto background is resolved to an image-derived gradient, for an aesthetic result the
    // UI no longer has to spell out. Deriving from the raw capture (not the beautified output)
    // means the same capture always yields the same values, so re-renders triggered by
    // background/shadow/aspect edits stay consistent. The renderer never sees Background.Auto —
    // it is always resolved here first.
    private static BeautifySpec WithAutoDimensions(BeautifySpec spec, CapturedImage raw, uint salt)
    {
        // Only auto-derive the dimensions the caller left on "auto" (the default). An explicit
        // Padding / CornerRadius (e.g. from the CLI --padding / MCP padding argument) is honoured.
        if (spec.AutoPadding)
        {
            spec = spec with { Padding = BeautifyDefaults.SuggestPadding(raw.Size) };
        }

        if (spec.AutoCornerRadius)
        {
            spec = spec with { CornerRadius = BeautifyDefaults.SuggestCornerRadius(raw.Size) };
        }

        if (spec.Background is Background.Auto)
        {
            spec = spec with { Background = BeautifyDefaults.SuggestBackground(raw, salt) };
        }

        return spec;
    }

    private async Task<Result<CapturedImage>> BeautifyCaptureAsync(Result<CapturedImage> capture, BeautifySpec spec, CancellationToken cancellationToken)
    {
        if (capture.IsFailure)
        {
            return capture;
        }

        // Render against locals, not the shared fields: this pipeline is a DI singleton, so under
        // the concurrent MCP HTTP transport two captures can race on _lastRaw/_backgroundSalt. The
        // fields are still updated for the WinUI live-rerender (which is single-threaded), but this
        // render always uses its own raw image + salt so a concurrent capture can't swap them out.
        CapturedImage raw = capture.Value;
        uint salt = NextSalt(); // new capture -> a new random gradient variation
        _lastRaw = raw;
        _backgroundSalt = salt;
        return await _renderer.RenderAsync(raw, WithAutoDimensions(spec, raw, salt), cancellationToken).ConfigureAwait(true);
    }

    // Cryptographically-strong RNG (no CA5394 suppression needed): fill 4 bytes and read a uint.
    private static uint NextSalt()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }
}
