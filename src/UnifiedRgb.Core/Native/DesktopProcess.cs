using System.Runtime.InteropServices;
using System.Text;

namespace UnifiedRgb.Core.Native;

/// <summary>Start a program as the DESKTOP USER rather than as this process.
///
/// The app runs elevated. A program it starts with its own token is elevated
/// too, and when that program is found by looking where the user's own
/// software says it is (a Steam path in HKCU, a library list in a file the
/// user owns), whoever can write those places chooses what the administrator
/// runs. Wallpaper Engine's control channel is exactly that case, and it
/// needs no elevation at all: it hands a request to the copy already running
/// on the desktop. So the request is sent with the desktop shell's token -
/// Explorer's, the ordinary user's, the one every double-click uses - and a
/// program it launches can do no more than the user could by launching it
/// themselves.
///
/// There is no fallback to this process's own token. If the shell token
/// cannot be had, the program is not started, and the caller says so.</summary>
public static class DesktopProcess
{
    /// <summary>Is this process running with a full administrator token.</summary>
    public static bool IsElevated { get; } = QueryElevated();

    static bool QueryElevated()
    {
        try
        {
            using var id = System.Security.Principal.WindowsIdentity.GetCurrent();
            return TokenIsElevated(id.Token);
        }
        catch { return false; }
    }

    /// <summary>Start <paramref name="exe"/> with the desktop shell's token.
    /// True when it was started. False, with the reason, when it was not, and
    /// it is then NOT started any other way.</summary>
    public static bool Start(string exe, IReadOnlyList<string> args, string? workingDirectory, out string? why)
    {
        why = null;
        IntPtr shellProcess = IntPtr.Zero, shellToken = IntPtr.Zero, primary = IntPtr.Zero;
        try
        {
            IntPtr shell = GetShellWindow();
            if (shell == IntPtr.Zero) { why = "there is no desktop shell window (is Explorer running?)"; return false; }
            GetWindowThreadProcessId(shell, out uint pid);
            if (pid == 0) { why = "the desktop shell window has no process"; return false; }

            shellProcess = OpenProcess(PROCESS_QUERY_INFORMATION, false, pid);
            if (shellProcess == IntPtr.Zero) { why = $"cannot open the shell process: {LastError()}"; return false; }
            if (!OpenProcessToken(shellProcess, TOKEN_DUPLICATE | TOKEN_QUERY, out shellToken))
            { why = $"cannot read the shell's token: {LastError()}"; return false; }
            // A shell somebody started elevated would hand its elevation on,
            // which is the one thing this must never do.
            if (TokenIsElevated(shellToken)) { why = "the desktop shell is itself running elevated"; return false; }
            if (!DuplicateTokenEx(shellToken, TOKEN_ASSIGN_PRIMARY | TOKEN_DUPLICATE | TOKEN_QUERY | TOKEN_ADJUST_DEFAULT | TOKEN_ADJUST_SESSIONID,
                                  IntPtr.Zero, SecurityImpersonation, TokenPrimary, out primary))
            { why = $"cannot duplicate the shell's token: {LastError()}"; return false; }

            // The command line is a buffer the API may write into, so it is an
            // array of our own rather than a pinned managed string.
            var commandLine = (Quote(exe) + " " + QuoteArguments(args) + '\0').ToCharArray();
            var si = new STARTUPINFO { cb = Marshal.SizeOf<STARTUPINFO>() };
            // A null environment means the new process gets the user's own,
            // from their profile - not this elevated process's copy.
            if (!CreateProcessWithTokenW(primary, 0, exe, commandLine, CREATE_NO_WINDOW, IntPtr.Zero,
                                         workingDirectory, ref si, out var pi))
            { why = $"CreateProcessWithToken failed: {LastError()}"; return false; }
            CloseHandle(pi.hThread);
            CloseHandle(pi.hProcess);
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
        finally
        {
            if (primary != IntPtr.Zero) CloseHandle(primary);
            if (shellToken != IntPtr.Zero) CloseHandle(shellToken);
            if (shellProcess != IntPtr.Zero) CloseHandle(shellProcess);
        }
    }

    /*--- command lines ---*/

    /// <summary>Arguments joined the way the C runtime's argv parser takes
    /// them apart, so a profile called "Night Sky" arrives as one argument and
    /// a quote inside a name survives. The same rules ProcessStartInfo's
    /// ArgumentList applies; they are here because the token launch takes a
    /// command line, not a list.</summary>
    public static string QuoteArguments(IEnumerable<string> args)
    {
        var sb = new StringBuilder();
        foreach (string arg in args)
        {
            if (sb.Length > 0) sb.Append(' ');
            sb.Append(Quote(arg));
        }
        return sb.ToString();
    }

    static string Quote(string arg)
    {
        if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '\n', '\v', '"' }) < 0) return arg;
        var sb = new StringBuilder(arg.Length + 2);
        sb.Append('"');
        int i = 0;
        while (i < arg.Length)
        {
            char c = arg[i++];
            if (c == '\\')
            {
                int slashes = 1;
                while (i < arg.Length && arg[i] == '\\') { i++; slashes++; }
                if (i == arg.Length) sb.Append('\\', slashes * 2);           // before the closing quote: doubled
                else if (arg[i] == '"') { sb.Append('\\', slashes * 2 + 1); sb.Append('"'); i++; }
                else sb.Append('\\', slashes);                                // backslashes elsewhere are literal
            }
            else if (c == '"') sb.Append("\\\"");
            else sb.Append(c);
        }
        sb.Append('"');
        return sb.ToString();
    }

    /*--- Win32 ---*/

    static bool TokenIsElevated(IntPtr token)
    {
        IntPtr buffer = Marshal.AllocHGlobal(4);
        try
        {
            if (!GetTokenInformation(token, TokenElevation, buffer, 4, out _)) return false;
            return Marshal.ReadInt32(buffer) != 0;
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    static string LastError() => new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error()).Message;

    const uint PROCESS_QUERY_INFORMATION = 0x0400;
    const uint TOKEN_ASSIGN_PRIMARY = 0x0001, TOKEN_DUPLICATE = 0x0002, TOKEN_QUERY = 0x0008,
               TOKEN_ADJUST_DEFAULT = 0x0080, TOKEN_ADJUST_SESSIONID = 0x0100;
    const int SecurityImpersonation = 2, TokenPrimary = 1, TokenElevation = 20;
    const uint CREATE_NO_WINDOW = 0x08000000;

    [DllImport("user32.dll")] static extern IntPtr GetShellWindow();
    [DllImport("user32.dll", SetLastError = true)] static extern uint GetWindowThreadProcessId(IntPtr window, out uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll", SetLastError = true)] static extern bool CloseHandle(IntPtr handle);
    [DllImport("advapi32.dll", SetLastError = true)] static extern bool OpenProcessToken(IntPtr process, uint access, out IntPtr token);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool DuplicateTokenEx(IntPtr token, uint access, IntPtr attributes, int impersonationLevel, int tokenType, out IntPtr duplicate);
    [DllImport("advapi32.dll", SetLastError = true)]
    static extern bool GetTokenInformation(IntPtr token, int infoClass, IntPtr info, int length, out int returned);
    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool CreateProcessWithTokenW(IntPtr token, uint logonFlags, string? applicationName, char[] commandLine,
                                               uint creationFlags, IntPtr environment, string? currentDirectory,
                                               ref STARTUPINFO startupInfo, out PROCESS_INFORMATION processInformation);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public string? lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }
}
