using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Persistence;

namespace MyCpuMonitor.Core.Tests;

public class StackingTests
{
    private static ChartWidgetConfig Chart(string pattern, GridRect bounds) => new()
    {
        Bounds = bounds,
        Series = [new ChartSeriesConfig { Source = new ProcessSeries(ProcessField.Cpu, pattern) }],
    };

    [Fact]
    public void Stack_ReplacesTargetWithStack_AtTargetBounds_ShowingDropped()
    {
        var target = Chart("a", new GridRect(0, 0, 6, 3));
        var dragged = Chart("b", new GridRect(6, 0, 4, 2));
        var layout = new LayoutDocument { Name = "L", Widgets = [target, dragged] };

        var stack = WidgetStacking.Stack(layout, dragged, target);

        Assert.Same(stack, Assert.Single(layout.Widgets));
        Assert.Equal(new GridRect(0, 0, 6, 3), stack.Bounds);
        Assert.Equal([target, dragged], stack.Items);
        Assert.All(stack.Items, i => Assert.Equal(stack.Bounds, i.Bounds));
        Assert.Equal(1, stack.CurrentIndex);
    }

    [Fact]
    public void Stack_OntoStack_Appends_AndDraggedStackMergesItsItems()
    {
        var a = Chart("a", new GridRect(0, 0, 6, 3));
        var b = Chart("b", new GridRect(6, 0, 6, 3));
        var c = Chart("c", new GridRect(0, 3, 6, 3));
        var d = Chart("d", new GridRect(6, 3, 6, 3));
        var layout = new LayoutDocument { Name = "L", Widgets = [a, b, c, d] };

        var first = WidgetStacking.Stack(layout, b, a);
        var second = WidgetStacking.Stack(layout, d, c);
        var merged = WidgetStacking.Stack(layout, second, first);

        Assert.Same(first, merged);
        Assert.Same(merged, Assert.Single(layout.Widgets));
        Assert.Equal([a, b, c, d], merged.Items);
        Assert.Equal(2, merged.CurrentIndex);
    }

    [Fact]
    public void CanStack_RejectsSelf_AndTargetSmallerThanDraggedMinimum()
    {
        var table = new ProcessTableWidgetConfig { Bounds = new GridRect(0, 0, 10, 8) }; // Min 3×2.
        var value = new ValueWidgetConfig { Bounds = new GridRect(10, 0, 2, 2), Source = new SystemSeries(SystemMetric.Cpu) };
        var layout = new LayoutDocument { Name = "L", Widgets = [table, value] };

        Assert.False(WidgetStacking.CanStack(layout, table, table));
        Assert.False(WidgetStacking.CanStack(layout, table, value));
        Assert.True(WidgetStacking.CanStack(layout, value, table));
        Assert.Throws<InvalidOperationException>(() => WidgetStacking.Stack(layout, table, value));
    }

    [Fact]
    public void Unstack_MovesItemOut_AndDissolvesSingleItemStack()
    {
        var a = Chart("a", new GridRect(0, 0, 6, 3));
        var b = Chart("b", new GridRect(6, 0, 6, 3));
        var c = Chart("c", new GridRect(0, 3, 6, 3));
        var layout = new LayoutDocument { Name = "L", Widgets = [a, b, c] };
        var stack = WidgetStacking.Stack(layout, b, a);
        stack = WidgetStacking.Stack(layout, c, stack);

        var outB = WidgetStacking.Unstack(layout, stack, 1, new GridRect(6, 0, 6, 3));
        Assert.Same(b, outB);
        Assert.Equal(new GridRect(6, 0, 6, 3), b.Bounds);
        Assert.Equal([a, c], stack.Items);
        Assert.Equal(1, stack.CurrentIndex);

        WidgetStacking.Unstack(layout, stack, 0, new GridRect(0, 3, 6, 3));
        Assert.DoesNotContain(stack, layout.Widgets);
        Assert.Equal([c, b, a], layout.Widgets);
        Assert.Equal(new GridRect(0, 0, 6, 3), c.Bounds); // The last item takes the stack's place and bounds.
        Assert.Empty(layout.FindCollisions());
    }

    [Fact]
    public void MinSize_OfStack_IsLargestItemMinimumPerAxis()
    {
        var stack = new StackWidgetConfig
        {
            Items = [new ProcessTableWidgetConfig(), new CpuGridWidgetConfig(), new ValueWidgetConfig { Source = new SystemSeries(SystemMetric.Cpu) }], // 3×2, 2×2, 1×1.
        };
        Assert.Equal(new GridSize(3, 2), WidgetCatalog.MinSize(stack));
        Assert.DoesNotContain(WidgetKind.Stack, WidgetCatalog.Kinds);
    }

    [Fact]
    public void Stack_RoundTripsThroughJson_WithoutCurrentIndex_AndPatternsSeeItems()
    {
        var a = Chart("dwm", new GridRect(0, 0, 6, 3));
        var b = new GpuEnginesWidgetConfig { Bounds = new GridRect(6, 0, 6, 3), Adapter = "NV", EnginePattern = "Compute*" };
        var layout = new LayoutDocument { Name = "L", Widgets = [a, b] };
        var stack = WidgetStacking.Stack(layout, b, a);
        stack.CycleSeconds = 5;

        var back = LayoutStore.Deserialize(LayoutStore.Serialize(layout));

        var restored = Assert.IsType<StackWidgetConfig>(Assert.Single(back.Widgets));
        Assert.Equal(5, restored.CycleSeconds);
        Assert.Equal(0, restored.CurrentIndex);
        Assert.IsType<ChartWidgetConfig>(restored.Items[0]);
        Assert.IsType<GpuEnginesWidgetConfig>(restored.Items[1]);
        Assert.Equal(["dwm"], LayoutPatterns.Watched(back));
        Assert.Equal([("NV", "Compute*")], LayoutPatterns.GpuEngines(back, []));
    }
}
