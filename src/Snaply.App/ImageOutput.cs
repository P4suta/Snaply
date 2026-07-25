namespace Snaply;

internal interface IImageOutput
{
    public Task<DeliveryOutcome> DeliverAsync(
        RenderedImage image,
        DeliveryRequest request,
        DateTimeOffset now,
        CancellationToken cancellationToken);

    public void OpenCaptureDirectory();
}

internal readonly record struct DeliveryRequest(bool Save, bool Clipboard)
{
    internal static DeliveryRequest All { get; } = new(Save: true, Clipboard: true);

    internal bool IsEmpty => !Save && !Clipboard;
}

internal enum DeliveryResult
{
    NotAttempted,
    Succeeded,
    Failed,
}

internal readonly record struct DeliveryOutcome(
    DeliveryResult Save,
    DeliveryResult Clipboard)
{
    internal bool AllSucceeded =>
        Save is DeliveryResult.Succeeded
        && Clipboard is DeliveryResult.Succeeded;

    internal DeliveryRequest FailedTargets => new(
        Save: Save is not DeliveryResult.Succeeded,
        Clipboard: Clipboard is not DeliveryResult.Succeeded);
}
