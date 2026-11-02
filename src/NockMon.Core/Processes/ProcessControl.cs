using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using NockMon.Core.Model;
using NockMon.Core.Native;
using NockMon.Core.Sampling;

namespace NockMon.Core.Processes;

/// <summary>Win32 priority classes (SetPriorityClass values).</summary>
public enum PriorityClass : uint
{
    Idle = 0x40,
    BelowNormal = 0x4000,
    Normal = 0x20,
    AboveNormal = 0x8000,
    High = 0x80,
    Realtime = 0x100,
}

/// <summary>A process action that failed: the message is meant for the user.</summary>
public sealed class ProcessControlException(string message) : Exception(message);

/// <summary>
/// Actions on a process identified by <see cref="ProcessKey"/>: every call reopens the process and checks its create
/// time, so a PID reused since the key was taken is never touched. Debug privilege is enabled on first use (elevated:
/// services and other users' processes become reachable; protected processes still refuse).
/// </summary>
public static unsafe partial class ProcessControl
{
    internal const uint Terminate = 0x0001, VmRead = 0x0010, DupHandle = 0x0040, SetInformation = 0x0200,
        QueryInformation = 0x0400, SuspendResume = 0x0800, QueryLimited = 0x1000;

    private const int ProcessBreakOnTermination = 29;
    private static readonly Lazy<bool> DebugPrivilege = new(() => AdvApi32.EnablePrivilege("SeDebugPrivilege"));

    /// <summary>
    /// Enables SeDebugPrivilege (once; false without it, e.g. not elevated). The probe calls it at start: which processes
    /// can be opened (watched processes, details) must not change the first time a process action runs.
    /// </summary>
    public static bool EnableDebugPrivilege() => DebugPrivilege.Value;
    private static readonly Lock DumpLock = new(); // DbgHelp is single-threaded.

    /// <summary>
    /// Pseudo-processes no action applies to: Idle, System, and the kernel's own (Secure System, Registry, Memory
    /// Compression) which have no image either.
    /// </summary>
    public static bool IsKernelProcess(int pid) => pid <= 4;

    /// <summary>The descendants of <paramref name="root"/> in <paramref name="processes"/>, children before grandchildren.
    /// A process whose parent PID matches but started before the parent is not its child (the parent PID was reused).</summary>
    public static List<ProcessKey> Descendants(ReadOnlySpan<ProcessStats> processes, ProcessKey root)
    {
        var result = new List<ProcessKey>();
        var seen = new HashSet<ProcessKey> { root };
        var queue = new Queue<ProcessKey>();
        queue.Enqueue(root);
        while (queue.TryDequeue(out var parent))
        {
            foreach (ref readonly var p in processes)
            {
                var key = new ProcessKey(p.Pid, p.CreateTime);
                if (p.ParentPid != parent.Pid || p.Pid == parent.Pid || p.CreateTime < parent.CreateTime || !seen.Add(key))
                    continue;
                result.Add(key);
                queue.Enqueue(key);
            }
        }
        return result;
    }

    public static void Kill(ProcessKey key)
    {
        using var process = Open(key, Terminate);
        if (!TerminateProcess(process, 1))
            throw Failed("End", Marshal.GetLastPInvokeError());
    }

    public static void Suspend(ProcessKey key)
    {
        using var process = Open(key, SuspendResume);
        var status = NtSuspendProcess(process);
        if (status < 0)
            throw Failed("Suspend", (int)RtlNtStatusToDosError(status));
    }

    public static void Resume(ProcessKey key)
    {
        using var process = Open(key, SuspendResume);
        var status = NtResumeProcess(process);
        if (status < 0)
            throw Failed("Resume", (int)RtlNtStatusToDosError(status));
    }

    public static PriorityClass? GetPriority(ProcessKey key)
    {
        try
        {
            using var process = Open(key, QueryLimited);
            var value = GetPriorityClass(process);
            return value == 0 ? null : (PriorityClass)value;
        }
        catch (ProcessControlException)
        {
            return null;
        }
    }

    /// <returns>The class the process has now: Windows silently gives High instead of Realtime without the privilege.</returns>
    public static PriorityClass SetPriority(ProcessKey key, PriorityClass priority)
    {
        if (priority == PriorityClass.Realtime)
            AdvApi32.EnablePrivilege("SeIncreaseBasePriorityPrivilege");
        using var process = Open(key, SetInformation | QueryLimited);
        if (!SetPriorityClass(process, (uint)priority))
            throw Failed("Set priority", Marshal.GetLastPInvokeError());
        var actual = GetPriorityClass(process);
        return actual == 0 ? priority : (PriorityClass)actual; // 0: could not be read back; it was set.
    }

