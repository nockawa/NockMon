using System.Globalization;
using System.Text;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Model;
using NockMon.Core.Processes;
using NockMon.Monitoring;

namespace NockMon.Widgets;

/// <summary>
/// What a process is: image, version, command line, account, token, priority, parent. A grouped row lists its
/// instances (their image is shared). Selectable text; Copy puts it all on the clipboard.
/// </summary>
internal static class ProcessDetailsDialog
{
    private const int MaxInstances = 32;

    private sealed record Instance(ProcessKey Key, ProcessDetails Details, bool? Critical, string? Parent);

    public static async Task ShowAsync(XamlRoot root, ProcessTarget target, MonitorService monitor)
    {
        // Parent names from the latest scan: a parent PID is only the parent if that process started before.
        var parents = monitor.Engine.Read(target, static (target, snapshot, _) =>
        {
            var result = new Dictionary<ProcessKey, string>();
            var processes = snapshot.ProcessSpan;
            foreach (var key in target.Instances.Take(MaxInstances))
            {
                foreach (ref readonly var p in processes)
                {
                    if (p.Pid != key.Pid || p.CreateTime != key.CreateTime)
                        continue;
                    var parent = "";
                    foreach (ref readonly var q in processes)
                    {
                        if (q.Pid == p.ParentPid && q.CreateTime <= p.CreateTime && q.Pid != p.Pid)
                            parent = $"{q.Name} (PID {q.Pid})";
                    }
                    result[key] = parent.Length > 0 ? parent : $"PID {p.ParentPid} (exited)";
                }
            }
            return result;
        });
        var instances = await Task.Run(() => target.Instances.Take(MaxInstances)
            .Select(k => new Instance(k, ProcessDetails.Query(k), ProcessControl.IsCritical(k), parents.GetValueOrDefault(k)))
            .ToList());

        var text = new StringBuilder();
        var panel = new StackPanel { Spacing = 12 };
        var first = instances[0].Details;
        panel.Children.Add(Table(text,
        [
            ("Name", target.Name),
            ("Path", first.Path),
            ("Description", first.Description),
            ("Company", first.Company),
            ("Version", first.Version),
        ]));
        foreach (var instance in instances)
        {
            if (target.IsGroup)
            {
                panel.Children.Add(new TextBlock { Text = $"PID {instance.Key.Pid}", FontWeight = FontWeights.SemiBold, Margin = new Thickness(0, 4, 0, -6) });
                text.AppendLine().AppendLine(CultureInfo.InvariantCulture, $"PID {instance.Key.Pid}");
            }
            var d = instance.Details;
            panel.Children.Add(Table(text,
            [
                ("PID", target.IsGroup ? null : instance.Key.Pid.ToString(CultureInfo.InvariantCulture)),
                ("Parent", instance.Parent),
                ("Started", Started(instance.Key.CreateTime)),
                ("Command line", d.CommandLine),
                ("User", d.User),
                ("Integrity", d.Integrity),
                ("Elevated", d.Elevated is { } elevated ? (elevated ? "Yes" : "No") : null),
                ("Protection", d.Protection ?? (d.Path is null ? null : "None")),
                ("Priority", d.Priority is { } priority ? ProcessMenu.PriorityName(priority) : null),
                ("Critical", instance.Critical is { } critical ? (critical ? "Yes: ending it crashes Windows" : "No") : null),
            ]));
        }
        if (target.Instances.Count > MaxInstances)
            panel.Children.Add(new TextBlock { Text = $"…and {target.Instances.Count - MaxInstances} more.", Opacity = 0.7 });

        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = target.ToString(),
            Content = new ScrollViewer { Content = panel, MaxHeight = 560, Padding = new Thickness(0, 0, 16, 0) },
            PrimaryButtonText = "Copy",
            CloseButtonText = "Close",
            DefaultButton = ContentDialogButton.Close,
        };
        dialog.Resources["ContentDialogMaxWidth"] = 900.0;
        dialog.PrimaryButtonClick += (_, e) =>
        {
            e.Cancel = true; // Stays open.
            ProcessMenu.Copy(text.ToString().Trim());
        };
        try
        {
            await dialog.ShowAsync();
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            // Another dialog is open: WinUI shows one at a time.
        }
    }

    private static string Started(long fileTime) => fileTime <= 0
        ? ""
        : DateTime.FromFileTimeUtc(fileTime).ToLocalTime().ToString("G", CultureInfo.CurrentCulture);

    /// <summary>Label/value rows (values selectable); null values are left out. Appends "Label: value" lines to <paramref name="text"/>.</summary>
    private static Grid Table(StringBuilder text, (string Label, string? Value)[] rows)
    {
        var grid = new Grid { ColumnSpacing = 12, RowSpacing = 4 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(96) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var row = 0;
        foreach (var (label, value) in rows)
        {
            if (string.IsNullOrEmpty(value))
                continue;
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var name = new TextBlock { Text = label, Opacity = 0.7 };
            var content = new TextBlock { Text = value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap };
            if (label == "Command line" || label == "Path")
                content.FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Cascadia Mono, Consolas");
            Grid.SetRow(name, row);
            Grid.SetRow(content, row);
            Grid.SetColumn(content, 1);
            grid.Children.Add(name);
            grid.Children.Add(content);
            text.AppendLine(CultureInfo.InvariantCulture, $"{label}: {value}");
            row++;
        }
        return grid;
    }
}
