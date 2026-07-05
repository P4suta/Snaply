using System.Runtime.InteropServices;
using Microsoft.UI.Dispatching;

namespace Snaply.Cli;

/// <summary>
/// Runs the CLI's async work on the process's STA thread with a live
/// <see cref="DispatcherQueue"/> and a Win32 message pump. WGC capture and Win2D
/// rendering are free-threaded, but the WinRT clipboard (<c>Clipboard.SetContent</c>,
/// used by <c>--clipboard</c>) delay-renders its data and therefore needs an STA thread
/// with a running message loop — exactly what this host provides, mirroring what the
/// WinUI app gets for free from its generated entry point.
/// </summary>
internal static partial class CliHost
{
    /// <summary>
    /// Pumps messages on the current (STA) thread while <paramref name="work"/> runs on the
    /// thread's dispatcher queue, then returns its exit code. The queue is shut down and the
    /// pump is stopped deterministically when the work completes.
    /// </summary>
    /// <param name="work">The CLI's root work, producing a process exit code.</param>
    /// <returns>The exit code produced by <paramref name="work"/>.</returns>
    public static int Run(Func<Task<int>> work)
    {
        DispatcherQueueController controller = DispatcherQueueController.CreateOnCurrentThread();
        DispatcherQueue queue = controller.DispatcherQueue;
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueSynchronizationContext(queue));

        var box = new ExitBox();

        // The dispatcher handler is void-returning, so start the async work from a synchronous
        // lambda (discarding the Task) rather than an async-void lambda; RunWorkAsync owns all
        // exceptions so the fire-and-forget is safe.
        bool enqueued = queue.TryEnqueue(() => _ = RunWorkAsync(controller, work, box));
        if (!enqueued)
        {
            return 1;
        }

        PumpMessages();
        return box.Exit;
    }

    private static async Task RunWorkAsync(DispatcherQueueController controller, Func<Task<int>> work, ExitBox box)
    {
        try
        {
            box.Exit = await work().ConfigureAwait(true);
        }
        catch (Exception ex)
        {
            box.Exit = 1;
            await Console.Error.WriteLineAsync(ex.ToString()).ConfigureAwait(true);
        }
        finally
        {
            // Best-effort orderly shutdown, then deterministically stop the pump so the
            // process exits regardless of the queue's internal shutdown signalling.
            _ = controller.ShutdownQueueAsync();
            PostQuitMessage(0);
        }
    }

    private sealed class ExitBox
    {
        public int Exit { get; set; }
    }

    private static void PumpMessages()
    {
        while (GetMessageW(out Msg message, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(in message);
            DispatchMessageW(in message);
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Msg
    {
        public IntPtr Hwnd;
        public uint Message;
        public UIntPtr WParam;
        public IntPtr LParam;
        public uint Time;
        public int PointX;
        public int PointY;
    }

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial int GetMessageW(out Msg lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TranslateMessage(in Msg lpMsg);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial IntPtr DispatchMessageW(in Msg lpMsg);

    [LibraryImport("user32.dll")]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    private static partial void PostQuitMessage(int nExitCode);
}
