using NothingShell.Core.Media;
using Windows.Media.Control;

namespace NothingShell.App.Services;

/// <summary>
/// Sessione media di sistema tramite GlobalSystemMediaTransportControls (Spotify, browser, lettori).
/// Se l'API non è disponibile resta senza sessione: <see cref="Current"/> è null e non solleva eccezioni.
/// </summary>
public sealed class MediaService : IMediaService, IDisposable
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    public MediaSnapshot? Current { get; private set; }

    public event Action<MediaSnapshot?>? Changed;

    public async Task StartAsync()
    {
        try
        {
            _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
            return;
        }
        _manager.CurrentSessionChanged += OnCurrentSessionChanged;
        Attach(_manager.GetCurrentSession());
    }

    public Task PlayPauseAsync() => _session is { } s ? s.TryTogglePlayPauseAsync().AsTask() : Task.CompletedTask;

    public Task NextAsync() => _session is { } s ? s.TrySkipNextAsync().AsTask() : Task.CompletedTask;

    public Task PreviousAsync() => _session is { } s ? s.TrySkipPreviousAsync().AsTask() : Task.CompletedTask;

    public void Dispose()
    {
        if (_manager is not null)
            _manager.CurrentSessionChanged -= OnCurrentSessionChanged;
        Attach(null);
    }

    private void OnCurrentSessionChanged(GlobalSystemMediaTransportControlsSessionManager sender, CurrentSessionChangedEventArgs args) =>
        Attach(sender.GetCurrentSession());

    private void Attach(GlobalSystemMediaTransportControlsSession? session)
    {
        if (_session is not null)
        {
            _session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            _session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
        }
        _session = session;
        if (session is not null)
        {
            session.MediaPropertiesChanged += OnMediaPropertiesChanged;
            session.PlaybackInfoChanged += OnPlaybackInfoChanged;
        }
        _ = RefreshAsync();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        _ = RefreshAsync();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        _ = RefreshAsync();

    private async Task RefreshAsync()
    {
        var session = _session;
        MediaSnapshot? snapshot = null;
        if (session is not null)
        {
            try
            {
                var props = await session.TryGetMediaPropertiesAsync();
                var playing = session.GetPlaybackInfo()?.PlaybackStatus ==
                              GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                snapshot = new MediaSnapshot(
                    props?.Title ?? string.Empty,
                    props?.Artist ?? string.Empty,
                    playing,
                    SourceNames.Friendly(session.SourceAppUserModelId));
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                // la sessione si è chiusa mentre la leggevamo
            }
        }

        if (!ReferenceEquals(session, _session) || snapshot == Current)
            return;
        Current = snapshot;
        Changed?.Invoke(snapshot);
    }
}
