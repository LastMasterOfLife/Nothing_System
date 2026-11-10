using System.Globalization;
using System.Windows;
using System.Windows.Media;
using NothingShell.Core.Leisure;

namespace NothingShell.App.Leisure;

/// <summary>
/// Gli asset di むちねこ (docs/25-svago.md), ridisegnati a vettori dal concept (docs/immagine_ispirazione.jpg): il gatto bianco
/// con orecchie, berretto, macchia sulla schiena e coda neri, in corsa, salto, caduta, ruzzolone e seduto; crocchette,
/// scatoletta, bandiera del traguardo, palazzi e tetti. Tutto in unità del mondo (<see cref="RooftopRun.ViewHeight"/> = 170): chi
/// disegna sposta e scala prima. Il gatto ha le zampe in (0, 0) e guarda a destra; il peso lo arrotonda in modo continuo.
/// </summary>
internal static class MuchiArt
{
    // La palette del concept: 111111 · 333333 · 777777 · BEB8AD · E6E1D7 · F6F4EE. Fuori palette, poco: il bianco del pelo,
    // il marrone delle crocchette e il rosa di naso e guance.
    public static readonly Color Ink = Rgb(0x111111);
    public static readonly Color Charcoal = Rgb(0x333333);
    public static readonly Color Stone = Rgb(0x777777);
    public static readonly Color Sand = Rgb(0xBEB8AD);
    public static readonly Color Paper = Rgb(0xE6E1D7);
    public static readonly Color Snow = Rgb(0xF6F4EE);

    public static readonly Brush InkBrush = Frozen(Ink);
    public static readonly Brush CharcoalBrush = Frozen(Charcoal);
    public static readonly Brush StoneBrush = Frozen(Stone);
    public static readonly Brush SandBrush = Frozen(Sand);
    public static readonly Brush PaperBrush = Frozen(Paper);
    public static readonly Brush SnowBrush = Frozen(Snow);
    private static readonly Brush FurBrush = Frozen(Rgb(0xFFFDF8));
    private static readonly Brush FarFurBrush = Frozen(Rgb(0xE9E4DA));
    private static readonly Brush NoseBrush = Frozen(Rgb(0xD98E86));
    private static readonly Brush BlushBrush = Frozen(Color.FromArgb(0x70, 0xEE, 0xA3, 0x9A));
    private static readonly Brush KibbleBrush = Frozen(Rgb(0x6E5545));
    private static readonly Brush KibbleLightBrush = Frozen(Rgb(0xA3897A));
    private static readonly Brush KibbleDarkBrush = Frozen(Rgb(0x3D2F27));
    private static readonly Brush WhiteBrush = Frozen(Colors.White);

    private static readonly Pen Outline = Round(new Pen(InkBrush, 1.1));
    private static readonly Pen ThinOutline = Round(new Pen(InkBrush, 0.8));
    private static readonly Pen KibblePen = Round(new Pen(KibbleDarkBrush, 0.7));
    private static readonly Pen SpeedPen = Round(new Pen(StoneBrush, 1.1));
    private static readonly Pen SparklePen = Round(new Pen(StoneBrush, 0.9));

    public static readonly FontFamily DinFont = new("Bahnschrift, Segoe UI");
    public static readonly FontFamily JapaneseFont = new("Yu Gothic UI, Meiryo UI, MS Gothic");

    // ── Gatto ────────────────────────────────────────────────────────────────

    /// <summary>Il gatto nella sua posa: di lato mentre corre, di fronte da seduto.</summary>
    public static void DrawCat(DrawingContext dc, CatPose pose, double weight, CatFace face = CatFace.Normal)
    {
        if (pose.Motion == CatMotion.Sit)
            DrawSitting(dc, weight, pose, face);
        else
            DrawSide(dc, pose, weight);
    }

    /// <summary>Le misure del gatto di lato: più pesante, corpo più lungo e alto, zampe più corte, pancia che scende.</summary>
    private readonly record struct Body(double HalfLength, double HalfHeight, double Sag, double Leg, double CenterY, double HeadRadius)
    {
        public static Body Of(double weight)
        {
            var f = Math.Clamp(weight, 0, 1);
            var a = 13 + (5 * f);
            var b = 7.5 + (3.5 * f);
            var sag = 0.04 + (0.12 * f);
            var bottom = -2.4 + (1.8 * f); // la pancia del gatto pesante sfiora il tetto
            return new(a, b, sag, 7 - (1.5 * f), bottom - (b * (1 + sag)), 7.6 + (1.2 * f));
        }
    }

