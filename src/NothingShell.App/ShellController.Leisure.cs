using NothingShell.Core.Leisure;
using NothingShell.Core.Notch;

namespace NothingShell.App;

/// <summary>Svago: il gioco del gatto nella pagina del notch e il suo record (docs/25-svago.md).</summary>
public sealed partial class ShellController
{
    public LeisureSettings Leisure => _settings.LeisureOrDefault();

    private void WireLeisure()
    {
        _notch.LeisureFinished += (score, stage) =>
        {
            var after = Leisure.WithResult(score, stage);
            if (after == Leisure)
                return;
            Update(_settings with { Leisure = after });
            _notch.SetLeisureBest(after.Best, after.BestStage);
        };
        _notch.SetLeisureBest(Leisure.Best, Leisure.BestStage);
    }

    /// <summary>Accesa, la pagina Svago entra nello swipe (in fondo, finché non la si sposta in Impostazioni › Notch).</summary>
    public void SetLeisureEnabled(bool enabled) => Update(_settings with { Leisure = Leisure with { Enabled = enabled } });

    public void ResetLeisureRecord()
    {
        Update(_settings with { Leisure = Leisure with { Best = 0, BestStage = 0 } });
        _notch.SetLeisureBest(0, 0);
    }

    /// <summary>Apre il notch sulla pagina Svago (dal pulsante delle impostazioni).</summary>
    public void OpenLeisure()
    {
        if (Leisure.Enabled)
            _notch.OpenPage(NotchPage.Leisure);
    }
}
