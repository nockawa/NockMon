using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace MyCpuMonitor.Shell;

internal static class Dialogs
{
    /// <returns>The trimmed name, or null if cancelled.</returns>
    public static async Task<string?> PromptNameAsync(XamlRoot root, string title, string initial, string primary, string placeholder = "Layout name")
    {
        var box = new TextBox { Text = initial, PlaceholderText = placeholder, MinWidth = 300 };
        box.SelectAll();
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = box,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(initial),
        };
        box.TextChanged += (_, _) => dialog.IsPrimaryButtonEnabled = !string.IsNullOrWhiteSpace(box.Text);
        return await dialog.ShowAsync() == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = root,
            Title = title,
            Content = message,
            PrimaryButtonText = primary,
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