    private static void DrawSide(DrawingContext dc, CatPose pose, double weight)
    {
        var f = Math.Clamp(weight, 0, 1);
        var body = Body.Of(weight);
        var (a, b, cy) = (body.HalfLength, body.HalfHeight, body.CenterY);
        var cycle = pose.Stride * Math.PI * 2;

        double tilt = 0, bob = 0, legReach = 1;
        (double Front, double Back) near, far;
        switch (pose.Motion)
        {
            case CatMotion.Jump:
                tilt = -13;
                a *= 1.05;
                b *= 0.95;
                legReach = 1.15;
                near = (0.95, -0.95);
                far = (0.75, -0.75);
                break;
            case CatMotion.Fall:
                tilt = 7;
                a *= 0.9;
                b *= 1.07;
                legReach = 0.6;
                near = (-0.35, 0.4);
                far = (-0.2, 0.25);
                break;
            case CatMotion.Tumble:
                legReach = 1.1;
                near = (1.2, -1.2);
                far = (0.6, -0.6);
                break;
            default:
                bob = -Math.Abs(Math.Sin(cycle)) * (1.5 - (0.7 * f));
                var swing = 0.62 - (0.15 * f); // il gatto pesante fa passi più corti
                near = (Math.Sin(cycle) * swing, Math.Sin(cycle + Math.PI) * swing);
                far = (Math.Sin(cycle + 0.8) * swing, Math.Sin(cycle + Math.PI + 0.8) * swing);
                break;
        }
        cy += bob;

        // allungato allo stacco, schiacciato all'atterraggio: intorno alle zampe
        dc.PushTransform(new ScaleTransform(1 / Math.Sqrt(pose.Squash), pose.Squash));
        if (pose.Motion == CatMotion.Tumble)
            dc.PushTransform(new RotateTransform(pose.Spin, 0, cy));
        else
            dc.PushTransform(new RotateTransform(tilt, 0, cy));

        var head = new Point((a * 0.74) + (pose.Motion == CatMotion.Fall ? -1 : 0), cy - (b * 0.5) - (body.HeadRadius * 0.32));
        var r = body.HeadRadius * (1 + (0.06 * pose.Chew)); // guance piene mentre mastica

        DrawTail(dc, pose, a, b, cy, f);

        var hipBack = new Point(-a * 0.52, cy + (b * 0.3));
        var hipFront = new Point(a * 0.42, cy + (b * 0.32));
        var reach = (-cy - (b * 0.3)) * legReach;
        // tutte le zampe dietro al corpo: ne spuntano solo i piedi, corti e paffuti come nel concept
        var legWidth = 4.2 + (1.2 * f);
        DrawLeg(dc, Offset(hipBack, 1.6, -0.6), far.Back, reach, legWidth, FarFurBrush);
        DrawLeg(dc, Offset(hipFront, 1.6, -0.6), far.Front, reach, legWidth, FarFurBrush);
        DrawLeg(dc, hipBack, near.Back, reach, legWidth, FurBrush);
        DrawLeg(dc, hipFront, near.Front, reach, legWidth, FurBrush);

        var bean = Bean(a, b, body.Sag);
        dc.PushTransform(new TranslateTransform(0, cy));
        dc.DrawGeometry(FurBrush, Outline, bean);
        dc.PushClip(bean);
        dc.DrawEllipse(InkBrush, null, new Point(-a * 0.12, -b * 0.82), a * 0.56, b * 0.62); // la macchia sulla schiena
        dc.DrawEllipse(InkBrush, null, new Point(-a * 0.95, -b * 0.25), a * 0.2, b * 0.45); // e dove nasce la coda
        dc.Pop();
        dc.DrawGeometry(null, Outline, bean); // il bordo sopra le macchie
        dc.Pop();

        DrawSideHead(dc, head, r, pose);

        dc.Pop();
        dc.Pop();

        DrawMotionLines(dc, pose, a, b, cy);
    }

    private static void DrawTail(DrawingContext dc, CatPose pose, double a, double b, double cy, double f)
    {
        var root = new Point(-a * 0.88, cy - (b * 0.35));
        var (control, tip) = pose.Motion switch
        {
            CatMotion.Jump => (new Point(-a - 6, cy - (b * 0.2)), new Point(-a - 12, cy + 1)),
            CatMotion.Fall => (new Point(-a - 6, cy - b - 2), new Point(-a - 2, cy - b - 11)),
            CatMotion.Tumble => (new Point(-a - 8, cy - b), new Point(-a - 4, cy - b - 8)),
            _ => (new Point(-a - 7, cy - (b * 0.6)), new Point(-a - 6 + (pose.Tail * 2), cy - b - 7 - (pose.Tail * 1.5))),
        };
        var tail = new StreamGeometry();
        using (var c = tail.Open())
        {
            c.BeginFigure(root, false, false);
            c.QuadraticBezierTo(control, tip, true, true);
        }
        tail.Freeze();
        dc.DrawGeometry(null, Round(new Pen(InkBrush, 3.6 + (0.8 * f))), tail);
    }

    /// <summary>Una zampa a capsula col bordo: dall'anca, ruotata di <paramref name="angle"/> radianti (positivo: in avanti).</summary>
    private static void DrawLeg(DrawingContext dc, Point hip, double angle, double length, double width, Brush fur)
    {
        var foot = new Point(hip.X + (Math.Sin(angle) * length), hip.Y + (Math.Cos(angle) * length));
        dc.DrawLine(Round(new Pen(InkBrush, width + 2.2)), hip, foot);
        dc.DrawLine(Round(new Pen(fur, width)), hip, foot);
    }

