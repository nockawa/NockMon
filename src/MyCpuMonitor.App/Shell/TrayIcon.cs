using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace MyCpuMonitor.Shell;

/// <summary>
/// Notification-area icon on top of Shell_NotifyIcon, with callbacks received by subclassing the main window
/// (runs on the UI thread). The app is elevated and Explorer is not: UIPI drops lower-integrity messages, so the
/// callback and TaskbarCreated messages are explicitly allowed. The icon is re-added when Explorer restarts.
/// Owns the icon handles it is given.
/// </summary>
internal sealed unsafe partial class TrayIcon : IDisposable
{
    public readonly record struct MenuItem(uint Id, string Text, bool Checked = false, bool Separator = false);

    private const uint CallbackMessage = 0x8000 + 0x10; // WM_APP + 16
    private const uint IconId = 1;
    private const nuint SubclassId = 0x6D63706D; // "mcpm"

    private const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2, NIM_SETVERSION = 4;
    private const uint NIF_MESSAGE = 0x1, NIF_ICON = 0x2, NIF_TIP = 0x4, NIF_SHOWTIP = 0x80;
    private const uint NOTIFYICON_VERSION_4 = 4;
    private const uint NIN_SELECT = 0x400, NIN_KEYSELECT = 0x401, WM_CONTEXTMENU = 0x7B, WM_NULL = 0;
    private const uint MSGFLT_ALLOW = 1;
    private const uint MF_STRING = 0, MF_CHECKED = 0x8, MF_SEPARATOR = 0x800;
    private const uint TPM_RIGHTBUTTON = 0x2, TPM_RETURNCMD = 0x100, TPM_BOTTOMALIGN = 0x20;

    private static readonly uint TaskbarCreated = RegisterWindowMessageW("TaskbarCreated");

    private readonly nint _hwnd;
    private GCHandle _self;
    private nint _icon;
    private string _tip;

    [StructLayout(LayoutKind.Sequential)]
    private struct NotifyIconData
    {
        public uint cbSize;
        public nint hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public nint hIcon;
        public fixed char szTip[128];
        public uint dwState;
        public uint dwStateMask;
        public fixed char szInfo[256];
        public uint uVersion;
        public fixed char szInfoTitle[64];
        public uint dwInfoFlags;
        public Guid guidItem;
        public nint hBalloonIcon;
    }

    public TrayIcon(nint hwnd, nint icon, string tip)
    {
        _hwnd = hwnd;
        _icon = icon;
        _tip = tip;
        _self = GCHandle.Alloc(this);
        ChangeWindowMessageFilterEx(hwnd, CallbackMessage, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(hwnd, TaskbarCreated, MSGFLT_ALLOW, 0);
        SetWindowSubclass(hwnd, &SubclassProc, SubclassId, (nuint)GCHandle.ToIntPtr(_self));
        Add();
    }

    /// <summary>Left click or keyboard selection.</summary>
    public event Action? Activated;

    /// <summary>Right click, with the screen anchor point.</summary>
    public event Action<int, int>? MenuRequested;

    /// <summary>Replaces icon and tooltip (the previous icon is destroyed).</summary>
    public void Update(nint icon, string tip)
    {
        var previous = _icon;
        _icon = icon;
        _tip = tip;
        var data = Data(NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_MODIFY, &data);
        if (previous != 0 && previous != icon)
            DestroyIcon(previous);
    }

    /// <summary>Shows a native popup menu at the anchor point; returns the chosen id, 0 if dismissed.</summary>
    public uint ShowMenu(int x, int y, IEnumerable<MenuItem> items)
    {
        var menu = CreatePopupMenu();
        try
        {
            foreach (var item in items)
            {
                if (item.Separator)
                    AppendMenuW(menu, MF_SEPARATOR, 0, null);
                else
                    AppendMenuW(menu, MF_STRING | (item.Checked ? MF_CHECKED : 0), item.Id, item.Text);
            }
            // Required for the menu to close when clicking elsewhere (documented TrackPopupMenu quirk).
            SetForegroundWindow(_hwnd);
            var id = TrackPopupMenuEx(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_BOTTOMALIGN, x, y, _hwnd, 0);
            PostMessageW(_hwnd, WM_NULL, 0, 0);
            return id;
        }
        finally
        {
            DestroyMenu(menu);
        }
    }

    private void Add()
    {
        var data = Data(NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_SHOWTIP);
        Shell_NotifyIconW(NIM_ADD, &data);
        data.uVersion = NOTIFYICON_VERSION_4;
        Shell_NotifyIconW(NIM_SETVERSION, &data);
    }

    private NotifyIconData Data(uint flags)
    {
        var data = new NotifyIconData
        {
            cbSize = (uint)sizeof(NotifyIconData),
            hWnd = _hwnd,
            uID = IconId,
            uFlags = flags,
            uCallbackMessage = CallbackMessage,
            hIcon = _icon,
        };
        var tip = _tip.AsSpan(0, Math.Min(_tip.Length, 127));
        tip.CopyTo(new Span<char>(data.szTip, 128));
        data.szTip[tip.Length] = '\0';
        return data;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        try
        {
            if (GCHandle.FromIntPtr((nint)refData).Target is TrayIcon self)
            {
                if (message == CallbackMessage)
                {
                    // NOTIFYICON_VERSION_4: LOWORD(lParam) = event, wParam = anchor point (x, y).
                    var evt = (uint)(lParam & 0xFFFF);
                    if (evt is NIN_SELECT or NIN_KEYSELECT)
                        self.Activated?.Invoke();
                    else if (evt == WM_CONTEXTMENU)
                        self.MenuRequested?.Invoke((short)(wParam & 0xFFFF), (short)((wParam >> 16) & 0xFFFF));
                    return 0;
                }
                if (message == TaskbarCreated)
                    self.Add(); // Explorer restarted.
            }
        }
        catch (Exception e)
        {
            // An exception escaping an unmanaged callback would terminate the process.
            System.Diagnostics.Debug.WriteLine($"Tray callback failed: {e}");
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    public void Dispose()
    {
        if (!_self.IsAllocated)
            return;
        var data = Data(0);
        Shell_NotifyIconW(NIM_DELETE, &data);
        RemoveWindowSubclass(_hwnd, &SubclassProc, SubclassId);
        _self.Free();
        if (_icon != 0)
            DestroyIcon(_icon);
        _icon = 0;
    }

    [LibraryImport("shell32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool Shell_NotifyIconW(uint message, NotifyIconData* data);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool RemoveWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("user32.dll")]
    private static partial nint CreatePopupMenu();

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool AppendMenuW(nint menu, uint flags, nuint id, string? text);

    [LibraryImport("user32.dll")]
    private static partial uint TrackPopupMenuEx(nint menu, uint flags, int x, int y, nint hwnd, nint parameters);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool DestroyMenu(nint menu);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool DestroyIcon(nint icon);
}
