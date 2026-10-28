namespace NothingShell.Core.Access;

/// <summary>Testi della card di accesso a partire dal nome dell'account.</summary>
public static class AccessIdentity
{
    /// <summary>Iniziali (al massimo 2) delle parole che iniziano con una lettera.</summary>
    public static string Initials(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var letters = displayName
            .Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Where(w => char.IsLetter(w[0]))
            .Select(w => char.ToUpperInvariant(w[0]))
            .Take(2)
            .ToArray();
        return letters.Length > 0 ? new string(letters) : "?";
    }

    /// <summary>Prima parola del nome visualizzato.</summary>
    public static string FirstName(string displayName)
    {
        ArgumentNullException.ThrowIfNull(displayName);
        var first = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first ?? string.Empty;
    }

    /// <summary>Saluto in base all'ora: neutro, senza genere.</summary>
    public static string Greeting(int hour) => hour switch
    {
        >= 5 and < 13 => "Buongiorno",
        >= 13 and < 18 => "Buon pomeriggio",
        >= 18 and < 23 => "Buonasera",
        _ => "Buonanotte",
    };

    public static string Caption(AccessCaption caption, string displayName, int hour) => caption switch
    {
        AccessCaption.Goodbye => "A presto",
        AccessCaption.Locked => "Bloccato",
        _ => FirstName(displayName) is { Length: > 0 } name ? $"{Greeting(hour)}, {name}" : Greeting(hour),
    };
}
