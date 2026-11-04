using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Animation;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// La mascotte del notch (docs/20-stile-nusa.md, docs/21-mascotte.md): due occhi tondi che sbattono le palpebre, seguono il mouse,
/// sorridono quando ci si passa sopra, reagiscono ad avvisi e lavori finiti, fanno gesti loro, rispondono ai gesti col mouse e si
/// addormentano. <see cref="MascotBrain"/> decide le pose e <see cref="MascotPlay"/> riconosce i gesti, qui ogni grandezza ha la sua
/// molla. Si ridisegna solo mentre qualcosa si muove: fermi, gli occhi non costano nulla.
/// </summary>
internal sealed class MascotView : Canvas
{
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromMilliseconds(60);
    private static readonly TimeSpan PlayInterval = TimeSpan.FromMilliseconds(30); // il mouse vicino: un solletico è svelto

    private readonly MascotBrain _brain = new();
    private readonly MascotPlay _play = new(NotchLayout.For(NotchState.Idle).Width / 2, NotchLayout.For(NotchState.Idle).Height / 2); // gli occhi stanno al centro della pillola
    private readonly Border _left = Eye();
    private readonly Border _right = Eye();
    private readonly Spring _open = new(1, 1100, 52);
    private readonly Spring _lookX = new(0, 140, 19);
    private readonly Spring _lookY = new(0, 140, 19);
    private readonly Spring _smile = new(0, 220, 26);
    private readonly Spring _lift = new(0, 520, 20);
    private readonly Spring _wink = new(0, 1100, 52);
    private readonly Spring[] _springs;
    private readonly DispatcherTimer _think = new() { Interval = ThinkInterval };
    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();
    private readonly double _scale;
    private Point? _lastCursor;
    private TimeSpan _cursorMoved;
    private TimeSpan _lastFrame;
    private bool _rendering;
    private bool _active;

    public MascotView(double scale)
    {
        _scale = scale;
        _springs = [_open, _lookX, _lookY, _smile, _lift, _wink];
        Width = 44 * scale;
        Height = 18 * scale;
        IsHitTestVisible = false;
        Children.Add(_left);
        Children.Add(_right);
        _think.Tick += (_, _) => Think();
        Render();
    }

    /// <summary>Il mouse sopra il notch che sta per aprirsi: gli occhi si strizzano in un sorriso.</summary>
    public bool Greeting { get; set; }

    /// <summary>Se fa gesti suoi col mouse fermo, e quanto spesso: con gli effetti di animazione di Windows spenti, mai.</summary>
    public MascotInitiative Initiative { get; set; }

    /// <summary>Se risponde ai gesti col mouse vicino al notch (docs/21-mascotte.md, Giochi col mouse).</summary>
    public bool Playful { get; set; }

    /// <summary>Batteria quasi scarica: occhi a mezz'asta.</summary>
    public bool Tired
    {
        get => _brain.Tired;
        set => _brain.Tired = value;
    }

    /// <summary>Dove guarda sempre, invece del mouse: la mascotte che parla nell'avviso guarda il suo testo.</summary>
    public (double X, double Y)? Gaze { get; init; }

    /// <summary>Reagisce a ciò che succede; se è nascosta, la reazione si vede appena torna (finché dura).</summary>
    public void React(MascotMood mood)
    {
        _brain.React(mood, _clock.Elapsed);
        if (_active)
            Think();
    }

    /// <summary>Visibile o no: nascosta non pensa e non anima.</summary>
    public void SetActive(bool active)
    {
        if (active == _active)
            return;
        _active = active;
        if (active)
        {
            Think();
            _think.Start();
        }
        else
        {
            _think.Stop();
        }
    }

    /// <summary>Ferma gli occhi su <paramref name="pose"/>, senza molle (per <c>--mascot-sheet</c>).</summary>
    public void Freeze(MascotPose pose)
    {
        Aim(pose);
        foreach (var spring in _springs)
            spring.SnapTo(spring.Target);
        Render();
    }

