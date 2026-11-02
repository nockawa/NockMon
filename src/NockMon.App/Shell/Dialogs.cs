using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace NockMon.Shell;

/// <summary>
/// The app's question dialogs. WinUI shows one ContentDialog at a time and throws for a second one (an AI agent's
/// question can be open): the user's own requests then simply do nothing; an agent's (<c>busyThrows</c>) learns why.
/// </summary>
internal static class Dialogs
{
    /// <summary>The exception WinUI throws when a dialog is already open.</summary>
    public static bool IsBusy(Exception e) => e is InvalidOperationException or System.Runtime.InteropServices.COMException;

    private static async Task<ContentDialogResult?> ShowAsync(ContentDialog dialog, bool busyThrows)
    {
        try
        {
            return await dialog.ShowAsync();
        }
        catch (Exception e) when (!busyThrows && IsBusy(e))
        {
            return null;
        }
    }

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
        return await ShowAsync(dialog, busyThrows: false) == ContentDialogResult.Primary ? box.Text.Trim() : null;
    }

    public static async Task<bool> ConfirmAsync(XamlRoot root, string title, string message, string primary, bool busyThrows = false)
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
        return await ShowAsync(dialog, busyThrows) == ContentDialogResult.Primary;
    }
}
