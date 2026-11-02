using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using NockMon.Core.Folders;

namespace NockMon.Editing;

/// <summary>A folder (typed, or picked with Browse) and its scan options: for the folder widget and folder series.</summary>
internal sealed partial class FolderScopeEditor : StackPanel
{
    private readonly TextBox _path = new() { Header = "Folder", PlaceholderText = @"D:\Downloads, %TEMP%…", MinWidth = 360 };
    private readonly Button _browse = new() { Content = "Browse…", VerticalAlignment = VerticalAlignment.Bottom };
    private readonly ToggleSwitch _recursive = new() { Header = "Subfolders" };
    private readonly ToggleSwitch _sizeOnDisk = new() { Header = "Size on disk" };
    private readonly TextBox _include = new() { Header = "Include files", PlaceholderText = "All (e.g. *.log; *.tmp)", MinWidth = 200 };
    private readonly TextBox _exclude = new() { Header = "Exclude folders", PlaceholderText = "None (e.g. node_modules; .git)", MinWidth = 200 };

    public FolderScopeEditor(FolderScope scope)
    {
        Spacing = 8;
        // A grid, not a horizontal stack: a long path must not push Browse out of the dialog.
        var pathRow = new Grid { ColumnSpacing = 8 };
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        pathRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(_browse, 1);
        pathRow.Children.Add(_path);
        pathRow.Children.Add(_browse);
        Children.Add(pathRow);
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { _include, _exclude } });
        Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { _recursive, _sizeOnDisk } });
        ToolTipService.SetToolTip(_sizeOnDisk, "Space taken on the volume (whole clusters; less for compressed, sparse and online-only files) instead of the files' sizes.");
        ToolTipService.SetToolTip(_exclude, "Folder names skipped with their content; * and ? wildcards, ; between names.");
        ToolTipService.SetToolTip(_include, "File names counted; * and ? wildcards, ; between patterns.");
        _browse.Click += OnBrowse;
        Load(scope);
    }

    public void Load(FolderScope scope)
    {
        _path.Text = scope.Path;
        _recursive.IsOn = scope.Recursive;
        _include.Text = scope.Include ?? "";
        _exclude.Text = scope.Exclude ?? "";
        _sizeOnDisk.IsOn = scope.SizeOnDisk;
    }

    /// <returns>The scope, or null with <paramref name="error"/> set (no folder, or not found: a typo would only show on the board).</returns>
    public FolderScope? Build(out string? error)
    {
        error = null;
        var scope = new FolderScope(_path.Text.Trim(), _recursive.IsOn, Optional(_include.Text), Optional(_exclude.Text), _sizeOnDisk.IsOn);
        if (scope.Path.Length == 0)
            error = "Choose a folder.";
        else if (!Directory.Exists(scope.FullPath))
            error = $"Folder not found: {scope.FullPath}";
        return error is null ? scope : null;
    }

    private static string? Optional(string text) => string.IsNullOrWhiteSpace(text) ? null : text.Trim();

    /// <summary>Windows App SDK picker: unlike the WinRT one, it works in an elevated app.</summary>
    private async void OnBrowse(object sender, RoutedEventArgs e)
    {
        try
        {
            var picker = new Microsoft.Windows.Storage.Pickers.FolderPicker(XamlRoot.ContentIslandEnvironment.AppWindowId);
            var current = Environment.ExpandEnvironmentVariables(_path.Text.Trim());
            if (current.Length > 0 && Directory.Exists(current))
                picker.SuggestedStartFolder = current;
            if (await picker.PickSingleFolderAsync() is { } result)
                _path.Text = result.Path;
        }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or InvalidOperationException or ArgumentException)
        {
            ToolTipService.SetToolTip(_browse, $"The folder picker failed ({ex.Message}): type the path.");
        }
    }
}
