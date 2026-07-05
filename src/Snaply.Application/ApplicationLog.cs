using System.Diagnostics.CodeAnalysis;
using Microsoft.Extensions.Logging;

namespace Snaply.Application;

/// <summary>
/// High-performance, source-generated log events for the Application layer. Using
/// <c>[LoggerMessage]</c> keeps allocations down and satisfies the CA1848/CA2254
/// analyzers under this repo's strict settings. These events cover the shared
/// use-case infrastructure (settings persistence) used by both the app and the CLI.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Source-generated logging declarations; no unit-testable body.")]
internal static partial class ApplicationLog
{
    [LoggerMessage(EventId = 3000, Level = LogLevel.Warning, Message = "Reading settings failed; falling back to defaults")]
    public static partial void SettingsReadFailed(ILogger logger, Exception exception);

    [LoggerMessage(EventId = 3001, Level = LogLevel.Warning, Message = "Writing settings failed; the change was not persisted")]
    public static partial void SettingsWriteFailed(ILogger logger, Exception exception);
}
