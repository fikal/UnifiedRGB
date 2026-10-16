using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;

namespace UnifiedRgb.Core;

/// <summary>A folder only administrators can write, for content an ELEVATED
/// process is going to execute.
///
/// The app runs as administrator and launches the bundled OpenRGB. Wherever
/// that executable lives, whoever can write there can choose what the
/// administrator runs - and every process of the user, elevated or not, can
/// write to LocalAppData. So the bundle lives in a folder with an explicit
/// ACL: owned by Administrators, writable by Administrators and SYSTEM only,
/// readable by everyone, nothing inherited from the parent and the same rules
/// inherited by everything inside. The config OpenRGB writes goes inside it
/// too, because OpenRGB loads plugins from its config folder's plugins/
/// subfolder: a writable config folder is a writable code folder.
///
/// The folders live under %ProgramData%, where any user may create an entry
/// before we ever run - a folder of their own, or a junction pointing at one.
/// So nothing here follows a link or trusts a path between two calls: what
/// stands at the name is examined through a handle that does not follow
/// reparse points, a folder is created with its ACL in the same call that
/// creates it, and anything that is not already ours is moved out of the way
/// rather than adopted or walked into.</summary>
public static class ProtectedFolder
{
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Anything in a rule that lets its holder change the folder or
    /// what is in it. Read and execute are fine for everyone; these are not.
    /// The generic bits too: a raw ACE can carry GENERIC_WRITE or GENERIC_ALL
    /// unmapped, and neither overlaps the specific rights above them.</summary>
    const FileSystemRights Writes =
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership
        | (FileSystemRights)0x40000000 /* GENERIC_WRITE */ | (FileSystemRights)0x10000000 /* GENERIC_ALL */;

    /// <summary>Whose folder it is: the owner it is created with, and the only
    /// principals that may own it or hold a write right in it (an owner can
    /// always rewrite the ACL, so owning is writing).
    ///
    /// The app has exactly one, <see cref="AdministratorsOnly"/>. The type
    /// exists so the harness, which is not elevated and so cannot make
    /// Administrators the owner of anything, can run the whole of Ensure in a
    /// temp folder as itself - the junction and race paths included.</summary>
    internal sealed class Policy
    {
        public SecurityIdentifier Owner { get; }
        public IReadOnlyList<SecurityIdentifier> Writers { get; }
        public Policy(SecurityIdentifier owner, params SecurityIdentifier[] writers)
        {
            Owner = owner;
            Writers = writers;
        }
        public bool MayWrite(SecurityIdentifier sid) => Writers.Any(w => w.Equals(sid));
    }

    internal static readonly Policy AdministratorsOnly = new(Admins, LocalSystem, Admins);

    /// <summary>Runs between clearing the name and creating the folder. For
    /// the harness only: it is where a test re-plants a junction, the race
    /// the atomic create exists to lose.</summary>
    internal static Action<string>? BeforeCreate;

    /// <summary>Make <paramref name="path"/> exist as ours.
    ///
    /// A folder already standing there that is ours - a real directory, owned
    /// by an administrator, its ACL as below - is used as it is. Anything else
    /// under the name (a folder somebody else made, a junction or symbolic
    /// link, a file) is renamed aside, never deleted: deleting a tree another
    /// user controls means walking into it as administrator, and they can turn
    /// any folder in it into a junction while we walk. The folder is then
    /// created with its ACL in one call, so there is no moment at which it
    /// exists with the inherited ProgramData ACL (which lets any user add
    /// files), and read back before this says yes. Something that appears
    /// under the name between the rename and the create is not adopted: the
    /// create fails and so does this.
    ///
    /// The folder it is made in must not be a link either. Callers ensure each
    /// level top-down (MachineDir, then the folder inside it), so for every
    /// level after the first the parent was verified a moment ago; the first
    /// level's parent is ProgramData itself, which no ordinary user can
    /// create or replace.
    ///
    /// False, with the reason, when this process cannot do that: it is not
    /// elevated (only an administrator can make Administrators the owner), or
    /// the folder resists. The caller must then NOT use the folder. Failing
    /// closed is the point: a bundle it cannot protect is a bundle it does not
    /// run.</summary>
    public static bool Ensure(string path, out string? why) => Ensure(path, AdministratorsOnly, out why);

