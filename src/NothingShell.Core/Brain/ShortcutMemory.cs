using System.Text.Json;
using System.Text.RegularExpressions;
using NothingShell.Core.Dev;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Brain;

/// <summary>Da dove viene una decisione, dal più veloce al più lento.</summary>
public enum DecisionSource
{
    Exact,
    Template,
    Similar,
    Model,
}

/// <param name="Tokens">Le parole della frase; in un modello, <c>{0}</c> è la parola che cambia.</param>
/// <param name="Command">Il comando; in un modello, <c>{0}</c> è il valore preso dalla frase.</param>
/// <param name="Successes">Esecuzioni riuscite dopo che qualcuno ha premuto Invio.</param>
/// <param name="Failures">Fallimenti di fila dall'ultima riuscita.</param>
public sealed record Shortcut(IReadOnlyList<string> Tokens, string Command, int Successes, int Failures, DateTimeOffset LastUsed, bool Seed = false)
{
    public bool IsTemplate => Tokens.Contains(ShortcutMemory.Slot);
}

public sealed record BrainDecision(string Command, DecisionSource Source, CommandRisk Risk, bool RunNow, int Successes);

/// <summary>
/// Le scorciatoie imparate (docs/15-comandi-cervello.md): frase → comando, senza passare dal modello. Una risposta
/// esatta costa una ricerca in un dizionario. Il modello propone, ma si impara solo ciò che qualcuno ha confermato con
/// Invio e che è riuscito; quando due scorciatoie differiscono per una parola, diventano un modello con un valore.
/// </summary>
public sealed partial class ShortcutMemory
{
    public const string Slot = "{0}";

    /// <summary>Riuscite necessarie perché un comando di sola lettura parta senza Invio.</summary>
    public const int TrustedAfter = 3;

    /// <summary>Fallimenti di fila dopo cui una scorciatoia si dimentica.</summary>
    public const int ForgottenAfter = 3;

    public const int MaxEntries = 2000;

    private const double SimilarThreshold = 0.75;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly object _gate = new();
    private readonly Dictionary<string, Shortcut> _exact = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Shortcut> _templates = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _index = new(StringComparer.Ordinal);
    private readonly HashSet<string> _forgotten = new(StringComparer.Ordinal);
    private readonly string? _path;

