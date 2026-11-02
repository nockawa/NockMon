using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using Microsoft.Win32.SafeHandles;
using NockMon.Core.Model;
using NockMon.Core.Native;
using NockMon.Core.Sampling;

namespace NockMon.Core.Processes;

/// <summary>What a process is: image, command line, account, token. Each field is null when it cannot be read.</summary>
public sealed partial record ProcessDetails(
    string? Path,
    string? CommandLine,
    string? User,
    string? Integrity,
    bool? Elevated,
    string? Protection,
    PriorityClass? Priority,
    string? Description,
    string? Company,
    string? Version)
{
    /// <summary>Reads what the process allows: a protected process gives its path and protection, little else.</summary>
    public static unsafe ProcessDetails Query(ProcessKey key)
    {
        SafeProcessHandle process;
        try
        {
            process = ProcessControl.Open(key, 0);
        }
        catch (ProcessControlException)
        {
            return new ProcessDetails(null, null, null, null, null, null, null, null, null, null);
        }
        using (process)
        {
            var path = ProcessImage.TryGetPath(key.Pid, key.CreateTime);
            FileVersionInfo? version = null;
            try
            {
                if (path is not null)
                    version = FileVersionInfo.GetVersionInfo(path);
            }
            catch (FileNotFoundException)
            {
            }
            var (user, integrity, elevated) = ReadToken(process);
            var priority = GetPriorityClass(process);
            return new ProcessDetails(path, ReadCommandLine(process), user, integrity, elevated, ReadProtection(process),
                priority == 0 ? null : (PriorityClass)priority,
                Blank(version?.FileDescription), Blank(version?.CompanyName), Blank(version?.ProductVersion ?? version?.FileVersion));
        }
    }

    private static string? Blank(string? text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    private static unsafe string? ReadCommandLine(SafeProcessHandle process)
    {
        const int processCommandLineInformation = 60;
        uint size = 0;
        ProcessControl.NtQueryInformationProcess(process, processCommandLineInformation, null, 0, &size);
        if (size < (uint)sizeof(UnicodeString))
            return null;
        var buffer = new byte[size];
        fixed (byte* data = buffer)
        {
            if (ProcessControl.NtQueryInformationProcess(process, processCommandLineInformation, data, size, &size) < 0)
                return null;
            var text = (UnicodeString*)data;
            return text->Buffer == 0 ? null : new string((char*)text->Buffer, 0, text->Length / 2);
        }
    }

    /// <summary>PS_PROTECTION: "PPL (Windows)", "Protected (WinTcb)"…; null for an ordinary process.</summary>
    private static unsafe string? ReadProtection(SafeProcessHandle process)
    {
        const int processProtectionInformation = 61;
        byte protection;
        if (ProcessControl.NtQueryInformationProcess(process, processProtectionInformation, &protection, 1, null) < 0 || protection == 0)
            return null;
        var type = (protection & 7) switch { 1 => "PPL", 2 => "Protected", _ => "Protected?" };
        string[] signers = ["", "Authenticode", "CodeGen", "Antimalware", "Lsa", "Windows", "WinTcb", "WinSystem", "App"];
        var signer = protection >> 4;
        return signer > 0 && signer < signers.Length ? $"{type} ({signers[signer]})" : type;
    }

    private static unsafe (string? User, string? Integrity, bool? Elevated) ReadToken(SafeProcessHandle process)
    {
        const uint tokenQuery = 0x8;
        const int tokenUser = 1, tokenElevation = 20, tokenIntegrityLevel = 25;
        if (!OpenProcessToken(process, tokenQuery, out var token))
            return (null, null, null);
        using (token)
        {
            var buffer = stackalloc byte[256];
            string? user = null, integrity = null;
            bool? elevated = null;
            if (GetTokenInformation(token, tokenUser, buffer, 256, out _))
                user = AccountName(new SecurityIdentifier(*(nint*)buffer));
            if (GetTokenInformation(token, tokenIntegrityLevel, buffer, 256, out _))
                integrity = IntegrityName(new SecurityIdentifier(*(nint*)buffer));
            if (GetTokenInformation(token, tokenElevation, buffer, sizeof(uint), out _)) // Exact size, or ERROR_BAD_LENGTH.
                elevated = *(uint*)buffer != 0;
            return (user, integrity, elevated);
        }
    }

    private static string AccountName(SecurityIdentifier sid)
    {
        try
        {
            return sid.Translate(typeof(NTAccount)).Value;
        }
        catch (IdentityNotMappedException)
        {
            return sid.Value;
        }
    }

    /// <summary>Mandatory label S-1-16-RID → its level name.</summary>
    internal static string IntegrityName(SecurityIdentifier label)
    {
        var value = label.Value;
        var rid = int.TryParse(value[(value.LastIndexOf('-') + 1)..], out var parsed) ? parsed : -1;
        return rid switch
        {
            0 => "Untrusted",
            < 0x2000 and >= 0x1000 => "Low",
            0x2000 => "Medium",
            0x2100 => "Medium plus",
            0x3000 => "High",
            0x4000 => "System",
            >= 0x5000 => "Protected",
            _ => value,
        };
    }

    [LibraryImport("kernel32.dll")]
    private static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool OpenProcessToken(SafeProcessHandle process, uint access, out SafeAccessTokenHandle token);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static unsafe partial bool GetTokenInformation(SafeAccessTokenHandle token, int informationClass, void* information, uint length, out uint returnLength);
}
