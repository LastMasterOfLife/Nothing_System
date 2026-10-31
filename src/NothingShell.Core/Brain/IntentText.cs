using System.Globalization;
using System.Text;

namespace NothingShell.Core.Brain;

/// <summary>
/// Riduce una frase alle parole che contano: "mi mostri quanta RAM è libera?" e "ram libera" diventano la stessa
/// chiave, senza accenti, articoli e verbi di richiesta. I verbi che agiscono (chiudi, cancella) e "non" restano.
/// </summary>
public static class IntentText
{
    private static readonly HashSet<string> Filler = new(StringComparer.Ordinal)
    {
        // italiano
        "il", "lo", "la", "i", "gli", "le", "l", "un", "uno", "una", "di", "del", "dello", "della", "dei", "degli", "delle",
        "d", "a", "al", "allo", "alla", "ai", "agli", "alle", "da", "dal", "dalla", "dai", "dalle", "in", "nel", "nello",
        "nella", "nei", "negli", "nelle", "su", "sul", "sulla", "sui", "sulle", "per", "con", "e", "ed", "o", "che", "c",
        "mi", "me", "ti", "ci", "ce", "si", "ne", "mio", "mia", "miei", "mie", "questo", "questa", "questi", "queste",
        "qui", "ora", "adesso", "e'", "sono", "ho", "ha", "hai", "abbiamo", "sta", "stanno", "qual", "quale", "quali",
        "quanto", "quanta", "quanti", "quante", "come", "cosa", "puoi", "potresti", "vorrei", "voglio", "favore", "grazie",
        "mostra", "mostrami", "mostri", "fammi", "vedere", "fai", "dammi", "dimmi", "dai", "elenca", "elencami", "visualizza",
        "controlla", "verifica", "trova", "trovami", "cerca", "calcola", "tutti", "tutte", "lista", "elenco",
        // inglese
        "the", "an", "of", "to", "is", "are", "am", "my", "me", "this", "these", "that", "here", "now", "and", "or", "for",
        "with", "on", "at", "what", "whats", "which", "how", "much", "many", "can", "could", "you", "please", "thanks", "i",
        "want", "show", "list", "get", "give", "tell", "see", "check", "find", "all",
    };

    /// <summary>Parole significative nell'ordine in cui compaiono; se sono tutte di riempimento, tutte.</summary>
    public static IReadOnlyList<string> Tokens(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var words = Words(text);
        var kept = words.Where(w => !Filler.Contains(w)).ToArray();
        return kept.Length > 0 ? kept : words;
    }

    /// <summary>Chiave senza ordine: "ram libera" e "libera ram" sono la stessa richiesta.</summary>
    public static string Key(IEnumerable<string> tokens) => string.Join(' ', tokens.Select(Stem).Order(StringComparer.Ordinal));

    /// <summary>Radice grezza: toglie la vocale finale per far coincidere singolare e plurale (processo, processi).</summary>
    public static string Stem(string token)
    {
        ArgumentNullException.ThrowIfNull(token);
        return token.Length > 4 && "aeio".Contains(token[^1], StringComparison.Ordinal) ? token[..^1] : token;
    }

    private static string[] Words(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (var c in text.Normalize(NormalizationForm.FormD))
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) == UnicodeCategory.NonSpacingMark)
                continue;
            builder.Append(char.IsLetterOrDigit(c) || c is '.' or '_' or '-' or ':' or '\\' or '/' ? char.ToLowerInvariant(c) : ' ');
        }
        return builder.ToString().Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Select(w => w.Trim('.', ':', '-')).Where(w => w.Length > 0).ToArray();
    }
}