    private static void DrawSideHead(DrawingContext dc, Point c, double r, CatPose pose)
    {
        // orecchie dietro la testa, nere
        DrawEar(dc, new Point(c.X - (r * 0.78), c.Y - (r * 0.4)), new Point(c.X - (r * 0.15), c.Y - (r * 0.95)), new Point(c.X - (r * 0.68), c.Y - (r * 1.45)));
        DrawEar(dc, new Point(c.X - (r * 0.05), c.Y - (r * 0.98)), new Point(c.X + (r * 0.62), c.Y - (r * 0.7)), new Point(c.X + (r * 0.32), c.Y - (r * 1.48)));

        var head = new EllipseGeometry(c, r, r * 0.94);
        dc.DrawGeometry(FurBrush, Outline, head);
        dc.PushClip(head);
        dc.DrawEllipse(InkBrush, null, new Point(c.X - (r * 0.55), c.Y - (r * 0.6)), r * 0.82, r * 0.6); // il berretto nero
        dc.Pop();
        dc.DrawGeometry(null, Outline, head);

        var eye = new Point(c.X + (r * 0.42), c.Y + (r * 0.02));
        dc.DrawEllipse(BlushBrush, null, new Point(c.X + (r * 0.42), c.Y + (r * 0.48)), r * 0.24, r * 0.15);
        switch (pose.Motion)
        {
            case CatMotion.Tumble:
                DrawCross(dc, eye, r * 0.18);
                break;
            case CatMotion.Fall:
                dc.DrawEllipse(InkBrush, null, eye, r * 0.19, r * 0.21); // occhi sgranati
                dc.DrawEllipse(WhiteBrush, null, Offset(eye, r * 0.06, -r * 0.07), r * 0.06, r * 0.06);
                break;
            default:
                if (pose.Happy)
                    DrawArc(dc, eye, r * 0.17, up: true);
                else if (pose.Blink)
                    dc.DrawLine(ThinOutline, Offset(eye, -r * 0.16, 0), Offset(eye, r * 0.16, 0));
                else
                    dc.DrawEllipse(InkBrush, null, eye, r * 0.14, r * 0.17);
                break;
        }
        dc.DrawEllipse(NoseBrush, null, new Point(c.X + (r * 0.95), c.Y + (r * 0.2)), r * 0.13, r * 0.1);
        if (pose.Chew > 0.05)
            dc.DrawEllipse(CharcoalBrush, null, new Point(c.X + (r * 0.8), c.Y + (r * 0.5)), r * (0.06 + (0.14 * pose.Chew)), r * (0.05 + (0.16 * pose.Chew)));
    }

    /// <summary>Le linee del movimento del concept: dietro in corsa (走る), sotto nel salto (跳ぶ), sopra nella caduta (落ちる).</summary>
    private static void DrawMotionLines(DrawingContext dc, CatPose pose, double a, double b, double cy)
    {
        switch (pose.Motion)
        {
            case CatMotion.Run:
                var shift = pose.Stride * 4;
                dc.DrawLine(SpeedPen, new Point(-a - 16 - shift, cy - 3), new Point(-a - 9 - shift, cy - 3));
                dc.DrawLine(SpeedPen, new Point(-a - 20 + shift, cy + 1.5), new Point(-a - 11 + shift, cy + 1.5));
                dc.DrawLine(SpeedPen, new Point(-a - 15 - shift, cy + 6), new Point(-a - 10 - shift, cy + 6));
                break;
            case CatMotion.Jump:
                dc.DrawLine(SpeedPen, new Point(-a * 0.6, 4), new Point((-a * 0.6) - 3, 9));
                dc.DrawLine(SpeedPen, new Point(-a * 0.1, 3), new Point((-a * 0.1) - 3, 8));
                dc.DrawLine(SpeedPen, new Point(a * 0.35, 4), new Point((a * 0.35) - 3, 9));
                break;
            case CatMotion.Fall:
                var top = cy - b - 8;
                dc.DrawLine(SpeedPen, new Point(-a * 0.4, top - 9), new Point(-a * 0.4, top - 3));
                dc.DrawLine(SpeedPen, new Point(0, top - 12), new Point(0, top - 4));
                dc.DrawLine(SpeedPen, new Point(a * 0.4, top - 9), new Point(a * 0.4, top - 3));
                break;
        }
    }

