using System.Runtime.InteropServices;
using NothingShell.Core.Bar;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Interop;

/// <summary>API Win32 per elencare, controllare e osservare le finestre delle altre app.</summary>
internal static partial class WindowNative
{
    public const int SwHide = 0;
    public const int SwShow = 5;
    public const int SwMinimize = 6;
    public const int SwRestore = 9;
    public const uint WmClose = 0x0010;

    public const uint EventSystemForeground = 0x0003;
    public const uint EventSystemMinimizeStart = 0x0016;
    public const uint EventSystemMinimizeEnd = 0x0017;
    public const uint EventObjectCreate = 0x8000;
    public const uint EventObjectHide = 0x8003;
    public const uint EventObjectLocationChange = 0x800B;
    public const uint EventObjectNameChange = 0x800C;
    public const uint EventObjectCloaked = 0x8017;
    public const uint EventObjectUncloaked = 0x8018;
    public const uint WinEventOutOfContext = 0x0000;
    public const uint WinEventSkipOwnProcess = 0x0002;

    public const long WsExToolWindow = 0x00000080;
    public const long WsExAppWindow = 0x00040000;
    public const long WsExNoActivate = 0x08000000;

    private const int GwlExStyle = -20;
    private const uint GwOwner = 4;
    private const uint GaRoot = 2;
    private const uint GaRootOwner = 3;
    private const int DwmwaCloaked = 14;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint WmGetIcon = 0x007F;
    private const uint SmtoAbortIfHung = 0x0002;
    private const int GclpHIcon = -14;
    private const int GclpHIconSm = -34;
    private const uint MonitorDefaultToNearest = 2;
    private const uint MonitorDefaultToPrimary = 1;
    private const uint KeyEventKeyUp = 0x0002;
    private const uint MonitorInfoFPrimary = 0x1;
    private const uint SwpNoZOrder = 0x0004;
    private const uint SwpNoActivate = 0x0010;
    private const uint ShgfiIcon = 0x100;
    private const uint ShgfiUseFileAttributes = 0x10;
    private const uint FileAttributeDirectory = 0x10;
    private const uint FileAttributeNormal = 0x80;

    public delegate bool EnumWindowsProc(nint hwnd, nint lParam);

    private delegate bool MonitorEnumProc(nint monitor, nint dc, nint rect, nint lParam);

