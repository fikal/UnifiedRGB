using System.Runtime.InteropServices;
using System.Security.Cryptography.X509Certificates;

namespace UnifiedRgb.Core.Native;

/// <summary>Authenticode verification for files we download and then EXECUTE
/// (the PawnIO kernel-driver installer). TLS only proves who served the bytes;
/// WinVerifyTrust proves who signed them and that they're intact, and the
/// publisher check proves it's the signer we expect.</summary>
public static class Authenticode
{
    /// <summary>True when the file carries a valid Authenticode signature whose
    /// signer certificate's subject contains <paramref name="expectedSubjectPart"/>
    /// (e.g. "CN=namazso.eu"). <paramref name="detail"/> says why it failed.</summary>
    public static bool IsSignedBy(string path, string expectedSubjectPart, out string detail)
    {
        try
        {
            if (!VerifyTrust(path, out int hr))
            {
                detail = $"signature not trusted (0x{hr:X8})";
                return false;
            }
            using var cert = new X509Certificate2(X509Certificate.CreateFromSignedFile(path));
            if (!cert.Subject.Contains(expectedSubjectPart, StringComparison.OrdinalIgnoreCase))
            {
                detail = $"signed by '{cert.Subject}', expected '{expectedSubjectPart}'";
                return false;
            }
            detail = cert.Subject;
            return true;
        }
        catch (Exception ex)
        {
            detail = ex.Message;
            return false;
        }
    }

    static bool VerifyTrust(string path, out int hr)
    {
        var fileInfo = new WINTRUST_FILE_INFO
        {
            cbStruct = (uint)Marshal.SizeOf<WINTRUST_FILE_INFO>(),
            pcwszFilePath = path,
        };
        IntPtr pFile = Marshal.AllocHGlobal((int)fileInfo.cbStruct);
        try
        {
            Marshal.StructureToPtr(fileInfo, pFile, false);
            var data = new WINTRUST_DATA
            {
                cbStruct = (uint)Marshal.SizeOf<WINTRUST_DATA>(),
                dwUIChoice = WTD_UI_NONE,
                fdwRevocationChecks = WTD_REVOKE_NONE,       // offline-safe; the chain itself still has to validate
                dwUnionChoice = WTD_CHOICE_FILE,
                pFile = pFile,
                dwStateAction = WTD_STATEACTION_IGNORE,
                dwProvFlags = WTD_SAFER_FLAG,
            };
            Guid action = WINTRUST_ACTION_GENERIC_VERIFY_V2;
            hr = WinVerifyTrust(IntPtr.Zero, ref action, ref data);
            return hr == 0;
        }
        finally { Marshal.FreeHGlobal(pFile); }
    }

    static readonly Guid WINTRUST_ACTION_GENERIC_VERIFY_V2 = new("00AAC56B-CD44-11d0-8CC2-00C04FC295EE");
    const uint WTD_UI_NONE = 2, WTD_REVOKE_NONE = 0, WTD_CHOICE_FILE = 1, WTD_STATEACTION_IGNORE = 0, WTD_SAFER_FLAG = 0x100;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct WINTRUST_FILE_INFO
    {
        public uint cbStruct;
        [MarshalAs(UnmanagedType.LPWStr)] public string pcwszFilePath;
        public IntPtr hFile;
        public IntPtr pgKnownSubject;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct WINTRUST_DATA
    {
        public uint cbStruct;
        public IntPtr pPolicyCallbackData;
        public IntPtr pSIPClientData;
        public uint dwUIChoice;
        public uint fdwRevocationChecks;
        public uint dwUnionChoice;
        public IntPtr pFile;
        public uint dwStateAction;
        public IntPtr hWVTStateData;
        public IntPtr pwszURLReference;
        public uint dwProvFlags;
        public uint dwUIContext;
        public IntPtr pSignatureSettings;
    }

    [DllImport("wintrust.dll", ExactSpelling = true)]
    static extern int WinVerifyTrust(IntPtr hwnd, ref Guid pgActionID, ref WINTRUST_DATA pWVTData);
}
