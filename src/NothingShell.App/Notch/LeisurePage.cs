using System.Windows;
using System.Windows.Input;
using NothingShell.App.Leisure;
using NothingShell.Core.Leisure;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Svago (docs/25-svago.md): むちねこ a tutta pagina. In corsa il clic salta subito (tenuto, salta più in alto); da
/// fermi (titolo, pausa, fine) il clic parte al rilascio, così trascinare cambia ancora pagina. Con la tastiera: Spazio, ↑, W
/// o Invio saltano, P mette in pausa, Esc mette in pausa e ridà la tastiera alla finestra di prima.
/// </summary>
public sealed class LeisurePage : NotchPageView
{
    private readonly RooftopView _game = new() { Margin = new Thickness(0, 0, 0, 2) };
    private bool _pressedHere;

    public LeisurePage()
    {
        Children.Add(_game);
        _game.MouseLeftButtonDown += OnDown;
        _game.AddHandler(MouseLeftButtonUpEvent, new MouseButtonEventHandler(OnUp), handledEventsToo: true);
        _game.KeyDown += OnKeyDown;
        _game.KeyUp += OnKeyUp;
        _game.Happened += OnHappened;
    }

    public override NotchPage Page => NotchPage.Leisure;

    /// <summary>La partita parte o riprende: il notch resta aperto e prende la tastiera.</summary>
    public event Action? PlayRequested;

    /// <summary>Esc: basta giocare, la tastiera torna dov'era.</summary>
    public event Action? LeaveRequested;

    /// <summary>Partita finita: punti e tappa.</summary>
    public event Action<int, int>? Finished;

    public bool IsRunning => _game.Run.Phase is RunPhase.Running or RunPhase.Lost;

    public void SetBest(int best, int stage)
    {
        _game.Best = best;
        _game.BestStage = stage;
        _game.InvalidateVisual();
    }

    public void FocusGame() => Keyboard.Focus(_game);

    /// <summary>Un clic altrove, un'altra pagina, il notch che si chiude: si mette in pausa.</summary>
    public void Interrupt() => _game.Run.Pause();

    /// <summary>Ferma la pagina su una partita preparata (per <c>--svago-sheet</c>).</summary>
    internal void ShowForSnapshot(RooftopRun run, bool newRecord)
    {
        _game.Use(run);
        _game.NewRecord = newRecord;
        _game.Density = 2; // le istantanee si salvano a 2×
    }

    public override void OnShown() => _game.Start();

    public override void OnHidden()
    {
        Interrupt();
        _game.Stop();
    }

    private void Press()
    {
        var was = _game.Run.Phase;
        if (was == RunPhase.Over)
            _game.NewRecord = false;
        _game.Run.Press();
        if (was != RunPhase.Running && _game.Run.Phase == RunPhase.Running)
            PlayRequested?.Invoke();
    }

    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        _game.CaptureMouse();
        _pressedHere = _game.Run.Phase != RunPhase.Running; // in corsa il clic è già servito qui: al rilascio non riparte
        if (_pressedHere)
            return;
        e.Handled = true;
        if (_game.IsPauseHit(e.GetPosition(_game)))
            _game.Run.Pause();
        else
            _game.Run.Press();
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _game.ReleaseMouseCapture();
        var pressed = _pressedHere;
        _pressedHere = false;
        if (_game.Run.Phase == RunPhase.Running)
        {
            _game.Run.Release();
            return;
        }
        if (pressed && !e.Handled) // gestito dalla pillola: era uno swipe
            Press();
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Space or Key.Up or Key.W or Key.Enter:
                if (!e.IsRepeat)
                    Press();
                break;
            case Key.P:
                if (_game.Run.Phase == RunPhase.Running)
                    _game.Run.Pause();
                else if (_game.Run.Phase == RunPhase.Paused)
                    Press();
                break;
            case Key.Escape:
                Interrupt();
                LeaveRequested?.Invoke();
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnKeyUp(object sender, KeyEventArgs e)
    {
        if (e.Key is Key.Space or Key.Up or Key.W or Key.Enter)
        {
            _game.Run.Release();
            e.Handled = true;
        }
    }

    private void OnHappened(RunEvent happened)
    {
        if (happened != RunEvent.Over)
            return;
        var run = _game.Run;
        _game.NewRecord = run.Score > _game.Best;
        Finished?.Invoke(run.Score, run.Stage);
    }
}
