using System.Runtime.InteropServices;

// This assembly runs inside an ELEVATED process. Without this attribute a
// P/Invoke whose DLL is not already loaded falls through to the default
// search, which includes every PATH entry - %LOCALAPPDATA%\Microsoft\WindowsApps
// among them, a folder any process of the user can write to - so a DLL planted
// there under a missing system DLL's name was loaded as administrator. Every
// DllImport here names a Windows DLL in System32; the two that are not
// (PawnIOLib, nvapi64) are resolved by absolute path in NativeLibraries.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
