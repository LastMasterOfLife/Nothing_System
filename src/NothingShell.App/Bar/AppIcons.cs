using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NothingShell.App.Interop;
using NothingShell.Core.Design;

namespace NothingShell.App.Bar;

/// <summary>Icone delle app con i loro colori originali, con cache per app.</summary>
public sealed class AppIcons
{
    private readonly Dictionary<string, ImageSource?> _cache = new(StringComparer.Ordinal);

    /// <summary>Icona dell'app; null se l'app non ne espone (si usa l'iniziale a punti).</summary>
    public ImageSource? Get(string appKey, nint hwnd, string? exePath)
    {
        if (_cache.TryGetValue(appKey, out var cached))
            return cached;

        // ApplicationFrameHost ospita tutte le app UWP: né la sua finestra né il suo eseguibile
        // hanno l'icona dell'app, quindi si usa l'iniziale a punti.
        var icon = IsUwpHost(exePath) ? null : FromWindow(hwnd) ?? FromExe(exePath);
        _cache[appKey] = icon;
        return icon;
    }

    /// <summary>
    /// Icona del tipo di file o di cartella. Sui percorsi di rete si decide dalla sola estensione senza
    /// toccare il disco: un server irraggiungibile bloccherebbe la barra.
    /// </summary>
    public ImageSource? ForFile(string path)
    {
        ArgumentNullException.ThrowIfNull(path);
        var extension = System.IO.Path.GetExtension(path.TrimEnd('\\')).ToLowerInvariant();
        var isFolder = path.StartsWith(@"\\", StringComparison.Ordinal)
            ? extension.Length == 0
            : System.IO.Directory.Exists(path); // es. "beelbo-connector-1.4.0" è una cartella
        var key = "file:" + (isFolder ? "<folder>" : extension);
        if (_cache.TryGetValue(key, out var cached))
            return cached;

        var hicon = WindowNative.FileTypeIcon(isFolder ? "folder" : "x" + extension, isFolder);
        ImageSource? icon = null;
        if (hicon != 0)
        {
            icon = Convert(hicon);
            WindowNative.DestroyIcon(hicon);
        }
        _cache[key] = icon;
        return icon;
    }

    private static bool IsUwpHost(string? exePath) =>
        exePath?.EndsWith("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) == true;

    private static BitmapSource? FromWindow(nint hwnd)
    {
        var hicon = WindowNative.GetWindowIcon(hwnd);
        return hicon == 0 ? null : Convert(hicon);
    }

    private static BitmapSource? FromExe(string? exePath)
    {
        if (string.IsNullOrEmpty(exePath) || exePath.StartsWith("pid:", StringComparison.Ordinal))
            return null;
        var hicon = WindowNative.ExtractExeIcon(exePath);
        if (hicon == 0)
            return null;
        try
        {
            return Convert(hicon);
        }
        finally
        {
            WindowNative.DestroyIcon(hicon);
        }
    }

    private static BitmapSource? Convert(nint hicon)
    {
        try
        {
            // Copia dei pixel a colori: la bitmap non dipende più dall'HICON, che può essere distrutto.
            var source = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
            var bgra = new FormatConvertedBitmap(source, PixelFormats.Bgra32, null, 0);
            var stride = bgra.PixelWidth * 4;
            var pixels = new byte[stride * bgra.PixelHeight];
            bgra.CopyPixels(pixels, stride, 0);

            var result = BitmapSource.Create(bgra.PixelWidth, bgra.PixelHeight, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            result.Freeze();
            return result;
        }
        catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.COMException)
        {
            return null; // icona non valida o già distrutta
        }
    }
}
