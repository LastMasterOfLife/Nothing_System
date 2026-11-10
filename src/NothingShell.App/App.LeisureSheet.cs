using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using NothingShell.App.Leisure;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.App.Settings;
using NothingShell.Core.Leisure;
using NothingShell.Core.Notch;

namespace NothingShell.App;

/// <summary>
/// <c>--svago-sheet</c> (docs/25-svago.md): i fogli degli asset di むちねこ (il gatto in ogni posa e corporatura, il cibo, la città)
/// e le foto della pagina Svago del notch nei suoi momenti e delle impostazioni. Disegna e basta: niente rete, niente shell.
/// </summary>
public partial class App
{
    private static void RenderLeisureSheet(string folder)
    {
        Directory.CreateDirectory(folder);
        SaveSnapshot(CatSheet(), Path.Combine(folder, "svago-gatto.png"));
        SaveSnapshot(ThingsSheet(), Path.Combine(folder, "svago-oggetti.png"));

        using var media = new MediaService(); // non avviato
        var notch = new NotchWindow(media, DevPageActions.None, ConnectedPageActions.None);
        notch.SetLeisureBest(42, 3);
        foreach (var (name, run, record) in LeisureMoments())
        {
            notch.ShowLeisureForSnapshot(run, record);
            SaveSnapshot(notch.PrepareSnapshot(NotchState.Expanded, NotchPage.Leisure), Path.Combine(folder, $"svago-notch-{name}.png"));
        }

        var settings = new SettingsWindow(new SnapshotSettings([]));
        var root = (FrameworkElement)settings.Content;
        root.Width = settings.Width;
        root.Height = 1400;
        settings.Show(SettingsPage.Leisure);
        SaveSnapshot(root, Path.Combine(folder, "svago-impostazioni.png"));
    }

    /// <summary>
    /// Ogni posa (righe) in tre corporature (colonne raggruppate), sulle linee di un tetto: corsa in quattro momenti, stacco, salto,
    /// caduta, atterraggio, mastica, contento, ruzzolone, seduto, battito, seduto contento e triste.
    /// </summary>
    private static Border CatSheet()
    {
        var poses = new (string Name, CatPose Pose, CatFace Face, double Lift)[]
        {
            ("Corre 1/4", Side(CatMotion.Run, stride: 0), CatFace.Normal, 0),
            ("Corre 2/4", Side(CatMotion.Run, stride: 0.25), CatFace.Normal, 0),
            ("Corre 3/4", Side(CatMotion.Run, stride: 0.5), CatFace.Normal, 0),
            ("Corre 4/4", Side(CatMotion.Run, stride: 0.75), CatFace.Normal, 0),
            ("Stacco", Side(CatMotion.Jump, squash: 1.15), CatFace.Normal, 6),
            ("Salta", Side(CatMotion.Jump), CatFace.Normal, 14),
            ("Cade", Side(CatMotion.Fall), CatFace.Normal, 10),
            ("Atterra", Side(CatMotion.Run, squash: 0.78), CatFace.Normal, 0),
            ("Mastica", Side(CatMotion.Run, stride: 0.25) with { Chew = 1, Happy = true }, CatFace.Normal, 0),
            ("Ruzzolone", Side(CatMotion.Tumble) with { Spin = 130 }, CatFace.Normal, 12),
            ("Seduto", CatPose.Sitting, CatFace.Normal, 0),
            ("Battito", CatPose.Sitting with { Blink = true, Tail = 1 }, CatFace.Normal, 0),
            ("Contento", CatPose.Sitting with { Tail = -1 }, CatFace.Happy, 0),
            ("Triste", CatPose.Sitting, CatFace.Sad, 0),
        };
        var builds = new[] { ("leggero", 0.1), ("normale", 0.5), ("pesante", 0.95) };
        return Sheet(builds.Length * 64, poses.Length * 56, 120, 30, dc =>
        {
            foreach (var (label, i) in builds.Select((b, i) => (b.Item1, i)))
                Label(dc, label, 120 + (i * 64) + 32, 18, center: true);
            foreach (var ((name, pose, face, lift), row) in poses.Select((p, i) => (p, i)))
            {
                var y = 30 + (row * 56) + 46;
                Label(dc, name, 14, y - 18, center: false);
                foreach (var ((_, weight), column) in builds.Select((b, i) => (b, i)))
                {
                    var x = 120 + (column * 64) + 32;
                    dc.DrawLine(new Pen(MuchiArt.SandBrush, 0.8), new Point(x - 28, y), new Point(x + 28, y));
                    dc.PushTransform(new MatrixTransform(1.25, 0, 0, 1.25, x - 4, y - (lift * 1.25)));
                    MuchiArt.DrawCat(dc, pose, weight, face);
                    dc.Pop();
                }
            }
        });
    }

    private static CatPose Side(CatMotion motion, double stride = 0, double squash = 1) =>
        new(motion, stride, squash, 0, false, 0, 0, false);

