using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace NockMon.Shell;

/// <summary>
/// One instance per session. The first instance holds a named mutex; a later launch detects it (an elevated owner
/// makes the mutex inaccessible to a non-elevated launcher: access denied also means "exists"), broadcasts a show
/// request and exits, before any UAC prompt. The running instance listens on its main window (subclass) and lets the
/// request through UIPI since the sender may be non-elevated. The uninstaller asks it to exit the same way
/// (<c>NockMon.exe --exit</c>): a clean exit stops the kernel trace session, which a forced kill would leave running.
/// The launcher duplicates <see cref="MutexName"/> and <see cref="ShowMessageName"/>.
/// </summary>
internal static unsafe partial class SingleInstance
{
    public const string MutexName = @"Local\NockMon.Instance";
    public const string ShowMessageName = "NockMon.ShowWindow";
    public const string ExitMessageName = "NockMon.Exit";

    private const nint HWND_BROADCAST = 0xFFFF;
    private const uint MSGFLT_ALLOW = 1;
    private const nuint SubclassId = 0x6D637369; // "mcsi"
    private const uint WM_ENDSESSION = 0x16;

    private static readonly uint ShowMessage = RegisterWindowMessageW(ShowMessageName);
    private static readonly uint ExitMessage = RegisterWindowMessageW(ExitMessageName);
    private static Mutex? _mutex;
    private static Action? _onShow;
    private static Action? _onExit;
    private static Action? _onEndSession;

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

    /// <summary>Asks the running instance (if any) to exit, and waits for it.</summary>
    /// <returns>True when no instance runs any more.</returns>
    public static bool ExitExisting(TimeSpan timeout)
    {
        if (!IsAnotherInstanceRunning())
            return true;
        PostMessageW(HWND_BROADCAST, ExitMessage, 0, 0);
        var deadline = Environment.TickCount64 + (long)timeout.TotalMilliseconds;
        while (Environment.TickCount64 < deadline)
        {
            Thread.Sleep(100);
            if (!IsAnotherInstanceRunning())
                return true;
        }
        return false;
    }

    /// <summary>
    /// Raises (UI thread) <paramref name="onShow"/> when another launch asks this instance to show itself,
    /// <paramref name="onExit"/> when the uninstaller asks it to exit, and <paramref name="onEndSession"/> when Windows
    /// shuts down or the user logs off (the process ends right after: save now).
    /// </summary>
    public static void Listen(nint hwnd, Action onShow, Action onExit, Action onEndSession)
    {
        _onShow = onShow;
        _onExit = onExit;
        _onEndSession = onEndSession;
        ChangeWindowMessageFilterEx(hwnd, ShowMessage, MSGFLT_ALLOW, 0);
        ChangeWindowMessageFilterEx(hwnd, ExitMessage, MSGFLT_ALLOW, 0);
        SetWindowSubclass(hwnd, &SubclassProc, SubclassId, 0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static nint SubclassProc(nint hwnd, uint message, nint wParam, nint lParam, nuint id, nuint refData)
    {
        var handler = message == ShowMessage ? _onShow
            : message == ExitMessage ? _onExit
            : message == WM_ENDSESSION && wParam != 0 ? _onEndSession
            : null;
        if (handler is not null)
        {
            try
            {
                handler();
            }
            catch (Exception e)
            {
                System.Diagnostics.Debug.WriteLine($"Message {message} failed: {e}");
            }
            if (message != WM_ENDSESSION)
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
