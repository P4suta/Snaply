using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;

namespace Snaply.Application;

/// <summary>
/// Loads and saves the shared <see cref="AppSettings"/> document at
/// <c>%LOCALAPPDATA%\Snaply\settings.json</c> (this is an unpackaged app, so we use
/// the file system rather than <c>ApplicationData</c>). Saves are read-modify-write so
/// each service's field survives; persistence is best-effort and never throws to callers.
/// </summary>
[ExcludeFromCodeCoverage(Justification = "Filesystem-backed settings I/O (fixed %LOCALAPPDATA% path); infrastructure, not unit-tested.")]
public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly string _settingsPath = AppPaths.SettingsFile;

    /// <summary>
    /// Optional logger for best-effort diagnostics. Assigned by the composition root once the
    /// provider is built (this store is created before logging, to read the log-level preference).
    /// </summary>
    public ILogger? Logger { get; set; }

    /// <summary>Reads the persisted settings, falling back to defaults if absent or unreadable.</summary>
    /// <returns>The loaded settings (never null).</returns>
    public AppSettings Load()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                string json = File.ReadAllText(_settingsPath);
                AppSettings? settings = JsonSerializer.Deserialize<AppSettings>(json, SerializerOptions);
                if (settings is not null)
                {
                    return settings;
                }
            }
        }
        catch (Exception ex) when (ex is IOException or JsonException or UnauthorizedAccessException)
        {
            // Corrupt or unreadable settings: fall back to defaults, but record why.
            if (Logger is not null)
            {
                ApplicationLog.SettingsReadFailed(Logger, ex);
            }
        }

        return new AppSettings();
    }

    /// <summary>
    /// Applies <paramref name="mutate"/> to the current settings and persists the result.
    /// Read-modify-write, so fields owned by other services are preserved. Best-effort:
    /// a failed write is swallowed rather than crashing the app.
    /// </summary>
    /// <param name="mutate">An action that updates the settings in place.</param>
    public void Update(Action<AppSettings> mutate)
    {
        ArgumentNullException.ThrowIfNull(mutate);

        AppSettings settings = Load();
        mutate(settings);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
            string json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Persistence is best-effort; a failed write must not crash the app, but record it.
            if (Logger is not null)
            {
                ApplicationLog.SettingsWriteFailed(Logger, ex);
            }
        }
    }
}
