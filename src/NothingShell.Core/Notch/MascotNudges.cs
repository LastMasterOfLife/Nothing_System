using NothingShell.Core.Design;

namespace NothingShell.Core.Notch;

/// <summary>Perché la mascotte prende la parola (docs/21-mascotte.md).</summary>
public enum MascotNudgeKind
{
    /// <summary>Al ritorno, cosa è successo mentre eri via.</summary>
    WelcomeBack,

    /// <summary>Sei al computer da tanto senza una pausa.</summary>
    Break,

    /// <summary>Si è fatto tardi.</summary>
    Late,

    /// <summary>La CPU è al massimo da un po': le gira la testa, senza dire niente.</summary>
    Busy,
}

/// <summary>Qualcosa che la mascotte si segna se succede mentre sei via.</summary>
public enum MascotEvent
{
    Mail,
    AgentDone,
    TaskDone,
    TaskFailed,
}

/// <summary>
/// Cosa vuole dire la mascotte: <see cref="Gesture"/> è come ti chiama, <see cref="Text"/> cosa dice nell'avviso (null: solo il
/// gesto), <see cref="Page"/> la pagina che si apre passandoci sopra.
/// </summary>
public sealed record MascotNudge(MascotNudgeKind Kind, MascotMood Gesture, string? Text, NotchPage? Page = null);

/// <summary>
/// L'iniziativa della mascotte (docs/21-mascotte.md): decide quando ha qualcosa da dirti. Si segna cosa succede mentre sei via e
/// te lo dice al ritorno; dopo <see cref="BreakAfter"/> di lavoro di fila ti propone una pausa; con
/// <see cref="MascotInitiative.Often"/> ti dice anche quando si è fatto tardi e barcolla se la CPU resta al massimo. Fra una
/// cosa e l'altra lascia passare <see cref="QuietGap"/>. Deterministica: ora e inattività arrivano da fuori.
/// </summary>
public sealed class MascotNudges
{
    /// <summary>Senza mouse né tastiera da tanto si è via: la mascotte dorme e si segna cosa succede.</summary>
    public static readonly TimeSpan AwayAfter = MascotBrain.SleepAfter;

    /// <summary>Una pausa vera: tanto tempo senza toccare niente azzera il conto del lavoro di fila.</summary>
    public static readonly TimeSpan BreakIdle = TimeSpan.FromMinutes(5);

    public static readonly TimeSpan BreakAfter = TimeSpan.FromMinutes(50);

    /// <summary>Al ritorno aspetta di essersi svegliata del tutto prima di parlare.</summary>
    public static readonly TimeSpan WelcomeDelay = TimeSpan.FromSeconds(1.5);

    /// <summary>Il riassunto del ritorno vale per poco: dopo, le cose si sono già viste altrove.</summary>
    public static readonly TimeSpan WelcomeWindow = TimeSpan.FromMinutes(2);

    /// <summary>Dopo aver detto o fatto qualcosa da sé, tanto silenzio prima della prossima (il riassunto del ritorno no).</summary>
    public static readonly TimeSpan QuietGap = TimeSpan.FromMinutes(10);

    public const double BusyCpu = 0.9;
    public static readonly TimeSpan BusyAfter = TimeSpan.FromSeconds(20);

    /// <summary>Da quest'ora fino alle 5 si è fatto tardi.</summary>
    public static readonly TimeSpan LateFrom = new(23, 30, 0);

    private static readonly TimeSpan LateUntil = TimeSpan.FromHours(5);

    private readonly Dictionary<MascotEvent, int> _missed = [];
    private DateTime? _activeSince;
    private DateTime? _nextBreak;
    private bool _away;
    private DateTime? _back;
    private DateTime? _hotSince;
    private DateTime _quietUntil = DateTime.MinValue;
    private DateOnly? _toldLate;

    public MascotInitiative Level { get; set; }

    /// <summary>Ogni pochi secondi: l'ora locale e da quanto non si tocca mouse o tastiera.</summary>
    public void Observe(DateTime now, TimeSpan idle)
    {
        if (idle >= BreakIdle)
        {
            _activeSince = _nextBreak = null;
        }
        else if (_activeSince is null)
        {
            _activeSince = now - idle;
            _nextBreak = _activeSince + BreakAfter;
        }

        if (idle >= AwayAfter)
        {
            _away = true;
            _back = null;
        }
        else if (_away)
        {
            _away = false;
            _back = now - idle;
        }
    }

