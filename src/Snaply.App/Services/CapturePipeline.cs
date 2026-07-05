using System.Security.Cryptography;
using Snaply.Core;
using Snaply.Core.Beautify;
using Snaply.Core.Geometry;
using Snaply.Core.Models;
using Snaply.Core.Ports;

namespace Snaply.Services;

/// <summary>
/// Orchestrates the capture -> beautify flow and remembers the last raw capture so
/// live beautify tweaks can re-render without re-grabbing the screen. Talks only to
/// Core ports and Core models; it holds no WinUI / platform types, so the ViewModel
/// above it stays platform-free too.
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

        return _renderer.RenderAsync(_lastRaw, WithAutoDimensions(spec, _lastRaw), cancellationToken);
    }

    // Padding and corner radius are chosen automatically from the raw capture size, and an
    // Auto background is resolved to an image-derived gradient, for an aesthetic result the
    // UI no longer has to spell out. Deriving from the raw capture (not the beautified output)
    // means the same capture always yields the same values, so re-renders triggered by
    // background/shadow/aspect edits stay consistent. The renderer never sees Background.Auto —
    // it is always resolved here first.
    private BeautifySpec WithAutoDimensions(BeautifySpec spec, CapturedImage raw)
    {
        spec = spec with
        {
            Padding = BeautifyDefaults.SuggestPadding(raw.Size),
            CornerRadius = BeautifyDefaults.SuggestCornerRadius(raw.Size),
        };

        if (spec.Background is Background.Auto)
        {
            spec = spec with { Background = BeautifyDefaults.SuggestBackground(raw, _backgroundSalt) };
        }

        return spec;
    }

    private async Task<Result<CapturedImage>> BeautifyCaptureAsync(Result<CapturedImage> capture, BeautifySpec spec, CancellationToken cancellationToken)
    {
        if (capture.IsFailure)
        {
            return capture;
        }

        _lastRaw = capture.Value;
        _backgroundSalt = NextSalt(); // new capture -> a new random gradient variation
        return await _renderer.RenderAsync(_lastRaw, WithAutoDimensions(spec, _lastRaw), cancellationToken).ConfigureAwait(true);
    }

    // Cryptographically-strong RNG (no CA5394 suppression needed): fill 4 bytes and read a uint.
    private static uint NextSalt()
    {
        Span<byte> bytes = stackalloc byte[sizeof(uint)];
        RandomNumberGenerator.Fill(bytes);
        return BitConverter.ToUInt32(bytes);
    }
}
