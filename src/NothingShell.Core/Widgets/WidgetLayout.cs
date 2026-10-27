using NothingShell.Core.Notch;

namespace NothingShell.Core.Widgets;

public enum WidgetKind
{
    Clock,
    Battery,
    System,
    Calendar,
}

public readonly record struct Rect2(double X, double Y, double Width, double Height)
{
    public double Right => X + Width;
    public double Bottom => Y + Height;

    public bool IntersectsWith(Rect2 other) =>
        X < other.Right && other.X < Right && Y < other.Bottom && other.Y < Bottom;
}

public sealed record WidgetPlacement(WidgetKind Kind, double X, double Y)
{
    public Rect2 Bounds => new(X, Y, WidgetSizes.For(Kind).Width, WidgetSizes.For(Kind).Height);
}

public static class WidgetSizes
{
    public const double Cell = 168;
    public const double Gap = 16;

    public static Size2 For(WidgetKind kind) => kind switch
    {
        WidgetKind.Clock => new(Cell * 2 + Gap, Cell),
        WidgetKind.Battery or WidgetKind.System or WidgetKind.Calendar => new(Cell, Cell),
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null),
    };
}

/// <summary>Disposizione dei widget sul desktop (docs/04-widgets.md).</summary>
public static class WidgetLayout
{
    public const double SnapGrid = 8;
    public const double Margin = 24;

    /// <summary>
    /// Colonna in alto a destra dell'area di lavoro:
    /// orologio, poi batteria + sistema affiancati, poi calendario.
    /// </summary>
    public static IReadOnlyList<WidgetPlacement> Default(Rect2 workArea)
    {
        var step = WidgetSizes.Cell + WidgetSizes.Gap;
        var left = workArea.Right - Margin - WidgetSizes.For(WidgetKind.Clock).Width;
        var top = workArea.Y + Margin;
        return
        [
            new(WidgetKind.Clock, Snap(left), Snap(top)),
            new(WidgetKind.Battery, Snap(left), Snap(top + step)),
            new(WidgetKind.System, Snap(left + step), Snap(top + step)),
            new(WidgetKind.Calendar, Snap(left), Snap(top + 2 * step)),
        ];
    }

    public static double Snap(double value) => Math.Round(value / SnapGrid) * SnapGrid;

    /// <summary>Riporta il widget interamente dentro <paramref name="bounds"/> (se ci entra).</summary>
    public static WidgetPlacement ClampInto(WidgetPlacement placement, Rect2 bounds)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var size = WidgetSizes.For(placement.Kind);
        var x = Math.Max(bounds.X, Math.Min(placement.X, bounds.Right - size.Width));
        var y = Math.Max(bounds.Y, Math.Min(placement.Y, bounds.Bottom - size.Height));
        return placement with { X = x, Y = y };
    }

    /// <summary>Aggancia alla griglia e tiene dentro i limiti: usata al termine di un trascinamento.</summary>
    public static WidgetPlacement Settle(WidgetPlacement placement, Rect2 bounds)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return ClampInto(placement with { X = Snap(placement.X), Y = Snap(placement.Y) }, bounds);
    }

    /// <summary>Come <see cref="Settle(WidgetPlacement, Rect2)"/> per un rettangolo qualsiasi (widget dei plugin).</summary>
    public static (double X, double Y) Settle(Rect2 rect, Rect2 bounds)
    {
        var x = Math.Max(bounds.X, Math.Min(Snap(rect.X), bounds.Right - rect.Width));
        var y = Math.Max(bounds.Y, Math.Min(Snap(rect.Y), bounds.Bottom - rect.Height));
        return (x, y);
    }

    /// <summary>
    /// Primo spazio libero sulla griglia dei widget (passo cella + spazio), cercando per colonne
    /// da destra a sinistra e dall'alto in basso. Se è tutto occupato: in alto a sinistra.
    /// </summary>
    public static (double X, double Y) FindFreeSlot(Size2 size, IEnumerable<Rect2> occupied, Rect2 workArea)
    {
        var taken = occupied.ToArray();
        var step = WidgetSizes.Cell + WidgetSizes.Gap;
        for (var right = workArea.Right - Margin; right - size.Width >= workArea.X + Margin; right -= step)
            for (var top = workArea.Y + Margin; top + size.Height <= workArea.Bottom - Margin; top += step)
            {
                var candidate = new Rect2(Snap(right - size.Width), Snap(top), size.Width, size.Height);
                if (!taken.Any(candidate.IntersectsWith))
                    return (candidate.X, candidate.Y);
            }
        return (Snap(workArea.X + Margin), Snap(workArea.Y + Margin));
    }

    /// <summary>
    /// Completa una disposizione salvata: aggiunge i widget mancanti nella posizione predefinita,
    /// scarta i duplicati e riporta tutto dentro lo schermo.
    /// </summary>
    public static IReadOnlyList<WidgetPlacement> Normalize(
        IEnumerable<WidgetPlacement> saved, Rect2 workArea, Rect2 screenBounds)
    {
        var byKind = new Dictionary<WidgetKind, WidgetPlacement>();
        foreach (var p in saved)
            if (Enum.IsDefined(p.Kind))
                byKind.TryAdd(p.Kind, p);
        foreach (var p in Default(workArea))
            byKind.TryAdd(p.Kind, p);
        return Enum.GetValues<WidgetKind>().Select(k => ClampInto(byKind[k], screenBounds)).ToArray();
    }
}
