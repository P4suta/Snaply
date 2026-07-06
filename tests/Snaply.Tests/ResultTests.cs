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

    [Fact]
    public void Ok_CarriesValueAndIsSuccess()
    {
        Result<int> result = Result<int>.Ok(42);

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
        Assert.Equal(42, result.Value);
    }

    [Fact]
    public void Value_OnFailure_ThrowsWithTheErrorInTheMessage()
    {
        Result<int> failed = Result<int>.Fail(ErrorCodes.ExportSave, "save failed");

        InvalidOperationException ex = Assert.Throws<InvalidOperationException>(() => failed.Value);
        Assert.Contains(ErrorCodes.ExportSave, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Map_OnSuccess_TransformsTheValue()
    {
        Result<int> ok = Result<int>.Ok(21);

        Result<int> mapped = ok.Map(v => v * 2);

        Assert.True(mapped.IsSuccess);
        Assert.Equal(42, mapped.Value);
    }

    [Fact]
    public void NonGenericOk_IsSuccess()
    {
        Result result = Result.Ok();

        Assert.True(result.IsSuccess);
        Assert.False(result.IsFailure);
    }
}
