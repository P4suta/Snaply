using Snaply.Application;
using Snaply.Core;

namespace Snaply.Tests;

/// <summary>
/// The documented <see cref="ExitCodeMap"/> contract (see <c>docs/CLI.md</c>): every stable failure
/// code maps to a specific process exit code that scripts and CI branch on. Pinned so a change to
/// the table is a deliberate, reviewed one — not a silent break of downstream automation.
/// </summary>
public class ExitCodeMapTests
{
    [Theory]
    [InlineData(ErrorCodes.CaptureMonitor, 10)]
    [InlineData(ErrorCodes.CaptureWindow, 10)]
    [InlineData(ErrorCodes.CaptureRegion, 10)]
    [InlineData(ErrorCodes.CaptureWindowAmbiguous, 15)]
    [InlineData(ErrorCodes.BeautifyRender, 11)]
    [InlineData(ErrorCodes.ExportSave, 12)]
    [InlineData(ErrorCodes.ExportClipboard, 13)]
    [InlineData(ErrorCodes.PipelineNoCapture, 14)]
    [InlineData(ErrorCodes.ConsentDenied, 20)]
    [InlineData(ErrorCodes.OutputMissing, 30)]
    [InlineData(ErrorCodes.InputInvalid, 2)]
    public void For_MapsEachErrorCodeToItsDocumentedExitCode(string code, int expected)
    {
        Assert.Equal(expected, ExitCodeMap.For(code));
    }

    [Theory]
    [InlineData("totally.unknown")]
    [InlineData("hotkey.register")] // a real code with no dedicated exit code
    [InlineData("")]
    public void For_UnmappedCode_IsUnexpected(string code)
    {
        Assert.Equal(ExitCodeMap.Unexpected, ExitCodeMap.For(code));
    }

    [Fact]
    public void Constants_MatchTheDocumentedBaseline()
    {
        Assert.Equal(0, ExitCodeMap.Success);
        Assert.Equal(1, ExitCodeMap.Unexpected);
        Assert.Equal(2, ExitCodeMap.Usage);
    }
}