    /// <returns>The process's affinity mask (processor group 0), or null if it cannot be read.</returns>
    public static ulong? GetAffinity(ProcessKey key)
    {
        try
        {
            using var process = Open(key, QueryLimited);
            return GetProcessAffinityMask(process, out var mask, out _) ? mask : null;
        }
        catch (ProcessControlException)
        {
            return null;
        }
    }

    public static void SetAffinity(ProcessKey key, ulong mask)
    {
        if (mask == 0)
            throw new ProcessControlException("Choose at least one processor.");
        using var process = Open(key, SetInformation | QueryLimited);
        if (!SetProcessAffinityMask(process, (nuint)mask))
            throw Failed("Set affinity", Marshal.GetLastPInvokeError());
    }

    /// <summary>
    /// True for a critical process (its end is a system crash: csrss, wininit, smss, services, lsass…); null when it
    /// cannot be told (protected processes refuse the query, and also refuse to be ended).
    /// </summary>
    public static bool? IsCritical(ProcessKey key)
    {
        if (IsKernelProcess(key.Pid))
            return true;
        try
        {
            using var process = Open(key, QueryInformation);
            uint critical;
            return NtQueryInformationProcess(process, ProcessBreakOnTermination, &critical, sizeof(uint), null) >= 0 ? critical != 0 : null;
        }
        catch (ProcessControlException)
        {
            return null;
        }
    }

    /// <summary>Writes a minidump (<paramref name="full"/>: with the whole memory, as Task Manager's dump) to <paramref name="path"/>.</summary>
    public static void WriteDump(ProcessKey key, string path, bool full)
    {
        // Handles, threads and unloaded modules in both; full adds every committed page.
        const uint withHandleData = 0x4, withUnloadedModules = 0x20, withIndirectMemory = 0x40, withProcessThreadData = 0x100,
            withFullMemory = 0x2, withFullMemoryInfo = 0x800, withThreadInfo = 0x1000;
        var type = withHandleData | withUnloadedModules | withFullMemoryInfo | withThreadInfo
                   | (full ? withFullMemory : withIndirectMemory | withProcessThreadData);
        using var process = Open(key, QueryInformation | VmRead | DupHandle);
        try
        {
            using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.None))
            {
                bool written;
                int error;
                lock (DumpLock)
                {
                    written = MiniDumpWriteDump(process, (uint)key.Pid, file.SafeFileHandle, type, 0, 0, 0);
                    error = Marshal.GetLastPInvokeError();
                }
                if (!written)
                    throw Failed("Dump", error & 0xFFFF); // HRESULT_FROM_WIN32 for most failures.
            }
        }
        catch
        {
            TryDelete(path);
            throw;
        }
    }

    /// <summary>Opens the process if it is still the one <paramref name="key"/> names.</summary>
    internal static SafeProcessHandle Open(ProcessKey key, uint access)
    {
        if (IsKernelProcess(key.Pid))
            throw new ProcessControlException("Not available for a kernel process.");
        _ = DebugPrivilege.Value;
        var process = OpenProcess(access | QueryLimited, false, key.Pid);
        if (process.IsInvalid)
        {
            var error = Marshal.GetLastPInvokeError();
            process.Dispose();
            throw error == 87 /* ERROR_INVALID_PARAMETER: no such PID */ ? Exited() : Failed("Open", error);
        }
        long creation, exit, kernel, user;
        if (!Kernel32.GetProcessTimes(process.DangerousGetHandle(), &creation, &exit, &kernel, &user) || creation != key.CreateTime)
        {
            process.Dispose();
            throw Exited();
        }
        return process;
    }

    private static ProcessControlException Exited() => new("The process has exited.");

    private static ProcessControlException Failed(string action, int error) => new(error == 5
        ? $"{action}: access denied (protected process, or a process of the system)."
        : $"{action}: {new Win32Exception(error).Message}");

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial SafeProcessHandle OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool TerminateProcess(SafeProcessHandle process, uint exitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    private static partial uint GetPriorityClass(SafeProcessHandle process);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetPriorityClass(SafeProcessHandle process, uint priorityClass);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetProcessAffinityMask(SafeProcessHandle process, out ulong processMask, out ulong systemMask);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetProcessAffinityMask(SafeProcessHandle process, nuint mask);

    [LibraryImport("ntdll.dll")]
    private static partial int NtSuspendProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    private static partial int NtResumeProcess(SafeProcessHandle process);

    [LibraryImport("ntdll.dll")]
    internal static partial int NtQueryInformationProcess(SafeProcessHandle process, int informationClass, void* information, uint length, uint* returnLength);

    [LibraryImport("ntdll.dll")]
    private static partial uint RtlNtStatusToDosError(int status);

    [LibraryImport("dbghelp.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool MiniDumpWriteDump(SafeProcessHandle process, uint processId, SafeFileHandle file, uint dumpType,
        nint exceptionParam, nint userStreamParam, nint callbackParam);
}
