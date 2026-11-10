using System.Windows;
using System.Windows.Media;
using NothingShell.Core.Leisure;

namespace NothingShell.App.Leisure;

/// <summary>
/// La tavola in fondo al concept, per la pagina Svago delle impostazioni: SISTEMA (corre, salta, cade), CORPORATURA (leggero →
/// normale → pesante) e CIBO (crocchette, scatoletta), disegnati con gli stessi asset del gioco su un foglio color carta.
/// </summary>
internal sealed class MuchiShowcase : FrameworkElement
{
    private const double Art = 1.55;
    private const double Baseline = 112;

    public MuchiShowcase()
    {
        Height = 168;
        IsHitTestVisible = false;
    }

    protected override void OnRender(DrawingContext dc)
    {
        var width = ActualWidth;
        if (width <= 0)
            return;
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRoundedRectangle(MuchiArt.SnowBrush, new Pen(MuchiArt.PaperBrush, 1), new Rect(0, 0, width, Height), 10, 10);

        var system = width * 0.37;
        var state = width * 0.4;
        var divider = new Pen(MuchiArt.SandBrush, 0.8);
        dc.DrawLine(divider, new Point(system, 18), new Point(system, Height - 18));
        dc.DrawLine(divider, new Point(system + state, 18), new Point(system + state, Height - 18));

        Heading(dc, "SISTEMA", 16, dpi);
        var step = system / 3;
        var weight = 0.3;
        Cat(dc, new Point(step * 0.8, Baseline), CatPose.Sitting with { Motion = CatMotion.Run, Stride = 0.3 }, weight);
        Cat(dc, new Point(step * 1.62, Baseline - 14), CatPose.Sitting with { Motion = CatMotion.Jump, Squash = 1.05 }, weight);
        Cat(dc, new Point(step * 2.45, Baseline - 6), CatPose.Sitting with { Motion = CatMotion.Fall }, weight);
        Caption(dc, "走る", "corre", step * 0.8, dpi);
        Caption(dc, "跳ぶ", "salta", step * 1.62, dpi);
        Caption(dc, "落ちる", "cade", step * 2.45, dpi);

        Heading(dc, "CORPORATURA", system + 16, dpi);
        var builds = new[] { (0.1, "軽い", "leggero"), (0.5, "ふつう", "normale"), (0.95, "重い", "pesante") };
        for (var i = 0; i < builds.Length; i++)
        {
            var (w, jp, it) = builds[i];
            var x = system + (state * (0.18 + (i * 0.32)));
            Cat(dc, new Point(x, Baseline), CatPose.Sitting, w);
            Caption(dc, jp, it, x, dpi);
            if (i < builds.Length - 1)
            {
                var arrow = MuchiArt.Text("→", 14, MuchiArt.CharcoalBrush, dpi);
                dc.DrawText(arrow, new Point(x + (state * 0.16) - (arrow.Width / 2), Baseline - 34));
            }
        }

        var items = system + state;
        var rest = width - items;
        Heading(dc, "CIBO", items + 16, dpi);
        Food(dc, new Point(items + (rest * 0.3), Baseline - 14), can: false);
        Food(dc, new Point(items + (rest * 0.72), Baseline - 16), can: true);
        Caption(dc, "カリカリ", "+1 · +3,5%", items + (rest * 0.3), dpi);
        Caption(dc, "ねこ缶", "+5 · +18%", items + (rest * 0.72), dpi);
    }

    private static void Cat(DrawingContext dc, Point feet, CatPose pose, double weight)
    {
        dc.PushTransform(new MatrixTransform(Art, 0, 0, Art, feet.X, feet.Y));
        MuchiArt.DrawCat(dc, pose, weight);
        dc.Pop();
    }

    private static void Food(DrawingContext dc, Point at, bool can)
    {
        dc.PushTransform(new MatrixTransform(Art * 1.4, 0, 0, Art * 1.4, at.X, at.Y));
        if (can)
            MuchiArt.DrawCan(dc, new Point(0, 0), 0.4);
        else
            MuchiArt.DrawKibble(dc, new Point(0, 0));
        dc.Pop();
    }

    private static void Heading(DrawingContext dc, string text, double x, double dpi) =>
        dc.DrawText(MuchiArt.Text(string.Join(' ', text.ToCharArray()), 10, MuchiArt.CharcoalBrush, dpi), new Point(x, 14));

    private static void Caption(DrawingContext dc, string japanese, string italian, double x, double dpi)
    {
        var jp = MuchiArt.Text(japanese, 11, MuchiArt.InkBrush, dpi, FontWeights.Normal, MuchiArt.JapaneseFont);
        var it = MuchiArt.Text(italian, 10, MuchiArt.StoneBrush, dpi);
        dc.DrawText(jp, new Point(x - (jp.Width / 2), Baseline + 10));
        dc.DrawText(it, new Point(x - (it.Width / 2), Baseline + 27));
    }
}
