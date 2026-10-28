using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Persistence;

namespace MyCpuMonitor.Core.Tests;

public class BoardGeometryTests
{
    private readonly BoardGeometry _geo = new(CellSize: 64, Gap: 4); // Pitch 68.

    [Fact]
    public void ToPixels_IncludesInnerGapsOnly()
    {
        Assert.Equal(new PixelRect(4, 4, 64, 64), _geo.ToPixels(new GridRect(0, 0, 1, 1)));
        Assert.Equal(new PixelRect(4 + 68 * 2, 4 + 68, 3 * 68 - 4, 2 * 68 - 4), _geo.ToPixels(new GridRect(2, 1, 3, 2)));
    }

    [Theory]
    [InlineData(0, 0)]
    [InlineData(71, 0)]
    [InlineData(72, 1)]
    [InlineData(4 + 68 * 10, 10)]
    public void FitCount(double pixels, int expected) => Assert.Equal(expected, _geo.FitCount(pixels));

    [Fact]
    public void Extent_IsInverseOfFitCount() => Assert.Equal(10, _geo.FitCount(_geo.Extent(10)));

    [Theory]
    [InlineData(33, 0, 0)]
    [InlineData(34, 0, 1)]
    [InlineData(-34, 0, -1)]
    [InlineData(150, -70, 2)]
    public void Move_SnapsToNearestCell(double dx, double dy, int expectedColumnShift)
    {
        var moved = _geo.Move(new GridRect(5, 5, 2, 2), dx, dy);
        Assert.Equal(5 + expectedColumnShift, moved.Column);
        Assert.Equal((2, 2), (moved.ColumnSpan, moved.RowSpan));
    }

    [Fact]
    public void Move_ClampsAtZero() => Assert.Equal(new GridRect(0, 0, 2, 2), _geo.Move(new GridRect(1, 1, 2, 2), -500, -500));

    [Fact]
    public void Resize_RespectsMinimum()
    {
        Assert.Equal(new GridRect(1, 1, 4, 3), _geo.Resize(new GridRect(1, 1, 2, 2), 136, 68, new GridSize(1, 1)));
        Assert.Equal(new GridRect(1, 1, 3, 2), _geo.Resize(new GridRect(1, 1, 5, 5), -1000, -1000, new GridSize(3, 2)));
    }
}

public class BoardRulesTests
{
    private static ValueWidgetConfig W(int c, int r, int w, int h) =>
        new() { Bounds = new GridRect(c, r, w, h), Source = new Metrics.SystemSeries(Metrics.SystemMetric.Cpu) };

    [Fact]
    public void CanPlace_RejectsOverlapOutOfBoundsAndNegative()
    {
        var a = W(0, 0, 2, 2);
        List<WidgetConfig> widgets = [a, W(4, 0, 2, 2)];

        Assert.True(BoardRules.CanPlace(widgets, null, new GridRect(2, 0, 2, 2)));
        Assert.False(BoardRules.CanPlace(widgets, null, new GridRect(1, 1, 2, 2)));
        Assert.False(BoardRules.CanPlace(widgets, null, new GridRect(-1, 3, 1, 1)));
        Assert.False(BoardRules.CanPlace(widgets, null, new GridRect(6, 0, 2, 2), maxColumns: 7));
        Assert.True(BoardRules.CanPlace(widgets, null, new GridRect(6, 0, 1, 2), maxColumns: 7));
    }

    [Fact]
    public void CanPlace_IgnoresTheMovingWidget()
    {
        var a = W(0, 0, 2, 2);
        Assert.True(BoardRules.CanPlace([a], a, new GridRect(1, 1, 2, 2)));
        Assert.False(BoardRules.CanPlace([a], null, new GridRect(1, 1, 2, 2)));
    }

    [Fact]
    public void FindFreeSpot_RowMajor_OrNullWhenFull()
    {
        List<WidgetConfig> widgets = [W(0, 0, 2, 2), W(3, 0, 1, 1)];

        Assert.Equal(new GridRect(2, 0, 1, 1), BoardRules.FindFreeSpot(widgets, new GridSize(1, 1), 4, 4));
        Assert.Equal(new GridRect(2, 1, 2, 2), BoardRules.FindFreeSpot(widgets, new GridSize(2, 2), 4, 4));
        Assert.Null(BoardRules.FindFreeSpot(widgets, new GridSize(3, 3), 4, 4));
    }

    [Fact]
    public void Extent_CoversAllWidgets() =>
        Assert.Equal(new GridSize(6, 5), BoardRules.Extent([W(0, 0, 2, 2), W(4, 3, 2, 2)]));

