using Microsoft.Extensions.Logging;

namespace Snaply.Platform;

/// <summary>
/// High-performance, source-generated log events for the Platform adapters. Using
/// <c>[LoggerMessage]</c> keeps allocations down and satisfies the CA1848/CA2254 analyzers
/// (which forbid ad-hoc logging templates under this repo's strict settings).
/// </summary>
internal static partial class PlatformLog
{
    [LoggerMessage(EventId = 1000, Level = LogLevel.Error, Message = "Capture failed ({Code}): {Reason}")]
    public static partial void CaptureFailed(ILogger logger, string code, string reason, Exception exception);

    [LoggerMessage(EventId = 1001, Level = LogLevel.Warning, Message = "Beautify background image load failed for '{Path}'; falling back to a solid fill")]
    public static partial void BeautifyBackgroundLoadFailed(ILogger logger, string path, Exception exception);

    [LoggerMessage(EventId = 1002, Level = LogLevel.Error, Message = "Beautify render failed: {Reason}")]
    public static partial void BeautifyRenderFailed(ILogger logger, string reason, Exception exception);

    [LoggerMessage(EventId = 1003, Level = LogLevel.Debug, Message = "Borderless capture consent unavailable; keeping the capture border")]
    public static partial void BorderlessConsentUnavailable(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 1004, Level = LogLevel.Error, Message = "Export failed ({Code}): {Reason}")]
    public static partial void ExportFailed(ILogger logger, string code, string reason, Exception exception);
}
