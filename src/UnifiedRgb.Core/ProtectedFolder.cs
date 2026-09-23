using System.Security.AccessControl;
using System.Security.Principal;

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
/// subfolder: a writable config folder is a writable code folder.</summary>
public static class ProtectedFolder
{
    static readonly SecurityIdentifier Admins = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    static readonly SecurityIdentifier Users = new(WellKnownSidType.BuiltinUsersSid, null);

    /// <summary>Anything in a rule that lets its holder change the folder or
    /// what is in it. Read and execute are fine for everyone; these are not.</summary>
    const FileSystemRights Writes =
        FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
        | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;

    /// <summary>Make <paramref name="path"/> exist as ours. A folder somebody
    /// else made under that name first is removed whole - its contents are
    /// theirs, not ours, and a folder that could be planted could be planted
    /// with an executable in it - and replaced. The ACL is (re)applied on every
    /// call, so a folder that was tampered with by an administrator's mistake
    /// is put right too, and read back before this says yes.
    ///
    /// False, with the reason, when this process cannot do that: it is not
    /// elevated (only an administrator can make Administrators the owner), or
    /// the folder resists. The caller must then NOT use the folder. Failing
    /// closed is the point: a bundle it cannot protect is a bundle it does not
    /// run.</summary>
    public static bool Ensure(string path, out string? why)
    {
        why = null;
        try
        {
            var dir = new DirectoryInfo(path);
            if (dir.Exists && !OwnedByAdministrators(dir))
            {
                Log.Warn("protected", $"{path} exists but was not made by an administrator - removing it");
                dir.Delete(recursive: true);
                dir.Refresh();
            }
            bool created = !dir.Exists;
            if (created) dir.Create();

            var acl = new DirectorySecurity();
            acl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags below = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            acl.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, below, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(Admins, FileSystemRights.FullControl, below, PropagationFlags.None, AccessControlType.Allow));
            acl.AddAccessRule(new FileSystemAccessRule(Users, FileSystemRights.ReadAndExecute, below, PropagationFlags.None, AccessControlType.Allow));
            acl.SetOwner(Admins);
            dir.SetAccessControl(acl);

            if (!IsProtected(path, out why)) return false;
            if (created) Log.Info("protected", $"{path} created, administrators only");
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    /// <summary>Does the folder's ACL, as it stands on disk, say what
    /// <see cref="Ensure"/> means it to say: owned by an administrator,
    /// nothing inherited, and no write right for anyone but Administrators
    /// and SYSTEM. Read back rather than assumed, because a SetAccessControl
    /// that partly failed is the kind of thing that fails quietly.</summary>
    public static bool IsProtected(string path, out string? why)
    {
        why = null;
        try
        {
            var dir = new DirectoryInfo(path);
            if (!dir.Exists) { why = "the folder does not exist"; return false; }
            if (!OwnedByAdministrators(dir)) { why = "the folder is not owned by an administrator"; return false; }
            var acl = dir.GetAccessControl(AccessControlSections.Access);
            if (!acl.AreAccessRulesProtected) { why = "the folder inherits permissions from its parent"; return false; }
            foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.AccessControlType != AccessControlType.Allow) continue;
                var who = (SecurityIdentifier)rule.IdentityReference;
                if (who.Equals(Admins) || who.Equals(LocalSystem)) continue;
                if ((rule.FileSystemRights & Writes) != 0)
                {
                    why = $"{Describe(who)} can write to the folder";
                    return false;
                }
            }
            return true;
        }
        catch (Exception ex)
        {
            why = ex.Message;
            return false;
        }
    }

    static bool OwnedByAdministrators(DirectoryInfo dir)
    {
        var owner = dir.GetAccessControl(AccessControlSections.Owner).GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        return owner != null && (owner.Equals(Admins) || owner.Equals(LocalSystem));
    }

    static string Describe(SecurityIdentifier sid)
    {
        try { return sid.Translate(typeof(NTAccount)).Value; }
        catch { return sid.Value; }
    }
}
