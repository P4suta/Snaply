namespace Snaply.App.Tests;

public sealed class MainViewModelTests
{
    private static readonly RenderedImage Image = new(
        Convert.FromBase64String(
            "iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAQAAAC1HAwCAAAAC0lEQVR42mNk+A8AAQUBAScY42YAAAAASUVORK5CYII="),
        1,
        1);

    [Fact]
    public async Task Capture_exposes_image_and_reports_complete_delivery()
    {
        var capture = new StubCapture(Image);
        var output = new StubOutput(
            new DeliveryOutcome(
                DeliveryResult.Succeeded,
                DeliveryResult.Succeeded));
        using var viewModel = CreateViewModel(capture, output);

        await viewModel.CaptureCommand.ExecuteAsync(null);

        Assert.Same(Image, viewModel.PreviewImage);
        Assert.True(viewModel.HasStatus);
        Assert.Equal(NoticeKind.Success, viewModel.StatusKind);
        Assert.Equal("StatusSavedAndCopied", viewModel.StatusMessage);
        Assert.False(viewModel.CanRetry);
        Assert.Equal(DeliveryRequest.All, Assert.Single(output.Requests));
    }

    [Fact]
    public async Task Cancelled_selection_leaves_the_previous_state_untouched()
    {
        var capture = new StubCapture(
            static _ => Task.FromResult<RenderedImage?>(null));
        var output = new StubOutput();
        using var viewModel = CreateViewModel(capture, output);

        await viewModel.CaptureCommand.ExecuteAsync(null);

        Assert.Null(viewModel.PreviewImage);
        Assert.False(viewModel.HasStatus);
        Assert.Empty(output.Requests);
    }

    [Fact]
    public async Task Partial_failure_retries_only_the_failed_target()
    {
        var capture = new StubCapture(Image);
        var output = new StubOutput(
            new DeliveryOutcome(
                DeliveryResult.Succeeded,
                DeliveryResult.Failed),
            new DeliveryOutcome(
                DeliveryResult.NotAttempted,
                DeliveryResult.Succeeded));
        using var viewModel = CreateViewModel(capture, output);

        await viewModel.CaptureCommand.ExecuteAsync(null);
        Assert.Equal(NoticeKind.Warning, viewModel.StatusKind);
        Assert.Equal("StatusSavedCopyFailed", viewModel.StatusMessage);
        Assert.True(viewModel.CanRetry);

        await viewModel.RetryDeliveryCommand.ExecuteAsync(null);

        Assert.Equal(NoticeKind.Success, viewModel.StatusKind);
        Assert.Equal("StatusSavedAndCopied", viewModel.StatusMessage);
        Assert.False(viewModel.CanRetry);
        Assert.Equal(
            new DeliveryRequest(Save: false, Clipboard: true),
            output.Requests[1]);
    }

    [Fact]
    public async Task Complete_delivery_failure_is_visible_and_retryable()
    {
        var capture = new StubCapture(Image);
        var output = new StubOutput(
            new DeliveryOutcome(
                DeliveryResult.Failed,
                DeliveryResult.Failed));
        using var viewModel = CreateViewModel(capture, output);

        await viewModel.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(NoticeKind.Error, viewModel.StatusKind);
        Assert.Equal("StatusDeliveryFailed", viewModel.StatusMessage);
        Assert.True(viewModel.CanRetry);
    }

    [Fact]
    public async Task Save_failure_retries_only_save()
    {
        var output = new StubOutput(
            new DeliveryOutcome(
                DeliveryResult.Failed,
                DeliveryResult.Succeeded),
            new DeliveryOutcome(
                DeliveryResult.Succeeded,
                DeliveryResult.NotAttempted));
        using var viewModel = CreateViewModel(new StubCapture(Image), output);

        await viewModel.CaptureCommand.ExecuteAsync(null);
        Assert.Equal("StatusCopiedSaveFailed", viewModel.StatusMessage);

        await viewModel.RetryDeliveryCommand.ExecuteAsync(null);

        Assert.Equal("StatusSavedAndCopied", viewModel.StatusMessage);
        Assert.Equal(
            new DeliveryRequest(Save: true, Clipboard: false),
            output.Requests[1]);
    }

    [Fact]
    public async Task Requested_but_unattempted_output_is_retryable()
    {
        var output = new StubOutput(
            new DeliveryOutcome(
                DeliveryResult.Succeeded,
                DeliveryResult.NotAttempted),
            new DeliveryOutcome(
                DeliveryResult.NotAttempted,
                DeliveryResult.Succeeded));
        using var viewModel = CreateViewModel(new StubCapture(Image), output);

        await viewModel.CaptureCommand.ExecuteAsync(null);
        Assert.Equal("StatusSavedCopyFailed", viewModel.StatusMessage);
        Assert.True(viewModel.CanRetry);

        await viewModel.RetryDeliveryCommand.ExecuteAsync(null);

        Assert.Equal("StatusSavedAndCopied", viewModel.StatusMessage);
        Assert.Equal(
            new DeliveryRequest(Save: false, Clipboard: true),
            output.Requests[1]);
    }

