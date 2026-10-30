using System.Globalization;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;

namespace ClaudeUsage.NockMon;

/// <summary>
/// The news bell, at the widget's bottom right: hidden while nothing is unread in the widget's categories; otherwise the
/// count, and a list to follow (an item opened is read) or dismiss at once.
/// </summary>
internal sealed partial class NewsBell : Button
{
    private readonly IReadOnlySet<NewsCategory> _categories;
    private readonly TextBlock _count = new() { FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly Flyout _flyout = new();
    private IReadOnlyList<NewsItem>? _source;
    private NewsItem[] _items = [];

    public NewsBell(IReadOnlySet<NewsCategory> categories)
    {
        _categories = categories;
        Padding = new Thickness(6, 2, 6, 2);
        MinWidth = 0;
        MinHeight = 0;
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Bottom;
        Margin = new Thickness(0, 0, 6, 4);
        Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        Content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 4,
            Children = { new FontIcon { Glyph = "", FontSize = 12 }, _count }, // Ringer
        };
        Visibility = Visibility.Collapsed;
        Flyout = _flyout;
        _flyout.Opening += (_, _) => _flyout.Content = BuildList();
    }

    /// <summary>At each widget refresh; does nothing until the service publishes another list.</summary>
    public void Refresh()
    {
        var unread = NewsService.Unread;
        if (ReferenceEquals(unread, _source))
            return;
        _source = unread;
        _items = unread.Where(i => _categories.Contains(i.Category)).ToArray();
        Visibility = _items.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        _count.Text = _items.Length.ToString(CultureInfo.CurrentCulture);
        ToolTipService.SetToolTip(this, _items.Length == 1 ? "1 unread Claude news" : $"{_items.Length} unread Claude news");
        if (_items.Length == 0)
            _flyout.Hide();
    }

    private UIElement BuildList()
    {
        var markAll = new HyperlinkButton { Content = "Mark all as read", Padding = new Thickness(0), FontSize = 12 };
        markAll.Click += (_, _) =>
        {
            NewsService.MarkRead(_items);
            _flyout.Hide();
        };
        var header = new Grid { ColumnSpacing = 12 };
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        header.Children.Add(new TextBlock { Text = "Claude news", FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(markAll, 1);
        header.Children.Add(markAll);

        var list = new StackPanel { Spacing = 12 };
        foreach (var item in _items)
        {
            var link = new Hyperlink { NavigateUri = Uri.TryCreate(item.Url, UriKind.Absolute, out var uri) ? uri : null };
            link.Inlines.Add(new Run { Text = item.Text });
            link.Click += (_, _) => NewsService.MarkRead([item]);
            var text = new TextBlock { TextWrapping = TextWrapping.Wrap, FontSize = 13 };
            text.Inlines.Add(link);
            list.Children.Add(new StackPanel
            {
                Spacing = 2,
                Children =
                {
                    new TextBlock
                    {
                        Text = $"{NewsFeeds.Label(item.Category)} · {item.Date.UtcDateTime.ToString("M", CultureInfo.CurrentCulture)}",
                        FontSize = 11, Opacity = 0.65,
                    },
                    text,
                },
            });
        }

        return new StackPanel
        {
            Width = 360,
            Spacing = 10,
            Children = { header, new ScrollViewer { MaxHeight = 420, Content = list, Padding = new Thickness(0, 0, 12, 0) } },
        };
    }
}
