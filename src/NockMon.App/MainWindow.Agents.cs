using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using NockMon.Core.Agents;
using NockMon.Core.Layout;

namespace NockMon;

/// <summary>AI agents (MCP) toolbar button: opt-in switches, connection state, how to connect an agent.</summary>
public sealed partial class MainWindow
{
    private const string AgentCommand = "claude mcp add nockmon -- nockmon mcp";

    private Agents.AgentAccess? _agents;

    private void SetUpAgents()
    {
        _agents = new Agents.AgentAccess(_session, _monitor.Engine, DispatcherQueue, new AgentLayoutHost(this));
        _agents.Changed += UpdateAgentsButton;
        Closed += (_, _) => _agents.Dispose();
        UpdateAgentsButton();
    }

    private void UpdateAgentsButton()
    {
        var clients = _agents!.Clients;
        if (clients.Count > 0)
            AgentsIcon.Foreground = (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];
        else
            AgentsIcon.ClearValue(IconElement.ForegroundProperty); // Back to the button's foreground.
        ToolTipService.SetToolTip(AgentsButton, $"AI agents (MCP): {AgentStatus()}");
    }

    private string AgentStatus()
    {
        var agents = _agents!;
        if (!agents.Enabled)
            return "off, agents cannot connect.";
        if (agents.Error is { } error)
            return $"not running. {error}";
        var clients = agents.Clients;
        return clients.Count == 0 ? "on, no agent connected." : $"connected: {string.Join(", ", clients)}.";
    }

    private void OnAgentsClick(object sender, RoutedEventArgs e)
    {
        var agents = _agents!;
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        TextBlock Caption(string text) => new() { Text = text, TextWrapping = TextWrapping.Wrap, Foreground = secondary, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };

        var status = new TextBlock { TextWrapping = TextWrapping.Wrap, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold };
        var enable = new ToggleSwitch { Header = "Allow AI agents to read NockMon's data", IsOn = agents.Enabled };
        var commandLines = new ToggleSwitch { Header = "Share process command lines", IsOn = agents.ShareCommandLines, IsEnabled = agents.Enabled };
        var layoutChanges = new ToggleSwitch { Header = "Allow layout changes", IsOn = agents.AllowLayoutChanges, IsEnabled = agents.Enabled };
        void Refresh()
        {
            status.Text = char.ToUpperInvariant(AgentStatus()[0]) + AgentStatus()[1..];
            commandLines.IsEnabled = layoutChanges.IsEnabled = agents.Enabled;
        }
        enable.Toggled += (_, _) =>
        {
            agents.Enabled = enable.IsOn;
            Refresh();
        };
        commandLines.Toggled += (_, _) => agents.ShareCommandLines = commandLines.IsOn;
        layoutChanges.Toggled += (_, _) => agents.AllowLayoutChanges = layoutChanges.IsOn;

        var command = new TextBox { Text = AgentCommand, IsReadOnly = true, FontFamily = new FontFamily("Cascadia Mono, Consolas"), FontSize = 12 };
        var copy = new Button { Content = new FontIcon { Glyph = "", FontSize = 14 }, VerticalAlignment = VerticalAlignment.Stretch };
        ToolTipService.SetToolTip(copy, "Copy");
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(copy, "Copy the command");
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(AgentCommand);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
        };
        var commandRow = new Grid { ColumnSpacing = 4, ColumnDefinitions = { new ColumnDefinition(), new ColumnDefinition { Width = GridLength.Auto } } };
        Grid.SetColumn(copy, 1);
        commandRow.Children.Add(command);
        commandRow.Children.Add(copy);

        var panel = new StackPanel
        {
            Width = 380,
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = "AI agents (MCP)", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"] },
                Caption("Lets AI agents such as Claude Code or VS Code (any MCP client) read what NockMon records: current values, " +
                        "the last hour of history, processes, drivers and kernel activity."),
                enable,
                status,
                commandLines,
                Caption("Command lines can contain secrets (tokens, passwords, file paths): share them only if you trust the agent."),
                layoutChanges,
                Caption("Agents can build dashboards: add, change and remove widgets and tabs, create layouts. Their changes open an " +
                        "edit session you review: Done keeps them, Cancel discards them. Deleting a layout asks you first."),
                new TextBlock { Text = "Connect an agent", Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(0, 8, 0, 0) },
                Caption("Claude Code, in a terminal:"),
                commandRow,
                Caption("Other MCP clients: a stdio server with command \"nockmon\" and argument \"mcp\". It connects to this running " +
                        "instance (it never starts NockMon)."),
            },
        };
        Refresh();
        var flyout = new Flyout { Content = panel, Placement = Microsoft.UI.Xaml.Controls.Primitives.FlyoutPlacementMode.BottomEdgeAlignedRight };
        agents.Changed += Refresh;
        flyout.Closed += (_, _) => agents.Changed -= Refresh;
        flyout.ShowAt(AgentsButton);
    }

    /// <summary>The window as seen by the agents' layout tools (UI thread).</summary>
    private sealed class AgentLayoutHost(MainWindow window) : ILayoutHost
    {
        public GridSize PlacementBounds => window.Board.PlacementBounds;
        public int LogicalProcessors => window._monitor.LogicalProcessors;
        public bool AllowChanges => window._session.Settings.AgentLayoutChanges;
        public bool IsValidIcon(string name) => Tabs.FluentIcons.Exists(name);

        public void AgentEditStarted(string client)
        {
            window.SetEditing(true);
            window.ShowInfo(InfoBarSeverity.Informational, $"{client} is editing this layout",
                "Its changes show live. Review them, then Done keeps them, Cancel discards them.");
        }

        public void LayoutChanged()
        {
            window.Board.Rebuild();
            window.UpdateRail();
            window.UpdateWatchedPatterns();
            window.RefreshPicker();
        }

        public async Task<bool> ConfirmAsync(string title, string message, string accept)
        {
            window.ShowFromTray(); // The question needs the window.
            try
            {
                return await Shell.Dialogs.ConfirmAsync(window.Content.XamlRoot, title, message, accept);
            }
            catch (Exception e) when (e is InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                throw new AgentException("NockMon has a dialog open: ask the user to close it, then retry.");
            }
        }
    }
}
