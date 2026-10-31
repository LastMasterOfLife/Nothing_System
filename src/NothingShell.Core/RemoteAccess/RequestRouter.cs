using System.Text.RegularExpressions;
using NothingShell.Core.Brain;

namespace NothingShell.Core.RemoteAccess;

/// <summary>
/// Quanto una richiesta corrisponde a una scorciatoia, dalla più sicura alla meno. Solo <see cref="Exact"/> può partire
/// con un Invio: "computer di paola" non è "computer di paolo" anche se le parole si somigliano.
/// </summary>
public enum MatchQuality
{
    /// <summary>Le stesse parole, in qualsiasi ordine.</summary>
    Exact,

    /// <summary>Tutte le parole chieste sono nella scorciatoia, che ne ha altre: "luca" per "computer di luca".</summary>
    Contained,

    /// <summary>Le stesse parole a parte un errore di battitura, o singolare e plurale: "compiuter di luca".</summary>
    Close,

    /// <summary>Qualche parola in comune, ma almeno una chiesta non c'è: "computer di marco" per "computer di luca".</summary>
    Partial,
}

/// <param name="Missing">Le parole chieste che nella scorciatoia non ci sono.</param>
public sealed record RequestMatch(MatchQuality Quality, IReadOnlyList<string> Missing);

/// <summary>Confronto tra le parole di una richiesta e quelle di una scorciatoia.</summary>
public static class RequestMatcher
{
    public static RequestMatch? Compare(IEnumerable<string> request, IEnumerable<string> shortcut)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(shortcut);
        var wanted = request.Distinct(StringComparer.Ordinal).ToArray();
        var known = shortcut.Distinct(StringComparer.Ordinal).ToArray();
        if (wanted.Length == 0 || known.Length == 0)
            return null;

        var common = wanted.Where(known.Contains).ToArray();
        var free = known.Except(common, StringComparer.Ordinal).ToList();
        var unmatched = wanted.Except(common, StringComparer.Ordinal).ToArray();
        if (unmatched.Length == 0)
            return new RequestMatch(free.Count == 0 ? MatchQuality.Exact : MatchQuality.Contained, []);

        var missing = new List<string>();
        foreach (var word in unmatched)
        {
            var near = free.FirstOrDefault(k => IsClose(word, k));
            if (near is null)
                missing.Add(word);
            else
                free.Remove(near);
        }
        if (missing.Count == 0)
            return new RequestMatch(MatchQuality.Close, []);
        return missing.Count < wanted.Length ? new RequestMatch(MatchQuality.Partial, missing) : null;
    }

    /// <summary>Due parole diverse che potrebbero essere la stessa. Le cifre contano sempre: server1 non è server2.</summary>
    public static bool IsClose(string a, string b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        if (a.Any(char.IsDigit) || b.Any(char.IsDigit))
            return false;
        if (string.Equals(IntentText.Stem(a), IntentText.Stem(b), StringComparison.Ordinal))
            return true;
        var shorter = Math.Min(a.Length, b.Length);
        var allowed = shorter < 4 ? 0 : shorter < 8 ? 1 : 2;
        return allowed > 0 && Math.Abs(a.Length - b.Length) <= allowed && EditDistance(a, b) <= allowed;
    }

    /// <summary>Le stesse parole danno la stessa chiave, in qualsiasi ordine.</summary>
    public static string Key(IEnumerable<string> tokens) => string.Join(' ', tokens.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal));

    /// <summary>Damerau-Levenshtein: lettere scambiate ("compuetr") contano un errore solo.</summary>
    private static int EditDistance(string a, string b)
    {
        var before = new int[b.Length + 1];
        var previous = new int[b.Length + 1];
        var current = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++)
            previous[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            current[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(current[j - 1] + 1, previous[j] + 1), previous[j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    current[j] = Math.Min(current[j], before[j - 2] + 1);
            }
            (before, previous, current) = (previous, current, before);
        }
        return previous[b.Length];
    }
}

