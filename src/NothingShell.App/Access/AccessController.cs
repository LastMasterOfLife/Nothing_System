using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Win32;
using NothingShell.App.Launcher;
using NothingShell.Core.Access;

namespace NothingShell.App.Access;

/// <summary>
/// Collega l'overlay di accesso agli eventi di sessione di Windows (docs/09-access.md).
/// Il blocco vero resta di Windows: qui si anima il prima e il dopo.
/// </summary>
public sealed class AccessController : IDisposable
{
    /// <summary>Senza evento di blocco visto, il controllo del desktop aspetta: LockWorkStation non è istantaneo.</summary>
    private static readonly TimeSpan WatchdogGrace = TimeSpan.FromSeconds(5);

    private readonly AccessFlow _flow = new();
    private readonly AccessWindow _window = new();
    private readonly List<AccessCover> _covers = [];
    private readonly DispatcherTimer _watchdog = new() { Interval = TimeSpan.FromSeconds(1) };
    private bool _rendering;
    private bool _sessionLockSeen;
    private bool _enabled = true;

    public AccessController()
    {
        _window.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true;
            Execute(_flow.Skip(DateTimeOffset.Now));
        };
        _watchdog.Tick += (_, _) => CheckDesktop();
        SystemEvents.SessionSwitch += OnSessionSwitch;
    }

    /// <summary>Spento: Blocca chiama subito Windows e l'overlay non compare.</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            _enabled = value;
            if (!value && _flow.State != AccessState.Hidden)
            {
                _flow.OnLockFailed();
                HideOverlay();
            }
        }
    }

    public async Task StartAsync() => _window.SetProfile(await UserProfile.LoadAsync());

    /// <summary>Blocca il PC: animazione di chiusura e poi la schermata di Windows.</summary>
    public void Lock()
    {
        if (!_enabled)
        {
            PowerActions.Lock();
            return;
        }
        Execute(_flow.RequestLock(DateTimeOffset.Now));
    }

    /// <summary>Solo prova grafica: fermo bloccato per un attimo, poi il bentornato, senza bloccare nulla.</summary>
    public void PlayWelcomeDemo()
    {
        Execute(_flow.OnSessionLocked(DateTimeOffset.Now));
        var delay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        delay.Tick += (_, _) =>
        {
            delay.Stop();
            Execute(_flow.OnSessionUnlocked(DateTimeOffset.Now));
        };
        delay.Start();
    }

    /// <summary>Fotogrammi disegnati dall'ultima sequenza animata (diagnostica della fluidità).</summary>
    public int FramesRendered { get; private set; }

    public event Action? Hidden;

    public void Dispose()
    {
        SystemEvents.SessionSwitch -= OnSessionSwitch;
        StopRendering();
        _watchdog.Stop();
        _window.Close();
        foreach (var cover in _covers)
            cover.Close();
    }

    private void OnSessionSwitch(object? sender, SessionSwitchEventArgs e) =>
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var now = DateTimeOffset.Now;
            switch (e.Reason)
            {
                case SessionSwitchReason.SessionLock when _enabled:
                    _sessionLockSeen = true;
                    Execute(_flow.OnSessionLocked(now));
                    break;
                case SessionSwitchReason.SessionUnlock:
                    Execute(_flow.OnSessionUnlocked(now));
                    break;
            }
        });

    /// <summary>Rete di sicurezza: se l'evento di sblocco si perde, l'overlay non resta sopra il desktop.</summary>
    private void CheckDesktop()
    {
        var now = DateTimeOffset.Now;
        Render(now);
        if (_flow.State == AccessState.Locked
            && (_sessionLockSeen || _flow.Elapsed(now) > WatchdogGrace)
            && AccessNative.IsUserDesktopActive())
            Execute(_flow.OnSessionUnlocked(now));
    }

    private void Execute(AccessCommand command)
    {
        switch (command)
        {
            case AccessCommand.Show:
                ShowOverlay();
                break;
            case AccessCommand.LockWorkstation:
                if (!PowerActions.Lock())
                    Execute(_flow.OnLockFailed());
                break;
            case AccessCommand.Hide:
                HideOverlay();
                return;
        }

        if (_flow.State == AccessState.Hidden)
            return;
        Render(DateTimeOffset.Now);
        if (_flow.IsAnimating)
            StartRendering();
        else
            StopRendering();
        if (_flow.State == AccessState.Locked)
            _watchdog.Start();
        else
            _watchdog.Stop();
    }

    private void ShowOverlay()
    {
        RefreshCovers();
        Render(DateTimeOffset.Now);
        if (!_window.IsVisible)
            _window.Show();
        _window.BringToTop();
        Mouse.OverrideCursor = null;
    }

    private void HideOverlay()
    {
        StopRendering();
        _watchdog.Stop();
        _sessionLockSeen = false;
        _window.Hide();
        foreach (var cover in _covers)
            cover.Hide();
        Hidden?.Invoke();
    }

    /// <summary>I monitor possono cambiare fra un blocco e l'altro: i pannelli si ricreano a ogni comparsa.</summary>
    private void RefreshCovers()
    {
        foreach (var cover in _covers)
            cover.Close();
        _covers.Clear();
        foreach (var bounds in AccessNative.SecondaryMonitors())
            _covers.Add(new AccessCover(bounds));
    }

    private void Render(DateTimeOffset now)
    {
        if (_flow.State == AccessState.Hidden)
            return;
        var frame = _flow.Frame(now);
        _window.Render(frame, now.LocalDateTime);
        foreach (var cover in _covers)
        {
            if (frame.CoversVisible && !cover.IsVisible)
            {
                cover.Show();
                cover.BringToTop();
            }
            else if (!frame.CoversVisible && cover.IsVisible)
            {
                cover.Hide();
            }
        }
    }

    private void StartRendering()
    {
        if (_rendering)
            return;
        _rendering = true;
        FramesRendered = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private void StopRendering()
    {
        if (!_rendering)
            return;
        _rendering = false;
        CompositionTarget.Rendering -= OnFrame;
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = DateTimeOffset.Now;
        FramesRendered++;
        var command = _flow.OnTick(now);
        if (command != AccessCommand.None)
            Execute(command);
        else
            Render(now);
    }
}
