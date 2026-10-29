using MyCpuMonitor.Core.Gpu;
using MyCpuMonitor.Core.Help;
using MyCpuMonitor.Core.Layout;
using MyCpuMonitor.Core.Metrics;
using MyCpuMonitor.Core.Sampling;

namespace MyCpuMonitor.Core.Tests;

public class DocsTests
{
    private static IEnumerable<Doc> All() =>
    [
        .. Enum.GetValues<ProcessField>().Select(Docs.Of),
        .. Enum.GetValues<SystemMetric>().Select(Docs.Of),
        .. Enum.GetValues<CoreMetric>().Select(Docs.Of),
        .. Enum.GetValues<GpuMetric>().Select(Docs.Of),
        .. Enum.GetValues<DriverField>().Select(Docs.Of),
        .. Enum.GetValues<ObjectTypeField>().Select(Docs.Of),
        .. Enum.GetValues<ObjectTypeMetric>().Select(Docs.Of),
        .. Enum.GetValues<PoolTagField>().Select(Docs.Of),
        .. Enum.GetValues<WidgetKind>().Select(Docs.Of),
    ];

    [Fact]
    public void EveryMetricAndWidget_IsDocumented_WithBalancedMarkup()
    {
        foreach (var doc in All())
        {
            Assert.False(string.IsNullOrWhiteSpace(doc.Title));
            Assert.False(string.IsNullOrWhiteSpace(doc.Summary), doc.Title);
            Assert.False(string.IsNullOrWhiteSpace(doc.Details), doc.Title);
            Assert.DoesNotContain("**", doc.Summary); // Summaries are plain text.
            // Every delimiter is consumed by the parser: none left over in the rendered text.
            var runs = DocMarkup.Parse(doc.Details).SelectMany(b => b.Runs).Where(r => r.Style != DocStyle.Code);
            Assert.All(runs, r => Assert.DoesNotContain("**", r.Text));
            Assert.All(runs, r => Assert.DoesNotContain("`", r.Text));
        }
    }

    [Fact]
    public void Series_UseTheirMetricDoc()
    {
        Assert.Equal(Docs.Of(ProcessField.HardFaults), Docs.Of(new ProcessSeries(ProcessField.HardFaults, "devenv")));
        Assert.Equal(Docs.Of(SystemMetric.DpcMax), Docs.Of(new SystemSeries(SystemMetric.DpcMax)));
        Assert.Equal(Docs.Of(GpuMetric.Engine), Docs.Of(new GpuSeries(GpuMetric.Engine, "GPU", "3D")));
    }

    [Fact]
    public void Parse_SplitsParagraphsAndBullets_JoinsContinuationLines()
    {
        var blocks = DocMarkup.Parse("First line\ncontinued.\n\n- one\n- two\n  wrapped\n\nLast.");

        Assert.Equal(4, blocks.Count);
        Assert.Equal([false, true, true, false], blocks.Select(b => b.Bullet));
        Assert.Equal("First line continued.", Assert.Single(blocks[0].Runs).Text);
        Assert.Equal("two wrapped", Assert.Single(blocks[2].Runs).Text);
    }

    [Fact]
    public void ParseInline_BoldAndCode_UnclosedDelimitersStayLiteral()
    {
        Assert.Equal(
            [new DocRun("a ", DocStyle.Normal), new DocRun("bold", DocStyle.Bold), new DocRun(" and ", DocStyle.Normal),
             new DocRun("*.sys", DocStyle.Code), new DocRun(" end", DocStyle.Normal)],
            DocMarkup.ParseInline("a **bold** and `*.sys` end"));
        Assert.Equal([new DocRun("x ** y ` z", DocStyle.Normal)], DocMarkup.ParseInline("x ** y ` z"));
    }
}