    /// <summary>
    /// Seduto di fronte, come in CAT STATE: corpo a pera che si allarga col peso, testa che ci affonda, coda che si arriccia di
    /// lato e ondeggia (<see cref="CatPose.Tail"/>).
    /// </summary>
    private static void DrawSitting(DrawingContext dc, double weight, CatPose pose, CatFace face)
    {
        var f = Math.Clamp(weight, 0, 1);
        var bw = 10.5 + (7 * f);
        var bh = 10 + (2.2 * f);
        var r = 9 + (1.6 * f);
        var head = new Point(0, (-2 * bh) - (r * 0.5) + (f * 3));

        // la coda dietro, a destra
        var tail = new StreamGeometry();
        using (var c = tail.Open())
        {
            c.BeginFigure(new Point(bw * 0.55, -bh * 0.35), false, false);
            c.BezierTo(new Point(bw + 7, -bh * 0.2), new Point(bw + 8, -bh * 0.9), new Point(bw + 5 + (pose.Tail * 2.5), (-bh * 1.35) - Math.Abs(pose.Tail)), true, true);
        }
        tail.Freeze();
        dc.DrawGeometry(null, Round(new Pen(InkBrush, 3.8 + (0.8 * f))), tail);

        var pear = new StreamGeometry();
        using (var c = pear.Open())
        {
            c.BeginFigure(new Point(0, 0), true, true);
            c.BezierTo(new Point(bw * 0.95, 0), new Point(bw, -bh * 0.35), new Point(bw * 0.97, -bh * 0.85), true, true);
            c.BezierTo(new Point(bw * 0.9, -bh * 1.55), new Point(bw * 0.55, -bh * 2.05), new Point(0, -bh * 2.05), true, true);
            c.BezierTo(new Point(-bw * 0.55, -bh * 2.05), new Point(-bw * 0.9, -bh * 1.55), new Point(-bw * 0.97, -bh * 0.85), true, true);
            c.BezierTo(new Point(-bw, -bh * 0.35), new Point(-bw * 0.95, 0), new Point(0, 0), true, true);
        }
        pear.Freeze();
        dc.DrawGeometry(FurBrush, Outline, pear);
        dc.PushClip(pear);
        dc.DrawEllipse(InkBrush, null, new Point(-bw * 0.72, -bh * 1.45), bw * 0.5, bh * 0.5);
        dc.DrawEllipse(InkBrush, null, new Point(bw * 0.95, -bh * 0.55), bw * 0.28, bh * 0.38);
        dc.Pop();
        dc.DrawGeometry(null, Outline, pear);

        // le zampe davanti
        foreach (var side in new[] { -1, 1 })
            dc.DrawEllipse(FurBrush, ThinOutline, new Point(side * bw * 0.3, -2), 3.2 + (0.6 * f), 2.3);

        DrawEar(dc, new Point(head.X - (r * 0.9), head.Y - (r * 0.35)), new Point(head.X - (r * 0.3), head.Y - (r * 0.92)), new Point(head.X - (r * 0.85), head.Y - (r * 1.45)));
        DrawEar(dc, new Point(head.X + (r * 0.3), head.Y - (r * 0.92)), new Point(head.X + (r * 0.9), head.Y - (r * 0.35)), new Point(head.X + (r * 0.85), head.Y - (r * 1.45)));
        var skull = new EllipseGeometry(head, r * 1.04, r * 0.94);
        dc.DrawGeometry(FurBrush, Outline, skull);
        dc.PushClip(skull);
        dc.DrawEllipse(InkBrush, null, new Point(head.X - (r * 0.62), head.Y - (r * 0.62)), r * 0.72, r * 0.62);
        dc.Pop();
        dc.DrawGeometry(null, Outline, skull);

        var eyeY = head.Y + (r * 0.1);
        foreach (var side in new[] { -1, 1 })
        {
            var eye = new Point(head.X + (side * r * 0.38), eyeY);
            dc.DrawEllipse(BlushBrush, null, new Point(head.X + (side * r * 0.64), head.Y + (r * 0.45)), r * 0.2, r * 0.12);
            switch (face)
            {
                case CatFace.Happy:
                    DrawArc(dc, eye, r * 0.15, up: true);
                    break;
                case CatFace.Sad:
                    dc.DrawLine(ThinOutline, Offset(eye, -r * 0.14, side * -r * 0.05), Offset(eye, r * 0.14, side * r * 0.05));
                    break;
                default:
                    if (pose.Blink)
                        dc.DrawLine(ThinOutline, Offset(eye, -r * 0.14, 0), Offset(eye, r * 0.14, 0));
                    else
                        dc.DrawEllipse(InkBrush, null, eye, r * 0.12, r * 0.14);
                    break;
            }
        }

        var nose = new Point(head.X, head.Y + (r * 0.3));
        dc.DrawEllipse(NoseBrush, null, nose, r * 0.11, r * 0.08);
        DrawArc(dc, new Point(nose.X - (r * 0.1), nose.Y + (r * 0.12)), r * 0.1, up: false); // la bocca a ω
        DrawArc(dc, new Point(nose.X + (r * 0.1), nose.Y + (r * 0.12)), r * 0.1, up: false);
    }

    private static void DrawEar(DrawingContext dc, Point baseA, Point baseB, Point tip)
    {
        var ear = new StreamGeometry();
        using (var c = ear.Open())
        {
            c.BeginFigure(baseA, true, true);
            c.LineTo(tip, true, true);
            c.LineTo(baseB, true, true);
        }
        ear.Freeze();
        dc.DrawGeometry(InkBrush, Outline, ear);
    }

