using MyCpuMonitor.Core.Persistence;
using MyCpuMonitor.Core.Settings;

namespace MyCpuMonitor.Core.Layout;

/// <summary>
/// The active layout and its lifecycle. Edits happen in an edit session: committing saves the layout,
/// cancelling restores the state captured when the session began. Layout management (switch, create,
/// duplicate, rename, delete) is refused during an edit session.
/// </summary>
public sealed class LayoutSession
{
    private readonly LayoutStore _store;
    private readonly SettingsStore _settingsStore;
    private LayoutDocument? _editBackup;

    public LayoutSession(LayoutStore store, SettingsStore settingsStore)
    {
        _store = store;
        _settingsStore = settingsStore;
        Settings = settingsStore.Load();
        Current = LoadInitial();
    }

    public AppSettings Settings { get; }
    public LayoutDocument Current { get; private set; }
    public bool IsEditing => _editBackup is not null;

    /// <summary>Raised when <see cref="Current"/> is replaced by another document.</summary>
    public event Action? CurrentChanged;

    public IReadOnlyList<string> Names => _store.List();

    public void SaveSettings() => _settingsStore.Save(Settings);

    public void Switch(string name)
    {
        EnsureNotEditing();
        SetCurrent(_store.Load(name));
    }

    public void CreateEmpty(string name)
    {
        EnsureNotEditing();
        EnsureAvailable(name);
        var layout = new LayoutDocument { Name = name, Board = Clone(Current).Board };
        _store.Save(layout);
        SetCurrent(layout);
    }

    public void Duplicate(string name)
    {
        EnsureNotEditing();
        EnsureAvailable(name);
        var layout = Clone(Current);
        layout.Name = name;
        _store.Save(layout);
        SetCurrent(layout);
    }

    public void Rename(string name)
    {
        EnsureNotEditing();
        if (name == Current.Name)
            return;
        if (!string.Equals(name, Current.Name, StringComparison.OrdinalIgnoreCase))
            EnsureAvailable(name);
        _store.Rename(Current.Name, name);
        Current.Name = name;
        Settings.ActiveLayout = name;
        SaveSettings();
    }

    /// <summary>Deletes the current layout and switches to another one (a default layout is created if none is left).</summary>
    public void Delete()
    {
        EnsureNotEditing();
        _store.Delete(Current.Name);
        SetCurrent(LoadInitial());
    }

    /// <summary>
    /// Persists a change made outside an edit session (e.g. sorting a table by clicking its header).
    /// During an edit session the change is part of the session: saved on commit, reverted on cancel.
    /// </summary>
    public void SaveCurrent()
    {
        if (!IsEditing)
            _store.Save(Current);
    }

    public void BeginEdit()
    {
        EnsureNotEditing();
        _editBackup = Clone(Current);
    }

    public void CommitEdit()
    {
        EnsureEditing();
        _store.Save(Current);
        _editBackup = null;
    }

    public void CancelEdit()
    {
        EnsureEditing();
        var backup = _editBackup!;
        _editBackup = null;
        Current = backup;
        CurrentChanged?.Invoke();
    }

    public static LayoutDocument Clone(LayoutDocument layout) => LayoutStore.Deserialize(LayoutStore.Serialize(layout));

    private LayoutDocument LoadInitial()
    {
        var names = _store.List();
        if (names.Count == 0)
        {
            var layout = LayoutDocument.CreateDefault();
            _store.Save(layout);
            return layout;
        }
        var name = names.FirstOrDefault(n => string.Equals(n, Settings.ActiveLayout, StringComparison.OrdinalIgnoreCase)) ?? names[0];
        return _store.Load(name);
    }

    private void SetCurrent(LayoutDocument layout)
    {
        Current = layout;
        Settings.ActiveLayout = layout.Name;
        SaveSettings();
        CurrentChanged?.Invoke();
    }

    private void EnsureAvailable(string name)
    {
        LayoutStore.FileNameFor(name); // Validates.
        if (_store.Exists(name))
            throw new InvalidOperationException($"A layout named '{name}' already exists.");
    }

    private void EnsureNotEditing()
    {
        if (IsEditing)
            throw new InvalidOperationException("Not allowed while editing the layout.");
    }

    private void EnsureEditing()
    {
        if (!IsEditing)
            throw new InvalidOperationException("No edit session in progress.");
    }
}
