using System.Text.Json;
using NothingShell.Core.Storage;

namespace NothingShell.Core.Phone;

/// <summary>Telefono associato: il certificato (DER in Base64) lo riconosce alle connessioni successive.</summary>
public sealed record TrustedPhone(string DeviceId, string Name, string Certificate);

/// <summary>
/// Identità della shell nel protocollo e telefoni associati (<c>phone.json</c>). La chiave privata del certificato
/// non è qui: il PFX sta accanto, cifrato con una password custodita nel Gestore credenziali di Windows.
/// </summary>
public sealed record PhoneTrust(string DeviceId, IReadOnlyList<TrustedPhone> Phones)
{
    public TrustedPhone? Find(string deviceId) => Phones.FirstOrDefault(p => p.DeviceId == deviceId);

    public PhoneTrust With(TrustedPhone phone)
    {
        ArgumentNullException.ThrowIfNull(phone);
        return this with { Phones = [.. Phones.Where(p => p.DeviceId != phone.DeviceId), phone] };
    }

    public PhoneTrust Without(string deviceId) => this with { Phones = Phones.Where(p => p.DeviceId != deviceId).ToArray() };
}

public sealed class PhoneTrustStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "phone.json");

    public string FilePath { get; } = path;

    public static string Serialize(PhoneTrust trust) => JsonSerializer.Serialize(trust, Options);

    /// <summary>File mancante o rotto: nuova identità, nessun telefono associato.</summary>
    public static PhoneTrust Deserialize(string? json)
    {
        try
        {
            if (json is not null && JsonSerializer.Deserialize<PhoneTrust>(json, Options) is { } trust
                && KdeConnect.IsValidDeviceId(trust.DeviceId))
                return trust with { Phones = (trust.Phones ?? []).Where(p => KdeConnect.IsValidDeviceId(p.DeviceId)).ToArray() };
        }
        catch (JsonException)
        {
            // si riparte da zero
        }
        return new PhoneTrust(KdeConnect.NewDeviceId(), []);
    }

    public PhoneTrust Load() => Deserialize(AtomicFile.TryReadAllText(FilePath));

    public void Save(PhoneTrust trust) => AtomicFile.WriteAllText(FilePath, Serialize(trust));
}
