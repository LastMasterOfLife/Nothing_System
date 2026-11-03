using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Notch;
using NothingShell.App.Rendering;
using NothingShell.Core.Work;

namespace NothingShell.App.Settings;

/// <summary>Pagina Ore: le ore calcolate per periodo, e dove si creano gruppi e progetti (docs/19-ore-di-lavoro.md).</summary>
public sealed partial class SettingsWindow
{
    private static readonly (string Text, string Key)[] HoursPeriods =
        [("OGGI", "oggi"), ("SETTIMANA", "settimana"), ("MESE", "mese"), ("MESE SCORSO", "mese scorso"), ("ANNO", "anno")];

    private string _hoursPeriod = "mese";
    private HoursCalendarView? _hoursCalendar;

    private void HoursPage()
    {
        if (_shell.Work is not { } store)
        {
            Section("ORE");
            Note("Le ore di lavoro sono spente: si accendono in Impostazioni › Sviluppo › Ore di lavoro.", Theme.MutedBrush);
            return;
        }

        var now = DateTime.Now;
        var period = WorkPeriod.Parse(_hoursPeriod, DateOnly.FromDateTime(now)) ?? WorkPeriod.Month(now.Year, now.Month);
        var report = store.Report(period);
        var config = store.Config;

        Section("ORE CALCOLATE", period.Label);
        ButtonRow(HoursPeriods.Select(p => (p.Key == _hoursPeriod ? p.Text + " ●" : p.Text, (Action)(() =>
        {
            _hoursPeriod = p.Key;
            Rebuild();
        }))).ToArray());
        CardTitle($"Totale {WorkReport.DurationWithDecimals(report.TotalSeconds)}", store.Tracker.Current is { } current ? $"in corso: {current}" : null, Theme.AccentBrush);
        if (config.Projects.Count == 0)
            Note("Nessun progetto: creane uno qui sotto. Il tempo si conta quando il titolo della finestra in primo piano contiene una sua parola chiave.", Theme.MutedBrush);
        foreach (var group in report.Groups)
        {
            var members = config.Group(group.Name)?.Members ?? [];
            ValueRow(group.Name, members.Count == 0 ? "gruppo vuoto" : string.Join(", ", members), WorkReport.Duration(group.Seconds), true);
        }
        foreach (var project in report.Projects)
            ValueRow(project.Name, null, WorkReport.Duration(project.Seconds), false);

        Section("CALENDARIO");
        Note("I giorni colorati hanno ore registrate: più sono, più il colore è pieno. Un clic su un giorno ne mostra il dettaglio.", Theme.MutedBrush);
        // Lo stato (mese e giorno scelti) resta quando la pagina si rifà; il controllo va staccato dalla scheda di prima.
        _hoursCalendar ??= new HoursCalendarView(store, null);
        (_hoursCalendar.Parent as Panel)?.Children.Remove(_hoursCalendar);
        _hoursCalendar.Refresh();
        _current.Children.Add(_hoursCalendar);

        Section("GRUPPI");
        Note("Un gruppo somma le ore dei suoi progetti: spunta quelli che ne fanno parte. Un progetto può stare in più gruppi.", Theme.MutedBrush);
        foreach (var group in config.Groups)
        {
            CardTitle(group.Name, group.Members.Count == 0 ? "vuoto" : $"{group.Members.Count} progetti", Theme.MutedBrush);
            foreach (var project in config.Projects)
            {
                var name = project.Name;
                ToggleRow(name, null, group.Members.Contains(name, StringComparer.OrdinalIgnoreCase), on => ChangeHours(store, c => c with
                {
                    Groups = [.. c.Groups.Select(g => !WorkConfig.Same(g.Name, group.Name) ? g : g with
                    {
                        Members = on ? [.. g.Members.Where(m => !WorkConfig.Same(m, name)), name] : [.. g.Members.Where(m => !WorkConfig.Same(m, name))],
                    })],
                }, rebuild: false));
            }
            ButtonRow(("AZZERA ORE", () => ResetHours(store, group.Members, [group.Name], $"il gruppo «{group.Name}» e i suoi progetti ({(group.Members.Count == 0 ? "nessuno" : string.Join(", ", group.Members))}). Se un progetto è anche in un altro gruppo, si azzera lì")), ("ELIMINA GRUPPO", () => ChangeHours(store, c => c with { Groups = [.. c.Groups.Where(g => !WorkConfig.Same(g.Name, group.Name))] })));
        }
        var groupName = Field("Nuovo gruppo: nome", string.Empty);
        ButtonRow(true, ("CREA GRUPPO", () => Create(store, "gruppo", groupName.Text, null)));

        Section("PROGETTI");
        foreach (var project in config.Projects)
        {
            var name = project.Name;
            ActionRow(name, "titoli che contengono: " + string.Join(", ", project.Keywords), [("AZZERA ORE", () => ResetHours(store, [name], [], $"il progetto «{name}»")), ("ELIMINA", () => ChangeHours(store, c => new WorkConfig(
                [.. c.Projects.Where(p => !WorkConfig.Same(p.Name, name))],
                [.. c.Groups.Select(g => g with { Members = [.. g.Members.Where(m => !WorkConfig.Same(m, name))] })])))]);
        }
        var projectName = Field("Nuovo progetto: nome", string.Empty);
        var keywords = Field("Parole chiave nel titolo della finestra, separate da virgola (vuoto: il nome)", string.Empty);
        ButtonRow(true, ("CREA PROGETTO", () => Create(store, "progetto", projectName.Text, keywords.Text)));
        Note("Con il nome di un progetto che c'è già, ne sostituisce le parole chiave. Eliminare un progetto non cancella le ore già contate: per quello c'è Azzera ore.", Theme.MutedBrush);
    }

