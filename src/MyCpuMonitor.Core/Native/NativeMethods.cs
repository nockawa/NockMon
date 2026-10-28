using System.Runtime.InteropServices;

namespace MyCpuMonitor.Core.Native;

internal static unsafe partial class NtDll
{
    public const int SystemProcessInformation = 5;
    public const int SystemProcessorPerformanceInformation = 8;
    public const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);

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