    [Fact]
    public void Catalog_DefaultSizesAreAtLeastMinimum_AndKindsRoundTrip()
    {
        foreach (var kind in WidgetCatalog.Kinds)
        {
            var def = WidgetCatalog.DefaultSize(kind);
            var min = WidgetCatalog.MinSize(kind);
            Assert.True(def.Columns >= min.Columns && def.Rows >= min.Rows, kind.ToString());
            Assert.Equal(kind, WidgetCatalog.KindOf(WidgetCatalog.Create(kind, new GridRect(0, 0, def.Columns, def.Rows))));
        }
    }
}

public class GridArrangementTests
{
    [Theory]
    [InlineData(32, 800, 400, 7, 5)]   // 114×80 (1.43); 8×4 is 100×100 (1.0), 6×6 is 133×67 (2.0).
    [InlineData(32, 1600, 300, 11, 3)] // 145×100 (1.45); 12×3 is 1.33, 16×2 is 0.67.
    [InlineData(32, 300, 1200, 2, 16)] // 150×75 (2.0); 3×11 is 0.92.
    [InlineData(1, 100, 100, 1, 1)]
    [InlineData(0, 100, 100, 0, 0)]
    public void Auto_PicksCellsClosestToTargetAspect(int count, double w, double h, int columns, int rows) =>
        Assert.Equal((columns, rows), GridArrangement.Arrange(count, w, h));

    [Fact]
    public void Fixed_Columns()
    {
        Assert.Equal((5, 7), GridArrangement.Arrange(32, 100, 100, columns: 5));
        Assert.Equal((32, 1), GridArrangement.Arrange(32, 100, 100, columns: 40));
    }

    [Fact]
    public void CpuGridConfig_RoundTrips()
    {
        var layout = new LayoutDocument
        {
            Name = "x",
            Widgets = [new CpuGridWidgetConfig { Metric = Metrics.CoreMetric.Dpc, ShowKernel = false, WindowSeconds = 300, Columns = 4 }],
        };
        var json = LayoutStore.Serialize(layout);
        Assert.Contains("\"$type\": \"cpuGrid\"", json);
        var back = Assert.IsType<CpuGridWidgetConfig>(LayoutStore.Deserialize(json).Widgets[0]);
        Assert.Equal((Metrics.CoreMetric.Dpc, false, 300, 4), (back.Metric, back.ShowKernel, back.WindowSeconds, back.Columns));
    }
}

public sealed class LayoutSessionTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "mcm-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, true);
    }

    private LayoutSession NewSession() =>
        new(new LayoutStore(Path.Combine(_dir, "Layouts")), new SettingsStore(Path.Combine(_dir, "settings.json")));

    [Fact]
    public void FirstRun_CreatesDefaultLayout()
    {
        var session = NewSession();
        Assert.Equal("Default", session.Current.Name);
        Assert.Equal(["Default"], session.Names);
        Assert.NotEmpty(session.Current.Widgets);
    }

    [Fact]
    public void ActiveLayout_IsRestored()
    {
        var session = NewSession();
        session.CreateEmpty("Work");
        Assert.Empty(session.Current.Widgets);

        Assert.Equal("Work", NewSession().Current.Name);
    }

    [Fact]
    public void EditSession_CommitSaves_CancelRestores()
    {
        var session = NewSession();
        var count = session.Current.Widgets.Count;

        session.BeginEdit();
        session.Current.Widgets.RemoveAt(0);
        session.CancelEdit();
        Assert.Equal(count, session.Current.Widgets.Count);

        session.BeginEdit();
        session.Current.Widgets.RemoveAt(0);
        session.CommitEdit();
        Assert.Equal(count - 1, NewSession().Current.Widgets.Count);
    }

    [Fact]
    public void Management_IsRefusedWhileEditing()
    {
        var session = NewSession();
        session.BeginEdit();
        Assert.Throws<InvalidOperationException>(() => session.Duplicate("Copy"));
        Assert.Throws<InvalidOperationException>(() => session.BeginEdit());
    }

    [Fact]
    public void DuplicateRenameDelete()
    {
        var session = NewSession();
        session.Duplicate("Copy");
        Assert.Equal(["Copy", "Default"], session.Names);
        Assert.Throws<InvalidOperationException>(() => session.Duplicate("default"));

        session.Rename("Renamed");
        Assert.Equal(["Default", "Renamed"], session.Names);
        Assert.Equal("Renamed", NewSession().Current.Name);

        session.Delete();
        Assert.Equal("Default", session.Current.Name);
        session.Delete();
        Assert.Equal(["Default"], session.Names); // Never left without a layout.
    }
}