    internal static bool Ensure(string path, Policy policy, out string? why)
    {
        why = null;
        try
        {
            string full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
            string? parent = Path.GetDirectoryName(full);
            if (parent == null) { why = $"{full} is a drive root, not a folder"; return false; }
            // The entry's OWN attributes: a link as the last component is not followed.
            if ((File.GetAttributes(parent) & FileAttributes.ReparsePoint) != 0)
            {
                why = $"{parent} is a junction or symbolic link, not a folder - refusing to make anything through it";
                return false;
            }

            switch (Inspect(full, policy, out string? problem))
            {
                case Found.Ours:
                    return true;
                case Found.Foreign:
                    string aside = $"{full}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..8]}";
                    // A rename moves the entry itself - a link, not what it
                    // points at - and never looks inside it.
                    if ((File.GetAttributes(full) & FileAttributes.Directory) != 0) Directory.Move(full, aside);
                    else File.Move(full, aside);
                    Log.Warn("protected", $"{full}: {problem} - moved it aside to {aside} (not deleted: it is not "
                                          + "ours to walk into) and making a new one");
                    break;
            }

            BeforeCreate?.Invoke(full);
            if (!CreateWithAcl(full, policy, out why)) return false;
            if (Inspect(full, policy, out why) != Found.Ours) return false;
            Log.Info("protected", $"{full} created, administrators only");
            return true;
        }
        // Fail closed with the reason: every caller logs it and refuses the folder.
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    /// <summary>Does the folder, as it stands on disk, say what
    /// <see cref="Ensure"/> means it to say: a real directory (not a junction
    /// or link), owned by an administrator, nothing inherited, and no write
    /// right for anyone but Administrators and SYSTEM. Read back rather than
    /// assumed, because a security descriptor that partly failed to apply is
    /// the kind of thing that fails quietly.</summary>
    public static bool IsProtected(string path, out string? why) => Verify(path, AdministratorsOnly, out why);

    internal static bool Verify(string path, Policy policy, out string? why)
    {
        try { return Inspect(path, policy, out why) == Found.Ours; }
        // Unreadable is not protected: the reason goes back to the caller.
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    enum Found { Missing, Ours, Foreign }

    /// <summary>What stands at <paramref name="path"/>, examined through ONE
    /// handle that does not follow reparse points, so the attributes and the
    /// security descriptor describe the same object - not a folder at the
    /// first read and a junction swapped in by the second.
    ///
    /// A link is refused whatever its descriptor says. The old check read only
    /// the descriptor, and a junction carries one of its own: the old
    /// create-then-ACL, run on a junction re-planted under the name, had just
    /// stamped the administrators-only ACL onto the junction itself, so it
    /// passed as protected - and everything put "inside" it went to the
    /// folder it pointed at, one its planter could write.</summary>
    static Found Inspect(string path, Policy policy, out string? why)
    {
        why = null;
        using var h = CreateFile(path, READ_CONTROL | FILE_READ_ATTRIBUTES,
            FILE_SHARE_READ | FILE_SHARE_WRITE | FILE_SHARE_DELETE, IntPtr.Zero, OPEN_EXISTING,
            FILE_FLAG_BACKUP_SEMANTICS | FILE_FLAG_OPEN_REPARSE_POINT, IntPtr.Zero);
        if (h.IsInvalid)
        {
            int err = Marshal.GetLastPInvokeError();
            if (err is ERROR_FILE_NOT_FOUND or ERROR_PATH_NOT_FOUND) { why = "the folder does not exist"; return Found.Missing; }
            why = $"it could not be opened to check who owns it ({new Win32Exception(err).Message})";
            return Found.Foreign;
        }
        var attrs = File.GetAttributes(h);
        if ((attrs & FileAttributes.ReparsePoint) != 0) { why = "it is a junction or symbolic link, not a folder"; return Found.Foreign; }
        if ((attrs & FileAttributes.Directory) == 0) { why = "it is a file, not a folder"; return Found.Foreign; }

        var acl = ReadSecurity(h);
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || !policy.MayWrite(owner))
        {
            why = "the folder is not owned by an administrator";
            return Found.Foreign;
        }
        if (!acl.AreAccessRulesProtected) { why = "the folder inherits permissions from its parent"; return Found.Foreign; }
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var who = (SecurityIdentifier)rule.IdentityReference;
            if (policy.MayWrite(who)) continue;
            if ((rule.FileSystemRights & Writes) != 0)
            {
                why = $"{Describe(who)} can write to the folder";
                return Found.Foreign;
            }
        }
        return Found.Ours;
    }

