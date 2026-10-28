using System.Runtime.InteropServices;

namespace MyCpuMonitor.Core.Native;

internal static unsafe partial class NtDll
{
    public const int SystemPerformanceInformation = 2;
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int SystemPoolTagInformation = 22;
    public const int SystemMemoryListInformation = 80;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

    public const int ObjectTypesInformation = 3;

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryObject(nint handle, int objectInformationClass, void* objectInformation, uint length, uint* returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQueryTimerResolution(uint* maximumTime, uint* minimumTime, uint* currentTime);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformation(int systemInformationClass, void* systemInformation, uint length, uint* returnLength);

    [LibraryImport("ntdll.dll")]
    public static partial int NtQuerySystemInformationEx(int systemInformationClass, void* inputBuffer, uint inputLength,
        void* systemInformation, uint length, uint* returnLength);
}

internal static unsafe partial class Kernel32
{
    [LibraryImport("kernel32.dll")]
    public static partial ushort GetActiveProcessorGroupCount();

    [LibraryImport("kernel32.dll")]
    public static partial uint GetActiveProcessorCount(ushort groupNumber);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetPerformanceInfo(PerformanceInformation* performanceInformation, uint cb);

    public const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;
    public const uint SYNCHRONIZE = 0x00100000;
    public const uint WAIT_OBJECT_0 = 0;

    [LibraryImport("kernel32.dll")]
    public static partial nint GetCurrentProcess();

    [LibraryImport("kernel32.dll")]
    public static partial nint OpenProcess(uint desiredAccess, [MarshalAs(UnmanagedType.Bool)] bool inheritHandle, int processId);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint WaitForSingleObject(nint handle, uint milliseconds);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessTimes(nint process, long* creationTime, long* exitTime, long* kernelTime, long* userTime);

    [LibraryImport("kernel32.dll", EntryPoint = "K32GetProcessMemoryInfo")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessMemoryInfo(nint process, ProcessMemoryCountersEx* counters, uint cb);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessIoCounters(nint process, IoCounters* counters);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetProcessHandleCount(nint process, uint* handleCount);
}

/// <summary>
/// SYSTEM_PERFORMANCE_INFORMATION (undocumented; 376 bytes on current builds). Only the fields used are declared,
/// at offsets validated against GetPerformanceInfo, per-thread context switch sums and memory list sums.
/// Counts are 32-bit and wrap.
/// </summary>
[StructLayout(LayoutKind.Explicit, Size = 512)]
internal struct SystemPerformanceInformation
{
    [FieldOffset(44)] public uint AvailablePages;
    [FieldOffset(52)] public uint CommitLimit;
    [FieldOffset(60)] public uint PageFaultCount;
    [FieldOffset(80)] public uint PageReadCount;
    [FieldOffset(84)] public uint PageReadIoCount;
    [FieldOffset(112)] public uint PagedPoolPages;
    [FieldOffset(116)] public uint NonPagedPoolPages;
    [FieldOffset(296)] public uint ContextSwitches;
    [FieldOffset(308)] public uint SystemCalls;
}

/// <summary>SYSTEM_MEMORY_LIST_INFORMATION (page counts). Needs SeProfileSingleProcessPrivilege.</summary>
[StructLayout(LayoutKind.Sequential)]
internal unsafe struct SystemMemoryListInformation
{
    public nuint ZeroPageCount;
    public nuint FreePageCount;
    public nuint ModifiedPageCount;
    public nuint ModifiedNoWritePageCount;
    public nuint BadPageCount;
    public fixed ulong PageCountByPriority[8];
    public fixed ulong RepurposedPagesByPriority[8];
    public nuint ModifiedPageCountPageFile;
}

/// <summary>SYSTEM_POOLTAG (x64: 40 bytes). Array starts at offset 8 of SYSTEM_POOLTAG_INFORMATION (after Count + padding).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemPoolTag
{
    public uint Tag;
    public uint PagedAllocs;
    public uint PagedFrees;
    public nuint PagedUsed;
    public uint NonPagedAllocs;
    public uint NonPagedFrees;
    public nuint NonPagedUsed;
}

internal static partial class AdvApi32
{
    public const uint TOKEN_ADJUST_PRIVILEGES = 0x20, TOKEN_QUERY = 0x8, SE_PRIVILEGE_ENABLED = 0x2;

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct TokenPrivileges
    {
        public uint Count;
        public long Luid;
        public uint Attributes;
    }

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool OpenProcessToken(nint process, uint access, out nint token);

