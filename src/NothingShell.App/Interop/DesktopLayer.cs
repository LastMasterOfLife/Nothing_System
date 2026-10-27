using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace NothingShell.App.Interop;

/// <summary>
/// Tiene una finestra sul livello del desktop (docs/04-widgets.md): top-level, di proprietà di
/// Progman (sopravvive a Win+D) e sempre in fondo allo z-order.
/// </summary>
internal static class DesktopLayer
{
    private static readonly uint TaskbarCreated = NativeMethods.RegisterWindowMessage("TaskbarCreated");

    public static void Attach(Window window)
    {
        window.SourceInitialized += (_, _) =>
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            NativeMethods.MakeNonActivatingToolWindow(hwnd);
            OwnByDesktop(hwnd);
            HwndSource.FromHwnd(hwnd)?.AddHook(Hook);
        };
        window.Loaded += (_, _) => NativeMethods.SendToBottom(new WindowInteropHelper(window).Handle);
    }

    private static void OwnByDesktop(nint hwnd)
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != 0)
            NativeMethods.SetOwner(hwnd, progman);
    }

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmWindowPosChanging)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
            if ((pos.Flags & NativeMethods.SwpNoZOrder) == 0 && pos.HwndInsertAfter != NativeMethods.HwndBottom)
            {
                pos.HwndInsertAfter = NativeMethods.HwndBottom;
                Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
            }
        }
        else if (TaskbarCreated != 0 && (uint)msg == TaskbarCreated)
        {
            // Explorer riavviato: Progman è una finestra nuova
            OwnByDesktop(hwnd);
            NativeMethods.SendToBottom(hwnd);
        }
        return 0;
    }
}
