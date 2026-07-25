using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Snaply.Tests;

public sealed class ResourceParityTests
{
    [Fact]
    public void Every_supported_language_has_the_same_resource_keys()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Strings");
        string[] files = Directory.GetFiles(root, "Resources.resw", SearchOption.AllDirectories);
        Assert.Equal(3, files.Length);

        string[][] keys = files
            .Select(file => XDocument.Load(file)
                .Root!
                .Elements("data")
                .Select(element => (string)element.Attribute("name")!)
                .Order(StringComparer.Ordinal)
                .ToArray())
            .ToArray();

        Assert.Equal(keys[0], keys[1]);
        Assert.Equal(keys[0], keys[2]);
        Assert.DoesNotContain(keys[0], static key =>
            Regex.IsMatch(key, @"(^|[_.])(Cli|Mcp|Hotkey)($|[_.])", RegexOptions.IgnoreCase));
    }

    [Fact]
    public void Keys_referenced_from_code_or_xaml_exist_in_every_locale()
    {
        string[] codeKeys = FindCodeResourceKeys();
        string[] uids = FindXamlResourceRoots();
        string[] manifestKeys = FindManifestResourceKeys();

        string root = Path.Combine(AppContext.BaseDirectory, "Strings");
        foreach (string file in Directory.GetFiles(root, "Resources.resw", SearchOption.AllDirectories))
        {
            HashSet<string> keys = XDocument.Load(file)
                .Root!
                .Elements("data")
                .Select(element => (string)element.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(codeKeys, key => Assert.Contains(key, keys));
            Assert.All(
                uids,
                uid => Assert.Contains(
                    keys,
                    key => key.Equals(uid, StringComparison.Ordinal)
                        || key.StartsWith($"{uid}.", StringComparison.Ordinal)));
            Assert.All(manifestKeys, key => Assert.Contains(key, keys));
        }
    }

    [Fact]
    public void Every_resource_is_referenced_by_product_code_or_metadata()
    {
        HashSet<string> referenced = FindCodeResourceKeys()
            .Concat(FindXamlResourceRoots())
            .Concat(FindManifestResourceKeys())
            .ToHashSet(StringComparer.Ordinal);
        string resourcePath = Path.Combine(
            AppContext.BaseDirectory,
            "Strings",
            "en-US",
            "Resources.resw");
        string[] orphaned = XDocument.Load(resourcePath)
            .Root!
            .Elements("data")
            .Select(element => ((string)element.Attribute("name")!).Split('.')[0])
            .Where(root => !referenced.Contains(root))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.Empty(orphaned);
    }

    [Fact]
    public void Interactive_xaml_controls_have_stable_identity_and_accessible_names()
    {
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        string uiRoot = Path.Combine(AppContext.BaseDirectory, "Ui");
        string[] interactiveTypes =
            ["Button", "InfoBar", "MenuFlyoutItem", "ProgressRing", "ScrollViewer", "SplitButton"];
        foreach (string path in Directory.GetFiles(uiRoot, "*.xaml", SearchOption.AllDirectories))
        {
            XElement[] controls = XDocument.Load(path)
                .Descendants()
                .Where(element =>
                    interactiveTypes.Contains(element.Name.LocalName, StringComparer.Ordinal)
                    || string.Equals(
                        (string?)element.Attribute("IsTabStop"),
                        "True",
                        StringComparison.OrdinalIgnoreCase))
                .ToArray();
            foreach (XElement control in controls)
            {
                string description = $"{Path.GetFileName(path)}:{control.Name.LocalName}";
                Assert.Contains(
                    control.Attributes(),
                    attribute => attribute.Name.LocalName.EndsWith(
                        "AutomationId",
                        StringComparison.Ordinal)
                        && !string.IsNullOrWhiteSpace(attribute.Value));
                Assert.False(
                    string.IsNullOrWhiteSpace((string?)control.Attribute(xaml + "Uid")),
                    $"{description} has no x:Uid accessible-name source.");
            }
        }
    }

    [Fact]
    public void Resources_are_unique_and_non_empty()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Strings");
        foreach (string file in Directory.GetFiles(root, "Resources.resw", SearchOption.AllDirectories))
        {
            XElement[] resources = XDocument.Load(file).Root!.Elements("data").ToArray();
            Assert.Equal(
                resources.Length,
                resources.Select(element => (string)element.Attribute("name")!).Distinct().Count());
            Assert.DoesNotContain(
                resources,
                static element => string.IsNullOrWhiteSpace(element.Element("value")?.Value));
        }
    }

    [Fact]
    public void Localized_text_hosts_are_not_constrained_to_fixed_widths()
    {
        string uiRoot = Path.Combine(AppContext.BaseDirectory, "Ui");
        string[] textHosts = ["TextBlock", "Button", "SplitButton", "AppBarButton", "MenuFlyoutItem"];
        XElement[] fixedWidthHosts = Directory
            .GetFiles(uiRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path).Descendants())
            .Where(element => textHosts.Contains(element.Name.LocalName, StringComparer.Ordinal)
                && element.Attribute("Width") is not null)
            .ToArray();
        Assert.Empty(fixedWidthHosts);

        string pagePath = Directory.GetFiles(
            uiRoot,
            "MainPage.xaml",
            SearchOption.AllDirectories).Single();
        XDocument page = XDocument.Load(pagePath);
        XElement captureButton = Assert.Single(
            page.Descendants(),
            element => element.Name.LocalName == "SplitButton"
                && (string?)element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "CaptureButton");
        Assert.Null(captureButton.Attribute("Width"));
    }

    private static string[] FindCodeResourceKeys()
    {
        string sourceRoot = Path.Combine(AppContext.BaseDirectory, "Source");
        return Directory
            .GetFiles(sourceRoot, "*.cs", SearchOption.AllDirectories)
            .SelectMany(path =>
            {
                string source = File.ReadAllText(path);
                IEnumerable<string> direct = Regex.Matches(
                    source,
                    """ResourceText\.Get\((?<argument>.*?)\)""",
                    RegexOptions.Singleline)
                    .SelectMany(call => Regex.Matches(
                        call.Groups["argument"].Value,
                        @"""(?<key>[^""]+)""")
                        .Select(match => match.Groups["key"].Value));
                IEnumerable<string> status = Regex.Matches(
                    source,
                    @"ShowStatus\(\s*""(?<key>[^""]+)""")
                    .Select(match => match.Groups["key"].Value);
                return direct.Concat(status);
            })
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] FindXamlResourceRoots()
    {
        string uiRoot = Path.Combine(AppContext.BaseDirectory, "Ui");
        XNamespace xaml = "http://schemas.microsoft.com/winfx/2006/xaml";
        return Directory
            .GetFiles(uiRoot, "*.xaml", SearchOption.AllDirectories)
            .SelectMany(path => XDocument.Load(path)
                .Descendants()
                .Select(element => (string?)element.Attribute(xaml + "Uid")))
            .Where(static uid => !string.IsNullOrWhiteSpace(uid))
            .Cast<string>()
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }

    private static string[] FindManifestResourceKeys()
    {
        string manifest = File.ReadAllText(
            Path.Combine(AppContext.BaseDirectory, "Ui", "Package.appxmanifest"));
        return Regex.Matches(manifest, @"ms-resource:(?<key>[A-Za-z0-9_]+)")
            .Select(match => match.Groups["key"].Value)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
    }
}
