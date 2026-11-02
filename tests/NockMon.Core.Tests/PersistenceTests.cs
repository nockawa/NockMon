using NockMon.Core.Layout;
using NockMon.Core.Metrics;
using NockMon.Core.Persistence;
using NockMon.Core.Settings;

namespace NockMon.Core.Tests;

public sealed class PersistenceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (!Directory.Exists(_dir))
            return;
        // Junctions first (removed, not followed): a recursive delete fails on them.
        foreach (var link in Directory.EnumerateDirectories(_dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
                     .Where(d => File.GetAttributes(d).HasFlag(FileAttributes.ReparsePoint)).ToList())
            Directory.Delete(link);
        Directory.Delete(_dir, true);
    }

    [Fact]
    public void MigrateLegacyRoot_CopiesTheOldFolderOnce_WithoutFollowingLinks()
    {
        var legacy = Path.Combine(_dir, "NockMon");
        var root = Path.Combine(_dir, "ProgramData", "S-1-5-21-1");
        Directory.CreateDirectory(Path.Combine(legacy, "Layouts"));
        File.WriteAllText(Path.Combine(legacy, "Layouts", "Default.json"), "{}");
        var outside = Path.Combine(_dir, "outside");
        Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "x");
        Junction(Path.Combine(legacy, "Plugins"), outside); // Planted: must not pull the target in.

        Assert.True(AppPaths.MigrateLegacyRoot(legacy, root));
        Assert.True(File.Exists(Path.Combine(root, "Layouts", "Default.json")));
        Assert.False(Path.Exists(Path.Combine(root, "Plugins")));
        Assert.False(Directory.Exists(legacy));
        Assert.True(Directory.Exists(legacy + " (moved to ProgramData)"));
        Assert.False(Directory.Exists(root + ".migrating"));

        Directory.CreateDirectory(legacy); // Both exist: the new folder wins, nothing is copied.
        Assert.False(AppPaths.MigrateLegacyRoot(legacy, root));
        Assert.True(Directory.Exists(legacy));
        Assert.False(AppPaths.MigrateLegacyRoot(Path.Combine(_dir, "missing"), Path.Combine(_dir, "other")));
    }

    [Fact]
    public void SecureFolder_CreatesAnAdministratorsOnlyFolder_AndMovesAnUntrustedOneAway()
    {
        if (!Environment.IsPrivilegedProcess)
            Assert.Skip("Needs elevation (an administrators-owned folder).");
        var path = Path.Combine(_dir, "Secure");

        Assert.Null(SecureFolder.Ensure(path));
        Assert.True(SecureFolder.IsSecured(path));
        Assert.Null(SecureFolder.Ensure(path)); // Already secured: kept.

        var squatted = Path.Combine(_dir, "Squatted");
        Directory.CreateDirectory(squatted); // Inherits the temp folder's ACL: the user may write.
        File.WriteAllText(Path.Combine(squatted, "planted.dll"), "x");
        var moved = SecureFolder.Ensure(squatted);
        Assert.NotNull(moved);
        Assert.True(File.Exists(Path.Combine(moved, "planted.dll")));
        Assert.True(SecureFolder.IsSecured(squatted));
        Assert.Empty(Directory.EnumerateFileSystemEntries(squatted));

        var link = Path.Combine(_dir, "Link");
        Junction(link, path); // A link, even to a secured folder, is not one.
        Assert.False(SecureFolder.IsSecured(link));
        Assert.NotNull(SecureFolder.Ensure(link));
        Assert.True(SecureFolder.IsSecured(link));
    }

    private static void Junction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", ["/c", "mklink", "/J", link, target])
        {
            CreateNoWindow = true,
            UseShellExecute = false,
            RedirectStandardOutput = true,
        })!;
        process.WaitForExit();
        Assert.True(Directory.Exists(link));
    }

    [Fact]
    public void Layout_RoundTrips_PolymorphicWidgetsAndSeries()
    {
        var layout = LayoutDocument.CreateDefault("Work");
        layout.Widgets.Add(new ValueWidgetConfig
        {
            Bounds = new GridRect(0, 8, 2, 2),
            Source = new ProcessSeries(ProcessField.Cpu, "devenv"),
        });
        layout.Widgets.Add(new ChartWidgetConfig
        {
            Bounds = new GridRect(2, 8, 4, 2),
            Series = [new ChartSeriesConfig { Source = new CoreSeries(CoreMetric.Dpc, 3), Color = "#FF8800" }],
        });

        var json = LayoutStore.Serialize(layout);
        var back = LayoutStore.Deserialize(json);

        Assert.Equal(json, LayoutStore.Serialize(back));
        Assert.Contains("\"$type\": \"processTable\"", json);
        Assert.Contains("\"kind\": \"process\"", json);
        Assert.Contains("\"field\": \"Cpu\"", json);
        var value = Assert.IsType<ValueWidgetConfig>(back.Widgets[3]);
        Assert.Equal(new ProcessSeries(ProcessField.Cpu, "devenv"), value.Source);
        var chart = Assert.IsType<ChartWidgetConfig>(back.Widgets[4]);
        Assert.Equal(new CoreSeries(CoreMetric.Dpc, 3), chart.Series[0].Source);
        Assert.Equal(new GridRect(2, 8, 4, 2), chart.Bounds);
    }

    [Fact]
    public void Layout_NewerSchema_IsRejected()
    {
        var json = LayoutStore.Serialize(LayoutDocument.CreateDefault()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 99");
        Assert.Throws<NotSupportedException>(() => LayoutStore.Deserialize(json));
    }

    [Fact]
    public void DefaultLayout_HasNoCollisions() => Assert.Empty(LayoutDocument.CreateDefault().FindCollisions());

    /// <summary>A new user's first view: the Idle process would top the CPU sort on a quiet machine.</summary>
    [Fact]
    public void DefaultLayout_ProcessTableExcludesIdle() =>
        Assert.True(Assert.Single(LayoutDocument.CreateDefault().Widgets.OfType<ProcessTableWidgetConfig>()).ExcludeIdle);

    [Theory]
    [InlineData(0, 0, 2, 2, 1, 1, 2, 2, true)]
    [InlineData(0, 0, 2, 2, 2, 0, 2, 2, false)]
    [InlineData(0, 0, 2, 2, 0, 2, 2, 2, false)]
    [InlineData(0, 0, 4, 4, 1, 1, 1, 1, true)]
    public void GridRect_Intersects(int c1, int r1, int w1, int h1, int c2, int r2, int w2, int h2, bool expected)
    {
        Assert.Equal(expected, new GridRect(c1, r1, w1, h1).Intersects(new GridRect(c2, r2, w2, h2)));
        Assert.Equal(expected, new GridRect(c2, r2, w2, h2).Intersects(new GridRect(c1, r1, w1, h1)));
    }

    [Fact]
    public void Deserialize_RepairsADamagedLayout()
    {
        const string json = """
            { "name": "Bad", "board": null, "pages": [ null, { "name": null, "widgets": [
                null,
                { "$type": "chart", "bounds": { "column": 2147483000, "row": -5, "columnSpan": 2147483000, "rowSpan": 0 },
                  "series": [ null, { "source": null } ] },
                { "$type": "value", "source": null },
                { "$type": "processTable", "columns": null },
                { "$type": "stack", "items": [ { "$type": "stack", "items": [ { "$type": "chart" } ] }, null ] },
                { "$type": "stack", "items": [] },
                { "$type": "plugin", "plugin": "", "widget": "w" } ] } ] }
            """;

        var layout = LayoutStore.Deserialize(json);

        var page = Assert.Single(layout.Pages);
        Assert.Equal("Main", page.Name);
        Assert.Equal(64, layout.Board.CellSize);
        Assert.Equal(3, page.Widgets.Count); // The chart, the table, the stack with an item; the rest dropped.
        var chart = Assert.IsType<ChartWidgetConfig>(page.Widgets[0]);
        Assert.Empty(chart.Series);
        Assert.Equal(new GridRect(LayoutRepair.MaxCoordinate, 0, LayoutRepair.MaxSpan, 1), chart.Bounds);
        Assert.False(chart.Bounds.Intersects(new GridRect(0, 0, 1, 1)));
        Assert.NotEmpty(Assert.IsType<ProcessTableWidgetConfig>(page.Widgets[1]).Columns);
        var stack = Assert.IsType<StackWidgetConfig>(page.Widgets[2]);
        Assert.IsType<ChartWidgetConfig>(Assert.Single(stack.Items)); // The nested stack flattened.
        Assert.Throws<InvalidDataException>(() => LayoutStore.Deserialize("""{ "name": null }"""));
    }

    [Fact]
    public void Store_SaveListLoadRenameDelete()
    {
        var store = new LayoutStore(_dir);
        Assert.Empty(store.List());

        store.Save(LayoutDocument.CreateDefault("Work"));
        store.Save(LayoutDocument.CreateDefault("Gaming: low"));
        Assert.Equal(["Gaming: low", "Work"], store.List());
        Assert.True(File.Exists(Path.Combine(_dir, "Gaming_ low.json")));
        Assert.Equal("Gaming: low", store.Load("Gaming: low").Name);

        store.Rename("Work", "Office");
        Assert.Equal(["Gaming: low", "Office"], store.List());
        store.Rename("Office", "office"); // Same file: rewritten in place, never deleted.
        Assert.Equal(["Gaming: low", "office"], store.List());
        store.Rename("office", "Office");
        // The new file is written before the old one goes: a failure keeps the layout.
        Directory.CreateDirectory(Path.Combine(_dir, "Locked.json")); // A folder where the new file would go.
        Assert.ThrowsAny<SystemException>(() => store.Rename("Office", "Locked"));
        Assert.Equal("Office", store.Load("Office").Name);
        Directory.Delete(Path.Combine(_dir, "Locked.json"));

        Assert.True(store.Delete("Office"));
        Assert.False(store.Delete("Office"));
        Assert.Throws<FileNotFoundException>(() => store.Load("Office"));
    }

    [Fact]
    public void Store_NamesSanitizingToSameFile_Conflict()
    {
        var store = new LayoutStore(_dir);
        store.Save(LayoutDocument.CreateDefault("a:b"));
        Assert.Throws<InvalidOperationException>(() => store.Save(LayoutDocument.CreateDefault("a?b")));
        store.Save(LayoutDocument.CreateDefault("A:B")); // Same name, different case: overwrite.
    }

    [Fact]
    public void Store_SkipsCorruptFiles()
    {
        var store = new LayoutStore(_dir);
        store.Save(LayoutDocument.CreateDefault("Good"));
        File.WriteAllText(Path.Combine(_dir, "bad.json"), "{ not json");
        Assert.Equal(["Good"], store.List());
    }

    [Fact]
    public void Settings_WindowPlacement_RoundTrips()
    {
        var store = new SettingsStore(Path.Combine(_dir, "settings.json"));
        Assert.Null(store.Load().Window);

        store.Save(new AppSettings { Window = new WindowPlacementSettings { Left = -1900, Top = 40, Right = -100, Bottom = 1040, Maximized = true } });
        var w = store.Load().Window!;
        Assert.Equal((-1900, 40, -100, 1040, true), (w.Left, w.Top, w.Right, w.Bottom, w.Maximized));
        Assert.True(w.IsValid);
        Assert.False(new WindowPlacementSettings { Right = 50, Bottom = 500 }.IsValid);
    }

    [Fact]
    public void Settings_RoundTrip_AndDefaultsOnCorrupt()
    {
        var path = Path.Combine(_dir, "settings.json");
        var store = new SettingsStore(path);
        Assert.Equal(new AppSettings().SampleIntervalMs, store.Load().SampleIntervalMs);

        store.Save(new AppSettings { SampleIntervalMs = 250, CpuScale = CpuScale.Cpus, ActiveLayout = "Work" });
        var back = store.Load();
        Assert.Equal(250, back.SampleIntervalMs);
        Assert.Equal(CpuScale.Cpus, back.CpuScale);
        Assert.DoesNotContain("sampleInterval\"", File.ReadAllText(path));

        File.WriteAllText(path, "garbage");
        Assert.Equal(new AppSettings().SampleIntervalMs, store.Load().SampleIntervalMs);
    }
}
