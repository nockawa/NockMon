using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Metrics;
using NockMon.Core.Model;
using NockMon.Core.Processes;
using NockMon.Core.Sampling;
using NockMon.Monitoring;
using NockMon.Shell;
using Windows.ApplicationModel.DataTransfer;

namespace NockMon.Widgets;

/// <summary>A table row's process: one process, or every process of a name (grouped row). Keys taken when the menu opened.</summary>
internal sealed record ProcessTarget(string Name, IReadOnlyList<ProcessKey> Instances)
{
    public bool IsGroup => Instances.Count > 1;
    public ProcessKey First => Instances[0];

    public override string ToString() => IsGroup ? $"{Name} ({Instances.Count} processes)" : $"{Name} (PID {First.Pid})";
}

/// <summary>
/// Context menu of a process row: inspect, chart, control (priority, affinity, suspend, end) and dump. Actions apply to
/// every instance of a grouped row, except the dump (one process). Critical processes, kernel processes and NockMon
/// itself cannot be ended or suspended; neither can the Desktop Window Manager be suspended (the screen would freeze).
/// </summary>
internal static class ProcessMenu
{
    private static readonly ProcessField[] ChartFields =
    [
        ProcessField.Cpu, ProcessField.PrivateBytes, ProcessField.WorkingSet, ProcessField.IoBytes, ProcessField.DiskBytes,
        ProcessField.NetBytes, ProcessField.Gpu, ProcessField.Handles, ProcessField.Threads,
    ];

    private static readonly PriorityClass[] Priorities =
        [PriorityClass.Realtime, PriorityClass.High, PriorityClass.AboveNormal, PriorityClass.Normal, PriorityClass.BelowNormal, PriorityClass.Idle];

    /// <summary>A chart of a process name's metric is asked for (name, field).</summary>
    public static event Action<string, ProcessField>? ChartRequested;

    /// <summary>Outcome of an action, for the window's info bar.</summary>
    public static event Action<InfoBarSeverity, string, string>? MessageRaised;

    /// <param name="rowText">The row as shown (headers and cells), for Copy.</param>
    public static MenuFlyout Create(ProcessTarget target, MonitorService monitor, XamlRoot root, Func<string> rowText)
    {
        var self = target.Instances.Any(k => k.Pid == Environment.ProcessId);
        var kernel = target.Instances.Any(k => ProcessControl.IsKernelProcess(k.Pid));
        var critical = !kernel && target.Instances.Any(k => ProcessControl.IsCritical(k) == true);
        var canEnd = !self && !kernel && !critical;
        var canSuspend = canEnd && !target.Name.Equals("dwm.exe", StringComparison.OrdinalIgnoreCase);

        var menu = new MenuFlyout();
        menu.Items.Add(new MenuFlyoutItem { Text = target.ToString(), IsEnabled = false });
        if (Restriction(self, kernel, critical, canSuspend) is { } restriction)
            menu.Items.Add(new MenuFlyoutItem { Text = restriction, IsEnabled = false, FontSize = 12 });
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item("Details…", "", () => _ = ProcessDetailsDialog.ShowAsync(root, target, monitor)));
        menu.Items.Add(Item("Open file location", "", () => OpenLocation(target), enabled: !kernel));
        menu.Items.Add(Item("Properties", "", () => ShowProperties(target, root), enabled: !kernel));
        menu.Items.Add(CopyMenu(target, rowText));
        menu.Items.Add(new MenuFlyoutSeparator());

