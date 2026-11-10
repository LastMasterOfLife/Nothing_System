using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using NothingShell.Core.Leisure;

namespace NothingShell.App.Leisure;

/// <summary>
/// Lo schermo di むちねこ (docs/25-svago.md): cielo, due strati di palazzi che scorrono più piano (parallasse), i tetti dove si
/// corre, il cibo, il gatto e sopra l'HUD del concept (PESO a sinistra, crocchette e pausa a destra). Il mondo è alto
/// <see cref="RooftopRun.ViewHeight"/> unità e si scala sull'altezza: più largo, si vede più città davanti. Mentre è
/// visibile ridisegna a ogni fotogramma; in attesa (titolo, pausa, fine) a 24 al secondo. La shell disegna in software: palazzi
/// e tetti, fatti di centinaia di finestre, diventano immagini alla densità dello schermo la prima volta che compaiono, e scorrono
/// allineati ai pixel.
/// </summary>
internal sealed class RooftopView : FrameworkElement
{
    private const double Corner = 12;
    private const double IdleFrame = 1.0 / 24;
    private const double PopLength = 0.6;

    private readonly Skyline _far = new(11, far: true, parallax: 0.15);
    private readonly Skyline _mid = new(23, far: false, parallax: 0.4);
    private readonly Dictionary<Roof, Drawing> _roofs = [];
    private readonly ConditionalWeakTable<Drawing, Sprite> _sprites = [];
    private readonly Dictionary<string, Drawing> _panels = [];
    private readonly Stopwatch _clock = new();
    private TimeSpan _last;
    private TimeSpan _lastDrawn;
    private bool _looping;

