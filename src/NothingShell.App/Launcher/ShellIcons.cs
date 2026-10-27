using System.Runtime.InteropServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NothingShell.Core.Design;

namespace NothingShell.App.Launcher;

/// <summary>
/// Icone delle app di shell:AppsFolder tramite IShellItemImageFactory, in scala di grigi e in cache.
/// Va usata dal thread UI (STA).
/// </summary>
public sealed class ShellIcons
{
    private const int IconPixels = 48;
    private const int SiigbfIconOnly = 0x4;

    private readonly Dictionary<string, BitmapSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetCached(string appId, out BitmapSource? icon) => _cache.TryGetValue(appId, out icon);

    public BitmapSource? Get(string appId)
    {
        if (_cache.TryGetValue(appId, out var cached))
            return cached;
        var icon = Load(appId);
        _cache[appId] = icon;
        return icon;
    }

    private static BitmapSource? Load(string appId)
    {
        if (appId.StartsWith(AppCatalog.InternalPrefix, StringComparison.Ordinal))
            return null; // voce della shell: si usa l'iniziale a punti

        nint bitmap = 0;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            // HRESULT restituito, non eccezione: un'app appena disinstallata dà "file non trovato".
            if (SHCreateItemFromParsingName($"shell:AppsFolder\\{appId}", 0, ref iid, out var factory) != 0 || factory is null)
                return null;
            if (factory.GetImage(new NativeSize { Width = IconPixels, Height = IconPixels }, SiigbfIconOnly, out bitmap) != 0)
                return null;
            return ToMonochrome(bitmap);
        }
        catch (Exception ex) when (ex is COMException or ArgumentException or InvalidCastException)
        {
            return null;
        }
        finally
        {
            if (bitmap != 0)
                DeleteObject(bitmap);
        }
    }

    /// <summary>
    /// HBITMAP a 32 bit premoltiplicato → BitmapSource grigia. CreateBitmapSourceFromHBitmap
    /// perderebbe l'alfa, quindi si leggono i pixel grezzi con GetDIBits.
    /// </summary>
    private static BitmapSource? ToMonochrome(nint bitmap)
    {
        if (GetObject(bitmap, Marshal.SizeOf<NativeBitmap>(), out var info) == 0 || info.Width <= 0 || info.Height <= 0)
            return null;

        var width = info.Width;
        var height = info.Height;
        var header = new BitmapInfoHeader
        {
            Size = (uint)Marshal.SizeOf<BitmapInfoHeader>(),
            Width = width,
            Height = -height, // dall'alto in basso
            Planes = 1,
            BitCount = 32,
        };
        var pixels = new byte[width * height * 4];
        var dc = GetDC(0);
        try
        {
            if (GetDIBits(dc, bitmap, 0, (uint)height, pixels, ref header, 0) == 0)
                return null;
        }
        finally
        {
            _ = ReleaseDC(0, dc);
        }

        Monochrome.Unpremultiply(pixels);
        Monochrome.ApplyBgra(pixels);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, width * 4);
        source.Freeze();
        return source;
    }

    [ComImport]
    [Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(NativeSize size, int flags, out nint bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeSize
    {
        public int Width;
        public int Height;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeBitmap
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPixel;
        public nint Bits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BitmapInfoHeader
    {
        public uint Size;
        public int Width;
        public int Height;
        public ushort Planes;
        public ushort BitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ClrUsed;
        public uint ClrImportant;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(
        string path, nint bindContext, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory? factory);

    [DllImport("gdi32.dll")]
    private static extern int GetObject(nint handle, int size, out NativeBitmap bitmap);

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(nint dc, nint bitmap, uint start, uint lines, [Out] byte[] bits, ref BitmapInfoHeader info, uint usage);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(nint handle);

    [DllImport("user32.dll")]
    private static extern nint GetDC(nint hwnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(nint hwnd, nint dc);
}
