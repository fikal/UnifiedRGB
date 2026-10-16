using System.Runtime.InteropServices;
using System.Windows;

// The app runs elevated, and its own P/Invokes (user32, kernel32, ...) must
// never be resolved from a user-writable PATH entry such as
// %LOCALAPPDATA%\Microsoft\WindowsApps. See src/UnifiedRgb.Core/AssemblyInfo.cs.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]

[assembly:ThemeInfo(
    ResourceDictionaryLocation.None,            //where theme specific resource dictionaries are located
                                                //(used if a resource is not found in the page,
                                                // or application resource dictionaries)
    ResourceDictionaryLocation.SourceAssembly   //where the generic resource dictionary is located
                                                //(used if a resource is not found in the page,
                                                // app, or any theme specific resource dictionaries)
)]
