using System.Runtime.InteropServices;

// The P/Invokes in this assembly (GetDpiForWindow, GetSystemMetrics,
// EnumDisplayMonitors, GetMonitorInfo) all target user32.dll, which always lives in
// System32. Pin the DLL search path there so none can be hijacked by a same-named
// DLL in the application or current directory — the hardening CA5392 asks for,
// applied once for the whole assembly.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