    /// <summary>È successo qualcosa: se sei via se lo segna, per dirtelo al ritorno.</summary>
    public void Noticed(MascotEvent what, int count = 1)
    {
        if (_away && Level != MascotInitiative.Off && count > 0)
            _missed[what] = _missed.GetValueOrDefault(what) + count;
    }

    /// <summary>La CPU a ogni campione, da 0 a 1.</summary>
    public void ObserveCpu(DateTime now, double cpu) => _hotSince = cpu >= BusyCpu ? _hotSince ?? now : null;

    /// <summary>Cosa dire adesso, se c'è. Chi lo riceve lo dice davvero: la prossima volta non torna.</summary>
    public MascotNudge? Next(DateTime now)
    {
        if (Level == MascotInitiative.Off || _away)
            return null;
        if (_back is { } back)
        {
            if (now - back < WelcomeDelay)
                return null;
            _back = null;
            var welcome = Welcome();
            _missed.Clear();
            if (welcome is not null && now - back < WelcomeWindow)
                return welcome;
        }
        if (now < _quietUntil)
            return null;
        if (_nextBreak is { } due && now >= due && _activeSince is { } since)
        {
            _nextBreak = now + BreakAfter;
            return Quiet(now, new(MascotNudgeKind.Break, MascotMood.Calling, $"SEI AL COMPUTER DA {Span(now - since)}: UNA PAUSA?"));
        }
        if (Level != MascotInitiative.Often)
            return null;
        if (Night(now) is { } night && night != _toldLate)
        {
            _toldLate = night;
            return Quiet(now, new(MascotNudgeKind.Late, MascotMood.Yawn, "SI È FATTO TARDI"));
        }
        if (_hotSince is { } hot && now - hot >= BusyAfter)
            return Quiet(now, new(MascotNudgeKind.Busy, MascotMood.Dizzy, null));
        return null;
    }

    private MascotNudge Quiet(DateTime now, MascotNudge nudge)
    {
        _quietUntil = now + QuietGap;
        return nudge;
    }

    /// <summary>Cosa è successo mentre eri via, e la pagina dove vederlo: prima la posta, poi gli agenti, poi i comandi.</summary>
    private MascotNudge? Welcome()
    {
        var mail = _missed.GetValueOrDefault(MascotEvent.Mail);
        var agents = _missed.GetValueOrDefault(MascotEvent.AgentDone);
        var done = _missed.GetValueOrDefault(MascotEvent.TaskDone);
        var failed = _missed.GetValueOrDefault(MascotEvent.TaskFailed);
        var parts = new List<string>();
        if (mail > 0)
            parts.Add($"{mail} MAIL");
        if (agents > 0)
            parts.Add(agents == 1 ? "1 AGENTE HA FINITO" : $"{agents} AGENTI HANNO FINITO");
        if (done > 0)
            parts.Add(done == 1 ? "1 COMANDO FINITO" : $"{done} COMANDI FINITI");
        if (failed > 0)
            parts.Add(failed == 1 ? "1 COMANDO FALLITO" : $"{failed} COMANDI FALLITI");
        if (parts.Count == 0)
            return null;
        NotchPage page = mail > 0 ? NotchPage.Mail : agents > 0 ? NotchPage.Agents : NotchPage.Work;
        return new(MascotNudgeKind.WelcomeBack, MascotMood.Calling, $"MENTRE ERI VIA: {string.Join(", ", parts)}", page);
    }

    /// <summary>La notte di <paramref name="now"/> (col giorno in cui è cominciata), se è tardi.</summary>
    private static DateOnly? Night(DateTime now) =>
        now.TimeOfDay >= LateFrom ? DateOnly.FromDateTime(now)
        : now.TimeOfDay < LateUntil ? DateOnly.FromDateTime(now.AddDays(-1))
        : null;

    /// <summary>50 MINUTI, 1 ORA, 1 ORA E 40: a passi di cinque minuti.</summary>
    private static string Span(TimeSpan span)
    {
        var minutes = (int)span.TotalMinutes / 5 * 5;
        var (hours, rest) = (minutes / 60, minutes % 60);
        var whole = hours == 1 ? "1 ORA" : $"{hours} ORE";
        return hours == 0 ? $"{minutes} MINUTI" : rest == 0 ? whole : $"{whole} E {rest}";
    }
}
