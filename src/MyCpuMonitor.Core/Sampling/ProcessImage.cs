using MyCpuMonitor.Core.Native;

namespace MyCpuMonitor.Core.Sampling;

/// <summary>Executable path of a running process.</summary>
public static unsafe class ProcessImage
{
    /// <summary>
    /// Win32 path of the process image, or null when the process has none (Idle, System, Registry, Memory Compression),
    /// has exited, or its PID now belongs to another process (create time mismatch).
    /// </summary>
    public static string? TryGetPath(int pid, long createTime)
    {
        if (pid <= 4)
            return null;
        var handle = Kernel32.OpenProcess(Kernel32.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (handle == 0)
            return null;
        try
        {
            long creation, exit, kernel, user;
            if (!Kernel32.GetProcessTimes(handle, &creation, &exit, &kernel, &user) || creation != createTime)
                return null;
            var buffer = stackalloc char[1024];
            var size = 1024u;
            return Kernel32.QueryFullProcessImageName(handle, 0, buffer, &size) ? new string(buffer, 0, (int)size) : null;
        }
        finally
        {
            Kernel32.CloseHandle(handle);
        }
    }
}