/// <summary>
/// Una frase letta come richiesta di collegamento: "collegati al pc di luca" chiede il computer [computer, luca]. I verbi
/// si tolgono solo all'inizio, così "computer del collega" resta un nome.
/// </summary>
public sealed partial record ConnectionPhrase(bool IsConnection, IReadOnlyList<string> Tokens, string? SuggestedName, string? SuggestedAddress)
{
    /// <summary>Verbi che bastano a dire che si chiede un collegamento.</summary>
    private static readonly HashSet<string> ConnectVerbs = new(StringComparer.Ordinal)
    {
        "collega", "collegami", "collegati", "collegarmi", "collegarti", "collegarsi", "collegare", "connetti", "connettimi",
        "connettiti", "connettermi", "connetterti", "connettersi", "connettere", "connessione", "collegamento", "connect",
    };

    /// <summary>Verbi che dicono un collegamento solo se si nomina anche un computer: "apri il browser" non lo è.</summary>
    private static readonly HashSet<string> OpenVerbs = new(StringComparer.Ordinal)
    {
        "apri", "aprimi", "accedi", "entra", "vai", "avvia", "lancia", "open", "access", "login",
    };

    private static readonly HashSet<string> RemoteWords = new(StringComparer.Ordinal) { "remoto", "remote", "rdp", "mstsc" };

    /// <summary>Articoli apostrofati che <see cref="IntentText"/> non conosce: "computer dell'ufficio".</summary>
    private static readonly HashSet<string> Elided = new(StringComparer.Ordinal) { "dell", "sull", "nell", "coll", "all", "quell" };

    private static readonly HashSet<string> Particles = new(StringComparer.Ordinal)
    {
        "di", "del", "dello", "della", "dei", "degli", "delle", "dell", "da", "in", "e", "a", "al", "su", "sul",
    };

    public static ConnectionPhrase Parse(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var tokens = IntentText.Tokens(text).Where(t => !Elided.Contains(t)).ToList();
        var remote = tokens.RemoveAll(RemoteWords.Contains) > 0;
        if (remote)
            tokens.RemoveAll(t => t is "desktop");
        var strong = remote;
        var open = false;
        while (tokens.Count > 0 && (IsConnectVerb(tokens[0]) || OpenVerbs.Contains(tokens[0])))
        {
            strong |= IsConnectVerb(tokens[0]);
            open |= OpenVerbs.Contains(tokens[0]);
            tokens.RemoveAt(0);
        }
        // Una frase di sole parole di riempimento ("collegati a me") non nomina nessun computer.
        var named = tokens.Select(t => t == "pc" ? "computer" : t).ToArray();
        var isConnection = strong || (open && named.Contains("computer"));
        return new ConnectionPhrase(isConnection, named, isConnection ? SuggestName(text) : null, isConnection ? FindAddress(text) : null);
    }

    private static bool IsConnectVerb(string word) =>
        ConnectVerbs.Contains(word) || (word.Length >= 6 && ConnectVerbs.Any(v => RequestMatcher.IsClose(word, v)));