    static DirectorySecurity BuildAcl(Policy policy)
    {
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags below = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        foreach (var writer in policy.Writers)
            acl.AddAccessRule(new FileSystemAccessRule(writer, FileSystemRights.FullControl, below, PropagationFlags.None, AccessControlType.Allow));
        acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, below, PropagationFlags.None, AccessControlType.Allow));
        acl.SetOwner(policy.Owner);
        return acl;
    }

    /// <summary>CreateDirectory with the security descriptor, so the folder is
    /// born administrators-only. .NET's own create (and the ACL overload of
    /// it) succeeds quietly when the name already exists, which would adopt
    /// whatever was planted there; this fails instead.</summary>
    static bool CreateWithAcl(string path, Policy policy, out string? why)
    {
        why = null;
        byte[] sd = BuildAcl(policy).GetSecurityDescriptorBinaryForm();
        IntPtr p = Marshal.AllocHGlobal(sd.Length);
        try
        {
            Marshal.Copy(sd, 0, p, sd.Length);
            var sa = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), lpSecurityDescriptor = p };
            if (CreateDirectory(path, ref sa)) return true;
            int err = Marshal.GetLastPInvokeError();
            why = err switch
            {
                ERROR_ALREADY_EXISTS => $"something else appeared at {path} while it was being made - not using it",
                ERROR_INVALID_OWNER => "only an elevated administrator can make Administrators the owner of a folder",
                _ => $"{path} could not be created ({new Win32Exception(err).Message})",
            };
            return false;
        }
        finally { Marshal.FreeHGlobal(p); }
    }

    static DirectorySecurity ReadSecurity(SafeFileHandle h)
    {
        int err = GetSecurityInfo(h, SE_FILE_OBJECT, OWNER_SECURITY_INFORMATION | DACL_SECURITY_INFORMATION,
            out _, out _, out _, out _, out IntPtr sd);
        if (err != 0) throw new Win32Exception(err);
        try
        {
            var bytes = new byte[GetSecurityDescriptorLength(sd)];
            Marshal.Copy(sd, bytes, 0, bytes.Length);
            var acl = new DirectorySecurity();
            acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Owner | AccessControlSections.Access);
            return acl;
        }
        finally { LocalFree(sd); }
    }

    static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }   // a SID with no name (a deleted account): the SID itself is the answer
    }

    /*-----------------------------------------------------*\
    | Win32                                                  |
    \*-----------------------------------------------------*/
    const uint READ_CONTROL = 0x00020000, FILE_READ_ATTRIBUTES = 0x0080;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
    const uint OPEN_EXISTING = 3;
    const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000, FILE_FLAG_OPEN_REPARSE_POINT = 0x00200000;
    const int ERROR_FILE_NOT_FOUND = 2, ERROR_PATH_NOT_FOUND = 3, ERROR_ALREADY_EXISTS = 183, ERROR_INVALID_OWNER = 1307;
    const int SE_FILE_OBJECT = 1;
    const uint OWNER_SECURITY_INFORMATION = 1, DACL_SECURITY_INFORMATION = 4;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES { public int nLength; public IntPtr lpSecurityDescriptor; public int bInheritHandle; }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateFileW")]
    static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security,
        uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "CreateDirectoryW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectory(string path, ref SECURITY_ATTRIBUTES security);

    [DllImport("advapi32.dll")]
    static extern int GetSecurityInfo(SafeFileHandle handle, int objectType, uint securityInfo,
        out IntPtr owner, out IntPtr group, out IntPtr dacl, out IntPtr sacl, out IntPtr descriptor);

    [DllImport("advapi32.dll")]
    static extern int GetSecurityDescriptorLength(IntPtr descriptor);

    [DllImport("kernel32.dll")]
    static extern IntPtr LocalFree(IntPtr mem);
}
