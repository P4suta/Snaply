using System.Runtime.InteropServices;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog;

namespace Snaply;

internal enum NoticeKind
{
    Information,
    Success,
    Warning,
    Error,
}

internal sealed partial class MainViewModel : ObservableObject, IDisposable
{
    private readonly ICapturePipeline _capture;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly IImageOutput _output;
    private readonly Func<string, string> _text;
    private RenderedImage? _lastImage;
    private DeliveryOutcome _lastDelivery;
    private bool _disposed;
    private bool _operationRunning;

    [ObservableProperty]
    internal partial RenderedImage? PreviewImage { get; set; }

    [ObservableProperty]
    internal partial CaptureMode SelectedMode { get; set; } = CaptureMode.Region;

    [ObservableProperty]
    internal partial bool HasStatus { get; set; }

    [ObservableProperty]
    internal partial string StatusMessage { get; set; } = string.Empty;

    [ObservableProperty]
    internal partial NoticeKind StatusKind { get; set; }

    [ObservableProperty]
    internal partial bool CanRetry { get; set; }

    internal MainViewModel(
        ICapturePipeline capture,
        IImageOutput output,
        Func<string, string>? text = null)
    {
        _capture = capture;
        _output = output;
        _text = text ?? ResourceText.Get;
    }

    [RelayCommand(CanExecute = nameof(CanStartOperation))]
    private async Task CaptureAsync()
    {
        using CancellationTokenSource operation = BeginOperation();
        try
        {
            ClearStatus();
            RenderedImage? image = await _capture.CaptureAsync(SelectedMode, operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            if (image is null)
            {
                return;
            }

            PreviewImage = image;
            _lastImage = image;
            _lastDelivery = await _output.DeliverAsync(
                image,
                DeliveryRequest.All,
                DateTimeOffset.Now,
                operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            ApplyDeliveryOutcome(_lastDelivery);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            LogFailure("Capture", exception);
            ShowStatus("ErrorCapture", NoticeKind.Error, canRetry: false);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanRetryDelivery))]
    private async Task RetryDeliveryAsync()
    {
        if (_lastImage is null)
        {
            return;
        }

        DeliveryRequest request = _lastDelivery.FailedTargets;
        if (request.IsEmpty)
        {
            return;
        }

        using CancellationTokenSource operation = BeginOperation();
        try
        {
            ClearStatus();
            DeliveryOutcome retried = await _output.DeliverAsync(
                _lastImage,
                request,
                DateTimeOffset.Now,
                operation.Token);
            operation.Token.ThrowIfCancellationRequested();
            _lastDelivery = Merge(_lastDelivery, retried, request);
            ApplyDeliveryOutcome(_lastDelivery);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            LogFailure("RetryDelivery", exception);
            ShowStatus("StatusDeliveryFailed", NoticeKind.Error, canRetry: true);
        }
        finally
        {
            EndOperation();
        }
    }

    [RelayCommand(CanExecute = nameof(CanOpenFolder))]
    private void OpenFolder()
    {
        try
        {
            _output.OpenCaptureDirectory();
        }
        catch (Exception exception) when (IsOperationalFailure(exception))
        {
            LogFailure("OpenFolder", exception);
            ShowStatus("ErrorOpenFolder", NoticeKind.Error, canRetry: false);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetime.Cancel();
        _lifetime.Dispose();
        if (!_operationRunning)
        {
            _capture.Dispose();
        }

        NotifyCommandStates();
    }

    private bool CanStartOperation() => !_disposed && !_operationRunning;

    private bool CanRetryDelivery() => CanStartOperation() && CanRetry;

    private bool CanOpenFolder() => !_disposed;

    private CancellationTokenSource BeginOperation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_operationRunning)
        {
            throw new InvalidOperationException("Another operation is already running.");
        }

        var operation = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        _operationRunning = true;
        NotifyCommandStates();
        return operation;
    }

    private void EndOperation()
    {
        _operationRunning = false;
        if (_disposed)
        {
            _capture.Dispose();
        }

        NotifyCommandStates();
    }

    private void ApplyDeliveryOutcome(DeliveryOutcome outcome)
    {
        if (outcome.AllSucceeded)
        {
            ShowStatus("StatusSavedAndCopied", NoticeKind.Success, canRetry: false);
        }
        else if (outcome.Save is DeliveryResult.Succeeded)
        {
            ShowStatus("StatusSavedCopyFailed", NoticeKind.Warning, canRetry: true);
        }
        else if (outcome.Clipboard is DeliveryResult.Succeeded)
        {
            ShowStatus("StatusCopiedSaveFailed", NoticeKind.Warning, canRetry: true);
        }
        else
        {
            ShowStatus("StatusDeliveryFailed", NoticeKind.Error, canRetry: true);
        }
    }

    private void ClearStatus()
    {
        HasStatus = false;
        CanRetry = false;
        RetryDeliveryCommand.NotifyCanExecuteChanged();
    }

    private void ShowStatus(string key, NoticeKind kind, bool canRetry)
    {
        StatusMessage = _text(key);
        StatusKind = kind;
        CanRetry = canRetry;
        HasStatus = true;
        RetryDeliveryCommand.NotifyCanExecuteChanged();
    }

    private void NotifyCommandStates()
    {
        CaptureCommand.NotifyCanExecuteChanged();
        RetryDeliveryCommand.NotifyCanExecuteChanged();
        OpenFolderCommand.NotifyCanExecuteChanged();
    }

    private static DeliveryOutcome Merge(
        DeliveryOutcome previous,
        DeliveryOutcome retried,
        DeliveryRequest request) =>
        new(
            request.Save ? retried.Save : previous.Save,
            request.Clipboard ? retried.Clipboard : previous.Clipboard);

    private static void LogFailure(string operation, Exception exception) =>
        Log.Warning(
            "{Operation} failed {ExceptionType} {HResult}",
            operation,
            exception.GetType().FullName,
            exception.HResult);

    private static bool IsOperationalFailure(Exception exception) =>
        exception is ArgumentException
        or ExternalException
        or IOException
        or InvalidOperationException
        or NotSupportedException
        or TimeoutException
        or UnauthorizedAccessException;
}
