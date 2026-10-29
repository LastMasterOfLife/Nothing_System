namespace NothingShell.Core.Notch;

public enum PhoneEventKind
{
    /// <summary>Notifica di un'app del telefono (SMS, WhatsApp…).</summary>
    Message,

    IncomingCall,

    MissedCall,
}

/// <summary>Chiamata o notifica del telefono arrivata da Collegamento al telefono. Resta solo in memoria.</summary>
public sealed record PhoneEvent(PhoneEventKind Kind, string Title, string Text, DateTimeOffset Received)
{
    /// <summary>App Android che l'ha mandata, se la notifica lo dice.</summary>
    public string? App { get; init; }
}

/// <summary>
/// Notifiche di Collegamento al telefono (docs/13-telefono.md): Windows le riceve dal telefono Android e le mostra
/// come notifiche sue; la shell le legge come fa per la posta e le porta nel notch.
/// </summary>
public static class PhoneLink
{
    /// <summary>AppUserModelID di Collegamento al telefono (anche il servizio "dispositivi collegati" di Windows 11).</summary>
    public const string AppId = "Microsoft.YourPhone_8wekyb3d8bbwe!App";

    private static readonly string[] IncomingCall = ["chiamata in arrivo", "incoming call", "sta chiamando", "is calling"];
    private static readonly string[] MissedCall = ["chiamata persa", "chiamate perse", "missed call"];
    private const int AppNameMaxLength = 30;

    public static bool IsPhoneLink(string? appId) =>
        appId is not null
        && (appId.StartsWith("Microsoft.YourPhone_", StringComparison.OrdinalIgnoreCase)
            || appId.StartsWith("MicrosoftWindows.CrossDevice_", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Chiamate: una riga dice che tipo è, un'altra chi chiama (nome o numero), in qualsiasi ordine.
    /// Messaggi: titolo (mittente o app), prima riga del testo; con tre righe o più l'ultima breve è l'app.
    /// </summary>
    public static PhoneEvent? Parse(IReadOnlyList<string> texts, DateTimeOffset received)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var lines = texts.Select(t => t.Trim()).Where(t => t.Length > 0).ToArray();
        if (lines.Length == 0)
            return null;

        foreach (var (keywords, kind) in new[] { (IncomingCall, PhoneEventKind.IncomingCall), (MissedCall, PhoneEventKind.MissedCall) })
        {
            var marker = Array.FindIndex(lines, l => keywords.Any(k => l.Contains(k, StringComparison.OrdinalIgnoreCase)));
            if (marker < 0)
                continue;
            var caller = lines.Where((_, i) => i != marker).FirstOrDefault()
                         ?? Caller(lines[marker], keywords); // "Mario Rossi sta chiamando"
            return new PhoneEvent(kind, caller, lines[marker], received);
        }

        string? app = null;
        if (lines.Length >= 3 && lines[^1].Length <= AppNameMaxLength && !lines[^1].Contains('\n'))
        {
            app = lines[^1];
            lines = lines[..^1];
        }
        return lines.Length == 1
            ? new PhoneEvent(PhoneEventKind.Message, app ?? string.Empty, FirstLine(lines[0]), received) { App = app }
            : new PhoneEvent(PhoneEventKind.Message, FirstLine(lines[0]), FirstLine(lines[1]), received) { App = app };
    }

    /// <summary>Riga per l'avviso del notch: "Mario: ci vediamo alle 8".</summary>
    public static string Summary(PhoneEvent phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return phone.Title.Length == 0 ? phone.Text
            : phone.Text.Length == 0 ? phone.Title
            : $"{phone.Title}: {phone.Text}";
    }

    private static string Caller(string line, string[] keywords)
    {
        foreach (var k in keywords)
        {
            var at = line.IndexOf(k, StringComparison.OrdinalIgnoreCase);
            if (at >= 0)
                return (line[..at] + line[(at + k.Length)..]).Trim(' ', ':', '-', '·', '•');
        }
        return string.Empty;
    }

    private static string FirstLine(string text) => text.Split('\n', 2)[0].Trim();
}
