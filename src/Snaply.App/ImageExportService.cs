using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Snaply;

internal sealed class ImageExportService : IImageOutput
{
    private const int ClipboardCannotOpen = unchecked((int)0x800401D0);
    private readonly string _captureDirectory;
    private int _temporarySequence;

    internal ImageExportService()
        : this(GetDefaultCaptureDirectory())
    {
    }

    internal ImageExportService(string captureDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(captureDirectory);
        _captureDirectory = Path.GetFullPath(captureDirectory);
    }

    internal static string CreateSuggestedFileName(DateTimeOffset now) =>
        $"Snaply-{now.ToLocalTime():yyyy-MM-dd_HH-mm-ss}.png";

    public async Task<DeliveryOutcome> DeliverAsync(
        RenderedImage image,
        DeliveryRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(image);
        if (request.IsEmpty)
        {
            throw new ArgumentException("At least one output target must be requested.", nameof(request));
        }

        Task<DeliveryResult> save = request.Save
            ? TrySaveAutomaticallyAsync(image, now, cancellationToken)
            : Task.FromResult(DeliveryResult.NotAttempted);
        Task<DeliveryResult> copy = request.Clipboard
            ? TryCopyAsync(image, cancellationToken)
            : Task.FromResult(DeliveryResult.NotAttempted);
        await Task.WhenAll(save, copy);
        return new DeliveryOutcome(await save, await copy);
    }

    internal async Task<string> SaveAutomaticallyAsync(
        RenderedImage image,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        string directory = _captureDirectory;
        Directory.CreateDirectory(directory);
        string stem = Path.GetFileNameWithoutExtension(CreateSuggestedFileName(now));
        string temporaryPath = Path.Combine(
            directory,
            $".{stem}.{Environment.ProcessId}.{Interlocked.Increment(ref _temporarySequence)}.tmp");

        try
        {
            await WriteNewFileAsync(temporaryPath, image.Png, cancellationToken);

            for (int collision = 0; ; collision++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string suffix = collision == 0 ? string.Empty : $"-{collision + 1}";
                string finalPath = Path.Combine(directory, $"{stem}{suffix}.png");
                try
                {
                    File.Move(temporaryPath, finalPath, false);
                    return finalPath;
                }
                catch (IOException) when (File.Exists(finalPath))
                {
                }
            }
        }
        finally
        {
            DeleteTemporaryFile(temporaryPath);
        }
    }

    internal static async Task CopyAsync(RenderedImage image, CancellationToken cancellationToken)
    {
        for (int attempt = 0; ; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var stream = new InMemoryRandomAccessStream();
                using Stream output = stream.AsStreamForWrite();
                await output.WriteAsync(image.Png, cancellationToken);
                await output.FlushAsync(cancellationToken);
                stream.Seek(0);

                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return;
            }
            catch (COMException exception) when (
                exception.HResult == ClipboardCannotOpen
                && attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }
    }

    public void OpenCaptureDirectory()
    {
        Directory.CreateDirectory(_captureDirectory);
        using Process? process = Process.Start(new ProcessStartInfo(_captureDirectory)
        {
            UseShellExecute = true,
        });
        if (process is null)
        {
            throw new InvalidOperationException("The capture directory could not be opened.");
        }
    }

    private static string GetDefaultCaptureDirectory()
    {
        string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        if (string.IsNullOrWhiteSpace(pictures))
        {
            throw new DirectoryNotFoundException();
        }

        return Path.Combine(pictures, "Screenshots", "Snaply");
    }

    private static async Task WriteNewFileAsync(
        string path,
        ReadOnlyMemory<byte> bytes,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            131_072,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await stream.WriteAsync(bytes, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task<DeliveryResult> TrySaveAutomaticallyAsync(
        RenderedImage image,
        DateTimeOffset now,
        CancellationToken cancellationToken)
    {
        try
        {
            _ = await SaveAutomaticallyAsync(image, now, cancellationToken);
            return DeliveryResult.Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure("AutoSave", exception);
            return DeliveryResult.Failed;
        }
    }

    private static async Task<DeliveryResult> TryCopyAsync(
        RenderedImage image,
        CancellationToken cancellationToken)
    {
        try
        {
            await CopyAsync(image, cancellationToken);
            return DeliveryResult.Succeeded;
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            LogFailure("Clipboard", exception);
            return DeliveryResult.Failed;
        }
    }

    private static void DeleteTemporaryFile(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Log.Warning(
                "Temporary export cleanup failed {ExceptionType} {HResult}",
                exception.GetType().FullName,
                exception.HResult);
        }
    }

    private static void LogFailure(string operation, Exception exception) =>
        Log.Warning(
            "{Operation} failed {ExceptionType} {HResult}",
            operation,
            exception.GetType().FullName,
            exception.HResult);
}
