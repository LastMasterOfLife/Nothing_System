using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;
using NothingShell.Core.Notch;

namespace NothingShell.Core.Dev;

public enum ClipKind
{
    Text,
    Secret,
    Jwt,
    Json,
    Code,
    Color,
    Timestamp,
    Url,
    Path,
}

/// <summary>Un colore letto dagli appunti, canali da 0 a 255.</summary>
public readonly record struct ClipColor(byte R, byte G, byte B, byte A)
{
    public string Hex => A == 255 ? $"#{R:X2}{G:X2}{B:X2}" : $"#{R:X2}{G:X2}{B:X2}{A:X2}";

    public string Rgb => A == 255
        ? $"rgb({R}, {G}, {B})"
        : string.Create(CultureInfo.InvariantCulture, $"rgba({R}, {G}, {B}, {Math.Round(A / 255.0, 2)})");
}

/// <summary>Header e payload di un JWT (JSON indentato) e la scadenza da <c>exp</c>.</summary>
public sealed record JwtToken(string Header, string Payload, DateTimeOffset? Expires);

/// <summary>Cosa è stato copiato (docs/12-sviluppo.md): nell'ordine della tabella della spec, il primo che vale.</summary>
public static partial class ClipClassifier
{
    public const int MaxLength = 100_000;

    private static readonly (string Name, Regex Pattern)[] Secrets =
    [
        ("CHIAVE PRIVATA", PemKey()),
        ("CHIAVE GITHUB", new(@"\b(?<s>gh[pousr]_[A-Za-z0-9]{36,}|github_pat_[A-Za-z0-9_]{50,})\b")),
        ("CHIAVE GITLAB", new(@"\b(?<s>glpat-[A-Za-z0-9_\-]{20,})")),
        ("TOKEN SLACK", new(@"\b(?<s>xox[abprs]-[A-Za-z0-9\-]{10,})")),
        ("CHIAVE AWS", new(@"\b(?<s>(?:AKIA|ASIA)[0-9A-Z]{16})\b")),
        ("CHIAVE STRIPE", new(@"\b(?<s>[rs]k_live_[A-Za-z0-9]{20,})")),
        ("CHIAVE ANTHROPIC", new(@"\b(?<s>sk-ant-[A-Za-z0-9_\-]{20,})")),
        ("CHIAVE OPENAI", new(@"\b(?<s>sk-(?:proj-)?[A-Za-z0-9_\-]{20,})")),
        ("CHIAVE GOOGLE", new(@"\b(?<s>AIza[0-9A-Za-z_\-]{35})")),
        ("TOKEN NPM", new(@"\b(?<s>npm_[A-Za-z0-9]{36})\b")),
        ("PASSWORD NELL'URL", new(@"\b[a-z][a-z0-9+.\-]*://[^\s:/@]+:(?<s>[^\s@/]{3,})@", RegexOptions.IgnoreCase)),
        ("CONNECTION STRING", new(@"(?:^|;)\s*(?:password|pwd)\s*=\s*(?<s>[^;\s]{3,})", RegexOptions.IgnoreCase)),
    ];

