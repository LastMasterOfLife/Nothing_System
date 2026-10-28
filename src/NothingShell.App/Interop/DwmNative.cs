using System.Runtime.InteropServices;
using NothingShell.Core.Widgets;

namespace NothingShell.App.Interop;

/// <summary>
/// Miniature dal vivo delle finestre (le stesse dell'anteprima della taskbar di Windows) e aspetto
/// della finestra di Windows 11. DWM disegna la miniatura sopra la finestra di destinazione, che non
/// può essere a trasparenza per pixel (<c>AllowsTransparency</c>): lì non comparirebbe.
/// </summary>
internal static partial class DwmNative
{
    private const uint ThumbnailRectDestination = 0x1;
    private const uint ThumbnailOpacity = 0x4;
    private const uint ThumbnailVisible = 0x8;
    private const uint ThumbnailSourceClientAreaOnly = 0x10;
    private const int WindowCornerPreference = 33;
    private const int BorderColor = 34;
    private const int CornerRound = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct ThumbnailProperties
    {
        public uint Flags;
        public WindowNative.Rect Destination;
        public WindowNative.Rect Source;
        public byte Opacity;
        public int Visible;
        public int SourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    /// <summary>Miniatura di <paramref name="source"/> dentro <paramref name="destination"/>; 0 se non si può.</summary>
    public static nint Register(nint destination, nint source) =>
        DwmRegisterThumbnail(destination, source, out var thumbnail) == 0 ? thumbnail : 0;

    public static void Unregister(nint thumbnail)
    {
        if (thumbnail != 0)
            _ = DwmUnregisterThumbnail(thumbnail);
    }

    /// <summary>Dimensioni della finestra sorgente in pixel fisici (0×0 se non disponibili).</summary>
    public static (int Width, int Height) SourceSize(nint thumbnail) =>
        thumbnail != 0 && DwmQueryThumbnailSourceSize(thumbnail, out var size) == 0 ? (size.Width, size.Height) : (0, 0);

    /// <summary>Mostra la miniatura nel rettangolo indicato (pixel fisici, relativi alla finestra di destinazione).</summary>
    public static void Show(nint thumbnail, Rect2 destination)
    {
        var properties = new ThumbnailProperties
        {
            Flags = ThumbnailRectDestination | ThumbnailOpacity | ThumbnailVisible | ThumbnailSourceClientAreaOnly,
            Destination = new WindowNative.Rect
            {
                Left = (int)destination.X,
                Top = (int)destination.Y,
                Right = (int)destination.Right,
                Bottom = (int)destination.Bottom,
            },
            Opacity = 255,
            Visible = 1,
            SourceClientAreaOnly = 0,
        };
        _ = DwmUpdateThumbnailProperties(thumbnail, ref properties);
    }

    /// <summary>Angoli arrotondati e bordo sottile disegnati da Windows 11 (ignorato dalle versioni precedenti).</summary>
    public static void RoundCorners(nint hwnd, uint borderColorRef)
    {
        var corner = CornerRound;
        _ = DwmSetWindowAttribute(hwnd, WindowCornerPreference, ref corner, sizeof(int));
        var color = (int)borderColorRef;
        _ = DwmSetWindowAttribute(hwnd, BorderColor, ref color, sizeof(int));
    }

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmRegisterThumbnail(nint destination, nint source, out nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmUnregisterThumbnail(nint thumbnail);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmQueryThumbnailSourceSize(nint thumbnail, out NativeSize size);

    [DllImport("dwmapi.dll")]
    private static extern int DwmUpdateThumbnailProperties(nint thumbnail, ref ThumbnailProperties properties);

    [LibraryImport("dwmapi.dll")]
    private static partial int DwmSetWindowAttribute(nint hwnd, int attribute, ref int value, int size);
}
