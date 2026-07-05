using System.Runtime.InteropServices;

// Every P/Invoke in this assembly targets a Win32 system DLL (user32, kernel32,
// gdi32, Shcore) which always lives in System32. Pin the DLL search path there so
// none of them can be hijacked by a same-named DLL dropped in the application or
// current directory — the security hardening CA5392 asks for, applied once for the
// whole assembly rather than per method.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
