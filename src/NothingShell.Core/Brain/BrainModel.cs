using NothingShell.Core.Storage;

namespace NothingShell.Core.Brain;

/// <summary>Il modello locale: file scaricato una volta, solo su richiesta, e controllato con l'hash pubblicato.</summary>
public sealed record BrainModel(string Name, string FileName, Uri Url, string Sha256, long Bytes)
{
    public static BrainModel Default { get; } = new(
        "Qwen2.5 0.5B",
        "qwen2.5-0.5b-instruct-q4_k_m.gguf",
        new Uri("https://huggingface.co/Qwen/Qwen2.5-0.5B-Instruct-GGUF/resolve/main/qwen2.5-0.5b-instruct-q4_k_m.gguf"),
        "74a4da8c9fdbcd15bd1f6d01d621410d31c6fc00986f5eb687824e7b93d7a9db",
        469L * 1024 * 1024);

    public string LocalPath => Path.Combine(AtomicFile.DataFolder, "brain", FileName);
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
