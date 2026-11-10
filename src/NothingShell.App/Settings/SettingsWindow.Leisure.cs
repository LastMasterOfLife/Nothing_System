using System.Windows;
using NothingShell.App.Leisure;
using NothingShell.App.Rendering;
using NothingShell.Core.Leisure;

namespace NothingShell.App.Settings;

/// <summary>Svago (docs/25-svago.md): il gioco del gatto nel notch, le sue regole e il record.</summary>
public sealed partial class SettingsWindow
{
    private void LeisurePage()
    {
        var leisure = _shell.Leisure;
        Section("SVAGO");
        ToggleRow("Gioco nel notch", "La pagina Svago, in fondo allo swipe: むちねこ, il gatto che corre sui tetti. Si sposta come le altre in Impostazioni › Notch",
            leisure.Enabled, v =>
            {
                _shell.SetLeisureEnabled(v);
                Rebuild();
            });
        if (leisure.Enabled)
            ButtonRow(primary: true, ("GIOCA NEL NOTCH", _shell.OpenLeisure));

        Section("MUCHINEKO");
        AddRow(new MuchiShowcase { Margin = new Thickness(0, 8, 0, 6) });
        Note("食べたい。でも、飛べなくなる。 Vuole mangiare, ma più mangia meno salta.", Theme.MutedBrush);

        Section("COME SI GIOCA");
        Note("Il gatto corre da solo da un tetto all'altro. Clic o Spazio per saltare: tenuto premuto il salto è pieno, " +
             "lasciato subito è un saltino che passa sotto le crocchette in alto.", Theme.InkBrush);
        Note($"Crocchette: {RooftopRun.KibblePoints} punto e +3,5% di peso. Scatoletta: {RooftopRun.CanPoints} punti e +18%. " +
             $"Ogni traguardo (la bandiera nera): {RooftopRun.GoalPoints} punti, e dopo si corre più svelti.", Theme.InkBrush);
        Note("Più pesa, meno salta: leggero passa tutto, sopra il 60% i vuoti larghi diventano troppi, al 100% quasi non si stacca " +
             "da terra. Correndo dimagrisce piano (1,5% al secondo).", Theme.InkBrush);
        Note("Giocando il notch resta aperto e prende la tastiera. P mette in pausa; Esc, un clic altrove o un'altra pagina " +
             "mettono in pausa e ridanno la tastiera.", Theme.MutedBrush);

        Section("RECORD");
        if (leisure.Best > 0)
        {
            ValueRow("Miglior partita", $"Arrivato alla tappa {leisure.BestStage}", $"{leisure.Best} punti", group: false);
            ConfirmButtonRow("AZZERA RECORD", "AZZERA DAVVERO", () =>
            {
                _shell.ResetLeisureRecord();
                Rebuild();
            });
        }
        else
        {
            Note("Ancora nessuna partita.", Theme.MutedBrush);
        }
    }
}
