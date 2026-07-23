using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Serilog;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage.Streams;

namespace Snaply;

internal sealed class ImageExportService
{
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
                await stream.WriteAsync(image.Png.AsBuffer()).AsTask(cancellationToken);
                stream.Seek(0);

                var package = new DataPackage();
                package.SetBitmap(RandomAccessStreamReference.CreateFromStream(stream));
                Clipboard.SetContent(package);
                Clipboard.Flush();
                return;
            }
            catch (COMException) when (attempt < 2)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50 * (attempt + 1)), cancellationToken);
            }
        }
    }

    internal void OpenCaptureDirectory()
    {
        Directory.CreateDirectory(_captureDirectory);
        // Shell-execute the directory itself rather than passing it as an explorer.exe argument:
        // an unquoted path that contains a space (e.g. a redirected Pictures folder) would be
        // misparsed and open the wrong location.
        Process.Start(new ProcessStartInfo(_captureDirectory)
        {
            UseShellExecute = true,
        });
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
        byte[] bytes,
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
        stream.Flush(true);
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
}