    /// <param name="path">File JSON; null per una memoria solo in RAM.</param>
    public ShortcutMemory(string? path = null, IEnumerable<Shortcut>? seeds = null)
    {
        _path = path;
        if (path is not null && AtomicFile.TryReadAllText(path) is { } json)
            Load(json);
        foreach (var seed in seeds ?? ShortcutSeeds.All)
        {
            var key = IntentText.Key(seed.Tokens);
            if (!_exact.ContainsKey(key) && !_forgotten.Contains(key))
                Put(seed);
        }
    }

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "brain", "shortcuts.json");

    /// <summary>Scorciatoie imparate dall'uso, senza quelle di serie mai usate.</summary>
    public int LearnedCount
    {
        get
        {
            lock (_gate)
                return _exact.Values.Concat(_templates.Values).Count(s => !s.Seed);
        }
    }

    /// <summary>Decisione senza modello, oppure null. Non scrive nulla ed è pensata per stare sotto il millisecondo.</summary>
    public BrainDecision? Decide(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        var tokens = IntentText.Tokens(input);
        if (tokens.Count == 0)
            return null;
        lock (_gate)
        {
            if (_exact.TryGetValue(IntentText.Key(tokens), out var exact))
                return Decision(exact.Command, DecisionSource.Exact, exact);
            if (MatchTemplate(tokens) is { } filled)
                return filled;
            return Nearest(tokens, 1).FirstOrDefault() is { Score: >= SimilarThreshold } near
                ? Decision(near.Shortcut.Command, DecisionSource.Similar, near.Shortcut)
                : null;
        }
    }

    /// <summary>Le scorciatoie più simili alla frase, come esempi per il modello: più se ne imparano, meglio decide.</summary>
    public IReadOnlyList<Shortcut> Examples(string input, int count)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
            return Nearest(IntentText.Tokens(input), count).Where(n => n.Score > 0).Select(n => n.Shortcut).ToArray();
    }

    /// <summary>
    /// Esito di un comando confermato con Invio per la frase <paramref name="input"/>. Un comando diverso da quello
    /// noto è una correzione e lo sostituisce. Restituisce true se la memoria è cambiata.
    /// </summary>
    public bool Record(string input, string command, bool succeeded, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(command);
        command = command.Trim();
        var tokens = IntentText.Tokens(input);
        if (tokens.Count == 0 || command.Length == 0 || command.Contains('\n', StringComparison.Ordinal))
            return false;
        lock (_gate)
        {
            var key = IntentText.Key(tokens);
            _exact.TryGetValue(key, out var known);
            if (!succeeded)
                return known is not null && string.Equals(known.Command, command, StringComparison.Ordinal) && Failed(key, known, now);
            if (!CanLearn(input, command))
                return false;

            var same = known is not null && string.Equals(known.Command, command, StringComparison.Ordinal);
            var learned = new Shortcut(known?.Tokens ?? tokens, command, same ? known!.Successes + 1 : 1, 0, now, same && known!.Seed);
            Put(learned);
            _forgotten.Remove(key);
            Generalize(learned);
            Trim();
            Save();
            return true;
        }
    }

    /// <summary>Scorciatoia chiesta a mano: vale come già confermata, tranne che per ciò che è pericoloso.</summary>
    public bool Remember(string input, string command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(command);
        var tokens = IntentText.Tokens(input);
        command = command.Trim();
        if (tokens.Count == 0 || command.Length == 0 || command.Contains('\n', StringComparison.Ordinal) || !CanLearn(input, command))
            return false;
        lock (_gate)
        {
            Put(new Shortcut(tokens, command, TrustedAfter, 0, now));
            _forgotten.Remove(IntentText.Key(tokens));
            Save();
            return true;
        }
    }

    public bool Forget(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            var key = IntentText.Key(IntentText.Tokens(input));
            if (!_exact.TryGetValue(key, out var known))
                return false;
            Drop(key, known);
            Save();
            return true;
        }
    }

    /// <summary>Dimentica tutto ciò che è stato imparato; le scorciatoie di serie tornano com'erano.</summary>
    public void Clear(IEnumerable<Shortcut>? seeds = null)
    {
        lock (_gate)
        {
            _exact.Clear();
            _templates.Clear();
            _index.Clear();
            _forgotten.Clear();
            foreach (var seed in seeds ?? ShortcutSeeds.All)
                Put(seed);
            Save();
        }
    }

    /// <summary>Non si impara ciò che è pericoloso né ciò che contiene chiavi o password: il file è in chiaro.</summary>
    private static bool CanLearn(string input, string command) =>
        CommandSafety.Classify(command) != CommandRisk.Dangerous &&
        ClipClassifier.SecretName(input) is null && ClipClassifier.SecretName(command) is null &&
        !SecretWord().IsMatch(input) && !SecretWord().IsMatch(command);

    [GeneratedRegex(@"password|passwd|pwd\s*=|secret|token|api[-_ ]?key|ConvertTo-SecureString|-Credential\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SecretWord();

    private static BrainDecision Decision(string command, DecisionSource source, Shortcut from)
    {
        var risk = CommandSafety.Classify(command);
        var run = source != DecisionSource.Similar && risk == CommandRisk.ReadOnly &&
                  from.Successes >= TrustedAfter && from.Failures == 0;
        return new BrainDecision(command, source, risk, run, from.Successes);
    }

    private BrainDecision? MatchTemplate(IReadOnlyList<string> tokens)
    {
        for (var i = 0; i < tokens.Count; i++)
        {
            if (!SafeValue().IsMatch(tokens[i]))
                continue;
            var pattern = tokens.Select((t, at) => at == i ? Slot : t);
            if (_templates.TryGetValue(IntentText.Key(pattern), out var template))
                return Decision(template.Command.Replace(Slot, tokens[i], StringComparison.Ordinal), DecisionSource.Template, template);
        }
        return null;
    }

    /// <summary>Un valore finisce dentro un comando: niente spazi, simboli della shell, né trattino iniziale (un parametro).</summary>
    [GeneratedRegex(@"^[\p{L}\p{N}_][\p{L}\p{N}._-]*$", RegexOptions.CultureInvariant)]
    private static partial Regex SafeValue();

    private (Shortcut Shortcut, double Score)[] Nearest(IReadOnlyList<string> tokens, int count)
    {
        var mine = tokens.Select(IntentText.Stem).ToHashSet(StringComparer.Ordinal);
        var candidates = mine.Where(_index.ContainsKey).SelectMany(t => _index[t]).ToHashSet(StringComparer.Ordinal);
        return candidates
            .Select(key => (Shortcut: _exact[key], Score: Similarity(mine, _exact[key].Tokens)))
            .OrderByDescending(c => c.Score).ThenByDescending(c => c.Shortcut.Successes)
            .ThenByDescending(c => c.Shortcut.LastUsed).Take(count).ToArray();
    }

    private static double Similarity(HashSet<string> mine, IReadOnlyList<string> tokens)
    {
        var other = tokens.Select(IntentText.Stem).ToHashSet(StringComparer.Ordinal);
        var common = other.Count(mine.Contains);
        return common == 0 ? 0 : (double)common / (mine.Count + other.Count - common);
    }

    /// <summary>
    /// "processi chrome → Get-Process chrome" e "processi firefox → Get-Process firefox" fanno nascere
    /// "processi {0} → Get-Process {0}". Servono due esempi: da uno solo non si sa quale parola è il valore.
    /// </summary>
    private void Generalize(Shortcut learned)
    {
        if (learned.Successes < 1)
            return;
        foreach (var other in _exact.Values.ToArray())
        {
            if (ReferenceEquals(other, learned) || other.Seed || other.Tokens.Count != learned.Tokens.Count)
                continue;
            var different = Enumerable.Range(0, other.Tokens.Count)
                .Where(i => !string.Equals(other.Tokens[i], learned.Tokens[i], StringComparison.Ordinal)).ToArray();
            if (different.Length != 1)
                continue;
            var at = different[0];
            if (!SafeValue().IsMatch(other.Tokens[at]) || !SafeValue().IsMatch(learned.Tokens[at]))
                continue;
            var a = Parameterize(learned.Command, learned.Tokens[at]);
            if (a is null || !string.Equals(a, Parameterize(other.Command, other.Tokens[at]), StringComparison.Ordinal))
                continue;
            var pattern = learned.Tokens.Select((t, i) => i == at ? Slot : t).ToArray();
            _templates[IntentText.Key(pattern)] = new Shortcut(pattern, a, learned.Successes + other.Successes, 0, learned.LastUsed);
        }
    }

    /// <summary>Il comando con il valore al posto di <c>{0}</c>; null se il valore non c'è o è il comando stesso.</summary>
    private static string? Parameterize(string command, string value)
    {
        var word = new Regex($@"(?<![\w.-]){Regex.Escape(value)}(?![\w.-])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromMilliseconds(50));
        var first = word.Match(command);
        if (!first.Success || first.Index == 0)
            return null;
        return word.Replace(command, Slot);
    }

    private bool Failed(string key, Shortcut known, DateTimeOffset now)
    {
        var failures = known.Failures + 1;
        // Quelle di serie falliscono per il contesto (git fuori da un repository): perdono la fiducia ma restano.
        if (failures >= ForgottenAfter && !known.Seed)
            Drop(key, known);
        else
            Put(known with { Failures = failures, LastUsed = now });
        Save();
        return true;
    }

    private void Put(Shortcut shortcut)
    {
        if (shortcut.IsTemplate)
        {
            _templates[IntentText.Key(shortcut.Tokens)] = shortcut;
            return;
        }
        var key = IntentText.Key(shortcut.Tokens);
        _exact[key] = shortcut;
        foreach (var token in shortcut.Tokens.Select(IntentText.Stem))
        {
            if (!_index.TryGetValue(token, out var keys))
                _index[token] = keys = new HashSet<string>(StringComparer.Ordinal);
            keys.Add(key);
        }
    }

    private void Drop(string key, Shortcut known)
    {
        _exact.Remove(key);
        foreach (var token in known.Tokens.Select(IntentText.Stem))
        {
            if (_index.TryGetValue(token, out var keys) && keys.Remove(key) && keys.Count == 0)
                _index.Remove(token);
        }
        // Una scorciatoia cancellata di proposito non deve rinascere dalle esistenti: il modello che la generalizzava sparisce.
        foreach (var template in _templates.Where(t => Parameterizes(t.Value, known)).Select(t => t.Key).ToArray())
            _templates.Remove(template);
        if (known.Seed)
            _forgotten.Add(key);
    }

    private static bool Parameterizes(Shortcut template, Shortcut known)
    {
        if (template.Tokens.Count != known.Tokens.Count)
            return false;
        var at = Enumerable.Range(0, template.Tokens.Count).First(i => template.Tokens[i] == Slot);
        return !template.Tokens.Where((t, i) => i != at && t != known.Tokens[i]).Any() &&
               string.Equals(template.Command.Replace(Slot, known.Tokens[at], StringComparison.Ordinal), known.Command, StringComparison.OrdinalIgnoreCase);
    }

    private void Trim()
    {
        var excess = _exact.Count - MaxEntries;
        if (excess <= 0)
            return;
        foreach (var (key, shortcut) in _exact.Where(e => !e.Value.Seed).OrderBy(e => e.Value.LastUsed).Take(excess).ToArray())
            Drop(key, shortcut);
    }

    private void Load(string json)
    {
        try
        {
            var file = JsonSerializer.Deserialize<MemoryFile>(json, Options);
            foreach (var key in file?.Forgotten ?? [])
                _forgotten.Add(key);
            foreach (var shortcut in file?.Shortcuts ?? [])
            {
                if (shortcut is { Tokens.Count: > 0, Command.Length: > 0 })
                    Put(shortcut);
            }
        }
        catch (JsonException)
        {
            // file rovinato: si riparte dalle scorciatoie di serie
        }
    }

    private void Save()
    {
        if (_path is null)
            return;
        try
        {
            var shortcuts = _exact.Values.Concat(_templates.Values).ToArray();
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(new MemoryFile(shortcuts, _forgotten.ToArray()), Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // resta in memoria fino al prossimo salvataggio riuscito
        }
    }

    private sealed record MemoryFile(IReadOnlyList<Shortcut> Shortcuts, IReadOnlyList<string>? Forgotten);
}
