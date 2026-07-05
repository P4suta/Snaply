using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Runtime.InteropServices;
using Snaply.Core;
using Snaply.Core.Ports;

namespace Snaply.Platform;

/// <summary>
/// System-wide hotkeys over the Win32 RegisterHotKey message loop. A dedicated
/// STA thread owns a message-only window (HWND_MESSAGE) that receives WM_HOTKEY.
/// <para>
/// Threading: <see cref="Pressed"/> is raised on that hotkey thread, NOT the UI
/// thread. The App layer must marshal to its DispatcherQueue before touching UI.
/// </para>
/// </summary>
[SuppressMessage(
    "Usage",
    "CA2216:Disposable types should declare a finalizer",
    Justification = "_hwnd is a message-only window owned by the dedicated _thread and torn down on that thread via a posted WM_CLOSE in Dispose; a GC-thread finalizer must not destroy a window it does not own, so none is declared.")]
public sealed class Win32HotkeyService : IHotkeyService
{
    private const int WmHotkey = 0x0312;
    private const int WmClose = 0x0010;
    private const int WmDestroy = 0x0002;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWin = 0x0008;
    private const uint ModNoRepeat = 0x4000;
    private static readonly IntPtr HwndMessage = new(-3);

    private readonly Thread _thread;
    private readonly ManualResetEventSlim _ready = new(false);
    private readonly Lock _gate = new();
    private readonly Dictionary<int, HotkeyAction> _hotkeys = new();
    private readonly WndProc _wndProc; // held to keep the delegate alive for the window's lifetime
    private readonly string _className = "SnaplyHotkeyWindow_" + Guid.NewGuid().ToString("N");
    private IntPtr _hwnd;
    private int _nextId = 1;
    private bool _disposed;

    /// <inheritdoc/>
    public event EventHandler<HotkeyAction>? Pressed;

    /// <summary>Starts the dedicated STA hotkey thread and its message-only window.</summary>
    public Win32HotkeyService()
    {
        _wndProc = WindowProc;
        _thread = new Thread(ThreadMain) { IsBackground = true, Name = "Snaply.Hotkeys" };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
        _ready.Wait();
    }

    /// <inheritdoc/>
    public Result Register(HotkeyAction action, string chord)
    {
        if (_disposed)
        {
            return Result.Fail(ErrorCodes.HotkeyDisposed, "The hotkey service has been disposed.");
        }

        if (!TryParseChord(chord, out uint modifiers, out uint vk))
        {
            return Result.Fail(ErrorCodes.HotkeyParse, $"Could not parse chord '{chord}'.");
        }

        int id;
        lock (_gate)
        {
            id = _nextId++;
            _hotkeys[id] = action;
        }

        // WM_HOTKEY is posted to the message-only window regardless of caller thread.
        if (!RegisterHotKey(_hwnd, id, modifiers | ModNoRepeat, vk))
        {
            lock (_gate)
            {
                _hotkeys.Remove(id);
            }

            return Result.Fail(ErrorCodes.HotkeyRegister, $"RegisterHotKey failed for '{chord}' (already in use?).");
        }

        return Result.Ok();
    }

    private void ThreadMain()
    {
        var wndClass = new WNDCLASS
        {
            lpfnWndProc = _wndProc,
            hInstance = GetModuleHandle(null),
            lpszClassName = _className,
        };
        RegisterClass(ref wndClass);

        _hwnd = CreateWindowEx(0, _className, string.Empty, 0, 0, 0, 0, 0, HwndMessage, IntPtr.Zero, wndClass.hInstance, IntPtr.Zero);
        _ready.Set();

        while (GetMessage(out MSG msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        UnregisterClass(_className, wndClass.hInstance);
    }

    private IntPtr WindowProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WmHotkey:
                int id = wParam.ToInt32();
                HotkeyAction action;
                bool found;
                lock (_gate)
                {
                    found = _hotkeys.TryGetValue(id, out action);
                }

                if (found)
                {
                    Pressed?.Invoke(this, action);
                }

                return IntPtr.Zero;

            case WmClose:
                DestroyWindow(hwnd);
                return IntPtr.Zero;

            case WmDestroy:
                PostQuitMessage(0);
                return IntPtr.Zero;

            default:
                return DefWindowProc(hwnd, msg, wParam, lParam);
        }
    }

