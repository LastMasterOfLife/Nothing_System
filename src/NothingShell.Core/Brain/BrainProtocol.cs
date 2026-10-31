using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NothingShell.Core.Brain;

/// <summary>Una richiesta sulla pipe <c>NothingShell.Brain</c>: una riga JSON per connessione, una riga di risposta.</summary>
public sealed record BrainRequest(string Op, string? Input = null, string? Command = null, bool Ok = false, bool Forced = false, string? Cwd = null);

public sealed record BrainReply(string? Command = null, string? Source = null, string? Risk = null, bool RunNow = false, double Ms = 0, string? Note = null);

/// <summary>Formato della pipe e del dialogo col processo del modello, e testo mandato al modello.</summary>
public static class BrainProtocol
{
    public const string PipeName = "NothingShell.Brain";

    public const int MaxMessageLength = 16 * 1024;

    /// <summary>Una frase più lunga di così non è una richiesta da decidere al volo.</summary>
    public const int MaxInputLength = 300;

    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        PropertyNameCaseInsensitive = true,
    };

    public static BrainRequest? ParseRequest(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<BrainRequest>(line, Options) is { Op.Length: > 0 } request ? request : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Write(BrainReply reply) => JsonSerializer.Serialize(reply, Options);

    public static string Write(BrainRequest request) => JsonSerializer.Serialize(request, Options);

    public static BrainReply? ParseReply(string line)
    {
        try
        {
            return JsonSerializer.Deserialize<BrainReply>(line, Options);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static BrainReply Reply(BrainDecision decision, double milliseconds) =>
        new(decision.Command, decision.Source.ToString().ToLowerInvariant(), decision.Risk.ToString().ToLowerInvariant(), decision.RunNow, milliseconds);

    /// <summary>Il prompt per il modello: istruzioni, le scorciatoie più vicine alla frase come esempi, la frase.</summary>
    public static string ModelPrompt(string input, IReadOnlyList<Shortcut> examples, string? cwd)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(examples);
        var system = new StringBuilder(
            "Traduci la richiesta in UN SOLO comando PowerShell per Windows. Rispondi solo col comando, su una riga, senza spiegazioni. " +
            "Se la richiesta non si può fare con un comando rispondi NESSUNO.\n");
        if (cwd is { Length: > 0 })
            system.Append("Cartella attuale: ").Append(cwd).Append('\n');
        system.Append("Esempi:\n");
        foreach (var example in examples.Where(e => !e.IsTemplate))
            system.Append(string.Join(' ', example.Tokens)).Append(" -> ").Append(example.Command).Append('\n');
        system.Append("elenca i file -> Get-ChildItem\ncrea cartella prova -> New-Item -ItemType Directory prova\nfile txt -> Get-ChildItem *.txt\n");
        return $"<|im_start|>system\n{system}<|im_end|>\n<|im_start|>user\n{input}<|im_end|>\n<|im_start|>assistant\n";
    }

    /// <summary>La prima riga utile della risposta, senza formattazione; null se il modello non ha un comando.</summary>
    public static string? CleanModelOutput(string output)
    {
        ArgumentNullException.ThrowIfNull(output);
        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim().Trim('`').Trim();
            if (line.Length == 0 || line.StartsWith("powershell", StringComparison.OrdinalIgnoreCase) && line.Length == "powershell".Length)
                continue;
            if (line.StartsWith("PS>", StringComparison.OrdinalIgnoreCase))
                line = line[3..].Trim();
            var arrow = line.LastIndexOf(" -> ", StringComparison.Ordinal);
            if (arrow >= 0)
                line = line[(arrow + 4)..].Trim();
            return line.Length == 0 || line.StartsWith("NESSUNO", StringComparison.OrdinalIgnoreCase) ? null : line;
        }
        return null;
    }
}