    // ── Cibo ─────────────────────────────────────────────────────────────────

    /// <summary>Crocchette (カリカリ): tre sassolini marroni, larghi una decina di unità, centrati su <paramref name="c"/>.</summary>
    public static void DrawKibble(DrawingContext dc, Point c)
    {
        foreach (var (dx, dy, angle) in new[] { (-2.7, 1.4, 20.0), (2.8, 1.6, -15.0), (0.0, -1.7, 40.0) })
        {
            var p = new Point(c.X + dx, c.Y + dy);
            dc.PushTransform(new RotateTransform(angle, p.X, p.Y));
            dc.DrawEllipse(KibbleBrush, KibblePen, p, 2.7, 2.15);
            dc.DrawEllipse(KibbleLightBrush, null, new Point(p.X - 0.8, p.Y - 0.7), 0.9, 0.55);
            dc.Pop();
        }
    }

    /// <summary>Scatoletta (ねこ缶): coperchio con l'anello, fascia scura, etichetta col muso del gatto e le scintille sopra.</summary>
    public static void DrawCan(DrawingContext dc, Point c, double clock)
    {
        const double W = 7.5;
        double top = c.Y - 5, bottom = c.Y + 6;
        var body = new StreamGeometry();
        using (var g = body.Open())
        {
            g.BeginFigure(new Point(c.X - W, top), true, true);
            g.LineTo(new Point(c.X - W, bottom), true, true);
            g.ArcTo(new Point(c.X + W, bottom), new Size(W, 2), 0, false, SweepDirection.Counterclockwise, true, true);
            g.LineTo(new Point(c.X + W, top), true, true);
        }
        body.Freeze();
        dc.DrawGeometry(SnowBrush, ThinOutline, body);
        dc.PushClip(body);
        dc.DrawRectangle(CharcoalBrush, null, new Rect(c.X - W, top, 2 * W, 2.6));
        dc.DrawRectangle(StoneBrush, null, new Rect(c.X - W, bottom - 1.2, 2 * W, 4));
        dc.Pop();
        dc.DrawGeometry(null, ThinOutline, body);

        // il muso sull'etichetta
        var face = new Point(c.X, c.Y + 1.6);
        DrawEar(dc, new Point(face.X - 2.4, face.Y - 0.6), new Point(face.X - 0.6, face.Y - 2), new Point(face.X - 2.2, face.Y - 3.4));
        DrawEar(dc, new Point(face.X + 0.6, face.Y - 2), new Point(face.X + 2.4, face.Y - 0.6), new Point(face.X + 2.2, face.Y - 3.4));
        dc.DrawEllipse(FurBrush, ThinOutline, face, 2.6, 2.2);
        dc.DrawEllipse(InkBrush, null, new Point(face.X - 0.9, face.Y - 0.1), 0.35, 0.4);
        dc.DrawEllipse(InkBrush, null, new Point(face.X + 0.9, face.Y - 0.1), 0.35, 0.4);

        dc.DrawEllipse(SandBrush, ThinOutline, new Point(c.X, top), W, 2);
        dc.DrawEllipse(null, Round(new Pen(StoneBrush, 0.6)), new Point(c.X, top), W - 1.6, 1.3);
        dc.DrawEllipse(null, Round(new Pen(CharcoalBrush, 0.7)), new Point(c.X + 2.2, top - 0.1), 1.3, 0.6); // l'anello

        // scintille che si accendono a turno
        for (var i = 0; i < 3; i++)
        {
            var on = 0.5 + (0.5 * Math.Sin((clock * 5) + (i * 2.1)));
            var (sx, sy) = (c.X - 6 + (i * 6), top - 5 - (i == 1 ? 2 : 0));
            var len = 0.5 + (1.1 * on);
            dc.DrawLine(SparklePen, new Point(sx, sy - len), new Point(sx, sy + len));
            dc.DrawLine(SparklePen, new Point(sx - len, sy), new Point(sx + len, sy));
        }
    }

    // ── Città ────────────────────────────────────────────────────────────────

    /// <summary>
    /// La bandiera del traguardo, Bauhaus: asta nera, drappo nero col cerchio chiaro e il numero della tappa; sventola con
    /// <paramref name="clock"/>. I piedi sul tetto in (0, 0).
    /// </summary>
    public static void DrawGoal(DrawingContext dc, int stage, double clock, double pixelsPerDip)
    {
        dc.DrawRectangle(CharcoalBrush, null, new Rect(-4, -3, 8, 3));
        dc.DrawLine(Round(new Pen(InkBrush, 1.6)), new Point(0, -3), new Point(0, -42));
        var flag = new StreamGeometry();
        using (var c = flag.Open())
        {
            const double W = 20, H = 13;
            double Wave(double x) => Math.Sin((clock * 6) - (x * 0.25)) * (x / W) * 1.6;
            c.BeginFigure(new Point(0.8, -42), true, true);
            for (var x = 2.0; x <= W; x += 2)
                c.LineTo(new Point(0.8 + x, -42 + Wave(x)), true, true);
            for (var x = W; x >= 0; x -= 2)
                c.LineTo(new Point(0.8 + x, -42 + H + Wave(x)), true, true);
        }
        flag.Freeze();
        dc.DrawGeometry(InkBrush, null, flag);
        dc.DrawEllipse(SnowBrush, null, new Point(10.8, -35.5 + (Math.Sin((clock * 6) - 2.5) * 0.8)), 4.6, 4.6);
        var number = Text(stage.ToString(CultureInfo.InvariantCulture), 6.5, InkBrush, pixelsPerDip, FontWeights.SemiBold);
        dc.DrawText(number, new Point(10.8 - (number.Width / 2), -35.5 - (number.Height / 2) + (Math.Sin((clock * 6) - 2.5) * 0.8)));
    }

