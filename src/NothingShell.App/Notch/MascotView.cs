using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.Animation;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// La mascotte del notch a riposo (docs/20-stile-nusa.md): due occhi tondi che sbattono le palpebre, seguono il mouse,
/// sorridono quando ci si passa sopra, reagiscono ad avvisi e lavori finiti e si addormentano. <see cref="MascotBrain"/> decide le pose, qui ogni grandezza ha la sua
/// molla. Si ridisegna solo mentre qualcosa si muove: fermi, gli occhi non costano nulla.
/// </summary>
internal sealed class MascotView : Canvas
{
    private static readonly TimeSpan ThinkInterval = TimeSpan.FromMilliseconds(60);

    private readonly MascotBrain _brain = new();
    private readonly Border _left = Eye();
    private readonly Border _right = Eye();
    private readonly Spring _open = new(1, 1100, 52);
    private readonly Spring _lookX = new(0, 140, 19);
    private readonly Spring _lookY = new(0, 140, 19);
    private readonly Spring _smile = new(0, 220, 26);
    private readonly Spring _lift = new(0, 520, 20);
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

    private static Border Eye() => new() { Background = Theme.InkBrush };

    private void Think()
    {
        var now = _clock.Elapsed;
        var pose = _brain.Pose(now, Pointer(now), NativeMethods.IdleTime(), Greeting);
        _open.Target = pose.Open;
        _lookX.Target = pose.LookX;
        _lookY.Target = pose.LookY;
        _smile.Target = pose.Smile;
        _lift.Target = pose.Lift;
        if (!Motion.Enabled)
        {
            foreach (var spring in new[] { _open, _lookX, _lookY, _smile, _lift })
                spring.SnapTo(spring.Target);
            Render();
            return;
        }
        if (!_rendering && !(_open.IsSettled && _lookX.IsSettled && _lookY.IsSettled && _smile.IsSettled && _lift.IsSettled))
        {
            _rendering = true;
            _lastFrame = now;
            CompositionTarget.Rendering += OnFrame;
        }
    }

    /// <summary>Il mouse rispetto agli occhi, in −1..1; null se è fermo da un po': allora la mascotte si guarda intorno.</summary>
    private (double X, double Y)? Pointer(TimeSpan now)
    {
        if (WindowNative.CursorPosition() is not { } screen || PresentationSource.FromVisual(this) is null)
            return null;
        if (_lastCursor is not { } last || (last - screen).Length > 2)
        {
            _lastCursor = screen;
            _cursorMoved = now;
        }
        if (now - _cursorMoved > MascotBrain.CursorRest)
            return null;
        var local = PointFromScreen(screen);
        return ((local.X - (Width / 2)) / 500, (local.Y - (Height / 2)) / 300);
    }

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        var dt = (now - _lastFrame).TotalSeconds * Motion.Speed;
        _lastFrame = now;
        var moving = _open.Step(dt) | _lookX.Step(dt) | _lookY.Step(dt) | _smile.Step(dt) | _lift.Step(dt);
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
        var width = 8 * s * (1 + (0.15 * smile));
        var height = Math.Max(1.6 * s, 10 * s * Math.Max(_open.Value, 0) * (1 - (0.5 * smile)));
        var x = _lookX.Value * 4 * s;
        var y = (_lookY.Value * 2 * s) - (smile * 1.5 * s) + (_lift.Value * 3 * s);
        var gap = 20 * s;
        Place(_left, (Width / 2) - (gap / 2) + x, (Height / 2) + y, width, height);
        Place(_right, (Width / 2) + (gap / 2) + x, (Height / 2) + y, width, height);
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