    private static bool TryParseChord(string chord, out uint modifiers, out uint vk)
    {
        modifiers = 0;
        vk = 0;
        if (string.IsNullOrWhiteSpace(chord))
        {
            return false;
        }

        string[] parts = chord.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        bool keySet = false;
        foreach (string part in parts)
        {
            switch (part.ToUpperInvariant())
            {
                case "CTRL":
                case "CONTROL":
                    modifiers |= ModControl;
                    break;
                case "SHIFT":
                    modifiers |= ModShift;
                    break;
                case "ALT":
                case "MENU":
                    modifiers |= ModAlt;
                    break;
                case "WIN":
                case "WINDOWS":
                case "META":
                    modifiers |= ModWin;
                    break;
                default:
                    if (!TryParseKey(part, out vk))
                    {
                        return false;
                    }

                    keySet = true;
                    break;
            }
        }

        return keySet;
    }

    private static bool TryParseKey(string key, out uint vk)
    {
        vk = key.ToUpperInvariant() switch
        {
            "PRINTSCREEN" or "PRTSC" or "PRTSCN" => 0x2C,
            "SPACE" => 0x20,
            "ENTER" or "RETURN" => 0x0D,
            "TAB" => 0x09,
            "ESC" or "ESCAPE" => 0x1B,
            "INSERT" or "INS" => 0x2D,
            "DELETE" or "DEL" => 0x2E,
            "HOME" => 0x24,
            "END" => 0x23,
            "PAGEUP" or "PGUP" => 0x21,
            "PAGEDOWN" or "PGDN" => 0x22,
            "LEFT" => 0x25,
            "UP" => 0x26,
            "RIGHT" => 0x27,
            "DOWN" => 0x28,
            _ => 0,
        };

        if (vk != 0)
        {
            return true;
        }

        // Function keys F1..F24 -> 0x70..0x87.
        if ((key.Length == 2 || key.Length == 3) && (key[0] == 'F' || key[0] == 'f')
            && int.TryParse(key.AsSpan(1), NumberStyles.Integer, CultureInfo.InvariantCulture, out int fn)
            && fn is >= 1 and <= 24)
        {
            vk = (uint)(0x70 + fn - 1);
            return true;
        }

        // Single alphanumeric character maps to its uppercase virtual-key code.
        if (key.Length == 1)
        {
            char c = char.ToUpperInvariant(key[0]);
            if (c is >= '0' and <= '9' || c is >= 'A' and <= 'Z')
            {
                vk = c;
                return true;
            }
        }

        return false;
    }

    /// <inheritdoc/>
    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        lock (_gate)
        {
            foreach (int id in _hotkeys.Keys)
            {
                UnregisterHotKey(_hwnd, id);
            }

            _hotkeys.Clear();
        }

        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
        }

        _thread.Join(TimeSpan.FromSeconds(2));
        _ready.Dispose();
    }

    // --- Win32 interop ---
    private delegate IntPtr WndProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WNDCLASS
    {
        public uint style;
        public WndProc lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string? lpszMenuName;
        [MarshalAs(UnmanagedType.LPWStr)]
        public string lpszClassName;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public int ptX;
        public int ptY;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern ushort RegisterClass(ref WNDCLASS lpWndClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool UnregisterClass([MarshalAs(UnmanagedType.LPWStr)] string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateWindowEx(
        uint dwExStyle,
        [MarshalAs(UnmanagedType.LPWStr)] string lpClassName,
        [MarshalAs(UnmanagedType.LPWStr)] string lpWindowName,
        uint dwStyle,
        int x,
        int y,
        int nWidth,
        int nHeight,
        IntPtr hWndParent,
        IntPtr hMenu,
        IntPtr hInstance,
        IntPtr lpParam);

    [DllImport("user32.dll")]
    private static extern bool DestroyWindow(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DefWindowProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern void PostQuitMessage(int nExitCode);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);
}