        var chart = new MenuFlyoutSubItem { Text = target.IsGroup ? $"Chart (all {target.Name})" : "Chart", Icon = Glyph("") };
        foreach (var field in ChartFields)
            chart.Items.Add(Item(MetricCatalog.Get(field).DisplayName, null, () => ChartRequested?.Invoke(target.Name, field)));
        menu.Items.Add(chart);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(PriorityMenu(target, root, enabled: !kernel));
        menu.Items.Add(AffinityMenu(target, root, enabled: !kernel));
        menu.Items.Add(Item("Suspend", "", () => Apply("Suspend", target, ProcessControl.Suspend, "Suspended."), canSuspend));
        menu.Items.Add(Item("Resume", "", () => Apply("Resume", target, ProcessControl.Resume, "Resumed."), !kernel && !self));
        var dump = new MenuFlyoutSubItem
        {
            Text = target.IsGroup ? "Create dump (one process: ungroup)" : "Create dump",
            Icon = Glyph(""),
            IsEnabled = !target.IsGroup && !kernel,
        };
        dump.Items.Add(Item("Minidump (stacks, handles)…", null, () => _ = Dump(target, root, full: false)));
        dump.Items.Add(Item("Full dump (whole memory)…", null, () => _ = Dump(target, root, full: true)));
        menu.Items.Add(dump);
        menu.Items.Add(new MenuFlyoutSeparator());

