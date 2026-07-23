using Microsoft.Windows.ApplicationModel.Resources;

namespace Snaply;

internal static class ResourceText
{
    private static readonly ResourceLoader Loader = new();

    internal static string Get(string key) => Loader.GetString(key);
}
