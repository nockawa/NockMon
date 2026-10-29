using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
using NockMon.Core.Help;
using NockMon.Core.Layout;
using NockMon.Core.Metrics;

namespace NockMon.Help;

/// <summary>Renders <see cref="Doc"/>s: short tooltips (title + summary) and full rich text (details).</summary>
internal static class DocView
{
    private const double TooltipMaxWidth = 420;
    private static readonly FontFamily Mono = new("Cascadia Mono, Consolas");

    /// <summary>Title and summary; with <paramref name="details"/>, the full text too (editors).</summary>
    public static ToolTip Tooltip(Doc doc, bool details = false)
    {
        var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap, MaxWidth = TooltipMaxWidth - 20 };
        text.Blocks.Add(Paragraph(new Run { Text = doc.Title, FontWeight = FontWeights.SemiBold }));
        text.Blocks.Add(Paragraph(new Run { Text = doc.Summary }));
        if (details)
            Append(text, doc.Details);
        return new ToolTip { Content = text, MaxWidth = TooltipMaxWidth };
    }

    /// <summary>Widget help on hover: what the widget is, and for charts and values what each series means.</summary>
    public static ToolTip WidgetTooltip(WidgetConfig config)
    {
        var tooltip = Tooltip(Docs.Of(config));
        var text = (RichTextBlock)tooltip.Content;
        foreach (var (label, series) in Series(config))
        {
            var paragraph = Paragraph(new Run { Text = label + ": ", FontWeight = FontWeights.SemiBold });
            paragraph.Inlines.Add(new Run { Text = Docs.Of(series).Summary });
            text.Blocks.Add(paragraph);
        }
        return tooltip;
    }

    /// <summary>Widget help pinned on click: the widget's details, then each series' summary.</summary>
    public static RichTextBlock WidgetDetails(WidgetConfig config)
    {
        var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
        Append(text, Docs.Of(config).Details);
        foreach (var (label, series) in Series(config))
        {
            var doc = Docs.Of(series);
            var paragraph = Paragraph(new Run { Text = label, FontWeight = FontWeights.SemiBold });
            paragraph.Inlines.Add(new Run { Text = label == doc.Title ? ": " : $" ({doc.Title}): " });
            paragraph.Inlines.Add(new Run { Text = doc.Summary });
            text.Blocks.Add(paragraph);
        }
        return text;
    }

    private static IEnumerable<(string Label, SeriesRef Series)> Series(WidgetConfig config) => config switch
    {
        ChartWidgetConfig chart => chart.Series.Select(s => (s.Label ?? s.Source.DefaultLabel, s.Source)),
        ValueWidgetConfig value => [(value.Source.DefaultLabel, value.Source)],
        _ => [],
    };

    /// <summary>
    /// Documents a picker: each item (by its Tag) gets a summary tooltip, and the returned panel shows the selected
    /// item's full documentation. Call once the items are added.
    /// </summary>
    public static DocPanel Attach(ComboBox combo, Func<object, Doc> doc)
    {
        var panel = new DocPanel();
        foreach (var item in combo.Items.Cast<ComboBoxItem>())
            ToolTipService.SetToolTip(item, Tooltip(doc(item.Tag)));
        void Update() => panel.Show(combo.SelectedItem is ComboBoxItem { Tag: { } tag } ? doc(tag) : null);
        combo.SelectionChanged += (_, _) => Update();
        Update();
        return panel;
    }

    /// <summary>The details as rich text.</summary>
    public static RichTextBlock Render(string markup)
    {
        var text = new RichTextBlock { TextWrapping = TextWrapping.Wrap };
        Append(text, markup);
        return text;
    }

    /// <summary>Appends the paragraphs and bullets of <paramref name="markup"/>.</summary>
    public static void Append(RichTextBlock text, string markup)
    {
        foreach (var block in DocMarkup.Parse(markup))
        {
            var paragraph = new Paragraph { Margin = new Thickness(block.Bullet ? 14 : 0, 0, 0, 6) };
            if (block.Bullet)
            {
                paragraph.TextIndent = -10;
                paragraph.Inlines.Add(new Run { Text = "•  " });
            }
            foreach (var run in block.Runs)
                paragraph.Inlines.Add(Inline(run));
            text.Blocks.Add(paragraph);
        }
    }

    private static Run Inline(DocRun run) => run.Style switch
    {
        DocStyle.Bold => new Run { Text = run.Text, FontWeight = FontWeights.SemiBold },
        DocStyle.Code => new Run { Text = run.Text, FontFamily = Mono },
        _ => new Run { Text = run.Text },
    };

    private static Paragraph Paragraph(Inline inline)
    {
        var paragraph = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        paragraph.Inlines.Add(inline);
        return paragraph;
    }
}

/// <summary>Editor card showing the full documentation of the selected metric; <see cref="Show"/> replaces it.</summary>
internal sealed partial class DocPanel : UserControl
{
    private readonly RichTextBlock _text = new() { TextWrapping = TextWrapping.Wrap, FontSize = 12 };

    public DocPanel()
    {
        Content = new Border
        {
            Padding = new Thickness(12, 8, 12, 2),
            CornerRadius = new CornerRadius(6),
            Background = (Brush)Application.Current.Resources["SubtleFillColorSecondaryBrush"],
            Child = _text,
        };
    }

    public void Show(Doc? doc)
    {
        _text.Blocks.Clear();
        Visibility = doc is null ? Visibility.Collapsed : Visibility.Visible;
        if (doc is null)
            return;
        var title = new Paragraph { Margin = new Thickness(0, 0, 0, 6) };
        title.Inlines.Add(new Run { Text = doc.Title, FontWeight = FontWeights.SemiBold });
        title.Inlines.Add(new Run { Text = " — " + doc.Summary });
        _text.Blocks.Add(title);
        DocView.Append(_text, doc.Details);
    }
}