    /// <summary>Il cielo color carta, piatto come nel concept, e un grande sole pallido (un cerchio, Bauhaus).</summary>
    public static void DrawSky(DrawingContext dc, Rect area, Point sun)
    {
        dc.DrawRectangle(SnowBrush, null, area);
        dc.DrawEllipse(SunBrush, null, sun, 24, 24);
    }
    private static readonly Brush SunBrush = Frozen(Rgb(0xE9E3D8));

    /// <summary>Un tetto dove corre il gatto, con la facciata fino in fondo alla vista: ringhiera, cassoni, finestre.</summary>
    public static Drawing RoofDrawing(Roof roof)
    {
        var random = new Random(roof.Look);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var height = RooftopRun.ViewHeight + 20 - roof.Top;
            dc.DrawRectangle(FacadeBrush, null, new Rect(0, 0, roof.Width, height));
            dc.DrawRectangle(InkBrush, null, new Rect(-1, 0, roof.Width + 2, 3.2)); // il cornicione

            // finestre: file regolari, qualcuna accesa
            var columns = Math.Max(1, (int)((roof.Width - 16) / 13));
            var margin = (roof.Width - ((columns - 1) * 13) - 6) / 2;
            for (var y = 11.0; y < height - 4; y += 14)
            {
                for (var i = 0; i < columns; i++)
                {
                    var lit = random.NextDouble() < 0.18;
                    dc.DrawRectangle(lit ? SandBrush : WindowDarkBrush, null, new Rect(margin + (i * 13), y, 6, 7));
                }
            }

            // sul tetto, dietro al gatto: un tratto di ringhiera e qualche oggetto
            var railStart = random.NextDouble() * Math.Max(0, roof.Width - 60);
            var railLength = Math.Min(roof.Width - railStart, 30 + (random.NextDouble() * 50));
            var rail = Round(new Pen(CharcoalBrush, 0.9));
            dc.DrawLine(rail, new Point(railStart, -6), new Point(railStart + railLength, -6));
            for (var x = railStart; x <= railStart + railLength + 0.1; x += 5)
                dc.DrawLine(rail, new Point(x, -6), new Point(x, 0));

            switch (random.Next(4))
            {
                case 0 when roof.Width > 90:
                    var ax = roof.Width - 30;
                    dc.DrawRectangle(CharcoalBrush, null, new Rect(ax, -9, 16, 9)); // un condizionatore
                    dc.DrawEllipse(null, Round(new Pen(StoneBrush, 0.8)), new Point(ax + 8, -4.5), 3, 3);
                    break;
                case 1 when roof.Width > 90:
                    var tx = roof.Width - 34;
                    dc.DrawRectangle(CharcoalBrush, null, new Rect(tx + 2, -6, 1.2, 6));
                    dc.DrawRectangle(CharcoalBrush, null, new Rect(tx + 12, -6, 1.2, 6));
                    dc.DrawRoundedRectangle(CharcoalBrush, null, new Rect(tx, -16, 15, 10), 2, 2); // un serbatoio
                    break;
                case 2:
                    var mx = Math.Min(roof.Width - 12, 20 + (random.NextDouble() * (roof.Width - 30)));
                    dc.DrawLine(rail, new Point(mx, 0), new Point(mx, -18)); // un'antenna
                    dc.DrawLine(rail, new Point(mx - 4, -14), new Point(mx + 4, -14));
                    break;
            }
        }
        group.Freeze();
        return group;
    }

    private static readonly Brush FacadeBrush = Frozen(Rgb(0x2C2B2A));
    private static readonly Brush WindowDarkBrush = Frozen(Rgb(0x464341));

    /// <summary>
    /// Un palazzo di sfondo: <paramref name="far"/> quelli lontani, chiari e piatti; gli altri grigi con le finestre, a volte col
    /// serbatoio dell'acqua, le antenne o l'insegna verticale むちねこ.
    /// </summary>
    public static Drawing BuildingDrawing(SkylineBuilding building, bool far, double pixelsPerDip)
    {
        ArgumentNullException.ThrowIfNull(building);
        var random = new Random(building.Look);
        var group = new DrawingGroup();
        using (var dc = group.Open())
        {
            var (w, h) = (building.Width, building.Height);
            var fill = far ? FarBrush : building.Look % 3 == 0 ? MidDarkBrush : MidBrush;
            dc.DrawRectangle(fill, null, new Rect(0, -h, w, h + 40));
            if (building.Step > 0)
                dc.DrawRectangle(fill, null, new Rect(w * 0.25, -h - building.Step, w * 0.5, building.Step + 1)); // il gradino in cima

            if (far)
            {
                for (var y = -h + 8; y < -6; y += 12)
                    dc.DrawRectangle(FarWindowBrush, null, new Rect(4, y, w - 8, 2));
            }
            else
            {
                var columns = Math.Max(1, (int)((w - 8) / 7));
                var margin = (w - ((columns - 1) * 7) - 4) / 2;
                for (var y = -h + 7; y < 0; y += 9)
                {
                    for (var i = 0; i < columns; i++)
                        dc.DrawRectangle(MidWindowBrush, null, new Rect(margin + (i * 7), y, 4, 5));
                }
            }

            if (!far && building.Sign)
            {
                // l'insegna verticale del concept, attaccata alla facciata
                var sx = w - 5;
                dc.DrawRectangle(SnowBrush, Round(new Pen(InkBrush, 1)), new Rect(sx - 6, -h + 10, 12, 44));
                var y = -h + 13.5;
                foreach (var ch in "むちねこ")
                {
                    var glyph = Text(ch.ToString(), 8.5, InkBrush, pixelsPerDip, FontWeights.Bold, JapaneseFont);
                    dc.DrawText(glyph, new Point(sx - (glyph.Width / 2), y));
                    y += 10;
                }
            }
            else if (!far)
            {
                switch (random.Next(5))
                {
                    case 0:
                        // il serbatoio dell'acqua tondo sulle gambe, come nel concept
                        var cx = w * (0.3 + (random.NextDouble() * 0.4));
                        var legs = Round(new Pen(StoneBrush, 1.2));
                        dc.DrawLine(legs, new Point(cx - 5, -h), new Point(cx - 4, -h - 9));
                        dc.DrawLine(legs, new Point(cx + 5, -h), new Point(cx + 4, -h - 9));
                        dc.DrawRoundedRectangle(StoneBrush, null, new Rect(cx - 8, -h - 22, 16, 14), 3, 3);
                        dc.DrawEllipse(StoneBrush, null, new Point(cx, -h - 22), 8, 4);
                        dc.DrawRectangle(MidWindowBrush, null, new Rect(cx - 8, -h - 16, 16, 1));
                        break;
                    case 1:
                        var ax = w * 0.7;
                        dc.DrawLine(Round(new Pen(StoneBrush, 0.9)), new Point(ax, -h), new Point(ax, -h - 16));
                        dc.DrawLine(Round(new Pen(StoneBrush, 0.9)), new Point(ax - 3, -h - 11), new Point(ax + 3, -h - 11));
                        break;
                }
            }
        }
        group.Freeze();
        return group;
    }

    private static readonly Brush FarBrush = Frozen(Rgb(0xD9D3C8));
    private static readonly Brush FarWindowBrush = Frozen(Rgb(0xE4DFD5));
    private static readonly Brush MidBrush = Frozen(Rgb(0x9D978E));
    private static readonly Brush MidDarkBrush = Frozen(Stone);
    private static readonly Brush MidWindowBrush = Frozen(Rgb(0xC4BEB3));

    // ── Mattoni ──────────────────────────────────────────────────────────────

    private static readonly Dictionary<(string, double, Brush, FontWeight, FontFamily, double), FormattedText> Texts = [];

    /// <summary>Un testo già impaginato: l'HUD ridice gli stessi a ogni fotogramma, e impaginarli costa.</summary>
    public static FormattedText Text(string text, double size, Brush brush, double pixelsPerDip, FontWeight? weight = null, FontFamily? family = null)
    {
        var key = (text, size, brush, weight ?? FontWeights.Normal, family ?? DinFont, pixelsPerDip);
        if (Texts.TryGetValue(key, out var cached))
            return cached;
        if (Texts.Count > 256)
            Texts.Clear();
        return Texts[key] = new(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
            new Typeface(key.Item5, FontStyles.Normal, key.Item4, FontStretches.Normal), size, brush, pixelsPerDip);
    }

    /// <summary>Un archetto: a ^ (occhi contenti) o a u (la bocca).</summary>
    private static void DrawArc(DrawingContext dc, Point c, double r, bool up)
    {
        var arc = new StreamGeometry();
        using (var g = arc.Open())
        {
            var dy = up ? r * 0.5 : -r * 0.5;
            g.BeginFigure(new Point(c.X - r, c.Y + dy), false, false);
            g.QuadraticBezierTo(new Point(c.X, c.Y - (dy * 1.6)), new Point(c.X + r, c.Y + dy), true, true);
        }
        arc.Freeze();
        dc.DrawGeometry(null, ThinOutline, arc);
    }

    private static void DrawCross(DrawingContext dc, Point c, double r)
    {
        dc.DrawLine(ThinOutline, Offset(c, -r, -r), Offset(c, r, r));
        dc.DrawLine(ThinOutline, Offset(c, -r, r), Offset(c, r, -r));
    }

    /// <summary>
    /// Il corpo a fagiolo, centrato: un'ellisse con la pancia che scende di <paramref name="sag"/> (parte di
    /// <paramref name="b"/>) e il sedere un po' più pieno della testa.
    /// </summary>
    private static StreamGeometry Bean(double a, double b, double sag)
    {
        const double K = 0.5523;
        var bottom = b * (1 + sag);
        var geometry = new StreamGeometry();
        using (var c = geometry.Open())
        {
            c.BeginFigure(new Point(a, 0), true, true);
            c.BezierTo(new Point(a, -b * K), new Point(a * K, -b), new Point(0, -b), true, true);
            c.BezierTo(new Point(-a * K * 1.1, -b), new Point(-a * 1.02, -b * K), new Point(-a * 1.02, 0), true, true);
            c.BezierTo(new Point(-a * 1.02, bottom * K), new Point(-a * K * 1.15, bottom), new Point(0, bottom), true, true);
            c.BezierTo(new Point(a * K * 1.1, bottom), new Point(a, bottom * K), new Point(a, 0), true, true);
        }
        geometry.Freeze();
        return geometry;
    }

    private static Point Offset(Point p, double dx, double dy) => new(p.X + dx, p.Y + dy);

    private static Pen Round(Pen pen)
    {
        pen.StartLineCap = pen.EndLineCap = PenLineCap.Round;
        pen.LineJoin = PenLineJoin.Round;
        pen.Freeze();
        return pen;
    }

    private static Color Rgb(int rgb) => Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8 & 0xFF), (byte)(rgb & 0xFF));

    private static Brush Frozen(Color color) => Frozen(new SolidColorBrush(color));

    private static Brush Frozen(Brush brush)
    {
        brush.Freeze();
        return brush;
    }
}

