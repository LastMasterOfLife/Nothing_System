using System.Text.Json;
using NothingShell.Core.Brain;
using NothingShell.Core.Storage;

namespace NothingShell.Core.RemoteAccess;

/// <summary>
/// Le scorciatoie dei computer remoti, in <c>%USERPROFILE%\.nothing-shell\brain\computers.json</c>. Nel file ci sono solo
/// nome, indirizzo e utente: le password restano nel Gestore credenziali di Windows.
/// </summary>
public sealed class RemoteComputerStore
{
    private const int MaxAliases = 20;

    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    private readonly string? _path;
    private readonly List<RemoteComputer> _computers = [];

    /// <param name="path">File JSON; null per una memoria solo in RAM.</param>
    public RemoteComputerStore(string? path = null)
    {
        _path = path;
        if (path is not null && AtomicFile.TryReadAllText(path) is { } json)
            Load(json);
    }

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "brain", "computers.json");

    public IReadOnlyList<RemoteComputer> All => _computers.ToArray();

    /// <summary>Le frasi che indicano un computer: il nome e quelle confermate.</summary>
    public static IEnumerable<IReadOnlyList<string>> Names(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        yield return NameTokens(computer.Name);
        foreach (var alias in computer.Aliases ?? [])
            yield return alias.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    /// <summary>Perché il computer non si può salvare, o null.</summary>
    public string? Problem(RemoteComputer computer, RemoteComputer? replacing = null)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var tokens = NameTokens(computer.Name);
        if (tokens.Count == 0)
            return "Il nome deve contenere almeno una parola.";
        var key = RequestMatcher.Key(tokens);
        return _computers.Where(c => !IsSame(c, replacing)).Any(c => Names(c).Any(n => RequestMatcher.Key(n) == key))
            ? $"C'è già una scorciatoia che si chiama così: {computer.Name}."
            : null;
    }

    /// <summary>Aggiunge o sostituisce un computer; restituisce il problema, o null.</summary>
    public string? Save(RemoteComputer computer, RemoteComputer? replacing = null)
    {
        if (Problem(computer, replacing) is { } problem)
            return problem;
        var tokens = NameTokens(computer.Name);

        // Una frase che bastava finché il computer era uno solo ora può indicare anche questo: si torna a chiedere.
        for (var i = 0; i < _computers.Count; i++)
        {
            if (IsSame(_computers[i], replacing) || _computers[i].Aliases is not { Count: > 0 } aliases)
                continue;
            var kept = aliases.Where(a => !a.Split(' ', StringSplitOptions.RemoveEmptyEntries).All(tokens.Contains)).ToArray();
            if (kept.Length != aliases.Count)
                _computers[i] = _computers[i] with { Aliases = kept };
        }

        var at = replacing is null ? -1 : _computers.FindIndex(c => IsSame(c, replacing));
        if (at >= 0)
            _computers[at] = computer;
        else
            _computers.Add(computer);
        Persist();
        return null;
    }

    public bool Remove(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (_computers.RemoveAll(c => IsSame(c, computer)) == 0)
            return false;
        Persist();
        return true;
    }

    /// <summary>
    /// Connessione riuscita. Se ci si è arrivati scegliendo da una frase diversa dal nome, quella frase diventa un altro
    /// nome del computer e la prossima volta non si chiede più: la scelta è di chi ha premuto Invio, non del programma.
    /// </summary>
    public void RecordSuccess(RemoteComputer computer, string? phrase, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(computer);
        var at = _computers.FindIndex(c => IsSame(c, computer));
        if (at < 0)
            return;
        var known = _computers[at];
        var aliases = known.Aliases ?? [];
        if (phrase is not null && ShortcutMemory.CanLearn(phrase, phrase) && ConnectionPhrase.Parse(phrase).Tokens is { Count: > 0 } tokens)
        {
            var key = RequestMatcher.Key(tokens);
            var taken = _computers.Any(c => Names(c).Any(n => RequestMatcher.Key(n) == key));
            if (!taken && aliases.Count < MaxAliases)
                aliases = [.. aliases, key];
        }
        _computers[at] = known with { Aliases = aliases, Successes = known.Successes + 1, LastUsed = now };
        Persist();
    }

    private static IReadOnlyList<string> NameTokens(string name)
    {
        var tokens = ConnectionPhrase.Parse(name).Tokens;
        return tokens.Count > 0 ? tokens : IntentText.Tokens(name);
    }

    private static bool IsSame(RemoteComputer a, RemoteComputer? b) =>
        b is not null && string.Equals(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);

    private void Load(string json)
    {
        try
        {
            foreach (var computer in JsonSerializer.Deserialize<RemoteComputer[]>(json, Options) ?? [])
            {
                // Il file si può modificare a mano: un indirizzo non valido non deve arrivare alla riga di comando.
                if (computer is { Name.Length: > 0 } && RemoteDesktop.ParseAddress(computer.Host, out _, out _) is null)
                    _computers.Add(computer);
            }
        }
        catch (JsonException)
        {
            // file rovinato: nessuna scorciatoia, si ricreano quando servono
        }
    }

    private void Persist()
    {
        if (_path is null)
            return;
        try
        {
            AtomicFile.WriteAllText(_path, JsonSerializer.Serialize(_computers, Options));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // resta in memoria fino al prossimo salvataggio riuscito
        }
    }
}
