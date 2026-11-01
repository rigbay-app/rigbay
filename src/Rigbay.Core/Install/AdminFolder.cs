using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace Rigbay.Core.Install;

/// <summary>Makes a folder writable only by Administrators and SYSTEM. Replaceable so unit tests do not lock their own scratch folders.</summary>
public interface IFolderSecurity
{
    /// <summary>Creates <paramref name="path"/> when missing and gives it the admin-only DACL. Throws <see cref="InstallException"/> (FolderSecurity) when that cannot be made true.</summary>
    void EnsureAdminOnly(string path);
}

/// <summary>Does nothing. For tests of the install flow that run without elevation.</summary>
public sealed class NoFolderSecurity : IFolderSecurity
{
    public void EnsureAdminOnly(string path) => Directory.CreateDirectory(path);
}

/// <summary>
/// The real thing: a protected DACL (no inheritance from the parent) with FullControl for BUILTIN\Administrators and NT AUTHORITY\SYSTEM
/// and no other entry, inherited by everything created below. A folder that already exists must be owned by Administrators or SYSTEM
/// (a standard user could have created it first and kept the right to change it) and must not be a link.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AdminFolderSecurity : IFolderSecurity
{
    private readonly bool _requireAdminOwner;

    /// <param name="requireAdminOwner">Production: true. A test that runs without elevation creates folders it owns itself and passes false for the owner check only; the DACL is built the same way.</param>
    public AdminFolderSecurity(bool requireAdminOwner = true) => _requireAdminOwner = requireAdminOwner;

    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);

    public void EnsureAdminOnly(string path)
    {
        try
        {
            if (Directory.Exists(path) && FileOps.IsReparsePoint(path))
                throw new InstallException(InstallErrorCode.FolderSecurity, $"'{path}' is a link, not a folder");
            bool existed = Directory.Exists(path);
            var info = Directory.CreateDirectory(path);

            var security = info.GetAccessControl();
            var owner = security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            if (_requireAdminOwner && (owner is null || (!owner.Equals(Administrators) && !owner.Equals(LocalSystem))))
            {
                if (!existed) throw new InstallException(InstallErrorCode.FolderSecurity, $"the new folder '{path}' is not owned by Administrators (is Rigbay running as administrator?)");
                // a folder somebody else created: try to take it over (works for an elevated administrator), otherwise refuse
                try
                {
                    security.SetOwner(Administrators);
                    info.SetAccessControl(security);
                }
                catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidOperationException or IOException)
                {
                    throw new InstallException(InstallErrorCode.FolderSecurity,
                        $"'{path}' exists and is owned by {owner?.Value ?? "an unknown account"}, not Administrators; delete it (as administrator) and try again", ex);
                }
            }

            if (IsExactlyAdminOnly(info.GetAccessControl(AccessControlSections.Access))) return;   // already right: setting it again would walk every file below for nothing

            var dacl = new DirectorySecurity();
            dacl.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
            const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
            dacl.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
            dacl.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, both, PropagationFlags.None, AccessControlType.Allow));
            info.SetAccessControl(dacl);

            var problem = Audit(path);
            if (problem is not null) throw new InstallException(InstallErrorCode.FolderSecurity, $"the permissions of '{path}' are not admin-only after setting them: {problem}");
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException or InvalidOperationException or ArgumentException)
        {
            throw new InstallException(InstallErrorCode.FolderSecurity, $"could not set permissions on '{path}': {ex.Message}", ex);
        }
    }

    private static bool IsExactlyAdminOnly(DirectorySecurity security)
    {
        if (!security.AreAccessRulesProtected) return false;
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        const InheritanceFlags both = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        return rules.Count == 2
            && rules.All(r => r.AccessControlType == AccessControlType.Allow && r.FileSystemRights == FileSystemRights.FullControl && !r.IsInherited && r.InheritanceFlags == both && r.PropagationFlags == PropagationFlags.None)
            && rules.Any(r => r.IdentityReference.Equals(Administrators)) && rules.Any(r => r.IdentityReference.Equals(LocalSystem));
    }

    /// <summary>
    /// Reads the DACL back and returns what is wrong with it for "only Administrators and SYSTEM may write", or null when it is fine:
    /// inheritance from the parent is on, or an allow entry for any other account carries a right that changes the folder or its content.
    /// </summary>
    public static string? Audit(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        if (!security.AreAccessRulesProtected) return "it still inherits permissions from its parent";
        return FindWriters(path);
    }

    /// <summary>The first account other than Administrators and SYSTEM that an allow entry lets change the folder or its content, or null. Does not look at inheritance, so it also fits a module folder inside the modules folder.</summary>
    public static string? FindWriters(string path)
    {
        var security = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access);
        const FileSystemRights writeLike = FileSystemRights.WriteData | FileSystemRights.AppendData | FileSystemRights.WriteExtendedAttributes
            | FileSystemRights.WriteAttributes | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles
            | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            var sid = (SecurityIdentifier)rule.IdentityReference;
            if (sid.Equals(Administrators) || sid.Equals(LocalSystem)) continue;
            if ((rule.FileSystemRights & writeLike) != 0) return $"{sid.Value} may write ({rule.FileSystemRights})";
        }
        return null;
    }
}
