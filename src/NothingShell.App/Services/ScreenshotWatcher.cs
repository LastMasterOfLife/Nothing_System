using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Notch;

namespace NothingShell.App.Services;

/// <summary>Una cattura appena fatta: l'immagine e, se Windows l'ha salvata (o la si salva), il file.</summary>
public sealed class ScreenshotCapture(BitmapSource image, string? path)
{
    public BitmapSource Image { get; } = image;

    public string? Path { get; set; } = path;

    /// <summary>Il file della cattura; se Windows non l'ha salvata la salva in Immagini\Screenshot.</summary>
    public string Save()
    {
        if (Path is { } existing && File.Exists(existing))
            return existing;
        var folder = ScreenshotWatcher.Folder;
        Directory.CreateDirectory(folder);
        var path = System.IO.Path.Combine(folder, $"Screenshot {DateTime.Now:yyyy-MM-dd HHmmss}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(Image));
        using (var file = File.Create(path))
            encoder.Save(file);
        Path = path;
        return path;
    }
}

/// <summary>
/// Screenshot appena fatti (docs/10-live-activities.md): Win+Shift+S e Stamp mettono la cattura negli appunti
/// tramite lo Strumento di cattura, Win+Stamp la salva in Immagini\Screenshot. Gli appunti si ascoltano con
/// <c>AddClipboardFormatListener</c> e contano solo se li ha scritti lo Strumento di cattura o Greenshot (non ogni
/// immagine copiata); se nello stesso momento si salva anche il file, la cattura è una sola, con il file.
/// Di Greenshot si osserva anche la cartella di salvataggio (dal suo <c>Greenshot.ini</c>), solo mentre è aperto.
/// </summary>
public sealed class ScreenshotWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    private static readonly TimeSpan FileWait = TimeSpan.FromMilliseconds(1500);

    /// <summary>Appunti e file della stessa cattura arrivano in un ordine qualsiasi, entro qualche secondo.</summary>
    private static readonly TimeSpan SameCapture = TimeSpan.FromSeconds(5);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DispatcherTimer _pending = new() { Interval = FileWait };
    private HwndSource? _window;
    private FileSystemWatcher? _folder;
    private FileSystemWatcher? _greenshotFolder;
    private BitmapSource? _clipboardImage;
    private ScreenshotCapture? _last;
    private DateTime _lastAt;

    public ScreenshotWatcher()
    {
        _pending.Tick += (_, _) =>
        {
            _pending.Stop();
            if (_clipboardImage is { } image)
                Raise(new ScreenshotCapture(image, null));
            _clipboardImage = null;
        };
    }

    public static string Folder =>
        ShellNative.ScreenshotsFolder
        ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");

    /// <summary>Cartella in cui salva Greenshot; null se Greenshot non è installato.</summary>
    public static string? GreenshotFolder
    {
        get
        {
            var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            foreach (var ini in GreenshotIniCandidates())
            {
                try
                {
                    if (File.Exists(ini))
                        return ScreenshotSources.GreenshotFolder(File.ReadAllText(ini), desktop, Environment.ExpandEnvironmentVariables);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                    // impostazioni di Greenshot non leggibili: si prova la prossima
                }
            }
            return null;
        }
    }

    public event Action<ScreenshotCapture>? Captured;

    public void Start()
    {
        if (_window is not null)
            return;
        _window = new HwndSource(new HwndSourceParameters("NothingShell.Screenshots")
        {
            ParentWindow = -3, // HWND_MESSAGE: solo messaggi, nessuna finestra visibile
            WindowStyle = 0,
        });
        _window.AddHook(OnMessage);
        AddClipboardFormatListener(_window.Handle);

        if (Directory.Exists(Folder))
        {
            _folder = new FileSystemWatcher(Folder) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            _folder.Created += (_, e) => _dispatcher.BeginInvoke(() => _ = OnFileAsync(e.FullPath));
            _folder.EnableRaisingEvents = true;
        }
        if (GreenshotFolder is { } greenshot && Directory.Exists(greenshot)
            && !Path.GetFullPath(greenshot).TrimEnd('\\').Equals(Path.GetFullPath(Folder).TrimEnd('\\'),StringComparison.OrdinalIgnoreCase))
        {
            _greenshotFolder = new FileSystemWatcher(greenshot) { NotifyFilter = NotifyFilters.FileName, IncludeSubdirectories = false };
            _greenshotFolder.Created += (_, e) => _dispatcher.BeginInvoke(() =>
            {
                if (GreenshotRunning()) // la cartella è spesso il Desktop: contano solo i file di Greenshot aperto
                    _ = OnFileAsync(e.FullPath);
            });
            _greenshotFolder.EnableRaisingEvents = true;
        }
    }

