using System.Diagnostics;
using System.Numerics;
using System.Security.Principal;
using NockMon.Core.Model;
using NockMon.Core.Processes;
using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

/// <summary>A ping that lasts two minutes: a disposable process of the current user to act on.</summary>
public sealed class Victim : IDisposable
{
    public Victim()
    {
        Process = Process.Start(new ProcessStartInfo("ping.exe", "-n 120 127.0.0.1") { CreateNoWindow = true, UseShellExecute = false })!;
        Key = new ProcessKey(Process.Id, Process.StartTime.ToFileTimeUtc());
    }

    public Process Process { get; }
    public ProcessKey Key { get; }

    public void Dispose()
    {
        try
        {
            Process.Kill();
        }
        catch (InvalidOperationException)
        {
        }
        Process.Dispose();
    }
}

public class ProcessControlTests
{
    private static ProcessStats Stats(int pid, int parent, long created) => new() { Pid = pid, ParentPid = parent, CreateTime = created, Name = "p" };

    [Fact]
    public void Descendants_WalksTheTree_IgnoresReusedParentPids()
    {
        ProcessStats[] processes =
        [
            Stats(10, 1, 100),
            Stats(11, 10, 110),
            Stats(12, 11, 120),
            Stats(13, 10, 90),  // Parent PID 10 belonged to an older process: not a child.
            Stats(14, 99, 130),
            Stats(15, 15, 140), // Its own parent (PID 0 style): no loop.
        ];

        Assert.Equal([new ProcessKey(11, 110), new ProcessKey(12, 120)], ProcessControl.Descendants(processes, new ProcessKey(10, 100)));
        Assert.Empty(ProcessControl.Descendants(processes, new ProcessKey(15, 140)));
        // Several roots (a grouped row): each descendant once, roots excluded.
        Assert.Equal([new ProcessKey(12, 120)], ProcessControl.Descendants(processes, [new ProcessKey(10, 100), new ProcessKey(11, 110)]));
    }

    [Fact]
    public void Kill_EndsTheProcess()
    {
        using var victim = new Victim();

        ProcessControl.Kill(victim.Key);

        Assert.True(victim.Process.WaitForExit(5000));
    }

    [Fact]
    public void Actions_RefuseAReusedPid_AndKernelProcesses()
    {
        using var victim = new Victim();
        var stale = victim.Key with { CreateTime = victim.Key.CreateTime - 1 };

        var e = Assert.Throws<ProcessControlException>(() => ProcessControl.Kill(stale));
        Assert.Contains("exited", e.Message);
        Assert.False(victim.Process.HasExited);
        Assert.Throws<ProcessControlException>(() => ProcessControl.Kill(new ProcessKey(4, 0)));
        Assert.True(ProcessControl.IsCritical(new ProcessKey(4, 0)));
    }

    [Fact]
    public void SuspendAndResume_FreezeAndThawEveryThread()
    {
        using var victim = new Victim();

        ProcessControl.Suspend(victim.Key);
        Assert.True(AllSuspended(victim.Process));
        ProcessControl.Resume(victim.Key);
        Assert.False(AllSuspended(victim.Process));
    }

    private static bool AllSuspended(Process process)
    {
        process.Refresh();
        return process.Threads.Cast<ProcessThread>().All(t => t.ThreadState == System.Diagnostics.ThreadState.Wait && t.WaitReason == ThreadWaitReason.Suspended);
    }

    [Fact]
    public void Priority_IsSetAndRead()
    {
        using var victim = new Victim();

        Assert.Equal(PriorityClass.BelowNormal, ProcessControl.SetPriority(victim.Key, PriorityClass.BelowNormal));

        Assert.Equal(PriorityClass.BelowNormal, ProcessControl.GetPriority(victim.Key));
        victim.Process.Refresh();
        Assert.Equal(ProcessPriorityClass.BelowNormal, victim.Process.PriorityClass);
    }

    [Fact]
    public void Affinity_IsSetAndRead()
    {
        if (Environment.ProcessorCount < 2 || !CpuTopology.SingleGroup)
            Assert.Skip("Needs several processors in one group.");
        using var victim = new Victim();

        ProcessControl.SetAffinity(victim.Key, 0b10);

        Assert.Equal(0b10UL, ProcessControl.GetAffinity(victim.Key));
        Assert.Throws<ProcessControlException>(() => ProcessControl.SetAffinity(victim.Key, 0));
    }

    [Fact]
    public void IsCritical_FalseForAnOrdinaryProcess()
    {
        using var victim = new Victim();

        Assert.False(ProcessControl.IsCritical(victim.Key));
    }

    [Fact]
    public void WriteDump_WritesAMinidump()
    {
        using var victim = new Victim();
        var path = Path.Combine(Path.GetTempPath(), $"nockmon-{Guid.NewGuid():N}.dmp");
        try
        {
            ProcessControl.WriteDump(victim.Key, path, full: false);

            var header = new byte[4];
            using (var file = File.OpenRead(path))
                file.ReadExactly(header);
            Assert.Equal("MDMP"u8.ToArray(), header);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Details_DescribeTheProcess()
    {
        using var victim = new Victim();

        var details = ProcessDetails.Query(victim.Key);

        Assert.EndsWith(@"\ping.exe", details.Path, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("127.0.0.1", details.CommandLine);
        Assert.Equal(WindowsIdentity.GetCurrent().Name, details.User, StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(details.Integrity);
        Assert.NotNull(details.Elevated);
        Assert.Null(details.Protection);
        Assert.Equal(PriorityClass.Normal, details.Priority);
        Assert.NotNull(details.Description);
    }

    [Fact]
    public void Details_OfAnExitedProcess_AreEmpty()
    {
        using var victim = new Victim();
        var key = victim.Key;
        victim.Process.Kill();
        victim.Process.WaitForExit();

        Assert.Null(ProcessDetails.Query(key).Path);
    }

    [Theory]
    [InlineData("S-1-16-0", "Untrusted")]
    [InlineData("S-1-16-4096", "Low")]
    [InlineData("S-1-16-8192", "Medium")]
    [InlineData("S-1-16-12288", "High")]
    [InlineData("S-1-16-16384", "System")]
    public void IntegrityName_MapsTheMandatoryLabel(string sid, string expected) =>
        Assert.Equal(expected, ProcessDetails.IntegrityName(new SecurityIdentifier(sid)));

    [Fact]
    public void CpuTopology_ListsEveryLogicalProcessor()
    {
        Assert.Equal(Environment.ProcessorCount, CpuTopology.All.Count);
        Assert.Equal(CpuTopology.All.Count, CpuTopology.All.Distinct().Count());
        if (CpuTopology.SingleGroup)
            Assert.Equal(Environment.ProcessorCount, BitOperations.PopCount(CpuTopology.AllMask));
        if (CpuTopology.IsHybrid)
        {
            var performance = CpuTopology.Mask(p => p.EfficiencyClass == CpuTopology.PerformanceClass);
            Assert.NotEqual(0UL, performance);
            Assert.NotEqual(CpuTopology.AllMask, performance);
        }
    }
}