    /// <summary>"collegati al computer di luca" → "Computer di Luca".</summary>
    private static string? SuggestName(string text)
    {
        var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ',', ';', '!', '?', ':', '"', '\'', '(', ')')).Where(w => w.Length > 0).ToList();
        var lower = words.Select(w => w.ToLowerInvariant()).ToList();
        for (var i = lower.Count - 1; i >= 0; i--)
        {
            var isDesktop = lower[i] == "desktop" && i + 1 < lower.Count && RemoteWords.Contains(lower[i + 1]);
            if (RemoteWords.Contains(lower[i]) || isDesktop)
            {
                words.RemoveAt(i);
                lower.RemoveAt(i);
            }
        }
        var start = 0;
        while (start < lower.Count && (IsConnectVerb(lower[start]) || OpenVerbs.Contains(lower[start]) || IntentText.IsFiller(lower[start])))
            start++;
        if (start == lower.Count)
            return null;
        var named = words.Skip(start).Select((w, i) => Capitalize(w, i == 0));
        return string.Join(' ', named);
    }

    private static string Capitalize(string word, bool first)
    {
        var lower = word.ToLowerInvariant();
        if (lower == "pc")
            return "PC";
        if (!first && Particles.Contains(lower.Split('\'')[0]))
            return lower;
        return word.Length == 0 ? word : char.ToUpperInvariant(word[0]) + word[1..];
    }

    private static string? FindAddress(string text) =>
        Ipv4Address().Match(text) is { Success: true } match ? match.Value : null;

    [GeneratedRegex(@"(?<![\w.])(?:\d{1,3}\.){3}\d{1,3}(?::\d{1,5})?(?![\w.])", RegexOptions.CultureInvariant)]
    private static partial Regex Ipv4Address();
}

/// <summary>Una scorciatoia che corrisponde alla richiesta: un computer o un comando imparato.</summary>
/// <param name="Title">Il nome: "Computer di Luca", "ram libera".</param>
/// <param name="Detail">Che cosa farebbe: l'indirizzo o il comando.</param>
public sealed record RequestOption(string Title, string Detail, MatchQuality Quality, IReadOnlyList<string> Missing, RemoteComputer? Computer = null, string? Command = null);

/// <summary>Che cosa fare premendo Invio nel campo del notch.</summary>
public enum RequestAction
{
    /// <summary>Una sola scorciatoia corrisponde esattamente: si usa quella.</summary>
    Use,

    /// <summary>Più scorciatoie, o una solo simile: decide chi ha chiesto.</summary>
    Choose,

    /// <summary>Si chiede un computer che non c'è: il modulo per crearlo.</summary>
    NewComputer,

    /// <summary>Non è una scorciatoia: passa all'assistente (docs/15-comandi-cervello.md).</summary>
    Ask,
}

/// <summary>Le scorciatoie per una richiesta, dalla migliore, e ciò che serve per crearne una nuova.</summary>
public sealed record RequestRoute(string Input, IReadOnlyList<RequestOption> Options, ConnectionPhrase Connection)
{
    /// <summary>
    /// La scorciatoia da usare senza chiedere: esiste solo se una sola corrisponde esattamente. Due uguali, o una solo
    /// simile, significano scegliere.
    /// </summary>
    public RequestOption? Sure => Options.Count(o => o.Quality == MatchQuality.Exact) == 1 ? Options[0] : null;

    /// <summary>Quelle che possono essere ciò che si chiede.</summary>
    public IReadOnlyList<RequestOption> Likely => Options.Where(o => o.Quality != MatchQuality.Partial).ToArray();

    /// <summary>Quelle che hanno parole in comune ma non sono ciò che si chiede: non si propongono mai per prime.</summary>
    public IReadOnlyList<RequestOption> Different => Options.Where(o => o.Quality == MatchQuality.Partial).ToArray();

    /// <summary>Con <paramref name="choose"/> (Ctrl+Invio) si sceglie anche quando la risposta sarebbe sicura.</summary>
    public RequestAction ActionFor(bool choose = false)
    {
        if (choose && Options.Count > 0)
            return RequestAction.Choose;
        if (Sure is not null)
            return RequestAction.Use;
        if (Likely.Count > 0)
            return RequestAction.Choose;
        if (!Connection.IsConnection)
            return RequestAction.Ask;
        // "computer di marco" quando c'è "computer di luca": forse è una svista, ma si propone prima un computer nuovo.
        return Different.Any(o => o.Computer is not null) ? RequestAction.Choose : RequestAction.NewComputer;
    }

