using System.Text;

namespace NothingShell.Core.Bar;

/// <summary>
/// Categorie personalizzate delle jump list (es. "Recent Folders" di VS Code), che Windows salva in
/// <c>Recent\CustomDestinations\&lt;hash dell'AppID&gt;.customDestinations-ms</c> e che nessuna API permette di leggere.
/// Formato del file (versione 2):
/// <code>
/// u32 versione, u32 categorie, u32 riservato
/// per categoria: u32 tipo
///   0 personalizzata: u16 lunghezza, nome UTF-16, u32 voci, voci
///   1 nota (recenti/frequenti di Windows): u32 id
///   2 attività ("Nuova finestra"): u32 voci, voci
///   poi u32 0xBABFFBAB
/// voce: CLSID (16 byte) + oggetto serializzato; qui si leggono solo i collegamenti (CLSID ShellLink).
/// </code>
/// </summary>
public static class CustomDestinations
{
    public const uint Footer = 0xBABFFBAB;
    public static readonly Guid ShellLinkClsid = new("00021401-0000-0000-C000-000000000046");

    /// <summary>
    /// Legge il collegamento serializzato che parte da <c>offset</c>: la voce (null se da scartare) e i byte occupati.
    /// Null se non si riesce a leggerlo: senza la sua lunghezza il resto del file non si può seguire.
    /// </summary>
    public delegate (JumpItem? Item, int Length)? LinkReader(byte[] data, int offset);

    /// <summary>Nome del file della jump list di un'app: CRC-64 dell'AppID in maiuscolo (UTF-16), in esadecimale.</summary>
    public static string FileName(string appId)
    {
        ArgumentNullException.ThrowIfNull(appId);
        const ulong polynomial = 0x92C64265D32139A4;
        var crc = ulong.MaxValue;
        foreach (var b in Encoding.Unicode.GetBytes(appId.ToUpperInvariant()))
        {
            crc ^= b;
            for (var i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ polynomial : crc >> 1;
        }
        return $"{crc:x16}.customDestinations-ms";
    }

    /// <summary>
    /// Categorie personalizzate con le loro voci, nell'ordine del file. Le attività e le categorie di Windows
    /// non sono incluse. Un file troncato o con voci sconosciute dà le categorie lette fin lì.
    /// </summary>
    public static IReadOnlyList<JumpSection> Parse(byte[] data, LinkReader readLink)
    {
        ArgumentNullException.ThrowIfNull(data);
        ArgumentNullException.ThrowIfNull(readLink);
        var sections = new List<JumpSection>();
        var pos = 0;
        if (!TryU32(data, ref pos, out var version) || version != 2 || !TryU32(data, ref pos, out var categories))
            return sections;
        pos += 4;
        for (var c = 0u; c < categories; c++)
        {
            if (!TryU32(data, ref pos, out var type))
                return sections;
            switch (type)
            {
                case 0:
                    if (pos + 2 > data.Length)
                        return sections;
                    var length = BitConverter.ToUInt16(data, pos) * 2;
                    pos += 2;
                    if (pos + length > data.Length)
                        return sections;
                    var name = Encoding.Unicode.GetString(data, pos, length);
                    pos += length;
                    var items = ReadEntries(data, ref pos, readLink);
                    if (items is null)
                        return sections;
                    if (items.Count > 0)
                        sections.Add(new JumpSection(name, items));
                    break;
                case 1:
                    pos += 4;
                    break;
                case 2:
                    if (ReadEntries(data, ref pos, readLink) is null)
                        return sections;
                    break;
                default:
                    return sections;
            }
            if (!TryU32(data, ref pos, out var footer) || footer != Footer)
                return sections;
        }
        return sections;
    }

    private static List<JumpItem>? ReadEntries(byte[] data, ref int pos, LinkReader readLink)
    {
        if (!TryU32(data, ref pos, out var count))
            return null;
        var items = new List<JumpItem>();
        for (var i = 0u; i < count; i++)
        {
            if (pos + 16 > data.Length || new Guid(data.AsSpan(pos, 16)) != ShellLinkClsid)
                return null;
            pos += 16;
            if (readLink(data, pos) is not { Length: > 0 } link || pos + link.Length > data.Length)
                return null;
            pos += link.Length;
            if (link.Item is { } item)
                items.Add(item);
        }
        return items;
    }

    private static bool TryU32(byte[] data, ref int pos, out uint value)
    {
        value = 0;
        if (pos + 4 > data.Length)
            return false;
        value = BitConverter.ToUInt32(data, pos);
        pos += 4;
        return true;
    }
}
