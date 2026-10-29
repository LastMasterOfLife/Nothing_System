using System.Text.Json;
using System.Text.Json.Nodes;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Phone;

/// <summary>Rete cellulare dal plugin <c>kdeconnect.connectivity_report</c>: tipo (4G, 5G…) e tacche 0–4.</summary>
public readonly record struct PhoneSignal(string NetworkType, int Strength);

/// <summary>Quello che il telefono condivide e la pagina Telefono del notch sa mostrare (docs/13-telefono.md).</summary>
public static class PhoneInfo
{
    public const string ConnectivityReport = "kdeconnect.connectivity_report";
    public const string ConnectivityReportRequest = "kdeconnect.connectivity_report.request";

    /// <summary>
    /// Cosa il telefono dice di mandare (<c>outgoingCapabilities</c> della sua identità), in italiano. Nell'app KDE
    /// Connect si spengono i plugin uno per uno: è lì che si dà o si toglie il permesso.
    /// </summary>
    public static readonly (string Capability, string Label)[] Shared =
    [
        (KdeConnect.Battery, "batteria"),
        (ConnectivityReport, "segnale"),
        (KdeConnect.Notification, "notifiche"),
        (KdeConnect.Telephony, "chiamate"),
        (KdeConnect.Clipboard, "appunti"),
        (KdeConnect.Share, "file e link"),
        (KdeConnect.Ping, "ping"),
    ];

    public static IReadOnlyList<string> SharedLabels(IEnumerable<string> outgoingCapabilities)
    {
        ArgumentNullException.ThrowIfNull(outgoingCapabilities);
        var set = outgoingCapabilities.ToHashSet(StringComparer.Ordinal);
        return Shared.Where(s => set.Contains(s.Capability)).Select(s => s.Label).ToArray();
    }

    /// <summary>Le capacità dell'identità; vuoto se il telefono non le dichiara (versioni vecchie).</summary>
    public static IReadOnlyList<string> Capabilities(KdePacket identity, string key)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return identity.Body[key] is JsonArray array
            ? array.OfType<JsonValue>().Where(v => v.GetValueKind() == JsonValueKind.String).Select(v => v.GetValue<string>()).ToArray()
            : [];
    }

    /// <summary>
    /// <c>{"signalStrengths": {"6": {"networkType": "LTE", "signalStrength": 3}}}</c>: con due SIM si tiene quella col
    /// segnale migliore. Null se il pacchetto non è un rapporto o non ha SIM.
    /// </summary>
    public static PhoneSignal? ReadSignal(KdePacket packet)
    {
        ArgumentNullException.ThrowIfNull(packet);
        if (packet.Type != ConnectivityReport || packet.Body["signalStrengths"] is not JsonObject sims)
            return null;
        PhoneSignal? best = null;
        foreach (var (_, node) in sims)
        {
            if (node is not JsonObject sim)
                continue;
            var type = sim["networkType"] is JsonValue t && t.GetValueKind() == JsonValueKind.String ? t.GetValue<string>() : "?";
            var strength = sim["signalStrength"] is JsonValue s && s.TryGetValue<int>(out var v) ? Math.Clamp(v, 0, 4) : 0;
            if (best is null || strength > best.Value.Strength)
                best = new PhoneSignal(NetworkName(type), strength);
        }
        return best;
    }

    /// <summary>"LTE" → "4G", "NR" → "5G": come lo scrive la barra del telefono.</summary>
    public static string NetworkName(string type) => type.ToUpperInvariant() switch
    {
        "NR" or "5G" => "5G",
        "LTE" or "4G" => "4G",
        "UMTS" or "HSPA" or "HSPA+" or "HSDPA" or "HSUPA" or "3G" => "3G",
        "EDGE" or "GPRS" or "GSM" or "CDMA" or "2G" => "2G",
        "" or "UNKNOWN" or "?" => "--",
        var other => other,
    };
}

/// <summary>Un file arrivato dal telefono e salvato nella cartella Download.</summary>
public sealed record ReceivedFile(string Path, string From, long Size, DateTimeOffset At);

/// <summary>
/// Ultimi file ricevuti dal telefono (solo percorso, dimensione e ora, mai il contenuto) in
/// <c>phone-files.json</c>: la pagina Telefono li mostra anche dopo un riavvio.
/// </summary>
public sealed class ReceivedFiles(string path)
{
    public const int Capacity = 20;

    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };

    private List<ReceivedFile> _files = [];

    public static string DefaultPath { get; } = System.IO.Path.Combine(AtomicFile.DataFolder, "phone-files.json");

    /// <summary>Dal più recente.</summary>
    public IReadOnlyList<ReceivedFile> Files => _files;

    public void Load()
    {
        try
        {
            _files = AtomicFile.TryReadAllText(path) is { } json
                ? JsonSerializer.Deserialize<List<ReceivedFile>>(json, Options) ?? []
                : [];
        }
        catch (JsonException)
        {
            _files = [];
        }
    }

    public void Add(ReceivedFile file)
    {
        ArgumentNullException.ThrowIfNull(file);
        _files = [file, .. _files.Where(f => !string.Equals(f.Path, file.Path, StringComparison.OrdinalIgnoreCase)).Take(Capacity - 1)];
        Save();
    }

    /// <summary>Toglie dall'elenco i file cancellati o spostati.</summary>
    public bool Prune(Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(exists);
        var kept = _files.Where(f => exists(f.Path)).ToList();
        if (kept.Count == _files.Count)
            return false;
        _files = kept;
        Save();
        return true;
    }

    private void Save() => AtomicFile.WriteAllText(path, JsonSerializer.Serialize(_files, Options));
}

/// <summary>File che sta arrivando: per la riga con l'avanzamento.</summary>
public sealed record IncomingFile(string Name, long Received, long Size)
{
    public double Progress => Size > 0 ? Math.Clamp((double)Received / Size, 0, 1) : 0;
}