    public void Stop()
    {
        if (_window is not null)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.Dispose();
            _window = null;
        }
        _folder?.Dispose();
        _folder = null;
        _greenshotFolder?.Dispose();
        _greenshotFolder = null;
        _pending.Stop();
        _clipboardImage = null;
    }

    public void Dispose() => Stop();

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate && FromCaptureTool() && ReadClipboardImage() is { } image)
        {
            if (_last is { Path: not null } && DateTime.Now - _lastAt < SameCapture)
                return 0; // Greenshot salva il file e poi copia negli appunti: la cattura è già mostrata
            _clipboardImage = image;
            _pending.Stop();
            _pending.Start(); // si aspetta un attimo il file salvato da Windows
        }
        return 0;
    }

    private async Task OnFileAsync(string path)
    {
        if (!ScreenshotSources.IsImage(path))
            return;
        if (_last?.Path is { } own && own.Equals(path, StringComparison.OrdinalIgnoreCase))
            return; // salvata da noi con il pulsante della scheda
        if (_last is { Path: null } recent && DateTime.Now - _lastAt < SameCapture)
        {
            recent.Path = path; // il file è arrivato dopo gli appunti: la cattura già mostrata ora ce l'ha
            return;
        }
        var image = await LoadAsync(path);
        if (image is null)
            return;
        _pending.Stop();
        _clipboardImage = null;
        Raise(new ScreenshotCapture(image, path));
    }

    private void Raise(ScreenshotCapture capture)
    {
        _last = capture;
        _lastAt = DateTime.Now;
        Captured?.Invoke(capture);
    }

    /// <summary>Il file appena creato può essere ancora in scrittura: qualche tentativo.</summary>
    private static async Task<BitmapSource?> LoadAsync(string path)
    {
        for (var attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(250);
            try
            {
                var bytes = await File.ReadAllBytesAsync(path);
                return Decode(new MemoryStream(bytes));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
            {
                // riprova
            }
        }
        return null;
    }

    private static BitmapFrame Decode(Stream stream)
    {
        var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);
        var frame = decoder.Frames[0];
        frame.Freeze();
        return frame;
    }

    /// <summary>Il formato PNG (con l'alfa giusto) se c'è, altrimenti la bitmap degli appunti.</summary>
    private static BitmapSource? ReadClipboardImage()
    {
        try
        {
            if (Clipboard.GetData("PNG") is MemoryStream png)
                return Decode(png);
            if (Clipboard.GetImage() is { } image)
            {
                image.Freeze();
                return image;
            }
        }
        catch (Exception ex) when (ex is COMException or ExternalException or NotSupportedException or FileFormatException)
        {
            // appunti occupati o formato non leggibile
        }
        return null;
    }

    private static bool FromCaptureTool()
    {
        var owner = GetClipboardOwner();
        if (owner == 0 || GetWindowThreadProcessId(owner, out var pid) == 0)
            return false;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            return ScreenshotSources.ClipboardTools.Any(t => process.ProcessName.Equals(t, StringComparison.OrdinalIgnoreCase));
        }
        catch (ArgumentException)
        {
            return false; // processo già chiuso
        }
    }

    private static bool GreenshotRunning()
    {
        var processes = Process.GetProcessesByName(ScreenshotSources.GreenshotProcess);
        foreach (var process in processes)
            process.Dispose();
        return processes.Length > 0;
    }

    /// <summary>Installato: in %APPDATA%\Greenshot; portatile: accanto all'eseguibile in esecuzione.</summary>
    private static IEnumerable<string> GreenshotIniCandidates()
    {
        yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Greenshot", "Greenshot.ini");
        foreach (var process in Process.GetProcessesByName(ScreenshotSources.GreenshotProcess))
        {
            string? folder = null;
            try
            {
                folder = Path.GetDirectoryName(process.MainModule?.FileName);
            }
            catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // processo di un altro utente o già chiuso
            }
            finally
            {
                process.Dispose();
            }
            if (folder is not null)
                yield return Path.Combine(folder, "Greenshot.ini");
        }
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern nint GetClipboardOwner();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(nint hwnd, out uint processId);
}
