namespace NothingShell.Core.Leisure;

/// <summary>Svago (docs/25-svago.md): il gioco nel notch, spento di serie, e il record.</summary>
public sealed record LeisureSettings(bool Enabled = false, int Best = 0, int BestStage = 0)
{
    public static LeisureSettings Default { get; } = new();

    /// <summary>Il record dopo una partita: cambia solo se è stato battuto.</summary>
    public LeisureSettings WithResult(int score, int stage) =>
        score > Best ? this with { Best = score, BestStage = stage } : this;
}
