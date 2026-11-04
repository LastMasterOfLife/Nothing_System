using NothingShell.Core.Design;

namespace NothingShell.Core.Notch;

/// <summary>Rigidità e smorzamento di una molla (massa 1).</summary>
public readonly record struct SpringTune(double Stiffness, double Damping);

/// <summary>
/// Il movimento del notch, misurato su Nusa fotogramma per fotogramma (docs/20-stile-nusa.md): due molle diverse per aprire e
/// chiudere, un'attesa prima di aprire durante la quale la pillola si gonfia, chiusura immediata.
/// </summary>
public static class NotchMotion
{
    /// <summary>Apertura: ζ ≈ 0,63, supera la misura del ~7% a ~260 ms e si assesta in ~470 ms.</summary>
    public static readonly SpringTune Open = new(250, 20);

    /// <summary>Chiusura: ζ ≈ 0,65, scende sotto la misura di riposo del ~12% e rimbalza indietro in ~480 ms.</summary>
    public static readonly SpringTune Close = new(290, 22);

    /// <summary>Il gonfiore mentre si aspetta di aprire: morbido, senza rimbalzo.</summary>
    public static readonly SpringTune Peek = new(170, 26);

    /// <summary>Morbida: ζ ≈ 0,95, nessun rimbalzo, si posa in ~0,6 s.</summary>
    public static readonly (SpringTune Open, SpringTune Close, SpringTune Peek) Soft = (new(70, 16), new(85, 18), new(80, 18));

    /// <summary>Scattante: ζ ≈ 0,55, supera la misura del ~13% a ~175 ms e si posa in ~0,4 s.</summary>
    public static readonly (SpringTune Open, SpringTune Close, SpringTune Peek) Snappy = (new(460, 23.5), new(520, 25), new(300, 34));

    /// <summary>Col mouse sopra, quanto si aspetta prima di aprire: passarci sopra di sfuggita non apre niente.</summary>
    public static readonly TimeSpan EnterDelay = TimeSpan.FromMilliseconds(290);

    /// <summary>Quanto si gonfia la pillola durante l'attesa.</summary>
    public const double PeekScale = 1.07;

    /// <summary>Le tre molle di un carattere (pagina Aspetto).</summary>
    public static (SpringTune Open, SpringTune Close, SpringTune Peek) Of(NotchFeel feel) => feel switch
    {
        NotchFeel.Soft => Soft,
        NotchFeel.Snappy => Snappy,
        _ => (Open, Close, Peek),
    };

    /// <summary>Molla per andare da <paramref name="from"/> a <paramref name="to"/>: più grande è un'apertura, più piccolo una chiusura.</summary>
    public static SpringTune For(Size2 from, Size2 to, bool peeking, NotchFeel feel = NotchFeel.Nusa)
    {
        var tunes = Of(feel);
        return peeking ? tunes.Peek
            : to.Width * to.Height >= from.Width * from.Height ? tunes.Open
            : tunes.Close;
    }
}
