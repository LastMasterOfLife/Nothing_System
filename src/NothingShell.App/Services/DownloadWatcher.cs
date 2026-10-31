using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Notch;

namespace NothingShell.App.Services;

/// <summary>
/// Download in corso nella cartella Download (docs/10-live-activities.md): i file temporanei dei browser
/// compaiono, crescono e vengono rinominati nel file vero. Quelli da Collegamento al telefono crescono col nome
/// definitivo nella sottocartella Mobile Devices. Il controllo delle dimensioni (ogni secondo) gira solo mentre c'è
/// un download.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DownloadTracker _tracker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _sizes = new() { Interval = TimeSpan.FromSeconds(1) };
    private FileSystemWatcher? _watcher;
    private TimeSpan _lastSizes;
    private string? _folder;

    public DownloadWatcher()
    {
        _sizes.Tick += (_, _) => RefreshSizes();
    }

    public IReadOnlyList<ActiveDownload> Active => _tracker.Active;

    /// <summary>Elenco dei download in corso cambiato (inizio, fine, dimensioni).</summary>
    public event Action<IReadOnlyList<ActiveDownload>>? Changed;

    /// <summary>Download finito: percorso del file vero.</summary>
    public event Action<string>? Completed;

    /// <summary>Un file ha cominciato ad arrivare da Collegamento al telefono: percorso del file.</summary>
    public event Action<string>? PhoneFileStarted;

    public void Start()
    {
        if (_watcher is not null || ShellNative.DownloadsFolder is not { } folder || !Directory.Exists(folder))
            return;
        _folder = folder;
        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.Size,
            IncludeSubdirectories = true, // la cartella del telefono può nascere dopo l'avvio
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Post(() =>
        {
            var started = DownloadTracker.IsPhoneFile(e.FullPath)
                ? TrackPhoneFile(e.FullPath)
                : InMainFolder(e.FullPath) && _tracker.OnCreated(e.FullPath, _clock.Elapsed);
            if (started)
                Publish();
        });
        // Un file del telefono che cresce ma non è in elenco: la shell è partita a metà, o il trasferimento è ripartito.
        _watcher.Changed += (_, e) =>
        {
            if (DownloadTracker.IsPhoneFile(e.FullPath))
                Post(() =>
                {
                    if (TrackPhoneFile(e.FullPath))
                        Publish();
                });
        };
        _watcher.Renamed += (_, e) => Post(() =>
        {
            if (!InMainFolder(e.FullPath))
                return;
            var completed = _tracker.OnRenamed(e.OldFullPath, e.FullPath, _clock.Elapsed);
            Publish();
            if (completed is not null)
                Completed?.Invoke(completed);
        });
        _watcher.Deleted += (_, e) => Post(() =>
        {
            if (_tracker.OnDeleted(e.FullPath))
                Publish();
        });
        _watcher.EnableRaisingEvents = true;
    }

    private bool TrackPhoneFile(string path)
    {
        if (!File.Exists(path) || !_tracker.OnPhoneFile(path, SizeOf(path), _clock.Elapsed))
            return false;
        PhoneFileStarted?.Invoke(path);
        return true;
    }

    private bool InMainFolder(string path) => string.Equals(Path.GetDirectoryName(path), _folder, StringComparison.OrdinalIgnoreCase);

    private static long SizeOf(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return 0;
        }
    }

    public void Stop()
    {
        _watcher?.Dispose();
        _watcher = null;
        _sizes.Stop();
        foreach (var download in _tracker.Active)
            _tracker.OnDeleted(download.PartialPath);
        Publish();
    }

    public void Dispose() => Stop();

    private void Post(Action action) => _dispatcher.BeginInvoke(action);

    private void RefreshSizes()
    {
        var now = _clock.Elapsed;
        var elapsed = now - _lastSizes;
        _lastSizes = now;
        foreach (var download in _tracker.Active)
        {
            try
            {
                _tracker.OnSize(download.PartialPath, new FileInfo(download.PartialPath).Length, now, elapsed);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // il browser l'ha appena rinominato: arriverà l'evento
            }
        }
        _tracker.Prune(now);
        var arrived = _tracker.TakeSettledPhoneFiles(now);
        Publish();
        foreach (var path in arrived)
            Completed?.Invoke(path);
    }

    private void Publish()
    {
        var active = _tracker.Active;
        if (active.Count > 0 && !_sizes.IsEnabled)
        {
            _lastSizes = _clock.Elapsed;
            _sizes.Start();
        }
        else if (active.Count == 0)
            _sizes.Stop();
        Changed?.Invoke(active);
    }
}
