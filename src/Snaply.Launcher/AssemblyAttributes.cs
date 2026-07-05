using System.Runtime.InteropServices;

// The only P/Invoke here (MessageBoxW) lives in user32; pin the search path to System32
// so a look-alike DLL beside the exe can't be loaded instead (CA5392).
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
