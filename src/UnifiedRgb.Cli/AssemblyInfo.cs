using System.Runtime.InteropServices;

// Same rule as Core and the App: the CLI is run elevated for its SMBus and fan
// probes, and its P/Invokes must never be resolved from a user-writable PATH
// entry. See src/UnifiedRgb.Core/AssemblyInfo.cs.
[assembly: DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
