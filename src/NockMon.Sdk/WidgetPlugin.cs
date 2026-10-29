using System.Text.Json.Nodes;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NockMon.Sdk;

/// <summary>Version of the plugin API. A plugin built against a newer minor, or another major, is not loaded.</summary>
public static class PluginApi
{
    /// <summary>Major.minor of the API this host implements.</summary>
    public static Version Version { get; } = new(1, 0);
}

/// <summary>Widget size in board cells.</summary>
public readonly record struct WidgetSize(int Columns, int Rows);

/// <summary>
/// A widget kind offered by a plugin. Public, non-abstract, with a parameterless constructor: the host finds it in the
/// plugin assembly and creates one instance at start. A plugin assembly may hold several.
/// </summary>
public interface IWidgetPlugin
{
    /// <summary>Stable identifier, unique within the plugin (stored in layouts: never change it).</summary>
    string Id { get; }

    /// <summary>Name in the add-widget menu and editor.</summary>
    string DisplayName { get; }

    /// <summary>One or two plain sentences (tooltips).</summary>
    string Summary { get; }

    /// <summary>
    /// Full help (editor, pinned help). Markup: blank line = paragraph, "- " = bullet, **bold**, `code`.
    /// </summary>
    string Details { get; }

    /// <summary>Size given to a new widget.</summary>
    WidgetSize DefaultSize { get; }

    /// <summary>Smallest size the widget can be resized to (also the minimum of a stack holding it).</summary>
    WidgetSize MinSize { get; }

    /// <summary>
    /// Creates a widget instance (UI thread). Called when the widget enters the board; the instance is disposed when it
    /// leaves (layout change, edit, removal), so a widget may be created and disposed several times per session.
    /// </summary>
    IPluginWidget CreateWidget(IWidgetContext context);

    /// <summary>Optional settings editor, shown in the widget's configuration dialog under the title.</summary>
    IPluginSettingsEditor? CreateSettingsEditor(IWidgetContext context) => null;
}

/// <summary>A widget instance on the board.</summary>
public interface IPluginWidget : IDisposable
{
    /// <summary>The widget's content, placed under the host's title bar.</summary>
    UIElement View { get; }

    /// <summary>
    /// Called on the UI thread at the host's sample rate (0.5 s by default) while the widget is visible. Keep it cheap:
    /// do I/O on background tasks and only update visuals here.
    /// </summary>
    void Refresh();
}

/// <summary>Settings editor content for one widget instance.</summary>
public interface IPluginSettingsEditor
{
    /// <summary>The editor's controls.</summary>
    UIElement View { get; }

    /// <summary>Writes the edited values into <see cref="IWidgetContext.Settings"/>.</summary>
    /// <returns>null on success, otherwise an error shown to the user (the dialog stays open).</returns>
    string? Apply();
}

/// <summary>What the host gives a widget instance.</summary>
public interface IWidgetContext
{
    /// <summary>This instance's settings, persisted in the layout. The host never interprets them.</summary>
    JsonObject Settings { get; }

    /// <summary>Persists <see cref="Settings"/> after a change made by the widget itself (e.g. a sort by click).</summary>
    void SaveSettings();

    /// <summary>Folder for the plugin's own files (cache, state), shared by its widgets. Created on first access.</summary>
    string DataDirectory { get; }

    /// <summary>The UI thread's queue, to marshal results of background work.</summary>
    DispatcherQueue Dispatcher { get; }

    /// <summary>Version of the running NockMon.</summary>
    Version HostVersion { get; }
}
