using System.Diagnostics;
using System.Security.Principal;
using NockMon.Core.Agents;
using NockMon.Core.Folders;
using NockMon.Core.History;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Persistence;
using NockMon.Core.Sampling;

namespace NockMon.Core.Tests;

/// <summary>
/// A temporary tree: root/a.txt (100), sub1/b.bin (1000), sub1/deep/c.bin (500), sub2/d.log (10), node_modules/x (5000).
/// </summary>
public sealed class FolderTree : IDisposable
{
    public FolderTree()
    {
        Root = Path.Combine(Path.GetTempPath(), "nockmon-folder-" + Guid.NewGuid().ToString("N"));
        File(@"a.txt", 100);
        File(@"sub1\b.bin", 1000);
        File(@"sub1\deep\c.bin", 500);
        File(@"sub2\d.log", 10);
        File(@"node_modules\x", 5000);
    }

    public string Root { get; }

    public void File(string relative, int bytes)
    {
        var path = Path.Combine(Root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(Root, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}

public class FolderScanTests
{
    [Theory]
    [InlineData(@"C:\work\.git\objects\ab", true)] // Inside an excluded folder.
    [InlineData(@"C:\work\src\node_modules\x\y.js", true)]
    [InlineData(@"C:\work\.git", false)] // The excluded folder itself: not inside it.
    [InlineData(@"C:\work\src\a.cs", false)]
    [InlineData(@"C:\work\gitlog.txt", false)]
    public void IsExcluded_ChangesInsideExcludedFolders(string path, bool expected) =>
        Assert.Equal(expected, FolderScan.IsExcluded(@"C:\work", FolderScan.Patterns(".git;node_modules"), path));

    [Fact]
    public void CountsSizeFilesFolders_AndLargestSubfolders()
    {
        using var tree = new FolderTree();

        var r = FolderScan.Run(new FolderScope(tree.Root));

        Assert.Null(r.Error);
        Assert.Equal(6610, r.Bytes);
        Assert.Equal(5, r.Files);
        Assert.Equal(4, r.Folders); // sub1, sub1\deep, sub2, node_modules.
        Assert.Equal(0, r.Unreadable);
        Assert.Equal([new FolderPart("node_modules", 5000), new FolderPart("sub1", 1500), new FolderPart("sub2", 10)], r.Largest);
    }

    [Fact]
    public void NotRecursive_CountsTheFolderOnly()
    {
        using var tree = new FolderTree();

        var r = FolderScan.Run(new FolderScope(tree.Root, Recursive: false));

        Assert.Equal((100L, 1L, 3L), (r.Bytes, r.Files, r.Folders)); // Its 3 subfolders counted, not walked.
        Assert.Empty(r.Largest);
    }

    [Fact]
    public void IncludeAndExclude_FilterFilesAndFolders()
    {
        using var tree = new FolderTree();

        var included = FolderScan.Run(new FolderScope(tree.Root, Include: "*.bin; *.LOG"));
        var excluded = FolderScan.Run(new FolderScope(tree.Root, Exclude: "node_*"));

        Assert.Equal((1510L, 3L, 4L), (included.Bytes, included.Files, included.Folders));
        Assert.Equal((1610L, 4L, 3L), (excluded.Bytes, excluded.Files, excluded.Folders));
        Assert.DoesNotContain(excluded.Largest, p => p.Name == "node_modules");
    }

    [Fact]
    public void Junction_IsNotFollowed()
    {
        using var tree = new FolderTree();
        var link = Path.Combine(tree.Root, "sub2", "loop");
        using (var mklink = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{tree.Root}\"") { CreateNoWindow = true, UseShellExecute = false })!)
            mklink.WaitForExit();
        Assert.True(Directory.Exists(link), "junction not created");

        var r = FolderScan.Run(new FolderScope(tree.Root));

        Assert.Equal((6610L, 5L, 4L), (r.Bytes, r.Files, r.Folders)); // The loop back to the root is neither walked nor counted.
        Directory.Delete(link); // The junction only, not its target.
    }

    [Fact]
    public void SizeOnDisk_RoundsEachFileToClusters()
    {
        using var tree = new FolderTree();

        var r = FolderScan.Run(new FolderScope(tree.Root, SizeOnDisk: true));

        // NTFS 4 KB clusters (the temp volume): four files under 4 KB take one cluster, the 5000-byte one two.
        Assert.Equal(0, r.Bytes % 4096);
        Assert.True(r.Bytes >= 6 * 4096, $"{r.Bytes}");
        Assert.True(r.Bytes > 6610);
    }

    [Fact]
    public void MissingFolder_IsAnError()
    {
        var r = FolderScan.Run(new FolderScope(@"C:\does\not\exist\" + Guid.NewGuid()));

        Assert.StartsWith("Folder not found", r.Error);
        Assert.Equal(0, r.Bytes);
    }

    [Fact]
    public void UnreadableSubfolder_IsCountedNotFatal()
    {
        using var tree = new FolderTree();
        var locked = Path.Combine(tree.Root, "sub2");
        var user = WindowsIdentity.GetCurrent().Name;
        Icacls($"\"{locked}\" /deny \"{user}:(RD)\""); // List folder denied.
        if (CanList(locked))
        {
            Icacls($"\"{locked}\" /remove:d \"{user}\"");
            Assert.Skip("This process bypasses folder permissions (backup privilege enabled).");
        }
        try
        {
            var r = FolderScan.Run(new FolderScope(tree.Root));

            Assert.Null(r.Error);
            Assert.Equal(1, r.Unreadable);
            Assert.Equal(6600, r.Bytes); // sub2\d.log (10) not counted.
        }
        finally
        {
            Icacls($"\"{locked}\" /remove:d \"{user}\"");
        }
    }

    private static bool CanList(string folder)
    {
        try
        {
            Directory.GetFileSystemEntries(folder);
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Icacls(string arguments)
    {
        using var icacls = Process.Start(new ProcessStartInfo("icacls.exe", arguments) { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        icacls.StandardOutput.ReadToEnd();
        icacls.WaitForExit();
        Assert.Equal(0, icacls.ExitCode);
    }

    [Fact]
    public void Scope_NameAndPathComparison()
    {
        Assert.Equal("Downloads", new FolderScope(@"C:\Users\me\Downloads\").Name);
        Assert.Equal(@"D:\", new FolderScope(@"D:\").Name);
        Assert.True(new FolderScope(@"C:\Data").IsPath(@" c:\data\ "));
        Assert.False(new FolderScope(@"C:\Data").IsPath(@"C:\Data2"));
    }
}

public class FolderScannerTests
{
    private static T WaitFor<T>(Func<T?> probe, TimeSpan timeout) where T : class
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (probe() is { } value)
                return value;
            Thread.Sleep(50);
        }
        throw new TimeoutException("Condition not met in time.");
    }

    [Fact]
    public void ScansWatched_ThenRescansSoonAfterAChange()
    {
        using var tree = new FolderTree();
        using var scanner = new FolderScanner();
        var scope = new FolderScope(tree.Root);

        scanner.SetWatched([new FolderRequest(scope, TimeSpan.FromHours(1), WatchChanges: true)]);
        var first = WaitFor(() => scanner.Latest(scope), TimeSpan.FromSeconds(10));
        Assert.Equal(6610, first.Bytes);
        Assert.Equal(scope, Assert.Single(scanner.Readings).Scope);

        tree.File(@"sub2\new.bin", 2000); // Hour-long interval: only the watcher can bring this in.
        var second = WaitFor(() => scanner.Latest(scope) is { Bytes: 8610 } r ? r : null, TimeSpan.FromSeconds(15));
        Assert.Equal(6, second.Files);
    }

    [Fact]
    public void Requests_MergePerScope_AndRemovalStopsTheScope()
    {
        using var tree = new FolderTree();
        using var scanner = new FolderScanner();
        var scope = new FolderScope(tree.Root);

        scanner.SetWatched([new FolderRequest(scope, TimeSpan.FromMinutes(5), false), new FolderRequest(scope, TimeSpan.FromSeconds(1), true),
            new FolderRequest(new FolderScope(" "), TimeSpan.FromSeconds(1), false)]);
        Assert.Equal(scope, Assert.Single(scanner.Scopes));
        WaitFor(() => scanner.Latest(scope), TimeSpan.FromSeconds(10));

        scanner.SetWatched([]);
        Assert.Empty(scanner.Scopes);
        Assert.Empty(scanner.Readings);
        Assert.Null(scanner.Latest(scope));
    }

    [Fact]
    public void FailingFolder_HasAnErrorButNoReading()
    {
        using var scanner = new FolderScanner();
        var scope = new FolderScope(@"C:\does\not\exist\" + Guid.NewGuid());

        scanner.SetWatched([new FolderRequest(scope, TimeSpan.FromMinutes(1), true)]);
        var result = WaitFor(() => scanner.Latest(scope), TimeSpan.FromSeconds(10));

        Assert.NotNull(result.Error);
        Assert.Empty(scanner.Readings);
    }
}

public class FolderHistoryTests
{
    private static readonly FolderScope Scope = new(@"C:\Data");
    private static readonly FolderScanResult Result = new(DateTime.UtcNow, 5_000, 12, 3, 0, TimeSpan.Zero, []);

    private static void Append(HistoryStore h, params FolderReading[] folders) => h.AppendSample(new Snapshot { Cores = [new CoreStats()] }, [], folders);

    [Fact]
    public void FolderSeries_StartAtTheFirstScan_AndEndWhenNoLongerWatched()
    {
        var h = new HistoryStore(capacity: 10, scanCapacity: 4, coreCount: 1);
        Append(h);
        Append(h);
        Append(h, new FolderReading(Scope, Result));
        Append(h, new FolderReading(Scope, Result with { Bytes = 6_000, Files = 13 }));
        var buffer = new float[10];

        Assert.Equal(2, h.Read(new FolderSeries(FolderMetric.Size, @"C:\Data"), buffer)); // Not 4: no values before the first scan.
        Assert.Equal([5_000f, 6_000f], buffer[..2]);
        Assert.Equal(2, h.Read(new FolderSeries(FolderMetric.Files, @"C:\Data"), buffer));
        Assert.Equal([12f, 13f], buffer[..2]);
        Assert.Equal(0, h.Read(new FolderSeries(FolderMetric.Size, @"C:\Data", Include: "*.log"), buffer)); // Another scope.

        Append(h);
        Assert.Equal(0, h.Read(new FolderSeries(FolderMetric.Size, @"C:\Data"), buffer));
        Assert.Empty(h.FolderScopes);
    }

    [Fact]
    public void LayoutPatterns_Folders_FromWidgetsAndSeries()
    {
        var layout = LayoutDocument.CreateDefault();
        layout.Widgets.Add(new FolderWidgetConfig { Path = @"C:\Data", ScanSeconds = 30, WatchChanges = true, Exclude = ".git" });
        layout.Widgets.Add(new ChartWidgetConfig { Series = [new ChartSeriesConfig { Source = new FolderSeries(FolderMetric.Files, @"D:\Logs", Recursive: false) }] });

        var requests = LayoutPatterns.Folders(layout).ToList();

        Assert.Equal(
            [new FolderRequest(new FolderScope(@"C:\Data", Exclude: ".git"), TimeSpan.FromSeconds(30), true),
             new FolderRequest(new FolderScope(@"D:\Logs", Recursive: false), TimeSpan.FromSeconds(FolderWidgetConfig.DefaultScanSeconds), false)],
            requests);
    }

    [Fact]
    public void FolderWidgetAndSeries_RoundTrip_WithDefaultsForOmittedProperties()
    {
        var widget = new FolderWidgetConfig { Path = @"%TEMP%", Include = "*.log", SizeOnDisk = true, ScanSeconds = 15, ShowFiles = false };
        var back = Assert.IsType<FolderWidgetConfig>(LayoutStore.DeserializeWidget(LayoutStore.SerializeWidget(widget)));
        Assert.Equal((widget.Path, widget.Include, widget.SizeOnDisk, widget.ScanSeconds, widget.ShowFiles, widget.Recursive),
            (back.Path, back.Include, back.SizeOnDisk, back.ScanSeconds, back.ShowFiles, back.Recursive));

        var chart = (ChartWidgetConfig)LayoutStore.DeserializeWidget(
            """{ "$type": "chart", "series": [ { "source": { "kind": "folder", "metric": "Size", "path": "C:\\Data" } } ] }""");
        Assert.Equal(new FolderSeries(FolderMetric.Size, @"C:\Data"), chart.Series[0].Source); // Recursive by default.
    }

    [Fact]
    public void MetricIds_FolderTargetResolvesToTheWatchedScope()
    {
        FolderScope[] watched = [new(@"C:\Data", Exclude: ".git")];

        Assert.Equal(new FolderSeries(FolderMetric.Files, @"C:\Data", Exclude: ".git"),
            MetricIds.ToSeries("folder.Files", @"c:\data\", [], 4, watched));
        Assert.Throws<AgentException>(() => MetricIds.ToSeries("folder.Size", @"C:\Other", [], 4, watched));
        Assert.Throws<AgentException>(() => MetricIds.ToSeries("folder.Size", null, [], 4, watched));
        Assert.Contains(MetricIds.All(), m => m.Id == "folder.Size" && m.History);
    }
}