    public RooftopView()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.NearestNeighbor); // le immagini sono già allineate ai pixel
    }

    public RooftopRun Run { get; private set; } = new(Environment.TickCount & 0xFFFF);

    /// <summary>Il record prima di questa partita: alla fine si vede se è stato battuto.</summary>
    public int Best { get; set; }

    public int BestStage { get; set; }

    /// <summary>La partita appena finita ha battuto il record.</summary>
    public bool NewRecord { get; set; }

    /// <summary>Pixel per DIP al posto di quelli dello schermo (le istantanee si fanno a 2×).</summary>
    public double? Density { get; set; }

    /// <summary>Gli eventi della partita, dopo ogni fotogramma.</summary>
    public event Action<RunEvent>? Happened;

    /// <summary>Un'altra partita (per le istantanee: ferma in un momento scelto).</summary>
    public void Use(RooftopRun run)
    {
        Run = run ?? throw new ArgumentNullException(nameof(run));
        _roofs.Clear();
        InvalidateVisual();
    }

    /// <summary>Il pulsante di pausa in alto a destra, in DIP.</summary>
    public bool IsPauseHit(Point point)
    {
        var scale = Scale;
        if (scale <= 0)
            return false;
        var right = ActualWidth / scale;
        return point.X / scale > right - 24 && point.Y / scale < 26;
    }

    public void Start()
    {
        if (_looping)
            return;
        _looping = true;
        _clock.Restart();
        _last = _lastDrawn = TimeSpan.Zero;
        CompositionTarget.Rendering += OnFrame;
        InvalidateVisual();
    }

    public void Stop()
    {
        if (!_looping)
            return;
        _looping = false;
        CompositionTarget.Rendering -= OnFrame;
        _clock.Stop();
    }

    private double Scale => ActualHeight / RooftopRun.ViewHeight;

    private double PixelsPerDip => VisualTreeHelper.GetDpi(this).PixelsPerDip;

    private void OnFrame(object? sender, EventArgs e)
    {
        var now = _clock.Elapsed;
        Run.Update((now - _last).TotalSeconds);
        _last = now;
        foreach (var happened in Run.TakeEvents())
            Happened?.Invoke(happened);
        var moving = Run.Phase is RunPhase.Running or RunPhase.Lost;
        if (!moving && (now - _lastDrawn).TotalSeconds < IdleFrame)
            return;
        _lastDrawn = now;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var (width, height) = (ActualWidth, ActualHeight);
        if (width <= 0 || height <= 0)
            return;
        var scale = Scale;
        var view = width / scale;
        var run = Run;
        var dpi = PixelsPerDip;
        var density = (Density ?? dpi) * scale; // pixel per unità del mondo

        dc.PushClip(new RectangleGeometry(new Rect(0, 0, width, height), Corner, Corner));
        dc.PushTransform(new ScaleTransform(scale, scale));

        MuchiArt.DrawSky(dc, new Rect(0, 0, view, RooftopRun.ViewHeight), new Point((view * 0.8) - (run.Distance * 0.01 % (view + 60)), 50));
        DrawSkyline(dc, _far, run.Distance, view, dpi, density);
        DrawSkyline(dc, _mid, run.Distance, view, dpi, density);
        DrawRoofs(dc, run, view, density);
        foreach (var goal in run.Goals)
            Place(dc, goal.X - run.Distance, goal.Top, view, () => MuchiArt.DrawGoal(dc, goal.Stage, run.Clock, dpi));
        DrawTreats(dc, run, view);

        var pose = CatPose.Of(run);
        if (run.Phase is not RunPhase.Over)
        {
            var face = run.Phase == RunPhase.Title ? CatFace.Normal : pose.Happy ? CatFace.Happy : CatFace.Normal;
            Place(dc, run.CatX - run.Distance, run.CatY, view, () => MuchiArt.DrawCat(dc, pose, run.Weight, face));
        }
        DrawPops(dc, run, dpi);

        // pannelli e HUD cambiano di rado: come immagini pronte, rifatte solo quando cambia quello che dicono
        var percent = (int)Math.Round(run.Weight * 100);
        switch (run.Phase)
        {
            case RunPhase.Title:
                DrawCached(dc, $"titolo {view:0} {Best} {BestStage}", g => DrawTitle(g, view, dpi), density);
                break;
            case RunPhase.Over:
                DrawCached(dc, $"fine {view:0} {run.Score} {run.Stage} {percent} {NewRecord} {Best}", g => DrawOver(g, view, dpi), density);
                var card = OverCard(view);
                dc.PushTransform(new TranslateTransform(card.X + 42, card.Bottom - 16));
                MuchiArt.DrawCat(dc, pose, run.Weight, NewRecord ? CatFace.Happy : CatFace.Sad);
                dc.Pop();
                break;
            default:
                DrawCached(dc, $"hud {view:0} {percent} {run.Score} {run.Stage}", g => DrawHud(g, run, view, dpi), density);
                DrawGoalBanner(dc, run, view, dpi);
                if (run.Phase == RunPhase.Paused)
                    DrawCached(dc, $"pausa {view:0}", g => DrawPaused(g, view, dpi), density);
                break;
        }

        dc.Pop();
        dc.Pop();
    }

    private static void Place(DrawingContext dc, double x, double y, double view, Action draw)
    {
        if (x < -60 || x > view + 60)
            return;
        dc.PushTransform(new TranslateTransform(x, y));
        draw();
        dc.Pop();
    }

    private void DrawSkyline(DrawingContext dc, Skyline layer, double distance, double view, double dpi, double density)
    {
        var offset = distance * layer.Parallax;
        layer.Forget(offset - 100);
        foreach (var (building, drawing) in layer.Between(offset - 10, offset + view + 10, dpi))
            DrawSprite(dc, drawing, building.X - offset, RooftopRun.ViewHeight, density);
    }

    private void DrawRoofs(DrawingContext dc, RooftopRun run, double view, double density)
    {
        foreach (var gone in _roofs.Keys.Where(r => !run.Roofs.Contains(r)).ToList())
            _roofs.Remove(gone);
        foreach (var roof in run.Roofs)
        {
            if (roof.Right < run.Distance - 20 || roof.Left > run.Distance + view + 20)
                continue;
            if (!_roofs.TryGetValue(roof, out var drawing))
                _roofs[roof] = drawing = MuchiArt.RoofDrawing(roof);
            DrawSprite(dc, drawing, roof.Left - run.Distance, roof.Top, density);
        }
    }

    private sealed record Sprite(BitmapSource Image, Rect Bounds, double Density);

    private void DrawCached(DrawingContext dc, string key, Action<DrawingContext> draw, double density)
    {
        if (!_panels.TryGetValue(key, out var drawing))
        {
            if (_panels.Count > 24)
                _panels.Clear();
            var group = new DrawingGroup();
            using (var g = group.Open())
                draw(g);
            group.Freeze();
            _panels[key] = drawing = group;
        }
        DrawSprite(dc, drawing, 0, 0, density);
    }

    /// <summary>Il disegno come immagine già pronta, con l'angolo in alto a sinistra su un pixel intero.</summary>
    private void DrawSprite(DrawingContext dc, Drawing drawing, double x, double y, double density)
    {
        if (!_sprites.TryGetValue(drawing, out var sprite) || Math.Abs(sprite.Density - density) > 1e-6)
        {
            sprite = Rasterize(drawing, density);
            _sprites.AddOrUpdate(drawing, sprite);
        }
        var left = Math.Round((x + sprite.Bounds.X) * density) / density;
        var top = Math.Round((y + sprite.Bounds.Y) * density) / density;
        dc.DrawImage(sprite.Image, new Rect(left, top, sprite.Image.PixelWidth / density, sprite.Image.PixelHeight / density));
    }

    private static Sprite Rasterize(Drawing drawing, double density)
    {
        var b = drawing.Bounds;
        var bounds = new Rect(Math.Floor(b.X), Math.Floor(b.Y), Math.Ceiling(b.Right) - Math.Floor(b.X), Math.Ceiling(b.Bottom) - Math.Floor(b.Y));
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new MatrixTransform(density, 0, 0, density, -bounds.X * density, -bounds.Y * density));
            dc.DrawDrawing(drawing);
        }
        var image = new RenderTargetBitmap(Math.Max(1, (int)Math.Ceiling(bounds.Width * density)), Math.Max(1, (int)Math.Ceiling(bounds.Height * density)), 96, 96, PixelFormats.Pbgra32);
        image.Render(visual);
        image.Freeze();
        return new Sprite(image, bounds, density);
    }

    /// <summary>Il cibo ancora lì ondeggia piano.</summary>
    private static void DrawTreats(DrawingContext dc, RooftopRun run, double view)
    {
        foreach (var treat in run.Treats)
        {
            var x = treat.X - run.Distance;
            if (x < -20 || x > view + 20 || treat.EatenAt is not null)
                continue;
            var bob = Math.Sin((run.Clock * 4) + (treat.X * 0.1)) * 1.2;
            if (treat.Kind == TreatKind.Can)
                MuchiArt.DrawCan(dc, new Point(x, treat.Y + (bob * 0.5)), run.Clock);
            else
                MuchiArt.DrawKibble(dc, new Point(x, treat.Y + bob));
        }
    }

    /// <summary>Il cibo appena mangiato lascia un +1 (o +5) che sale davanti al gatto e sparisce.</summary>
    private static void DrawPops(DrawingContext dc, RooftopRun run, double dpi)
    {
        foreach (var treat in run.Treats)
        {
            if (treat.EatenAt is not { } eaten || run.Time - eaten > PopLength)
                continue;
            var age = run.Time - eaten;
            var points = treat.Kind == TreatKind.Can ? RooftopRun.CanPoints : RooftopRun.KibblePoints;
            var text = MuchiArt.Text($"+{points}", treat.Kind == TreatKind.Can ? 9 : 7, MuchiArt.CharcoalBrush, dpi, FontWeights.SemiBold);
            dc.PushOpacity(1 - (age / PopLength));
            dc.DrawText(text, new Point(treat.X - run.Distance + 6 - (text.Width / 2), treat.Y - 34 - (age * 24))); // sopra le orecchie
            dc.Pop();
        }
    }

    /// <summary>L'HUD del concept: PESO con la barra e la percentuale, le crocchette mangiate, la pausa, la tappa al centro.</summary>
    private static void DrawHud(DrawingContext dc, RooftopRun run, double view, double dpi)
    {
        dc.DrawRoundedRectangle(HudBrush, null, new Rect(6, 5, 92, 30), 6, 6);
        dc.DrawText(MuchiArt.Text(Spaced("PESO"), 6, MuchiArt.CharcoalBrush, dpi), new Point(11, 8));
        var bar = new Rect(11, 16.5, 72, 5.5);
        dc.DrawRoundedRectangle(MuchiArt.SnowBrush, BarPen, bar, 2.75, 2.75);
        var percent = (int)Math.Round(run.Weight * 100);
        if (percent > 0)
            dc.DrawRoundedRectangle(MuchiArt.InkBrush, null, new Rect(bar.X, bar.Y, Math.Max(bar.Height, bar.Width * percent / 100), bar.Height), 2.75, 2.75);
        dc.DrawText(MuchiArt.Text($"{percent}%", 6.5, MuchiArt.InkBrush, dpi, FontWeights.SemiBold), new Point(11, 24));
        var build = MuchiArt.Text(Spaced(BuildName(RooftopRun.BuildOf(run.Weight))), 5.2, MuchiArt.StoneBrush, dpi);
        dc.DrawText(build, new Point(83 - build.Width, 25));

        var count = MuchiArt.Text($"× {run.Score}", 8, MuchiArt.InkBrush, dpi, FontWeights.SemiBold);
        var countX = view - 28 - count.Width;
        dc.DrawRoundedRectangle(HudBrush, null, new Rect(countX - 20, 5, count.Width + 46, 18), 6, 6);
        MuchiArt.DrawKibble(dc, new Point(countX - 9, 14));
        dc.DrawText(count, new Point(countX, 9));
        dc.DrawRectangle(MuchiArt.InkBrush, null, new Rect(view - 17, 9.5, 2, 9)); // ⏸
        dc.DrawRectangle(MuchiArt.InkBrush, null, new Rect(view - 12.5, 9.5, 2, 9));

        var stage = MuchiArt.Text(Spaced($"TAPPA {run.Stage}"), 5.5, MuchiArt.StoneBrush, dpi);
        dc.DrawText(stage, new Point((view - stage.Width) / 2, 9));
    }

    /// <summary>Passato il traguardo: TAPPA 1 ✓ e il premio al centro, per un secondo e mezzo.</summary>
    private static void DrawGoalBanner(DrawingContext dc, RooftopRun run, double view, double dpi)
    {
        if (run.GoalAt is not { } goal || run.Time - goal >= 1.6)
            return;
        var age = run.Time - goal;
        dc.PushOpacity(age < 1.2 ? 1 : 1 - ((age - 1.2) / 0.4));
        var title = MuchiArt.Text($"TAPPA {run.Stage - 1}  ✓", 14, MuchiArt.InkBrush, dpi, FontWeights.SemiBold);
        var bonus = MuchiArt.Text($"+{RooftopRun.GoalPoints}", 8, MuchiArt.CharcoalBrush, dpi);
        var y = 40 - (Math.Max(0, 0.2 - age) * 40);
        dc.DrawText(title, new Point((view - title.Width) / 2, y));
        dc.DrawText(bonus, new Point((view - bonus.Width) / 2, y + 18));
        dc.Pop();
    }

    private void DrawTitle(DrawingContext dc, double view, double dpi)
    {
        var panel = new Rect(112, 16, Math.Min(view - 124, 230), 92);
        dc.DrawRoundedRectangle(PanelBrush, null, panel, 8, 8);
        var x = panel.X + 12;
        dc.DrawText(MuchiArt.Text("むちねこ", 22, MuchiArt.InkBrush, dpi, FontWeights.Normal, MuchiArt.JapaneseFont), new Point(x, panel.Y + 7));
        dc.DrawText(MuchiArt.Text(Spaced("MUCHINEKO"), 5.5, MuchiArt.CharcoalBrush, dpi), new Point(x + 1, panel.Y + 38));
        dc.DrawText(MuchiArt.Text("食べたい。でも、飛べなくなる。", 6.5, MuchiArt.CharcoalBrush, dpi, FontWeights.Normal, MuchiArt.JapaneseFont), new Point(x, panel.Y + 49));
        dc.DrawRectangle(MuchiArt.InkBrush, null, new Rect(x, panel.Y + 62, 14, 0.8));
        dc.DrawText(MuchiArt.Text("Clic o Spazio per partire", 7, MuchiArt.InkBrush, dpi, FontWeights.SemiBold), new Point(x, panel.Y + 68));
        var record = Best > 0 ? $"Record {Best} · tappa {BestStage}" : "Tieni premuto per saltare più in alto";
        dc.DrawText(MuchiArt.Text(record, 6, MuchiArt.StoneBrush, dpi), new Point(x, panel.Y + 79));
    }

    private static Rect OverCard(double view) => new((view - 230) / 2, 34, 230, 102);

    /// <summary>La scheda di fine partita, senza il gatto (lui si muove e si disegna sopra).</summary>
    private void DrawOver(DrawingContext dc, double view, double dpi)
    {
        dc.DrawRectangle(VeilBrush, null, new Rect(0, 0, view, RooftopRun.ViewHeight));
        var card = OverCard(view);
        dc.DrawRoundedRectangle(MuchiArt.SnowBrush, CardPen, card, 10, 10);

        var x = card.X + 90;
        dc.DrawText(MuchiArt.Text(Spaced(NewRecord ? "NUOVO RECORD" : "CHE VOLO"), 6, MuchiArt.CharcoalBrush, dpi), new Point(x, card.Y + 14));
        var score = MuchiArt.Text(Run.Score.ToString(CultureInfo.InvariantCulture), 26, MuchiArt.InkBrush, dpi, FontWeights.SemiBold);
        dc.DrawText(score, new Point(x, card.Y + 22));
        dc.DrawText(MuchiArt.Text(Run.Score == 1 ? "punto" : "punti", 7, MuchiArt.CharcoalBrush, dpi), new Point(x + score.Width + 4, card.Y + 39));
        dc.DrawText(MuchiArt.Text($"Tappa {Run.Stage} · peso {(int)Math.Round(Run.Weight * 100)}%", 6.5, MuchiArt.StoneBrush, dpi), new Point(x, card.Y + 57));
        if (!NewRecord && Best > 0)
            dc.DrawText(MuchiArt.Text($"Record {Best}", 6.5, MuchiArt.StoneBrush, dpi), new Point(x, card.Y + 67));
        dc.DrawText(MuchiArt.Text("Clic o Spazio per riprovare", 7, MuchiArt.InkBrush, dpi, FontWeights.SemiBold), new Point(x, card.Y + 82));
    }

    private static void DrawPaused(DrawingContext dc, double view, double dpi)
    {
        dc.DrawRectangle(VeilBrush, null, new Rect(0, 0, view, RooftopRun.ViewHeight));
        var title = MuchiArt.Text(Spaced("IN PAUSA"), 12, MuchiArt.InkBrush, dpi, FontWeights.SemiBold);
        dc.DrawText(title, new Point((view - title.Width) / 2, 66));
        var hint = MuchiArt.Text("Clic o Spazio per riprendere · Esc per uscire", 7, MuchiArt.CharcoalBrush, dpi);
        dc.DrawText(hint, new Point((view - hint.Width) / 2, 86));
    }

    private static string BuildName(CatBuild build) => build switch
    {
        CatBuild.Light => "LEGGERO",
        CatBuild.Normal => "NORMALE",
        _ => "PESANTE",
    };

    /// <summary>Le lettere distanziate delle etichette del concept (W E I G H T).</summary>
    private static string Spaced(string text) => string.Join(' ', text.ToCharArray());

    private static readonly Brush HudBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xC8, 0xF6, 0xF4, 0xEE)));
    private static readonly Brush PanelBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xE8, 0xF6, 0xF4, 0xEE)));
    private static readonly Brush VeilBrush = Frozen(new SolidColorBrush(Color.FromArgb(0xA0, 0xF6, 0xF4, 0xEE)));
    private static readonly Pen BarPen = Frozen(new Pen(MuchiArt.InkBrush, 0.8));
    private static readonly Pen CardPen = Frozen(new Pen(MuchiArt.SandBrush, 0.8));

    private static T Frozen<T>(T freezable)
        where T : Freezable
    {
        freezable.Freeze();
        return freezable;
    }
}
