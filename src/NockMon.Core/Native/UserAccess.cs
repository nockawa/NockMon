using System.Runtime.InteropServices;
using System.Security.AccessControl;

namespace NockMon.Core.Native;

/// <summary>
/// What the user could do without elevation. NockMon runs elevated, so requests from the user's non-elevated processes
/// (AI agents) must not make it reach what those processes could not: they are checked against the elevated token's
/// linked token, the one Windows gives the user's other programs.
/// </summary>
public static unsafe partial class UserAccess
{
    private const uint TOKEN_QUERY = 0x8;
    private const int TokenLinkedToken = 19;
    private const uint FILE_LIST_DIRECTORY = 0x1;

    /// <summary>
    /// True if the user, not elevated, may list <paramref name="path"/> (an existing folder). Not elevated (tests,
    /// profiling builds), the process's own rights.
    /// </summary>
    public static bool CanListFolder(string path)
    {
        if (!Directory.Exists(path))
            return false;
        if (!Environment.IsPrivilegedProcess)
            return CanEnumerate(path);
        byte[] descriptor;
        try
        {
            descriptor = new DirectoryInfo(path).GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group)
                .GetSecurityDescriptorBinaryForm();
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException or PrivilegeNotHeldException)
        {
            return false;
        }
        if (!AdvApi32.OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_QUERY, out var token))
            return false;
        nint linked = 0;
        try
        {
            // An elevated token's linked token is the filtered one, at identification level: usable for AccessCheck.
            if (!GetTokenInformation(token, TokenLinkedToken, &linked, (uint)sizeof(nint), out _))
                return CanEnumerate(path); // No split token (UAC off): the user's programs run with these rights anyway.
            var mapping = new GenericMapping { Read = 0x120089, Write = 0x120116, Execute = 0x1200A0, All = 0x1F01FF };
            var privileges = stackalloc byte[256];
            var privilegesLength = 256u;
            fixed (byte* sd = descriptor)
            {
                return AccessCheck(sd, linked, FILE_LIST_DIRECTORY, &mapping, privileges, &privilegesLength, out var granted, out var status) &&
                       status && (granted & FILE_LIST_DIRECTORY) != 0;
            }
        }
        finally
        {
            if (linked != 0)
                Kernel32.CloseHandle(linked);
            Kernel32.CloseHandle(token);
        }
    }

    /// <summary>
    /// True when this elevated process has a linked (filtered) token: UAC splits the administrator's rights, and the
    /// user's other programs run without them. False with UAC off (build machines), or not elevated.
    /// </summary>
    public static bool HasLinkedToken()
    {
        if (!Environment.IsPrivilegedProcess || !AdvApi32.OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_QUERY, out var token))
            return false;
        nint linked = 0;
        try
        {
            return GetTokenInformation(token, TokenLinkedToken, &linked, (uint)sizeof(nint), out _) && linked != 0;
        }
        finally
        {
            if (linked != 0)
                Kernel32.CloseHandle(linked);
            Kernel32.CloseHandle(token);
        }
    }

    private static bool CanEnumerate(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception e) when (e is UnauthorizedAccessException or IOException)
        {
            return false;
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct GenericMapping
    {
        public uint Read;
        public uint Write;
        public uint Execute;
        public uint All;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetTokenInformation(nint token, int informationClass, void* information, uint length, out uint returnLength);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AccessCheck(void* securityDescriptor, nint clientToken, uint desiredAccess, GenericMapping* mapping,
        void* privilegeSet, uint* privilegeSetLength, out uint grantedAccess, [MarshalAs(UnmanagedType.Bool)] out bool accessStatus);
}
