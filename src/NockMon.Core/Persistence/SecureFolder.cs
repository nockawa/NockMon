using System.Security.AccessControl;
using System.Security.Principal;

namespace NockMon.Core.Persistence;

/// <summary>
/// A folder only administrators and SYSTEM can modify: owned by administrators, protected DACL (nothing inherited),
/// OWNER RIGHTS limited to reading (a file created with the user as owner gives that user no implicit WRITE_DAC).
/// </summary>
internal static class SecureFolder
{
    private static readonly SecurityIdentifier Administrators = new(WellKnownSidType.BuiltinAdministratorsSid, null);
    private static readonly SecurityIdentifier LocalSystem = new(WellKnownSidType.LocalSystemSid, null);
    private static readonly SecurityIdentifier OwnerRights = new("S-1-3-4");

    /// <summary>Any right that changes the folder, its contents or its security; GENERIC_ALL and GENERIC_WRITE included.</summary>
    private const FileSystemRights Modify = FileSystemRights.Write | FileSystemRights.Delete | FileSystemRights.DeleteSubdirectoriesAndFiles |
        FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership | (FileSystemRights)0x50000000;

    /// <summary>
    /// Creates <paramref name="path"/> secured, or checks the existing one. An entry that is not secured (created first
    /// by someone else, where users may create folders, like %ProgramData%; or a link) is renamed away, never used:
    /// it may hold planted files.
    /// </summary>
    /// <returns>Where an untrusted entry was moved, or null.</returns>
    /// <exception cref="UnauthorizedAccessException">The folder keeps being replaced.</exception>
    public static string? Ensure(string path)
    {
        string? moved = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            if (!Exists(path))
            {
                try
                {
                    new DirectoryInfo(path).Create(Security());
                }
                catch (IOException)
                {
                    // Created meanwhile by another process: checked below.
                }
            }
            if (IsSecured(path))
                return moved;
            moved = $"{path}.untrusted-{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid().ToString("N")[..6]}";
            // Administrators may rename in the parent whatever the entry's own security; a link is renamed, not followed.
            if (File.GetAttributes(path).HasFlag(FileAttributes.Directory))
                Directory.Move(path, moved);
            else
                File.Move(path, moved);
        }
        throw new UnauthorizedAccessException($"{path} keeps being replaced by another program: NockMon cannot secure its data folder.");
    }

    /// <summary>A real folder (not a link), owned by administrators or SYSTEM, which only they can modify.</summary>
    internal static bool IsSecured(string path)
    {
        var attributes = File.GetAttributes(path);
        if (!attributes.HasFlag(FileAttributes.Directory) || attributes.HasFlag(FileAttributes.ReparsePoint))
            return false;
        var security = new DirectoryInfo(path).GetAccessControl();
        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner || (owner != Administrators && owner != LocalSystem))
            return false;
        if (!security.AreAccessRulesProtected)
            return false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType == AccessControlType.Allow && (rule.FileSystemRights & Modify) != 0 &&
                rule.IdentityReference is SecurityIdentifier sid && sid != Administrators && sid != LocalSystem)
                return false;
        }
        return true;
    }

    private static DirectorySecurity Security()
    {
        var security = new DirectorySecurity();
        security.SetOwner(Administrators);
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        const InheritanceFlags inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        security.AddAccessRule(new FileSystemAccessRule(LocalSystem, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        security.AddAccessRule(new FileSystemAccessRule(OwnerRights, FileSystemRights.ReadAndExecute, inherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }

    private static bool Exists(string path)
    {
        try
        {
            File.GetAttributes(path); // The entry itself, even a link to nowhere.
            return true;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return false;
        }
    }
}
