using Snaply.Core;

namespace Snaply.Tests;

public class ResultTests
{
    [Fact]
    public void Fail_WithException_PreservesCauseAndMessageAndCode()
    {
        var cause = new InvalidOperationException("boom");

        Result<int> result = Result<int>.Fail(ErrorCodes.ExportSave, "save failed", cause);

        Assert.True(result.IsFailure);
        Assert.Equal(ErrorCodes.ExportSave, result.Error.Code);
        Assert.Equal("save failed", result.Error.Message);
        Assert.Same(cause, result.Error.Cause);
    }

    [Fact]
    public void Fail_WithoutException_LeavesCauseNull()
    {
        Result result = Result.Fail(ErrorCodes.PipelineNoCapture, "nothing captured");

        Assert.True(result.IsFailure);
        Assert.Null(result.Error.Cause);
    }

    [Fact]
    public void NonGenericFail_WithException_PreservesCause()
    {
        var cause = new IOException("disk full");

        Result result = Result.Fail(ErrorCodes.ExportClipboard, "clipboard failed", cause);

        Assert.Same(cause, result.Error.Cause);
        Assert.Equal(ErrorCodes.ExportClipboard, result.Error.Code);
    }

    [Fact]
    public void Error_ToString_IgnoresCause()
    {
        var error = new Error(ErrorCodes.BeautifyRender, "render failed", new InvalidOperationException("x"));

        Assert.Equal("[beautify.render] render failed", error.ToString());
    }

    [Fact]
    public void Map_PropagatesFailureWithCause()
    {
        var cause = new InvalidOperationException("boom");
        Result<int> failed = Result<int>.Fail(ErrorCodes.CaptureMonitor, "capture failed", cause);

        Result<string> mapped = failed.Map(value => value.ToString(System.Globalization.CultureInfo.InvariantCulture));

        Assert.True(mapped.IsFailure);
        Assert.Same(cause, mapped.Error.Cause);
    }
}
