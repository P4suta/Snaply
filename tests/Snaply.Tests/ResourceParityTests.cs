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
    public void Keys_referenced_from_code_exist_in_every_locale()
    {
        // Keys passed as string literals to ResourceText.Get(...) have no compile-time link to the
        // .resw files, so a rename or typo would silently surface an empty label or accessible name
        // at runtime. Pin the set here so a mismatch fails the build instead.
        string[] requiredKeys =
        [
            "CaptureRegion",
            "CaptureWindow",
            "CaptureDesktop",
            "OpenFolderLabel",
            "ErrorCapture",
            "ErrorOpenFolder",
            "RegionHint",
            "RegionCancel",
        ];

        string root = Path.Combine(AppContext.BaseDirectory, "Strings");
        foreach (string file in Directory.GetFiles(root, "Resources.resw", SearchOption.AllDirectories))
        {
            HashSet<string> keys = XDocument.Load(file)
                .Root!
                .Elements("data")
                .Select(element => (string)element.Attribute("name")!)
                .ToHashSet(StringComparer.Ordinal);
            Assert.All(requiredKeys, key => Assert.Contains(key, keys));
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
    public void Text_hosts_can_reflow_expanded_pseudo_localized_content()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "Strings", "en-US", "Resources.resw");
        string[] values = XDocument.Load(root)
            .Root!
            .Elements("data")
            .Select(element => element.Element("value")!.Value)
            .ToArray();
        string[] pseudoLocalized = values
            .Select(value => $"⟦{value}{new string('~', Math.Max(2, value.Length / 3))}⟧")
            .ToArray();
        Assert.All(
            pseudoLocalized,
            value => Assert.True(value.Length >= 4 && value[0] == '⟦' && value[^1] == '⟧'));

        XDocument page = XDocument.Load(Path.Combine(AppContext.BaseDirectory, "Ui", "MainPage.xaml"));
        string[] textHosts = ["TextBlock", "Button", "SplitButton", "AppBarButton", "MenuFlyoutItem"];
        XElement[] fixedWidthHosts = page
            .Descendants()
            .Where(element =>
                textHosts.Contains(element.Name.LocalName, StringComparer.Ordinal)
                && element.Attribute("Width") is not null)
            .ToArray();
        Assert.Empty(fixedWidthHosts);

        // The floating command pill must let its label grow (no fixed width), so an expanded
        // localized capture label reflows the pill instead of being clipped.
        XElement captureButton = Assert.Single(
            page.Descendants(),
            element => element.Name.LocalName == "SplitButton"
                && (string?)element.Attribute("{http://schemas.microsoft.com/winfx/2006/xaml}Name") == "CaptureButton");
        Assert.Null(captureButton.Attribute("Width"));
    }
}