    public delegate void WinEventProc(nint hook, uint eventType, nint hwnd, int idObject, int idChild, uint thread, uint time);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ShFileInfo
    {
        public nint Icon;
        public int IconIndex;
        public uint Attributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string DisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
        public string TypeName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct MonitorInfoEx
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string Device;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool EnumWindows(EnumWindowsProc callback, nint lParam);

    [DllImport("user32.dll")]
    public static extern nint SetWinEventHook(uint eventMin, uint eventMax, nint module, WinEventProc callback, uint processId, uint threadId, uint flags);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool UnhookWinEvent(nint hook);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsWindowVisible(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool IsIconic(nint hwnd);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ShowWindow(nint hwnd, int command);

    [LibraryImport("user32.dll")]
    public static partial nint GetForegroundWindow();

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool SetForegroundWindow(nint hwnd);

    [LibraryImport("user32.dll", EntryPoint = "PostMessageW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool PostMessage(nint hwnd, uint msg, nint wParam, nint lParam);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetWindowRect(nint hwnd, out Rect rect);

    [LibraryImport("user32.dll")]
    public static partial uint GetWindowThreadProcessId(nint hwnd, out uint processId);

    [LibraryImport("user32.dll")]
    private static partial void keybd_event(byte vk, byte scan, uint flags, nuint extraInfo);

    [LibraryImport("user32.dll")]
    private static partial nint GetWindow(nint hwnd, uint command);

    [LibraryImport("user32.dll")]
    private static partial nint GetAncestor(nint hwnd, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static partial nint GetWindowLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    private static partial nint GetClassLongPtr(nint hwnd, int index);

    [LibraryImport("user32.dll", EntryPoint = "GetWindowTextW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetWindowText(nint hwnd, [Out] char[] buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "GetClassNameW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int GetClassName(nint hwnd, [Out] char[] buffer, int maxCount);

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW")]
    private static partial nint SendMessageTimeout(nint hwnd, uint msg, nint wParam, nint lParam, uint flags, uint timeout, out nint result);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromWindow(nint hwnd, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint MonitorFromPoint(long point, uint flags);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmGetWindowAttribute(nint hwnd, int attribute, out int value, int size);

    [LibraryImport("kernel32.dll")]
    private static partial nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint processId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool QueryFullProcessImageName(nint process, uint flags, [Out] char[] buffer, ref int size);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseHandle(nint handle);

    [LibraryImport("shell32.dll", EntryPoint = "ExtractIconExW", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint ExtractIconEx(string file, int index, out nint large, out nint small, uint count);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DestroyIcon(nint icon);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint lParam);

    [DllImport("user32.dll", EntryPoint = "GetMonitorInfoW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoEx(nint monitor, ref MonitorInfoEx info);

    [LibraryImport("shcore.dll")]
    private static partial int GetDpiForMonitor(nint monitor, int type, out uint dpiX, out uint dpiY);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetCursorPos(out NativePoint point);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern nint SHGetFileInfo(string path, uint attributes, ref ShFileInfo info, uint size, uint flags);

    public static string GetTitle(nint hwnd) => ReadString(hwnd, 512, GetWindowText);

    public static string GetClassName(nint hwnd) => ReadString(hwnd, 256, GetClassName);

    public static nint GetOwner(nint hwnd) => GetWindow(hwnd, GwOwner);

    public static bool IsTopLevel(nint hwnd) => GetAncestor(hwnd, GaRoot) == hwnd;

    public static nint GetRootOwner(nint hwnd) => hwnd == 0 ? 0 : GetAncestor(hwnd, GaRootOwner) is var r and not 0 ? r : hwnd;

    public static long GetExStyle(nint hwnd) => GetWindowLongPtr(hwnd, GwlExStyle);

    public static bool IsCloaked(nint hwnd) =>
        DwmGetWindowAttribute(hwnd, DwmwaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0;

    public static string? GetProcessPath(uint processId)
    {
        var process = OpenProcess(ProcessQueryLimitedInformation, false, processId);
        if (process == 0)
            return null;
        try
        {
            var buffer = new char[1024];
            var size = buffer.Length;
            return QueryFullProcessImageName(process, 0, buffer, ref size) ? new string(buffer, 0, size) : null;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    /// <summary>Icona della finestra (non va distrutta: appartiene alla finestra/classe).</summary>
    public static nint GetWindowIcon(nint hwnd)
    {
        foreach (var kind in new nint[] { 1, 2, 0 }) // ICON_BIG, ICON_SMALL2, ICON_SMALL
            if (SendMessageTimeout(hwnd, WmGetIcon, kind, 0, SmtoAbortIfHung, 100, out var icon) != 0 && icon != 0)
                return icon;
        var classIcon = GetClassLongPtr(hwnd, GclpHIcon);
        return classIcon != 0 ? classIcon : GetClassLongPtr(hwnd, GclpHIconSm);
    }

    /// <summary>Prima icona grande dell'eseguibile; il chiamante deve distruggerla con <see cref="DestroyIcon"/>.</summary>
    public static nint ExtractExeIcon(string path)
    {
        if (ExtractIconEx(path, 0, out var large, out var small, 1) == 0)
            return 0;
        if (small != 0 && small != large)
            DestroyIcon(small);
        return large;
    }

    /// <summary>
    /// Icona grande del tipo di file (dal nome, senza accedere al disco); il chiamante deve distruggerla
    /// con <see cref="DestroyIcon"/>.
    /// </summary>
    public static nint FileTypeIcon(string name, bool isFolder)
    {
        var info = new ShFileInfo();
        var attributes = isFolder ? FileAttributeDirectory : FileAttributeNormal;
        return SHGetFileInfo(name, attributes, ref info, (uint)Marshal.SizeOf<ShFileInfo>(), ShgfiIcon | ShgfiUseFileAttributes) != 0
            ? info.Icon
            : 0;
    }

    /// <summary>Monitor collegati, in pixel fisici, con la loro scala.</summary>
    public static IReadOnlyList<DisplayMonitor> Monitors()
    {
        var monitors = new List<DisplayMonitor>();
        EnumDisplayMonitors(0, 0, (monitor, _, _, _) =>
        {
            var info = new MonitorInfoEx { Size = (uint)Marshal.SizeOf<MonitorInfoEx>() };
            if (!GetMonitorInfoEx(monitor, ref info))
                return true;
            var scale = GetDpiForMonitor(monitor, 0, out var dpi, out _) == 0 ? dpi / 96.0 : 1.0;
            monitors.Add(new DisplayMonitor(monitor, info.Device, ToRect2(info.Monitor), ToRect2(info.Work),
                (info.Flags & MonitorInfoFPrimary) != 0, scale));
            return true;
        }, 0);
        return monitors;
    }

    /// <summary>HMONITOR del monitor più vicino alla finestra.</summary>
    public static nint MonitorOf(nint hwnd) => MonitorFromWindow(hwnd, MonitorDefaultToNearest);

    /// <summary>HMONITOR del monitor sotto il puntatore del mouse.</summary>
    public static nint MonitorAtCursor() =>
        GetCursorPos(out var p) ? MonitorFromPoint(((long)p.Y << 32) | (uint)p.X, MonitorDefaultToNearest) : 0;

    /// <summary>
    /// Sposta e ridimensiona in pixel fisici. Due passaggi: se la finestra cambia monitor e DPI, WPF si
    /// riscala al primo e il secondo rimette le misure esatte.
    /// </summary>
    public static void Place(nint hwnd, Rect2 rect)
    {
        for (var i = 0; i < 2; i++)
            SetWindowPos(hwnd, 0, (int)rect.X, (int)rect.Y, (int)rect.Width, (int)rect.Height, SwpNoZOrder | SwpNoActivate);
    }

    /// <summary>Rettangolo (pixel fisici) del monitor che contiene la finestra, e se è il principale.</summary>
    public static (Rect Bounds, bool IsPrimary) GetMonitor(nint hwnd)
    {
        var monitor = MonitorFromWindow(hwnd, MonitorDefaultToNearest);
        var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
        GetMonitorInfo(monitor, ref info);
        return (info.Monitor, monitor == MonitorFromPoint(0, MonitorDefaultToPrimary));
    }

    /// <summary>Porta la finestra in primo piano anche se Windows limita il cambio di focus.</summary>
    public static void BringToFront(nint hwnd)
    {
        if (SetForegroundWindow(hwnd) && GetForegroundWindow() == hwnd)
            return;

        // Windows concede il primo piano a chi ha appena ricevuto input: un tocco di Alt sblocca il cambio.
        const byte vkMenu = 0x12;
        PressKeys(vkMenu);
        SetForegroundWindow(hwnd);
    }

    /// <summary>Premi e rilascia una combinazione di tasti (es. Win+A).</summary>
    public static void PressKeys(params byte[] keys)
    {
        ArgumentNullException.ThrowIfNull(keys);
        foreach (var k in keys)
            keybd_event(k, 0, 0, 0);
        foreach (var k in keys.Reverse())
            keybd_event(k, 0, KeyEventKeyUp, 0);
    }

    private static Rect2 ToRect2(Rect r) => new(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);

    private static string ReadString(nint hwnd, int capacity, Func<nint, char[], int, int> read)
    {
        var buffer = new char[capacity];
        var length = read(hwnd, buffer, capacity);
        return length > 0 ? new string(buffer, 0, length) : string.Empty;
    }
}