/// <summary>L'espressione del gatto seduto: normale, contento (traguardo, record), triste (fine partita).</summary>
internal enum CatFace
{
    Normal,
    Happy,
    Sad,
}

/// <summary>Un palazzo dello sfondo: dove comincia nel suo strato, quanto è largo e alto, e i dettagli.</summary>
internal sealed record SkylineBuilding(double X, double Width, double Height, double Step, bool Sign, int Look);

/// <summary>
/// Uno strato di palazzi che non finisce mai, uguale a ogni partita: si allunga quando la camera avanza. Lo spostamento lento
/// (<paramref name="parallax"/>) dà la profondità.
/// </summary>
internal sealed class Skyline(int seed, bool far, double parallax)
{
    private readonly Random _random = new(seed);
    private readonly List<SkylineBuilding> _buildings = [];
    private readonly Dictionary<SkylineBuilding, Drawing> _drawings = [];
    private double _end = -60;
    private int _sinceSign = 3;

    public double Parallax { get; } = parallax;

    public bool Far { get; } = far;

    /// <summary>I palazzi fra <paramref name="from"/> e <paramref name="to"/> (coordinate dello strato) con il loro disegno.</summary>
    public IEnumerable<(SkylineBuilding Building, Drawing Drawing)> Between(double from, double to, double pixelsPerDip)
    {
        while (_end < to)
            Add();
        foreach (var building in _buildings)
        {
            if (building.X + building.Width < from || building.X > to)
                continue;
            if (!_drawings.TryGetValue(building, out var drawing))
                _drawings[building] = drawing = MuchiArt.BuildingDrawing(building, Far, pixelsPerDip);
            yield return (building, drawing);
        }
    }

    /// <summary>Dimentica i palazzi già passati (una partita lunga non deve crescere per sempre).</summary>
    public void Forget(double before)
    {
        foreach (var gone in _buildings.Where(b => b.X + b.Width < before).ToList())
        {
            _buildings.Remove(gone);
            _drawings.Remove(gone);
        }
    }

    private void Add()
    {
        var width = Far ? 18 + (_random.NextDouble() * 26) : 22 + (_random.NextDouble() * 30);
        var height = Far ? 55 + (_random.NextDouble() * 60) : 45 + (_random.NextDouble() * 70);
        var step = _random.NextDouble() < 0.3 ? 4 + (_random.NextDouble() * 8) : 0;
        var sign = !Far && ++_sinceSign > 5 && width > 30 && _random.NextDouble() < 0.4;
        if (sign)
        {
            _sinceSign = 0;
            height = Math.Max(height, 105); // alta, così i tetti davanti non coprono l'insegna
            width = Math.Max(width, 34);
        }
        _buildings.Add(new SkylineBuilding(_end, width, height, step, sign, _random.Next()));
        _end += width + (Far ? _random.NextDouble() * 4 : 2 + (_random.NextDouble() * 8));
    }
}
