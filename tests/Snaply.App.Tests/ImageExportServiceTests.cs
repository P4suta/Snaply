namespace Snaply.App.Tests;

public sealed class ImageExportServiceTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "Snaply.Tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Automatic_save_creates_new_file()
    {
        var service = new ImageExportService(_directory);
        var image = new RenderedImage([1, 2, 3], 1, 1);
        var now = new DateTimeOffset(2026, 7, 20, 9, 30, 0, TimeZoneInfo.Local.GetUtcOffset(
            new DateTime(2026, 7, 20, 9, 30, 0)));
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string path = await service.SaveAutomaticallyAsync(image, now, cancellationToken);

        Assert.Equal(image.Png, await File.ReadAllBytesAsync(path, cancellationToken));
        Assert.Equal(ImageExportService.CreateSuggestedFileName(now), Path.GetFileName(path));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task Concurrent_automatic_saves_use_unique_collision_suffixes()
    {
        var service = new ImageExportService(_directory);
        var image = new RenderedImage([4, 5, 6], 1, 1);
        DateTimeOffset now = DateTimeOffset.Now;
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;

        string[] paths = await Task.WhenAll(
            Enumerable.Range(0, 20)
                .Select(_ => service.SaveAutomaticallyAsync(image, now, cancellationToken)));

        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        byte[][] contents = await Task.WhenAll(
            paths.Select(path => File.ReadAllBytesAsync(path, cancellationToken)));
        Assert.All(contents, bytes => Assert.Equal(image.Png, bytes));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task Cancelled_automatic_save_removes_temporary_file()
    {
        var service = new ImageExportService(_directory);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.SaveAutomaticallyAsync(
                new RenderedImage([1], 1, 1),
                DateTimeOffset.Now,
                cancellation.Token));

        Assert.Empty(TemporaryFiles());
        Assert.Empty(Directory.EnumerateFiles(_directory));
    }

    [Fact]
    public async Task Save_as_atomically_replaces_existing_file()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "capture.png");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, [9], cancellationToken);
        var service = new ImageExportService(_directory);
        var image = new RenderedImage([7, 8], 1, 1);

        await service.SaveAsAsync(image, path, cancellationToken);

        Assert.Equal(image.Png, await File.ReadAllBytesAsync(path, cancellationToken));
        Assert.Empty(TemporaryFiles());
    }

    [Fact]
    public async Task Locked_destination_preserves_existing_file_and_removes_temporary_file()
    {
        Directory.CreateDirectory(_directory);
        string path = Path.Combine(_directory, "capture.png");
        CancellationToken cancellationToken = TestContext.Current.CancellationToken;
        await File.WriteAllBytesAsync(path, [9], cancellationToken);
        var service = new ImageExportService(_directory);
        await using (var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.None))
        {
            Exception? exception = await Record.ExceptionAsync(() =>
                service.SaveAsAsync(
                    new RenderedImage([7, 8], 1, 1),
                    path,
                    cancellationToken));
            Assert.True(exception is IOException or UnauthorizedAccessException);
        }

        Assert.Equal(new byte[] { 9 }, await File.ReadAllBytesAsync(path, cancellationToken));
        Assert.Empty(TemporaryFiles());
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, true);
        }
    }

    private string[] TemporaryFiles() =>
        Directory.Exists(_directory)
            ? Directory.GetFiles(_directory, "*.tmp", SearchOption.AllDirectories)
            : [];
}