    /// <summary>Cibo, bandiera, un tetto e i palazzi dello sfondo (anche quello con l'insegna).</summary>
    private static Border ThingsSheet() => Sheet(560, 250, 0, 0, dc =>
    {
        const double Dpi = 2;
        void At(double x, double y, double scale, Action draw)
        {
            dc.PushTransform(new MatrixTransform(scale, 0, 0, scale, x, y));
            draw();
            dc.Pop();
        }

        Label(dc, "Crocchette", 50, 18, center: true);
        At(50, 60, 3, () => MuchiArt.DrawKibble(dc, new Point(0, 0)));
        Label(dc, "Scatoletta", 130, 18, center: true);
        At(130, 62, 3, () => MuchiArt.DrawCan(dc, new Point(0, 0), 0.4));
        Label(dc, "Traguardo", 210, 18, center: true);
        At(198, 100, 1.6, () => MuchiArt.DrawGoal(dc, 2, 0.3, Dpi));

        Label(dc, "Tetto", 380, 18, center: true);
        dc.PushClip(new RectangleGeometry(new Rect(270, 30, 220, 90)));
        At(280, 60, 1, () => dc.DrawDrawing(MuchiArt.RoofDrawing(new Roof(0, 200, 0, 7))));
        dc.Pop();

        Label(dc, "Città: lontano e vicino, con l'insegna", 14, 132, center: false);
        dc.PushClip(new RectangleGeometry(new Rect(14, 145, 532, 95)));
        dc.DrawRectangle(MuchiArt.SnowBrush, null, new Rect(14, 145, 532, 95));
        foreach (var layer in new[] { new Skyline(11, far: true, 1), new Skyline(23, far: false, 1) })
        {
            foreach (var (building, drawing) in layer.Between(0, 420, Dpi))
                At(14 + (building.X * 1.25), 240, 1.25, () => dc.DrawDrawing(drawing));
        }
        dc.Pop();
    });

    /// <summary>Un foglio color carta, con lo spazio per le etichette a sinistra e in alto.</summary>
    private static Border Sheet(double width, double height, double left, double top, Action<DrawingContext> draw)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(MuchiArt.SnowBrush, null, new Rect(0, 0, left + width + 14, top + height + 14));
            draw(dc);
        }
        var image = new Image
        {
            Source = new DrawingImage(visual.Drawing),
            Width = left + width + 14,
            Height = top + height + 14,
            Stretch = Stretch.None,
        };
        return new Border { Width = image.Width, Height = image.Height, Background = MuchiArt.SnowBrush, Child = image };
    }

    private static void Label(DrawingContext dc, string text, double x, double y, bool center)
    {
        var label = MuchiArt.Text(text, 10, MuchiArt.CharcoalBrush, 2);
        dc.DrawText(label, new Point(center ? x - (label.Width / 2) : x, y));
    }

    /// <summary>
    /// I momenti della pagina: il titolo, la corsa con le crocchette davanti, un salto sopra il vuoto, il gatto pesante, la
    /// pausa, il traguardo e la fine (normale e col record).
    /// </summary>
    private static IEnumerable<(string Name, RooftopRun Run, bool Record)> LeisureMoments()
    {
        yield return ("titolo", new RooftopRun(4), false);
        yield return ("corsa", Played(4, until: r => r.Time > 4.2 && r.Grounded && r.Treats.Any(t => t.EatenAt is null && t.X > r.CatX + 20 && t.X < r.CatX + 160)), false);
        yield return ("salto", Played(4, until: r => r.Time > 6 && !r.Grounded && r.VelocityY is > -120 and < -30), false);
        yield return ("pesante", Played(6, until: r => r.Weight > 0.8 && r.Grounded && r.Time - r.LandedAt > 0.3), false);
        var paused = Played(4, until: r => r.Time > 3);
        paused.Pause();
        yield return ("pausa", paused, false);
        yield return ("traguardo", Played(1, until: r => r.GoalAt is { } goal && r.Time - goal > 0.35), false);
        yield return ("fine", Played(4, until: r => r.Phase == RunPhase.Over, careless: true), false);
        yield return ("record", Played(1, until: r => r.Phase == RunPhase.Over), true);
    }

    /// <summary>Una partita giocata da un giocatore attento (o distratto, che non salta mai) finché succede <paramref name="until"/>.</summary>
    private static RooftopRun Played(int seed, Func<RooftopRun, bool> until, bool careless = false)
    {
        const double Frame = 1.0 / 60;
        var run = new RooftopRun(seed);
        run.Press();
        for (var i = 0; i < 60 * 120 && !until(run); i++)
        {
            var roof = run.Roofs.FirstOrDefault(r => run.CatX + RooftopRun.FootHalf > r.Left && run.CatX - RooftopRun.FootHalf < r.Right && Math.Abs(r.Top - run.CatY) < 0.5);
            if (!careless && run.Grounded && roof.Width > 0 && run.CatX - RooftopRun.FootHalf + (run.Speed * Frame * 1.5) >= roof.Right)
                run.Press();
            else if (!run.Grounded && run.VelocityY > 0)
                run.Release();
            run.Update(Frame);
            if (run.Phase == RunPhase.Over && !until(run))
                run.Update(1); // ricomincia la prossima volta
            if (run.Phase == RunPhase.Over && !until(run))
                run.Press();
        }
        run.TakeEvents();
        return run;
    }
}
