using System.Diagnostics;
using System.IO;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Notch;

namespace NothingShell.App.Services;

/// <summary>
/// Download in corso nella cartella Download (docs/10-live-activities.md): i file temporanei dei browser
/// compaiono, crescono e vengono rinominati nel file vero. Il controllo delle dimensioni (ogni secondo)
/// gira solo mentre c'è un download.
/// </summary>
public sealed class DownloadWatcher : IDisposable
{
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly DownloadTracker _tracker = new();
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly DispatcherTimer _sizes = new() { Interval = TimeSpan.FromSeconds(1) };
    private FileSystemWatcher? _watcher;
    private TimeSpan _lastSizes;

    public DownloadWatcher()
    {
        _sizes.Tick += (_, _) => RefreshSizes();
    }

    public IReadOnlyList<ActiveDownload> Active => _tracker.Active;

    /// <summary>Elenco dei download in corso cambiato (inizio, fine, dimensioni).</summary>
    public event Action<IReadOnlyList<ActiveDownload>>? Changed;

    /// <summary>Download finito: percorso del file vero.</summary>
    public event Action<string>? Completed;

    public void Start()
    {
        if (_watcher is not null || ShellNative.DownloadsFolder is not { } folder || !Directory.Exists(folder))
            return;
        _watcher = new FileSystemWatcher(folder)
        {
            NotifyFilter = NotifyFilters.FileName,
            IncludeSubdirectories = false,
            InternalBufferSize = 64 * 1024,
        };
        _watcher.Created += (_, e) => Post(() =>
        {
            if (_tracker.OnCreated(e.FullPath, _clock.Elapsed))
                Publish();
        });
        _watcher.Renamed += (_, e) => Post(() =>
        {
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
        Publish();
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