    private static Border Eye() => new() { Background = Theme.InkBrush };

    private void Aim(MascotPose pose)
    {
        _open.Target = pose.Open;
        _lookX.Target = pose.LookX;
        _lookY.Target = pose.LookY;
        _smile.Target = pose.Smile;
        _lift.Target = pose.Lift;
        _wink.Target = pose.Wink;
    }

    private void Think()
    {
        var now = _clock.Elapsed;
        var cursor = Mouse();
        var near = false;
        if (Playful && Gaze is null && cursor is { } at)
        {
            if (_play.Observe(now, at.X, at.Y) is { } answer)
                _brain.React(answer, now); // ha riconosciuto un gesto: risponde
            near = _play.Near;
        }
        var interval = near ? PlayInterval : ThinkInterval;
        if (_think.Interval != interval)
            _think.Interval = interval;
        _brain.Initiative = Motion.Enabled ? Initiative : MascotInitiative.Off; // a scatti, i gesti sarebbero solo un fastidio
        Aim(_brain.Pose(now, Gaze ?? Follow(now, cursor), NativeMethods.IdleTime(), Greeting));
        if (!Motion.Enabled)
        {
            foreach (var spring in _springs)
                spring.SnapTo(spring.Target);
            Render();
            return;
        }
        if (!_rendering && !_springs.All(s => s.IsSettled))
        {
            _rendering = true;
            _lastFrame = now;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Il mouse in DIP rispetto al centro degli occhi, y verso il basso; null se non si sa o la mascotte non è sullo schermo.</summary>
    private Point? Mouse()
    {
        if (WindowNative.CursorPosition() is not { } screen || PresentationSource.FromVisual(this) is null)
            return null;
        var local = PointFromScreen(screen);
        return new Point(local.X - (Width / 2), local.Y - (Height / 2));
    }

    /// <summary>Il mouse rispetto agli occhi, in −1..1; null se è fermo da un po': allora la mascotte si guarda intorno.</summary>
    private (double X, double Y)? Follow(TimeSpan now, Point? cursor)
    {
        if (cursor is not { } at)
            return null;
        if (_lastCursor is not { } last || (last - at).Length > 2)
        {
            _lastCursor = at;
            _cursorMoved = now;
        }
        return now - _cursorMoved > MascotBrain.CursorRest ? null : (at.X / 500, at.Y / 300);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _lastFrame).TotalSeconds * Motion.Speed;
        _lastFrame = now;
        var moving = false;
        foreach (var spring in _springs)
            moving |= spring.Step(dt);
        Render();
        if (moving)
            return;
        CompositionTarget.Rendering -= OnFrame;
        _rendering = false;
    }

    private void Render()
    {
        var s = _scale;
        var smile = Math.Clamp(_smile.Value, 0, 1);
        var wink = Math.Clamp(_wink.Value, -1, 1);
        var width = 8 * s * (1 + (0.15 * smile));
        var height = 10 * s * Math.Max(_open.Value, 0) * (1 - (0.5 * smile));
        var x = _lookX.Value * 4 * s;
        var y = (_lookY.Value * 2 * s) - (smile * 1.5 * s) + (_lift.Value * 3 * s);
        var gap = 20 * s;
        // l'occhiolino chiude un occhio solo: verso −1 il sinistro, verso 1 il destro
        Place(_left, (Width / 2) - (gap / 2) + x, (Height / 2) + y, width, Math.Max(1.6 * s, height * (1 - Math.Max(0, -wink))));
        Place(_right, (Width / 2) + (gap / 2) + x, (Height / 2) + y, width, Math.Max(1.6 * s, height * (1 - Math.Max(0, wink))));
    }

    private static void Place(Border eye, double centerX, double centerY, double width, double height)
    {
        eye.Width = width;
        eye.Height = height;
        eye.CornerRadius = new CornerRadius(Math.Min(width, height) / 2);
        SetLeft(eye, centerX - (width / 2));
        SetTop(eye, centerY - (height / 2));
    }
}
