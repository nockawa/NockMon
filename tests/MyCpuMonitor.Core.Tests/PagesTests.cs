using MyCpuMonitor.Core.Gpu;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;

namespace MyCpuMonitor.Core.Tests;

public class PagesTests
{
    private static ChartWidgetConfig Chart(string pattern, GridRect bounds) => new()
    {
        Bounds = bounds,
        Series = [new ChartSeriesConfig { Source = new ProcessSeries(ProcessField.Cpu, pattern) }],
    };

    [Fact]
    public void NewLayout_HasOnePage_AndWidgetsAreTheShownPages()
    {
        var a = Chart("a", new GridRect(0, 0, 2, 2));
        var layout = new LayoutDocument { Name = "L", Widgets = [a] };
        Assert.Same(a, Assert.Single(Assert.Single(layout.Pages).Widgets));

        LayoutPages.Add(layout, "Second", "cpu");
        Assert.Equal(1, layout.ActivePage);
        Assert.Empty(layout.Widgets);
        layout.ActivePage = 0;
        Assert.Same(a, Assert.Single(layout.Widgets));
        layout.ActivePage = 7;
        Assert.Equal(1, layout.ActivePage); // Clamped.
    }

    [Fact]
    public void Pages_RoundTripThroughJson_WithActivePage_EvenWhenItComesFirst()
    {
        var layout = new LayoutDocument { Name = "L", Widgets = [Chart("a", new GridRect(0, 0, 2, 2))] };
        LayoutPages.Add(layout, "GPU", "gpu");
        layout.Widgets.Add(Chart("b", new GridRect(0, 0, 2, 2)));

        var back = LayoutStore.Deserialize(LayoutStore.Serialize(layout));
        Assert.Equal(["Main", "GPU"], back.Pages.Select(p => p.Name));
        Assert.Equal(["board", "gpu"], back.Pages.Select(p => p.Icon));
        Assert.Equal(1, back.ActivePage);
        Assert.Equal("b", ((ProcessSeries)((ChartWidgetConfig)Assert.Single(back.Widgets)).Series[0].Source).Pattern);

        var reordered = LayoutStore.Deserialize("""{ "name": "X", "activePage": 1, "pages": [ { "name": "A" }, { "name": "B" } ] }""");
        Assert.Equal("B", reordered.CurrentPage.Name);
        Assert.Single(LayoutStore.Deserialize("""{ "name": "X", "pages": [] }""").Pages);
    }

    [Fact]
    public void HiddenPages_KeepTheirSeriesWatched()
    {
        var layout = new LayoutDocument { Name = "L", Widgets = [Chart("shown", new GridRect(0, 0, 2, 2))] };
        LayoutPages.Add(layout, "Hidden");
        layout.Widgets.Add(Chart("hidden", new GridRect(0, 0, 2, 2)));
        layout.Widgets.Add(new GpuEnginesWidgetConfig { Adapter = "NV", EnginePattern = "3D", Bounds = new GridRect(2, 0, 2, 2) });
        layout.ActivePage = 0;

        Assert.Equal(["shown", "hidden"], LayoutPatterns.Watched(layout));
        Assert.Equal([("NV", "3D")], LayoutPatterns.GpuEngines(layout, Array.Empty<GpuAdapterInfo>()));
    }

    [Fact]
    public void Remove_KeepsShownPage_RefusesTheLast()
    {
        var layout = new LayoutDocument { Name = "L" };
        LayoutPages.Add(layout, "B");
        LayoutPages.Add(layout, "C");
        layout.ActivePage = 2;

        LayoutPages.Remove(layout, 0);
        Assert.Equal("C", layout.CurrentPage.Name);
        LayoutPages.Remove(layout, 1); // The shown one: its neighbour is shown.
        Assert.Equal("B", layout.CurrentPage.Name);
        Assert.Throws<InvalidOperationException>(() => LayoutPages.Remove(layout, 0));
    }

    [Fact]
    public void Move_ReordersAndKeepsShownPage()
    {
        var layout = new LayoutDocument { Name = "L" };
        LayoutPages.Add(layout, "B");
        LayoutPages.Add(layout, "C");
        layout.ActivePage = 0;

        LayoutPages.Move(layout, 0, 2);
        Assert.Equal(["B", "C", "Main"], layout.Pages.Select(p => p.Name));
        Assert.Equal("Main", layout.CurrentPage.Name);
    }

    [Fact]
    public void Duplicate_CopiesWidgetsWithNewIds_AfterTheSource()
    {
        var stack = new StackWidgetConfig { Bounds = new GridRect(0, 0, 2, 2), Items = [Chart("a", new GridRect(0, 0, 2, 2)), Chart("b", new GridRect(0, 0, 2, 2))] };
        var layout = new LayoutDocument { Name = "L", Widgets = [stack] };
        LayoutPages.Add(layout, "Other");

        var copy = LayoutPages.Duplicate(layout, 0);

        Assert.Equal(["Main", "Main (copy)", "Other"], layout.Pages.Select(p => p.Name));
        Assert.Same(copy, layout.CurrentPage);
        var copied = Assert.IsType<StackWidgetConfig>(Assert.Single(copy.Widgets));
        Assert.NotEqual(stack.Id, copied.Id);
        Assert.DoesNotContain(copied.Items, i => stack.Items.Any(s => s.Id == i.Id));
    }

    [Fact]
    public void MoveWidget_KeepsPlaceWhenFree_ElseFirstFreeSpot_ElseRefuses()
    {
        var moving = Chart("m", new GridRect(2, 0, 2, 2));
        var layout = new LayoutDocument { Name = "L", Widgets = [moving] };
        LayoutPages.Add(layout, "Target");
        var blocker = Chart("x", new GridRect(2, 0, 2, 2));
        layout.Widgets.Add(blocker);
        layout.ActivePage = 0;

        Assert.True(LayoutPages.MoveWidget(layout, moving, 1, columns: 6, rows: 2));
        Assert.Empty(layout.Widgets);
        Assert.Equal(new GridRect(0, 0, 2, 2), moving.Bounds); // Its place was taken.
        Assert.Contains(moving, layout.Pages[1].Widgets);

        layout.ActivePage = 1;
        var big = Chart("big", new GridRect(0, 2, 6, 2));
        layout.Widgets.Add(big);
        Assert.False(LayoutPages.MoveWidget(layout, big, 1, 6, 4)); // Same page.
        LayoutPages.Add(layout, "Full");
        layout.Widgets.Add(Chart("fill", new GridRect(0, 0, 6, 4)));
        layout.ActivePage = 1;
        Assert.False(LayoutPages.MoveWidget(layout, big, 2, 6, 4));
        Assert.Contains(big, layout.Widgets);
    }
}