    [LibraryImport("advapi32.dll", EntryPoint = "LookupPrivilegeValueW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool LookupPrivilegeValue(string? system, string name, out long luid);

    [LibraryImport("advapi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool AdjustTokenPrivileges(nint token, [MarshalAs(UnmanagedType.Bool)] bool disableAll, ref TokenPrivileges state, uint length, nint previous, nint returnLength);

    /// <summary>Enables a privilege the (elevated) token holds but has disabled.</summary>
    public static bool EnablePrivilege(string name)
    {
        if (!OpenProcessToken(Kernel32.GetCurrentProcess(), TOKEN_ADJUST_PRIVILEGES | TOKEN_QUERY, out var token))
            return false;
        try
        {
            if (!LookupPrivilegeValue(null, name, out var luid))
                return false;
            var state = new TokenPrivileges { Count = 1, Luid = luid, Attributes = SE_PRIVILEGE_ENABLED };
            // Succeeds with ERROR_NOT_ALL_ASSIGNED (1300) when the token lacks the privilege.
            return AdjustTokenPrivileges(token, false, ref state, 0, 0, 0) && Marshal.GetLastPInvokeError() == 0;
        }
        finally
        {
            Kernel32.CloseHandle(token);
        }
    }
}

/// <summary>PROCESS_MEMORY_COUNTERS_EX.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct ProcessMemoryCountersEx
{
    public uint cb;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivateUsage;
}

/// <summary>IO_COUNTERS.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct IoCounters
{
    public ulong ReadOperationCount;
    public ulong WriteOperationCount;
    public ulong OtherOperationCount;
    public ulong ReadTransferCount;
    public ulong WriteTransferCount;
    public ulong OtherTransferCount;
}

[StructLayout(LayoutKind.Sequential)]
internal struct UnicodeString
{
    public ushort Length;
    public ushort MaximumLength;
    public nint Buffer;
}

/// <summary>SYSTEM_PROCESS_INFORMATION (x64: 256 bytes, followed by NumberOfThreads SYSTEM_THREAD_INFORMATION).</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemProcessInformation
{
    public uint NextEntryOffset;
    public uint NumberOfThreads;
    public long WorkingSetPrivateSize;
    public uint HardFaultCount;
    public uint NumberOfThreadsHighWatermark;
    public ulong CycleTime;
    public long CreateTime;
    public long UserTime;
    public long KernelTime;
    public UnicodeString ImageName;
    public int BasePriority;
    public nint UniqueProcessId;
    public nint InheritedFromUniqueProcessId;
    public uint HandleCount;
    public uint SessionId;
    public nuint UniqueProcessKey;
    public nuint PeakVirtualSize;
    public nuint VirtualSize;
    public uint PageFaultCount;
    public nuint PeakWorkingSetSize;
    public nuint WorkingSetSize;
    public nuint QuotaPeakPagedPoolUsage;
    public nuint QuotaPagedPoolUsage;
    public nuint QuotaPeakNonPagedPoolUsage;
    public nuint QuotaNonPagedPoolUsage;
    public nuint PagefileUsage;
    public nuint PeakPagefileUsage;
    public nuint PrivatePageCount;
    public long ReadOperationCount;
    public long WriteOperationCount;
    public long OtherOperationCount;
    public long ReadTransferCount;
    public long WriteTransferCount;
    public long OtherTransferCount;
}

/// <summary>SYSTEM_PROCESSOR_PERFORMANCE_INFORMATION. KernelTime includes IdleTime.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct SystemProcessorPerformanceInformation
{
    public long IdleTime;
    public long KernelTime;
    public long UserTime;
    public long DpcTime;
    public long InterruptTime;
    public uint InterruptCount;
}

/// <summary>PERFORMANCE_INFORMATION (psapi). Sizes in pages.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct PerformanceInformation
{
    public uint cb;
    public nuint CommitTotal;
    public nuint CommitLimit;
    public nuint CommitPeak;
    public nuint PhysicalTotal;
    public nuint PhysicalAvailable;
    public nuint SystemCache;
    public nuint KernelTotal;
    public nuint KernelPaged;
    public nuint KernelNonpaged;
    public nuint PageSize;
    public uint HandleCount;
    public uint ProcessCount;
    public uint ThreadCount;
}