        menu.Items.Add(Item(target.IsGroup ? "End processes" : "End process", "", () => _ = End(target, root), canEnd));
        menu.Items.Add(Item("End process tree", "", () => _ = EndTree(target, monitor, root), canEnd));
        return menu;
    }

    private static string? Restriction(bool self, bool kernel, bool critical, bool canSuspend) =>
        self ? "NockMon itself: use Exit to end it"
        : kernel ? "Kernel process: no action applies"
        : critical ? "Critical: ending it would crash Windows"
        : !canSuspend ? "Suspending it would freeze the screen"
        : null;

    private static MenuFlyoutItem Item(string text, string? glyph, Action click, bool enabled = true)
    {
        var item = new MenuFlyoutItem { Text = text, IsEnabled = enabled };
        if (glyph is not null)
            item.Icon = Glyph(glyph);
        item.Click += (_, _) => click();
        return item;
    }

    private static FontIcon Glyph(string glyph) => new() { Glyph = glyph };

    private static void Raise(InfoBarSeverity severity, string title, string message) => MessageRaised?.Invoke(severity, title, message);

    // --- Inspect --------------------------------------------------------------------------------------

    private static void OpenLocation(ProcessTarget target)
    {
        if (ProcessImage.TryGetPath(target.First.Pid, target.First.CreateTime) is not { } path)
        {
            Raise(InfoBarSeverity.Warning, "Open file location", "The process has exited, or its path cannot be read.");
            return;
        }
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = false });
    }

    private static void ShowProperties(ProcessTarget target, XamlRoot root)
    {
        if (ProcessImage.TryGetPath(target.First.Pid, target.First.CreateTime) is not { } path)
        {
            Raise(InfoBarSeverity.Warning, "Properties", "The process has exited, or its path cannot be read.");
            return;
        }
        Win32.ShowFileProperties(Microsoft.UI.Win32Interop.GetWindowFromWindowId(root.ContentIslandEnvironment.AppWindowId), path);
    }

    private static MenuFlyoutSubItem CopyMenu(ProcessTarget target, Func<string> rowText)
    {
        var copy = new MenuFlyoutSubItem { Text = "Copy", Icon = Glyph("") };
        copy.Items.Add(Item("Name", null, () => Copy(target.Name)));
        copy.Items.Add(Item(target.IsGroup ? "PIDs" : "PID", null, () => Copy(string.Join(", ", target.Instances.Select(k => k.Pid)))));
        copy.Items.Add(Item("Path", null, () => Copy(ProcessImage.TryGetPath(target.First.Pid, target.First.CreateTime))));
        copy.Items.Add(Item(target.IsGroup ? "Command lines" : "Command line", null, () =>
            Copy(string.Join(Environment.NewLine, target.Instances.Select(k => ProcessDetails.Query(k).CommandLine).OfType<string>()))));
        copy.Items.Add(Item("Row", null, () => Copy(rowText())));
        return copy;
    }

    internal static void Copy(string? text)
    {
        if (string.IsNullOrEmpty(text))
        {
            Raise(InfoBarSeverity.Warning, "Copy", "Nothing to copy: the process has exited, or this cannot be read.");
            return;
        }
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
    }

    // --- Control --------------------------------------------------------------------------------------

    internal static string PriorityName(PriorityClass priority) => priority switch
    {
        PriorityClass.AboveNormal => "Above normal",
        PriorityClass.BelowNormal => "Below normal",
        _ => priority.ToString(),
    };

    private static MenuFlyoutSubItem PriorityMenu(ProcessTarget target, XamlRoot root, bool enabled)
    {
        var menu = new MenuFlyoutSubItem { Text = "Priority", Icon = Glyph(""), IsEnabled = enabled };
        var current = enabled ? ProcessControl.GetPriority(target.First) : null;
        foreach (var priority in Priorities)
        {
            var item = new RadioMenuFlyoutItem { Text = PriorityName(priority), GroupName = "priority", IsChecked = priority == current };
            item.Click += async (_, _) =>
            {
                if (priority == PriorityClass.Realtime && !await Confirm(root, "Realtime priority",
                        $"Run {target} at realtime priority? It preempts input and system work: a busy realtime process can make the machine unresponsive.", "Set realtime"))
                    return;
                Apply("Priority", target, key =>
                {
                    var actual = ProcessControl.SetPriority(key, priority);
                    if (actual != priority)
                        throw new ProcessControlException($"Windows set {PriorityName(actual)} instead.");
                }, $"{target}: {PriorityName(priority)}.");
            };
            menu.Items.Add(item);
        }
        return menu;
    }

    private static MenuFlyoutSubItem AffinityMenu(ProcessTarget target, XamlRoot root, bool enabled)
    {
        var menu = new MenuFlyoutSubItem { Text = "Affinity", Icon = Glyph(""), IsEnabled = enabled };
        if (!CpuTopology.SingleGroup)
        {
            menu.Items.Add(new MenuFlyoutItem { Text = "Not available with more than 64 logical processors", IsEnabled = false });
            return menu;
        }
        var current = enabled ? ProcessControl.GetAffinity(target.First) : null;
        var presets = new List<(string Text, ulong Mask)> { ("All processors", CpuTopology.AllMask) };
        if (CpuTopology.IsHybrid)
        {
            var performance = CpuTopology.PerformanceClass;
            presets.Add(("P-cores only", CpuTopology.Mask(p => p.EfficiencyClass == performance)));
            presets.Add(("E-cores only", CpuTopology.Mask(p => p.EfficiencyClass < performance)));
        }
        foreach (var (text, mask) in presets)
        {
            var item = new RadioMenuFlyoutItem { Text = text, GroupName = "affinity", IsChecked = current == mask };
            item.Click += (_, _) => SetAffinity(target, mask);
            menu.Items.Add(item);
        }
        var custom = new RadioMenuFlyoutItem
        {
            Text = "Custom…",
            GroupName = "affinity",
            IsChecked = current is { } c && presets.All(p => p.Mask != c),
        };
        custom.Click += async (_, _) =>
        {
            if (await AffinityDialog.ShowAsync(root, target.ToString(), current ?? CpuTopology.AllMask) is { } mask)
                SetAffinity(target, mask);
        };
        menu.Items.Add(custom);
        return menu;
    }

    private static void SetAffinity(ProcessTarget target, ulong mask) =>
        Apply("Affinity", target, key => ProcessControl.SetAffinity(key, mask),
            $"{target}: {System.Numerics.BitOperations.PopCount(mask)} of {CpuTopology.All.Count} logical processors.");

    private static async Task End(ProcessTarget target, XamlRoot root)
    {
        if (await Confirm(root, target.IsGroup ? "End processes" : "End process", $"End {target}? Unsaved work in it is lost.", "End"))
            Apply("End process", target, ProcessControl.Kill, $"Ended {target}.");
    }

    /// <summary>The instances and their descendants (as of the latest scan), parents first; never NockMon itself.</summary>
    private static async Task EndTree(ProcessTarget target, MonitorService monitor, XamlRoot root)
    {
        var (keys, names) = monitor.Engine.Read(target, static (target, snapshot, _) =>
        {
            var processes = snapshot.ProcessSpan;
            var keys = new List<ProcessKey>(target.Instances);
            foreach (var instance in target.Instances)
                keys.AddRange(ProcessControl.Descendants(processes, instance));
            keys = keys.Distinct().Where(k => k.Pid != Environment.ProcessId).ToList();
            var names = new Dictionary<ProcessKey, string>();
            foreach (ref readonly var p in processes)
                names[new ProcessKey(p.Pid, p.CreateTime)] = p.Name;
            return (keys, names);
        });
        var descendants = keys.Skip(target.Instances.Count).ToList();
        var groups = descendants.GroupBy(k => names.GetValueOrDefault(k, "?"), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(g => g.Count())
            .Select(g => g.Count() > 1 ? $"{g.Key} ×{g.Count()}" : g.Key)
            .ToList();
        var listed = string.Join(", ", groups.Take(8)) + (groups.Count > 8 ? ", …" : "");
        var message = descendants.Count == 0
            ? $"End {target}? It has no child process. Unsaved work in it is lost."
            : $"End {target} and its {descendants.Count} descendant process{(descendants.Count == 1 ? "" : "es")} ({listed})? Unsaved work in them is lost.";
        if (await Confirm(root, "End process tree", message, "End all"))
            Apply("End process tree", target with { Instances = keys }, ProcessControl.Kill, $"Ended {keys.Count} process{(keys.Count == 1 ? "" : "es")}.");
    }

    private static async Task Dump(ProcessTarget target, XamlRoot root, bool full)
    {
        var key = target.First;
        string? path;
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FileSavePicker(root.ContentIslandEnvironment.AppWindowId)
            {
                SuggestedFileName = $"{Path.GetFileNameWithoutExtension(target.Name)}_{key.Pid}_{DateTime.Now:yyyyMMdd_HHmmss}",
                DefaultFileExtension = ".dmp",
            };
            picker.FileTypeChoices.Add("Dump file", [".dmp"]);
            path = (await picker.PickSaveFileAsync())?.Path;
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            Raise(InfoBarSeverity.Error, "Create dump", $"The file picker failed: {e.Message}");
            return;
        }
        if (path is null)
            return;
        Raise(InfoBarSeverity.Informational, "Create dump", $"Writing {Path.GetFileName(path)}…");
        try
        {
            await Task.Run(() => ProcessControl.WriteDump(key, path, full));
            var size = new FileInfo(path).Length;
            Raise(InfoBarSeverity.Success, "Create dump", $"{path} ({MetricFormatter.FormatBytes(size)}).");
        }
        catch (Exception e) when (e is ProcessControlException or IOException or UnauthorizedAccessException)
        {
            Raise(InfoBarSeverity.Error, "Create dump", e.Message);
        }
    }

    /// <summary>Runs the action on each instance (off the UI thread), then reports success or the failures.</summary>
    private static async void Apply(string title, ProcessTarget target, Action<ProcessKey> action, string done)
    {
        var errors = await Task.Run(() =>
        {
            var failed = new List<string>();
            foreach (var key in target.Instances)
            {
                try
                {
                    action(key);
                }
                catch (ProcessControlException e)
                {
                    failed.Add(target.IsGroup ? $"PID {key.Pid}: {e.Message}" : e.Message);
                }
            }
            return failed;
        });
        if (errors.Count == 0)
        {
            Raise(InfoBarSeverity.Success, title, done);
            return;
        }
        var shown = string.Join(" ", errors.Take(3));
        var more = errors.Count > 3 ? $" (+{errors.Count - 3} more)" : "";
        var partial = target.IsGroup && errors.Count < target.Instances.Count ? $"Done for {target.Instances.Count - errors.Count} of {target.Instances.Count}. " : "";
        Raise(partial.Length > 0 ? InfoBarSeverity.Warning : InfoBarSeverity.Error, title, partial + shown + more);
    }

    private static async Task<bool> Confirm(XamlRoot root, string title, string message, string primary)
    {
        try
        {
            return await Dialogs.ConfirmAsync(root, title, message, primary);
        }
        catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            Raise(InfoBarSeverity.Warning, title, "Close the open dialog first.");
            return false;
        }
    }
}
