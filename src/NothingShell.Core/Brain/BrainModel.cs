using NothingShell.Core.Storage;

namespace NothingShell.Core.Brain;

/// <summary>Un modello locale: file scaricato una volta, solo su richiesta, e controllato con l'hash pubblicato.</summary>
public sealed record BrainModel(string Name, string FileName, Uri Url, string Sha256, long Bytes)
{
    /// <summary>Il modello piccolo del terminale: propone un comando in un secondo, ma non sa leggere i risultati.</summary>
    public static BrainModel Default { get; } = new(
        "Qwen2.5 0.5B",
        "qwen2.5-0.5b-instruct-q4_k_m.gguf",
        new Uri("https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf"),
        "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db",
        469L * 1024 * 1024);

    /// <summary>Il modello dell'assistente nel notch (docs/15-comandi-cervello.md).</summary>
    public static BrainModel Assistant { get; } = new(
        "Qwen3.5 4B",
        "Qwen3.5-4B-Q4_K_M.gguf",
        new Uri("https://huggingface.co/unsloth/Qwen3.5-4B-GGUF/resolve/main/Qwen3.5-4B-Q4_K_M.gguf"),
        "00fe7986ff5f6b463e62455821146049db6f9313603938a70800d1fb69ef11a4",
        2740937888L);

    public string LocalPath => Path.Combine(AtomicFile.DataFolder, "brain", FileName);
}

/// <summary>Una conversazione per il processo del modello.</summary>
public sealed record AssistantChat(string System, IReadOnlyList<AssistantTurn> Turns);

/// <summary>Il formato di conversazione dei modelli Qwen, com'è nel template del file GGUF, senza il ragionamento.</summary>
public static class ChatMl
{
    public const string End = "<|im_end|>";

    public const string Reply = "<|im_start|>assistant\n<think>\n\n</think>\n\n";

    /// <summary>I risultati dei comandi finiscono qui dentro: scritti così non possono chiudere il turno o aprirne uno falso.</summary>
    public static string Turn(string role, string content)
    {
        ArgumentNullException.ThrowIfNull(role);
        ArgumentNullException.ThrowIfNull(content);
        return $"{TurnStart(role)}{content.Replace("<|", "< |", StringComparison.Ordinal)}{End}\n";
    }

    public static string TurnStart(string role) => $"<|im_start|>{role}\n";
}

public static class PowerShellText
{
    /// <summary>Testo come stringa PowerShell tra apici singoli: lì dentro nulla viene interpretato.</summary>
    public static string Quote(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        return "'" + text.Replace("'", "''", StringComparison.Ordinal).Replace("’", "’’", StringComparison.Ordinal) + "'";
    }
}