    /// <summary>Il tipo del testo copiato; <paramref name="pathExists"/> controlla i percorsi (file o cartella).</summary>
    public static ClipKind Classify(string text, Func<string, bool> pathExists)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(pathExists);
        var trimmed = text.Trim();
        if (trimmed.Length == 0)
            return ClipKind.Text;
        if (SecretName(trimmed) is not null)
            return ClipKind.Secret;
        if (Jwt(trimmed) is not null)
            return ClipKind.Jwt;
        if (IsJson(trimmed))
            return ClipKind.Json;
        if (CodeLocations.Any(trimmed))
            return ClipKind.Code;
        if (Color(trimmed) is not null)
            return ClipKind.Color;
        if (Timestamp(trimmed) is not null)
            return ClipKind.Timestamp;
        if (Url(trimmed) is not null)
            return ClipKind.Url;
        if (PathOf(trimmed) is { } path && pathExists(path))
            return ClipKind.Path;
        return ClipKind.Text;
    }

    public static bool IsSensitive(ClipKind kind) => kind is ClipKind.Secret or ClipKind.Jwt;

    /// <summary>Etichetta a punti della pagina Appunti.</summary>
    public static string Tag(ClipKind kind) => kind switch
    {
        ClipKind.Secret or ClipKind.Jwt => "KEY",
        ClipKind.Json => "JSON",
        ClipKind.Code => "ERR",
        ClipKind.Color => "CLR",
        ClipKind.Timestamp => "TS",
        ClipKind.Url => "URL",
        ClipKind.Path => "DIR",
        _ => "TXT",
    };

    /// <summary>Nome del segreto ("CHIAVE GITHUB"), o null se il testo non ne contiene.</summary>
    public static string? SecretName(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return Secrets.FirstOrDefault(s => s.Pattern.IsMatch(text)).Name;
    }

    /// <summary>Anteprima su una riga con il segreto mascherato: <c>ghp_••••3f2a</c>.</summary>
    public static string Mask(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var masked = text;
        foreach (var (name, pattern) in Secrets)
        {
            masked = pattern.Replace(masked, m =>
            {
                if (name == "CHIAVE PRIVATA")
                    return "-----BEGIN PRIVATE KEY----- ••••";
                var secret = m.Groups["s"];
                var value = secret.Value;
                var hidden = value.Length > 12 ? value[..4] + "••••" + value[^4..] : "••••";
                return m.Value[..(secret.Index - m.Index)] + hidden + m.Value[(secret.Index - m.Index + secret.Length)..];
            });
        }
        if (Jwt(text.Trim()) is not null)
            masked = text.Trim()[..Math.Min(6, text.Trim().Length)] + "••••";
        return Preview(masked);
    }

    /// <summary>Una riga, spazi compressi, al massimo <paramref name="max"/> caratteri.</summary>
    public static string Preview(string text, int max = 80)
    {
        ArgumentNullException.ThrowIfNull(text);
        var line = Whitespace().Replace(text.Trim(), " ");
        return line.Length <= max ? line : line[..(max - 1)] + "…";
    }

    public static JwtToken? Jwt(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!JwtShape().IsMatch(text))
            return null;
        var parts = text.Split('.');
        if (DecodeJson(parts[0]) is not { } header || !header.Contains("\"alg\"", StringComparison.Ordinal) || DecodeJson(parts[1]) is not { } payload)
            return null;
        DateTimeOffset? expires = null;
        using (var doc = JsonDocument.Parse(payload))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("exp", out var exp)
                && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var seconds) && seconds is > 0 and < 253402300799)
                expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        return new JwtToken(Format(header), Format(payload), expires);
    }

    /// <summary>"scade tra 2 h", "scade tra 5 min", "scaduto 3 giorni fa", null senza <c>exp</c>.</summary>
    public static string? Expiry(JwtToken token, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(token);
        if (token.Expires is not { } expires)
            return null;
        var left = expires - now;
        static string Span(TimeSpan t) => t.TotalMinutes < 60 ? $"{Math.Max(1, (int)t.TotalMinutes)} min"
            : t.TotalHours < 48 ? $"{(int)t.TotalHours} h"
            : $"{(int)t.TotalDays} giorni";
        return left > TimeSpan.Zero ? $"scade tra {Span(left)}" : $"scaduto {Span(-left)} fa";
    }

    public static bool IsJson(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        if (trimmed.Length < 2 || !((trimmed[0] == '{' && trimmed[^1] == '}') || (trimmed[0] == '[' && trimmed[^1] == ']')))
            return false;
        try
        {
            using var _ = JsonDocument.Parse(trimmed, new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>JSON indentato di due spazi, caratteri non ASCII lasciati come sono; le chiavi doppie restano.</summary>
    public static string Format(string json) => Rewrite(json, indented: true);

    public static string Minify(string json) => Rewrite(json, indented: false);

    public static ClipColor? Color(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var hex = HexColor().Match(text);
        if (hex.Success)
        {
            var digits = hex.Groups[1].Value;
            if (digits.Length is 3 or 4)
                digits = string.Concat(digits.Select(c => $"{c}{c}"));
            byte At(int i) => byte.Parse(digits.AsSpan(i * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            return new ClipColor(At(0), At(1), At(2), digits.Length == 8 ? At(3) : (byte)255);
        }
        var rgb = RgbColor().Match(text);
        if (!rgb.Success)
            return null;
        var channels = new int[3];
        for (var i = 0; i < 3; i++)
        {
            channels[i] = int.Parse(rgb.Groups[i + 1].Value, CultureInfo.InvariantCulture);
            if (channels[i] > 255)
                return null;
        }
        byte alpha = 255;
        if (rgb.Groups[4].Success)
        {
            var raw = rgb.Groups[4].Value;
            var percent = raw.EndsWith('%');
            if (!double.TryParse(raw.TrimEnd('%'), NumberStyles.Float, CultureInfo.InvariantCulture, out var a))
                return null;
            alpha = (byte)Math.Round(Math.Clamp(percent ? a / 100 : a, 0, 1) * 255);
        }
        return new ClipColor((byte)channels[0], (byte)channels[1], (byte)channels[2], alpha);
    }

    /// <summary>Secondi (10 cifre) o millisecondi (13 cifre) Unix fra il 2001 e il 2100.</summary>
    public static DateTimeOffset? Timestamp(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TimestampShape().IsMatch(text) || !long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var value))
            return null;
        var at = text.Length == 13 ? DateTimeOffset.FromUnixTimeMilliseconds(value) : DateTimeOffset.FromUnixTimeSeconds(value);
        return at.Year is >= 2001 and <= 2100 ? at : null;
    }

    public static Uri? Url(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return !text.Contains('\n', StringComparison.Ordinal) && !text.Contains(' ', StringComparison.Ordinal)
               && Uri.TryCreate(text, UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps)
            ? uri
            : null;
    }

    /// <summary>Percorso di Windows su una riga, anche fra virgolette ("Copia come percorso"); null se non lo sembra.</summary>
    public static string? PathOf(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var path = text.Trim().Trim('"');
        return !path.Contains('\n', StringComparison.Ordinal) && WindowsPath().IsMatch(path) && path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0
            ? path
            : null;
    }

    /// <summary>Avviso a punti al momento della copia (solo JSON, errori, colori e timestamp), o null.</summary>
    public static string? Hint(ClipKind kind, string text, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        var trimmed = text.Trim();
        return kind switch
        {
            ClipKind.Json => $"JSON - {DownloadTracker.FormatBytes(Encoding.UTF8.GetByteCount(trimmed))}",
            ClipKind.Code => CodeLocations.FirstError(trimmed) is { } error ? $"ERRORE - {error.Label}"
                : CodeLocations.Frames(trimmed) is [var frame, ..] ? $"STACK TRACE - {frame.Label}" : "ERRORE",
            ClipKind.Color => Color(trimmed)?.Hex,
            ClipKind.Timestamp => Timestamp(trimmed) is { } at
                ? at.ToOffset(now.Offset).ToString("dd/MM/yyyy HH:mm:ss", CultureInfo.InvariantCulture)
                : null,
            _ => null,
        };
    }

    private static string Rewrite(string json, bool indented)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var doc = JsonDocument.Parse(json.Trim(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = indented, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            doc.WriteTo(writer);
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string? DecodeJson(string base64Url)
    {
        var padded = base64Url.Replace('-', '+').Replace('_', '/');
        padded = padded.PadRight(padded.Length + ((4 - (padded.Length % 4)) % 4), '=');
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(padded));
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? json : null;
        }
        catch (Exception ex) when (ex is FormatException or JsonException or ArgumentException)
        {
            return null;
        }
    }

    [GeneratedRegex(@"-----BEGIN (?:[A-Z]+ )?PRIVATE KEY-----")]
    private static partial Regex PemKey();

    [GeneratedRegex(@"^eyJ[A-Za-z0-9_\-]{6,}\.eyJ[A-Za-z0-9_\-]{6,}\.[A-Za-z0-9_\-]*$")]
    private static partial Regex JwtShape();

    [GeneratedRegex(@"^#([0-9a-fA-F]{3}|[0-9a-fA-F]{4}|[0-9a-fA-F]{6}|[0-9a-fA-F]{8})$")]
    private static partial Regex HexColor();

    [GeneratedRegex(@"^rgba?\(\s*(\d{1,3})\s*,\s*(\d{1,3})\s*,\s*(\d{1,3})\s*(?:[,/]\s*([\d.]+%?)\s*)?\)$", RegexOptions.IgnoreCase)]
    private static partial Regex RgbColor();

    [GeneratedRegex(@"^(?:\d{10}|\d{13})$")]
    private static partial Regex TimestampShape();

    [GeneratedRegex(@"^(?:[A-Za-z]:\\|\\\\[^\\\s]+\\)")]
    private static partial Regex WindowsPath();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Whitespace();
}

/// <summary>Una voce della cronologia: i segreti hanno solo l'anteprima mascherata, mai il testo.</summary>
public sealed record ClipEntry(ClipKind Kind, string? Text, string Preview, DateTimeOffset CopiedAt);

/// <summary>Ultime copie, solo in memoria (docs/12-sviluppo.md); due copie uguali di seguito contano una volta.</summary>
public sealed class ClipboardHistory
{
    public const int Capacity = 30;

    private readonly List<ClipEntry> _entries = [];

    /// <summary>Le più recenti per prime.</summary>
    public IReadOnlyList<ClipEntry> Entries => _entries.AsEnumerable().Reverse().ToArray();

    public ClipEntry Add(string text, ClipKind kind, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(text);
        var sensitive = ClipClassifier.IsSensitive(kind);
        var entry = new ClipEntry(kind, sensitive ? null : text, sensitive ? ClipClassifier.Mask(text) : ClipClassifier.Preview(text), now);
        if (_entries is [.., var last] && last.Kind == kind && last.Preview == entry.Preview && last.Text == entry.Text)
            _entries.RemoveAt(_entries.Count - 1);
        _entries.Add(entry);
        if (_entries.Count > Capacity)
            _entries.RemoveAt(0);
        return entry;
    }

    public void Clear() => _entries.Clear();
}
