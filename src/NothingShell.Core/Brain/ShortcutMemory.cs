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
/// <param name="Created">Quando è nata; null per quelle di serie e per quelle imparate prima che si salvasse la data.</param>
public sealed record Shortcut(IReadOnlyList<string> Tokens, string Command, int Successes, int Failures, DateTimeOffset LastUsed, bool Seed = false, DateTimeOffset? Created = null)
{
    public bool IsTemplate => Tokens.Contains(ShortcutMemory.Slot);

    /// <summary>La frase, com'è ridotta a parole chiave.</summary>
    public string Phrase => string.Join(' ', Tokens);
}

public sealed record BrainDecision(string Command, DecisionSource Source, CommandRisk Risk, bool RunNow, int Successes);

/// <summary>
/// Le scorciatoie imparate (docs/15-comandi-cervello.md): frase → comando, senza passare dal modello. Una risposta
/// esatta costa una ricerca in un dizionario. Il modello propone, ma si impara solo ciò che qualcuno ha confermato con
/// Invio e che è riuscito; quando due scorciatoie differiscono per una parola, diventano un modello con un valore.
/// Un comando critico può diventare una scorciatoia, ma resta da confermare ogni volta: il rischio si ricalcola a ogni
/// decisione, non si generalizza e non vale come frase simile.
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
    private const int MaxExampleCandidates = 50;

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

    /// <summary>Le scorciatoie imparate, con i modelli con un valore e senza quelle di serie: prima le più recenti.</summary>
    public IReadOnlyList<Shortcut> Learned
    {
        get
        {
            lock (_gate)
                return _exact.Values.Concat(_templates.Values).Where(s => !s.Seed).OrderByDescending(s => s.Created ?? s.LastUsed).ToArray();
        }
    }

    /// <summary>
    /// Le scorciatoie di serie ancora valide, per argomento e per comando. Non ci sono quelle dimenticate né quelle che l'utente
    /// ha corretto con un altro comando: quelle ormai sono sue e stanno fra le imparate.
    /// </summary>
    public IReadOnlyList<BuiltInSection> BuiltIn
    {
        get
        {
            lock (_gate)
            {
                var sections = new List<BuiltInSection>();
                foreach (var section in ShortcutSeeds.Sections)
                {
                    var active = section.Shortcuts
                        .Select(seed => (Seed: seed, Current: _exact.GetValueOrDefault(IntentText.Key(seed.Tokens))))
                        .Where(p => p.Current is { Seed: true })
                        .ToArray();
                    var commands = active.GroupBy(p => p.Current!.Command, StringComparer.Ordinal).Select(group =>
                    {
                        // Due frasi con la stessa chiave sono una scorciatoia sola: i suoi usi si contano una volta.
                        var stored = group.Select(p => p.Current!).Distinct().ToArray();
                        return new BuiltInShortcut(
                            group.Select(p => ShortcutSeeds.Spoken(p.Seed)).ToArray(),
                            stored[0] with
                            {
                                Successes = TrustedAfter + stored.Sum(s => Math.Max(0, s.Successes - TrustedAfter)),
                                Failures = stored.Max(s => s.Failures),
                                LastUsed = stored.Max(s => s.LastUsed),
                            });
                    }).ToArray();
                    if (commands.Length > 0)
                        sections.Add(new BuiltInSection(section.Title, commands));
                }
                return sections;
            }
        }
    }

    /// <summary>Le scorciatoie con una frase fissa, senza i modelli con un valore: per confrontarle con una richiesta.</summary>
    public IReadOnlyList<Shortcut> Phrases
    {
        get
        {
            lock (_gate)
                return _exact.Values.ToArray();
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
            // Una frase che somiglia non basta per proporre un comando critico.
            return Nearest(tokens, 1).FirstOrDefault() is { Score: >= SimilarThreshold } near && CommandSafety.Classify(near.Shortcut.Command) != CommandRisk.Dangerous
                ? Decision(near.Shortcut.Command, DecisionSource.Similar, near.Shortcut)
                : null;
        }
    }

    /// <summary>Le scorciatoie più simili alla frase, come esempi per il modello: più se ne imparano, meglio decide.</summary>
    public IReadOnlyList<Shortcut> Examples(string input, int count)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
            return Nearest(IntentText.Tokens(input), MaxExampleCandidates).Where(n => n.Score > 0 && CommandSafety.Classify(n.Shortcut.Command) != CommandRisk.Dangerous)
                .Select(n => n.Shortcut).Take(count).ToArray();
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
            var learned = new Shortcut(known?.Tokens ?? tokens, command, same ? known!.Successes + 1 : 1, 0, now, same && known!.Seed, same ? known!.Created : now);
            Put(learned);
            _forgotten.Remove(key);
            Generalize(learned);
            Trim();
            Save();
            return true;
        }
    }

    /// <summary>
    /// Scorciatoia chiesta a mano: vale come già confermata, ma un comando critico chiederà comunque conferma. Con
    /// <c>{0}</c> nella frase e nel comando nasce un modello con un valore ("apri progetto {0}" → "code C:\dev\{0}").
    /// </summary>
    public bool Remember(string input, string command, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(command);
        if (WhyNot(input, command) is not null)
            return false;
        var tokens = PhraseTokens(input);
        command = command.Trim();
        lock (_gate)
        {
            var key = IntentText.Key(tokens);
            var known = (tokens.Contains(Slot) ? _templates : _exact).GetValueOrDefault(key);
            var kept = known is { Seed: false } && string.Equals(known.Command, command, StringComparison.Ordinal);
            Put(new Shortcut(tokens, command, TrustedAfter, 0, now, Created: kept ? known!.Created : now));
            _forgotten.Remove(key);
            Save();
            return true;
        }
    }

    /// <summary>
    /// Perché una scorciatoia scritta a mano non si può tenere, in parole per le impostazioni; null se va bene.
    /// Sono le stesse regole di <see cref="Remember"/>.
    /// </summary>
    public static string? WhyNot(string phrase, string command)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        ArgumentNullException.ThrowIfNull(command);
        command = command.Trim();
        var tokens = PhraseTokens(phrase);
        var slots = tokens.Count(t => t == Slot);
        if (tokens.Count == 0)
            return "Scrivi la frase da dire.";
        if (command.Length == 0)
            return "Scrivi il comando da eseguire.";
        if (command.Contains('\n', StringComparison.Ordinal) || command.Contains('\r', StringComparison.Ordinal))
            return "Il comando sta su una riga sola: più comandi si separano con ;";
        if (!CanLearn(phrase, command))
            return "Non si salvano chiavi, token o password: il file delle scorciatoie è in chiaro.";
        if (slots > 1 || Count(command, Slot) > 1)
            return $"Al massimo un {Slot}, nella frase e nel comando.";
        if (slots == 1 && tokens.Count == 1)
            return $"Oltre a {Slot} serve almeno una parola nella frase.";
        if ((slots == 1) != command.Contains(Slot, StringComparison.Ordinal))
            return slots == 1 ? $"La frase ha {Slot}: mettilo anche nel comando, dove va il valore." : $"Il comando ha {Slot}: mettilo anche nella frase, dove si dice il valore.";
        if (slots == 1 && CommandSafety.Classify(command) == CommandRisk.Dangerous)
            return $"Un comando critico non può avere {Slot}: scrivi una scorciatoia per ogni valore.";
        return null;
    }

    /// <summary>La scorciatoia che risponde già esattamente a questa frase (anche un modello con <c>{0}</c>), o null.</summary>
    public Shortcut? Find(string phrase)
    {
        ArgumentNullException.ThrowIfNull(phrase);
        var tokens = PhraseTokens(phrase);
        if (tokens.Count == 0)
            return null;
        lock (_gate)
            return (tokens.Contains(Slot) ? _templates : _exact).GetValueOrDefault(IntentText.Key(tokens));
    }

    /// <summary>Dimentica la scorciatoia di questa frase; con <c>{0}</c> il modello con un valore.</summary>
    public bool Forget(string input)
    {
        ArgumentNullException.ThrowIfNull(input);
        lock (_gate)
        {
            var tokens = PhraseTokens(input);
            var key = IntentText.Key(tokens);
            if (tokens.Contains(Slot))
            {
                if (!_templates.Remove(key))
                    return false;
                Save();
                return true;
            }
            if (!_exact.TryGetValue(key, out var known))
                return false;
            Drop(key, known);
            Save();
            return true;
        }
    }

    /// <summary>Le parole della frase, con <c>{0}</c> dove l'utente l'ha scritto (le parentesi altrimenti si perdono).</summary>
    private static List<string> PhraseTokens(string phrase)
    {
        var tokens = new List<string>();
        var parts = phrase.Split(Slot);
        for (var i = 0; i < parts.Length; i++)
        {
            if (i > 0)
                tokens.Add(Slot);
            tokens.AddRange(IntentText.Tokens(parts[i]).Where(t => !IntentText.IsFiller(t) || parts.Length == 1));
        }
        return tokens;
    }

    private static int Count(string text, string part)
    {
        var count = 0;
        for (var at = text.IndexOf(part, StringComparison.Ordinal); at >= 0; at = text.IndexOf(part, at + part.Length, StringComparison.Ordinal))
            count++;
        return count;
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

    /// <summary>Non si impara ciò che contiene chiavi o password: il file è in chiaro.</summary>
    public static bool CanLearn(string input, string command) =>
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
        if (learned.Successes < 1 || CommandSafety.Classify(learned.Command) == CommandRisk.Dangerous)
            return; // "cancella {0}" non deve poter nascere
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
            var patternKey = IntentText.Key(pattern);
            var born = _templates.TryGetValue(patternKey, out var existing) ? existing.Created : learned.LastUsed;
            _templates[patternKey] = new Shortcut(pattern, a, learned.Successes + other.Successes, 0, learned.LastUsed, Created: born);
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
