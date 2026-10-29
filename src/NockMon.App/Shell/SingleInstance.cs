using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NockMon.Shell;

/// <summary>
/// One instance per session. The first instance holds a named mutex; a later launch detects it (an elevated owner
/// makes the mutex inaccessible to a non-elevated launcher: access denied also means "exists"), broadcasts a show
/// request and exits, before any UAC prompt. The running instance listens on its main window (subclass) and lets the
/// request through UIPI since the sender may be non-elevated.
/// The launcher duplicates <see cref="MutexName"/> and <see cref="ShowMessageName"/>.
/// </summary>
internal static unsafe partial class SingleInstance
{
    public const string MutexName = @"Local\NockMon.Instance";
    public const string ShowMessageName = "NockMon.ShowWindow";

    private const nint HWND_BROADCAST = 0xFFFF;
    private const uint MSGFLT_ALLOW = 1;
    private const nuint SubclassId = 0x6D637369; // "mcsi"

    private static readonly uint ShowMessage = RegisterWindowMessageW(ShowMessageName);
    private static Mutex? _mutex;
    private static Action? _onShow;

    /// <summary>True if another instance already runs in this session (whatever its elevation).</summary>
    public static bool IsAnotherInstanceRunning()
    {
        try
        {
            if (!Mutex.TryOpenExisting(MutexName, out var existing))
                return false;
            existing.Dispose();
            return true;
        }
        catch (UnauthorizedAccessException)
        {
            return true; // Owned by an elevated instance.
        }
    }

    /// <summary>Claims the instance. False if another instance won the race.</summary>
    public static bool TryAcquire()
    {
        try
        {
            _mutex = new Mutex(initiallyOwned: false, MutexName, out var createdNew);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Asks the running instance to show its window.</summary>
    public static void SignalExisting() => PostMessageW(HWND_BROADCAST, ShowMessage, 0, 0);

    /// <summary>Raises <paramref name="onShow"/> (UI thread) when another launch asks this instance to show itself.</summary>
    public static void Listen(nint hwnd, Action onShow)
    {
        _onShow = onShow;
        ChangeWindowMessageFilterEx(hwnd, ShowMessage, MSGFLT_ALLOW, 0);
        SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        if (message == ShowMessage)
        {
            try
            {
                _onShow?.Invoke();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Show request failed: {e}");
            }
            return 0;
        }
        return DefSubclassProc(hwnd, message, wParam, lParam);
    }

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterWindowMessageW(string name);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool PostMessageW(nint hwnd, uint message, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ChangeWindowMessageFilterEx(nint hwnd, uint message, uint action, nint changeFilterStruct);

    [LibraryImport("comctl32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowSubclass(nint hwnd, delegate* unmanaged[Stdcall]<nint, uint, nint, nint, nuint, nuint, nint> proc, nuint id, nuint refData);

    [LibraryImport("comctl32.dll")]
    private static partial nint DefSubclassProc(nint hwnd, uint message, nint wParam, nint lParam);
}
