using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using NothingShell.App.Rendering;

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
        window.Loaded += (_, _) => NativeMethods.SendToBottom(BottomOf(new WindowInteropHelper(window).Handle));
    }

    /// <summary>In fondo ci sta la compagna del vetro, se c'è; il widget le sta subito sopra.</summary>
    private static nint BottomOf(nint hwnd)
    {
        var owner = NativeMethods.GetOwner(hwnd);
        return owner != 0 && WindowNative.GetClassName(owner) == GlassBackdrop.ClassName ? owner : hwnd;
    }

    private static void OwnByDesktop(nint hwnd)
    {
        var progman = NativeMethods.FindWindow("Progman", null);
        if (progman != 0)
            NativeMethods.SetOwner(BottomOf(hwnd), progman);
    }

    private static nint Hook(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == NativeMethods.WmWindowPosChanging)
        {
            var pos = Marshal.PtrToStructure<NativeMethods.WindowPos>(lParam);
            if ((pos.Flags & NativeMethods.SwpNoZOrder) == 0)
            {
                // Solo riscrivendo il messaggio: chiamare SetWindowPos da qui farebbe rimbalzare i widget all'infinito.
                var bottom = BottomOf(hwnd);
                if (bottom == hwnd)
                {
                    pos.HwndInsertAfter = NativeMethods.HwndBottom;
                }
                else if (NativeMethods.WindowAbove(bottom) is var above && above == hwnd)
                {
                    pos.Flags |= NativeMethods.SwpNoZOrder;
                }
                else
                {
                    pos.HwndInsertAfter = above;
                }
                Marshal.StructureToPtr(pos, lParam, fDeleteOld: false);
            }
        }
        else if (TaskbarCreated != 0 && (uint)msg == TaskbarCreated)
        {
            // Explorer riavviato: Progman è una finestra nuova
            OwnByDesktop(hwnd);
            NativeMethods.SendToBottom(BottomOf(hwnd));
        }
        return 0;
    }
}
