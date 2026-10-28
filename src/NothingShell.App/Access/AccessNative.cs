using System.Runtime.InteropServices;

namespace NothingShell.App.Access;

/// <summary>API Win32 per l'overlay di accesso: monitor, primo piano assoluto, stato del desktop di input.</summary>
internal static partial class AccessNative
{
    private const uint SwpNoActivate = 0x0010;
    private const uint SwpNoMove = 0x0002;
    private const uint SwpNoSize = 0x0001;
    private const uint MonitorInfoFPrimary = 0x1;
    private const uint DesktopReadObjects = 0x0001;
    private const int UoiName = 2;
    private const int NameDisplay = 3;
    private static readonly nint HwndTopmost = -1;

    public delegate bool MonitorEnumProc(nint monitor, nint dc, ref Rect rect, nint data);

    [StructLayout(LayoutKind.Sequential)]
    public struct Rect
    {
        public int Left;
        public int Top;
        public int Right;
        public int Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MonitorInfo
    {
        public uint Size;
        public Rect Monitor;
        public Rect Work;
        public uint Flags;
    }

    /// <summary>Rettangoli (pixel fisici) dei monitor diversi dal principale.</summary>
    public static List<Rect> SecondaryMonitors()
    {
        var result = new List<Rect>();
        EnumDisplayMonitors(0, 0, (nint monitor, nint _, ref Rect _, nint _) =>
        {
            var info = new MonitorInfo { Size = (uint)Marshal.SizeOf<MonitorInfo>() };
            if (GetMonitorInfo(monitor, ref info) && (info.Flags & MonitorInfoFPrimary) == 0)
                result.Add(info.Monitor);
            return true;
        }, 0);
        return result;
    }

    /// <summary>In cima alle finestre "sempre in primo piano" (sopra notch e barra), senza focus.</summary>
    public static void BringToTopmost(nint hwnd) =>
        SetWindowPos(hwnd, HwndTopmost, 0, 0, 0, 0, SwpNoMove | SwpNoSize | SwpNoActivate);

    public static void PlaceTopmost(nint hwnd, Rect bounds) =>
        SetWindowPos(hwnd, HwndTopmost, bounds.Left, bounds.Top, bounds.Right - bounds.Left, bounds.Bottom - bounds.Top, SwpNoActivate);

    /// <summary>
    /// Il desktop che riceve l'input è quello normale dell'utente? Con la sessione bloccata è
    /// "Winlogon" e un processo utente non riesce nemmeno ad aprirlo.
    /// </summary>
    public static bool IsUserDesktopActive()
    {
        var desktop = OpenInputDesktop(0, false, DesktopReadObjects);
        if (desktop == 0)
            return false;
        try
        {
            var buffer = new char[64];
            return GetUserObjectInformation(desktop, UoiName, buffer, buffer.Length * 2, out _)
                && new string(buffer).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            CloseDesktop(desktop);
        }
    }

    /// <summary>Nome visualizzato dell'account (es. "Andrea Rossi"); vuoto se non disponibile.</summary>
    public static string DisplayName()
    {
        var buffer = new char[256];
        var size = (uint)buffer.Length;
        return GetUserNameEx(NameDisplay, buffer, ref size) ? new string(buffer, 0, (int)size) : string.Empty;
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EnumDisplayMonitors(nint dc, nint clip, MonitorEnumProc callback, nint data);

    [LibraryImport("user32.dll", EntryPoint = "GetMonitorInfoW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetMonitorInfo(nint monitor, ref MonitorInfo info);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetWindowPos(nint hwnd, nint insertAfter, int x, int y, int cx, int cy, uint flags);

    [LibraryImport("user32.dll")]
    private static partial nint OpenInputDesktop(uint flags, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint access);

    [LibraryImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool CloseDesktop(nint desktop);

    [LibraryImport("user32.dll", EntryPoint = "GetUserObjectInformationW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserObjectInformation(nint handle, int index, [Out] char[] info, int length, out int needed);

    [LibraryImport("secur32.dll", EntryPoint = "GetUserNameExW", StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetUserNameEx(int format, [Out] char[] buffer, ref uint size);
}
