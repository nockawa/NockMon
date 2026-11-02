using System.Numerics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Processes;

namespace NockMon.Widgets;

/// <summary>Logical processors a process may run on: one box per processor (P/E on a hybrid CPU), with quick selections.</summary>
internal static class AffinityDialog
{
    private const int Columns = 8;

    /// <returns>The chosen mask (group 0), or null if cancelled.</returns>
    public static async Task<ulong?> ShowAsync(XamlRoot root, string title, ulong current)
    {
        var processors = CpuTopology.All.Where(p => p.Group == 0 && p.Index < 64).ToArray();
        var hybrid = CpuTopology.IsHybrid;
        var performance = CpuTopology.PerformanceClass;
        var boxes = processors.Select(p => new CheckBox
        {
            Content = hybrid ? $"{p.Index} {(p.EfficiencyClass == performance ? "P" : "E")}" : p.Index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            IsChecked = (current >> p.Index & 1) != 0,
            MinWidth = 64,
            Tag = p,
        }).ToArray();

        var grid = new Grid { ColumnSpacing = 4 };
        for (var c = 0; c < Columns; c++)
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        for (var i = 0; i < boxes.Length; i++)
        {
            if (i % Columns == 0)
                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(boxes[i], i / Columns);
            Grid.SetColumn(boxes[i], i % Columns);
            grid.Children.Add(boxes[i]);
        }

        var quick = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        Quick("All", _ => true);
        if (hybrid)
        {
            Quick("P-cores", p => p.EfficiencyClass == performance);
            Quick("E-cores", p => p.EfficiencyClass < performance);
        }
        Quick("One per core", p => processors.First(q => q.Core == p.Core).Index == p.Index);
        Quick("None", _ => false);

        var summary = new TextBlock { Opacity = 0.7 };
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = $"Affinity: {title}",
            Content = new StackPanel { Spacing = 12, Children = { quick, grid, summary } },
            PrimaryButtonText = "Apply",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 800.0;
        foreach (var box in boxes)
        {
            box.Checked += (_, _) => Update();
            box.Unchecked += (_, _) => Update();
        }
        Update();
        try
        {
            return await dialog.ShowAsync() == ContentDialogResult.Primary ? Mask() : null;
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            return null; // Another dialog is open.
        }

        ulong Mask() => boxes.Where(b => b.IsChecked == true).Aggregate(0UL, (mask, b) => mask | 1UL << ((LogicalProcessor)b.Tag).Index);

        void Update()
        {
            var count = BitOperations.PopCount(Mask());
            summary.Text = $"{count} of {boxes.Length} logical processors.";
            dialog.IsPrimaryButtonEnabled = count > 0;
        }

        void Quick(string text, Func<LogicalProcessor, bool> select)
        {
            var button = new Button { Content = text };
            button.Click += (_, _) =>
            {
                foreach (var box in boxes)
                    box.IsChecked = select((LogicalProcessor)box.Tag);
            };
            quick.Children.Add(button);
        }
    }
}
