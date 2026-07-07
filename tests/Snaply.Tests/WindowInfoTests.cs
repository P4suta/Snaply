using Snaply.Core.Geometry;
using Snaply.Core.Models;

namespace Snaply.Tests;

/// <summary>
/// The <see cref="WindowInfo"/> model and its <see cref="WindowInfo.FromHandle"/> minimal
/// descriptor — the shape the CLI <c>--hwnd</c> / MCP <c>handle</c> path produces when a caller
/// supplies a handle that is not in the enumeration (only its identity is known).
/// </summary>
public class WindowInfoTests
{
    [Fact]
    public void FromHandle_CarriesOnlyTheIdentity()
    {
        WindowInfo info = WindowInfo.FromHandle(0x1234);

        Assert.Equal(0x1234, info.Handle);
        Assert.Equal(string.Empty, info.Title);
        Assert.Equal(default, info.Bounds);
        Assert.True(info.Bounds.IsEmpty);
        Assert.Equal(0, info.ProcessId);
        Assert.Equal(string.Empty, info.ProcessName);
        Assert.False(info.IsForeground);
    }

    [Fact]
    public void With_UpdatesOnlyTheNamedField()
    {
        WindowInfo original = WindowInfo.FromHandle(0x1);

        WindowInfo renamed = original with { Title = "Editor", Bounds = new PhysicalRect(0, 0, 800, 600) };

        Assert.Equal("Editor", renamed.Title);
        Assert.Equal(new PhysicalRect(0, 0, 800, 600), renamed.Bounds);
        Assert.Equal(original.Handle, renamed.Handle);
        Assert.Equal(string.Empty, original.Title); // the original is untouched
    }

    [Fact]
    public void Equality_IsByValue()
    {
        var a = new WindowInfo(0x1, "App", new PhysicalRect(0, 0, 10, 10), ProcessId: 42, ProcessName: "app");
        var b = new WindowInfo(0x1, "App", new PhysicalRect(0, 0, 10, 10), ProcessId: 42, ProcessName: "app");

        Assert.Equal(a, b);
        Assert.NotEqual(a, a with { IsForeground = true });
    }
}
