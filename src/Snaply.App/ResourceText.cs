using Microsoft.Windows.ApplicationModel.Resources;
using Serilog;

namespace Snaply;

internal static class ResourceText
{
    private static readonly ResourceLoader Loader = new();

    // MRT Core returns an empty string for a missing key (it does not throw). An empty UI label or
    // accessible name is a silent failure, so log it and fall back to the key name — a visibly wrong
    // label beats an invisibly absent one, and the warning points straight at the offending key.
    internal static string Get(string key)
    {
        string value = Loader.GetString(key);
        if (string.IsNullOrEmpty(value))
        {
            Log.Warning("Missing resource string {ResourceKey}", key);
            return key;
        }

        return value;
    }
}
