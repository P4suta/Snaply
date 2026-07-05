using Microsoft.Extensions.Logging;

namespace Snaply.Diagnostics;

/// <summary>
/// High-performance, source-generated log events for the App layer. Using <c>[LoggerMessage]</c>
/// keeps allocations down and satisfies the CA1848/CA2254 analyzers under this repo's strict settings.
/// </summary>
internal static partial class AppLog
{
    [LoggerMessage(EventId = 2000, Level = LogLevel.Information, Message = "Snaply started (version {Version}, language {Language})")]
    public static partial void Started(ILogger logger, string version, string language);

    [LoggerMessage(EventId = 2001, Level = LogLevel.Warning, Message = "User action failed ({Code}): {Reason}")]
    public static partial void UserActionFailed(ILogger logger, string code, string reason, Exception? exception);

    [LoggerMessage(EventId = 2002, Level = LogLevel.Critical, Message = "Unhandled exception from {Source}")]
    public static partial void UnhandledException(ILogger logger, string source, Exception exception);

    [LoggerMessage(EventId = 2003, Level = LogLevel.Error, Message = "Unobserved task exception")]
    public static partial void UnobservedTaskException(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2006, Level = LogLevel.Warning, Message = "Global hotkey unavailable for '{Chord}': {Reason}")]
    public static partial void HotkeyUnavailable(ILogger logger, string chord, string reason);

    [LoggerMessage(EventId = 2007, Level = LogLevel.Information, Message = "Crash dump written to {Path}")]
    public static partial void CrashDumpWritten(ILogger logger, string path);

    [LoggerMessage(EventId = 2008, Level = LogLevel.Error, Message = "Failed to write crash dump")]
    public static partial void CrashDumpFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 2009, Level = LogLevel.Error, Message = "Capture routing failed for {Action}")]
    public static partial void CaptureRoutingFailed(ILogger logger, string action, Exception exception);

    [LoggerMessage(EventId = 2010, Level = LogLevel.Warning, Message = "Failed to open the logs folder")]
    public static partial void OpenLogsFailed(ILogger logger);

    [LoggerMessage(EventId = 2011, Level = LogLevel.Error, Message = "Background operation '{Operation}' failed")]
    public static partial void BackgroundOperationFailed(ILogger logger, string operation, Exception exception);
}