    [Fact]
    public async Task Capture_cancellation_is_not_reported_as_an_error()
    {
        var capture = new StubCapture(
            static cancellationToken => Task.FromCanceled<RenderedImage?>(cancellationToken));
        var output = new StubOutput();
        using var viewModel = CreateViewModel(capture, output);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        capture.ForcedToken = cancellation.Token;

        await viewModel.CaptureCommand.ExecuteAsync(null);

        Assert.False(viewModel.HasStatus);
        Assert.Empty(output.Requests);
    }

    [Fact]
    public async Task Capture_failure_is_visible()
    {
        var capture = new StubCapture(
            static _ => Task.FromException<RenderedImage?>(new InvalidOperationException("boom")));
        using var viewModel = CreateViewModel(capture, new StubOutput());

        await viewModel.CaptureCommand.ExecuteAsync(null);

        Assert.Equal(NoticeKind.Error, viewModel.StatusKind);
        Assert.Equal("ErrorCapture", viewModel.StatusMessage);
    }

    [Fact]
    public async Task Disposal_cancels_the_active_operation_and_disables_commands()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new StubCapture(async cancellationToken =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return Image;
        });
        var viewModel = CreateViewModel(capture, new StubOutput());

        Task operation = viewModel.CaptureCommand.ExecuteAsync(null);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        viewModel.Dispose();
        Assert.Equal(0, capture.DisposeCount);
        await operation;

        Assert.Equal(1, capture.DisposeCount);
        Assert.False(viewModel.CaptureCommand.CanExecute(null));
        Assert.False(viewModel.OpenFolderCommand.CanExecute(null));
        Assert.False(viewModel.HasStatus);
    }

    [Fact]
    public async Task Disposal_ignores_a_late_result_from_a_noncompliant_capture()
    {
        var result = new TaskCompletionSource<RenderedImage?>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var capture = new StubCapture(_ => result.Task);
        var output = new StubOutput();
        var viewModel = CreateViewModel(capture, output);

        Task operation = viewModel.CaptureCommand.ExecuteAsync(null);
        viewModel.Dispose();
        result.SetResult(Image);
        await operation;

        Assert.Null(viewModel.PreviewImage);
        Assert.Empty(output.Requests);
        Assert.Equal(1, capture.DisposeCount);
    }

    [Fact]
    public void Open_folder_failure_is_visible()
    {
        var output = new StubOutput
        {
            OpenAction = static () => throw new IOException("unavailable"),
        };
        using var viewModel = CreateViewModel(new StubCapture(Image), output);

        viewModel.OpenFolderCommand.Execute(null);

        Assert.Equal(NoticeKind.Error, viewModel.StatusKind);
        Assert.Equal("ErrorOpenFolder", viewModel.StatusMessage);
        Assert.False(viewModel.CanRetry);
    }

    private static MainViewModel CreateViewModel(
        ICapturePipeline capture,
        IImageOutput output) =>
        new(capture, output, static key => key);

    private sealed class StubCapture : ICapturePipeline
    {
        private readonly Func<CancellationToken, Task<RenderedImage?>> _capture;

        internal StubCapture(RenderedImage image)
            : this(_ => Task.FromResult<RenderedImage?>(image))
        {
        }

        internal StubCapture(Func<CancellationToken, Task<RenderedImage?>> capture)
        {
            _capture = capture;
        }

        internal CancellationToken? ForcedToken { get; set; }

        internal int DisposeCount { get; private set; }

        public void Dispose() => DisposeCount++;

        public Task<RenderedImage?> CaptureAsync(
            CaptureMode mode,
            CancellationToken cancellationToken) =>
            _capture(ForcedToken ?? cancellationToken);
    }

    private sealed class StubOutput(params DeliveryOutcome[] outcomes) : IImageOutput
    {
        private readonly Queue<DeliveryOutcome> _outcomes = new(outcomes);

        internal List<DeliveryRequest> Requests { get; } = [];

        internal Action OpenAction { get; init; } = static () => { };

        public Task<DeliveryOutcome> DeliverAsync(
            RenderedImage image,
            DeliveryRequest request,
            DateTimeOffset now,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Requests.Add(request);
            return Task.FromResult(_outcomes.Dequeue());
        }

        public void OpenCaptureDirectory() => OpenAction();
    }
}