    /// <summary>La riga sotto il campo del notch: quale scorciatoia userebbe Invio, prima di premerlo.</summary>
    public string Preview
    {
        get
        {
            if (Sure is { } sure)
            {
                var others = Likely.Count - 1;
                return $"→ {sure.Title} · {sure.Detail}{(others > 0 ? $" · +{others}, Ctrl+Invio per scegliere" : string.Empty)}";
            }
            if (Likely is { Count: > 0 } likely)
            {
                var names = string.Join(", ", likely.Take(2).Select(o => o.Title));
                var more = likely.Count > 2 ? $" +{likely.Count - 2}" : string.Empty;
                return $"{names}{more} · Invio per scegliere";
            }
            if (Connection.IsConnection)
                return Connection.SuggestedName is { } name ? $"Nuovo computer: {name} · Invio" : "Nuovo computer · Invio";
            return "Nessuna scorciatoia · Invio per chiedere all'assistente";
        }
    }
}

/// <summary>Da una frase alle scorciatoie che le corrispondono. Pura e veloce: gira a ogni tasto nel campo del notch.</summary>
public static class RequestRouter
{
    public const int MaxLikely = 5;
    public const int MaxDifferent = 3;

    public static RequestRoute Route(string input, IReadOnlyList<RemoteComputer> computers, ShortcutMemory memory)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(computers);
        ArgumentNullException.ThrowIfNull(memory);

        var phrase = ConnectionPhrase.Parse(input);
        var options = new List<RequestOption>();
        foreach (var computer in computers)
        {
            // "collegati" senza nomi: vanno bene tutti, ma bisogna scegliere.
            var match = phrase.Tokens.Count == 0
                ? phrase.IsConnection ? new RequestMatch(MatchQuality.Contained, []) : null
                : Best(phrase.Tokens, computer);
            if (match is not null)
                options.Add(new RequestOption(computer.Name, computer.Address, match.Quality, match.Missing, computer));
        }

        var commands = new Dictionary<string, RequestOption>(StringComparer.Ordinal);
        if (memory.Decide(input) is { Source: DecisionSource.Exact or DecisionSource.Template } decided)
        {
            var title = string.Join(' ', IntentText.Tokens(input));
            commands[decided.Command] = new RequestOption(title, decided.Command, MatchQuality.Exact, [], Command: decided.Command);
        }
        var tokens = IntentText.Tokens(input);
        foreach (var shortcut in memory.Phrases)
        {
            if (RequestMatcher.Compare(tokens, shortcut.Tokens) is not { } match)
                continue;
            // Molte frasi portano allo stesso comando: se ne mostra una, la più vicina.
            if (commands.TryGetValue(shortcut.Command, out var known) && known.Quality <= match.Quality)
                continue;
            commands[shortcut.Command] = new RequestOption(string.Join(' ', shortcut.Tokens), shortcut.Command, match.Quality, match.Missing, Command: shortcut.Command);
        }
        options.AddRange(commands.Values);

        var ordered = options
            .OrderBy(o => o.Quality)
            .ThenByDescending(o => o.Computer is not null)
            .ThenByDescending(o => o.Computer?.Successes ?? 0)
            .ThenByDescending(o => o.Computer?.LastUsed ?? default)
            .ThenBy(o => o.Title, StringComparer.Ordinal)
            .ToArray();
        var likely = ordered.Where(o => o.Quality != MatchQuality.Partial).Take(MaxLikely);
        var different = ordered.Where(o => o.Quality == MatchQuality.Partial).Take(MaxDifferent);
        return new RequestRoute(input, likely.Concat(different).ToArray(), phrase);
    }

    private static RequestMatch? Best(IReadOnlyList<string> tokens, RemoteComputer computer)
    {
        RequestMatch? best = null;
        foreach (var candidate in RemoteComputerStore.Names(computer))
        {
            if (RequestMatcher.Compare(tokens, candidate) is { } match && (best is null || match.Quality < best.Quality))
                best = match;
        }
        return best;
    }
}