    /// <summary>Azzera le ore dopo una conferma che dice cosa si cancella: non si torna indietro.</summary>
    private void ResetHours(WorkStore store, IEnumerable<string> projects, IEnumerable<string> groups, string what)
    {
        var answer = MessageBox.Show($"Azzerare tutte le ore contate di {what}?\n\nSi cancellano le ore di ogni giorno, anche quelle aggiunte a mano. Non si può annullare.",
            "Nothing Shell", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (answer != MessageBoxResult.Yes)
            return;
        store.Reset(projects, groups);
        Rebuild();
    }

    private void Create(WorkStore store, string kind, string name, string? keywords)
    {
        name = name.Trim();
        if (name.Length == 0 || name.IndexOfAny(['=', ',']) >= 0)
        {
            MessageBox.Show("Serve un nome, senza = né virgole.", "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var answer = WorkCommands.Run(keywords is null ? $"{kind} {name}" : $"{kind} {name} = {keywords.Trim()}", store, DateTime.Now);
        if (answer.Config is null)
        {
            MessageBox.Show(answer.Rows[0].Value, "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        store.SetConfig(answer.Config);
        Rebuild();
    }

    private void ChangeHours(WorkStore store, Func<WorkConfig, WorkConfig> change, bool rebuild = true)
    {
        store.SetConfig(change(store.Config));
        if (rebuild)
            Rebuild();
    }

    /// <summary>Riga con nome (e dettaglio) a sinistra e le ore a destra.</summary>
    private void ValueRow(string title, string? detail, string value, bool group)
    {
        var row = new Grid { Margin = new Thickness(0, 8, 0, 8) };
        row.ColumnDefinitions.Add(new ColumnDefinition());
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(Label(title, 14, group ? Theme.AccentBrush : Theme.InkBrush));
        if (detail is not null)
        {
            var d = Label(detail, 12, Theme.MutedBrush);
            d.Margin = new Thickness(0, 3, 0, 0);
            d.TextWrapping = TextWrapping.Wrap;
            text.Children.Add(d);
        }
        var hours = Label(value, 14, Theme.InkBrush);
        hours.VerticalAlignment = VerticalAlignment.Center;
        hours.Margin = new Thickness(16, 0, 0, 0);
        Grid.SetColumn(hours, 1);
        row.Children.Add(text);
        row.Children.Add(hours);
        AddRow(row);
    }
}
