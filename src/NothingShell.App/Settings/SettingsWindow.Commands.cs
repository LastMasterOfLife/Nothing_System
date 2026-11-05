using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Brain;

namespace NothingShell.App.Settings;

/// <summary>
/// Sviluppo › Le tue scorciatoie: una frase e il comando che la shell esegue quando la si dice, nel notch, nel terminale o
/// nel launcher con &gt; (docs/15-comandi-cervello.md). Sono le stesse scorciatoie che si imparano con l'uso.
/// </summary>
public sealed partial class SettingsWindow
{
    private const int RecentShortcuts = 6;
    private static readonly FontFamily CodeFont = new("Cascadia Mono, Consolas");

    /// <summary>Il modulo resta scritto anche se la pagina si rifà (una scorciatoia imparata intanto dal terminale).</summary>
    private string _commandPhrase = string.Empty;
    private string _commandText = string.Empty;

    /// <summary>La scorciatoia che si sta modificando: salvando con un'altra frase, la vecchia si dimentica.</summary>
    private Shortcut? _commandEditing;

    private string? _commandError;
    private string? _commandSaved;

    /// <summary>Apre il modulo già compilato con una scorciatoia (imparata da modificare, o di serie da personalizzare).</summary>
    public void EditShortcut(Shortcut shortcut, string? phrase = null, bool replace = true)
    {
        ArgumentNullException.ThrowIfNull(shortcut);
        _selected = SettingsPage.Dev;
        CloseForms();
        _listOffset = null;
        _commandEditing = replace ? shortcut : null;
        _commandPhrase = phrase ?? shortcut.Phrase;
        _commandText = shortcut.Command;
        Rebuild();
        FocusCommandForm();
    }

    private void ResetCommandForm()
    {
        _commandSaved = null;
        _commandEditing = null;
        _commandPhrase = string.Empty;
        _commandText = string.Empty;
        _commandError = null;
    }

    private TextBox? _commandPhraseBox;

    private void FocusCommandForm() => Dispatcher.BeginInvoke(() =>
    {
        if (_commandPhraseBox is not { } box)
            return;
        box.BringIntoView();
        box.Focus();
        box.CaretIndex = box.Text.Length;
    }, System.Windows.Threading.DispatcherPriority.Loaded);

    private void MyShortcutsSection()
    {
        var learned = _shell.LearnedShortcuts;
        Section(_commandEditing is null ? "LE TUE SCORCIATOIE" : "MODIFICA SCORCIATOIA",
            learned.Count > 0 ? learned.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) : null);
        Note("Una frase e il comando PowerShell da eseguire quando la scrivi: nel campo del notch, nel terminale Nothing e nel " +
             "launcher dopo >. Con " + ShortcutMemory.Slot + " la frase porta un valore che finisce nel comando, per esempio " +
             "\"apri progetto " + ShortcutMemory.Slot + "\" → code C:\\dev\\" + ShortcutMemory.Slot + ".", Theme.MutedBrush);

        var phrase = Field("FRASE", _commandPhrase);
        _commandPhraseBox = phrase;
        var command = Field("COMANDO", _commandText);
        command.FontFamily = CodeFont;
        command.FontSize = 12;

        var hint = Note(string.Empty, Theme.MutedBrush);
        var error = Note(_commandError ?? string.Empty, Theme.CriticalBrush);
        error.Visibility = _commandError is null ? Visibility.Collapsed : Visibility.Visible;
        var done = Note(_commandSaved ?? string.Empty, Theme.AccentBrush);
        done.Visibility = _commandSaved is null ? Visibility.Collapsed : Visibility.Visible;

        void Explain()
        {
            if (!string.Equals(_commandPhrase, phrase.Text, StringComparison.Ordinal) || !string.Equals(_commandText, command.Text, StringComparison.Ordinal))
            {
                _commandSaved = null;
                done.Visibility = Visibility.Collapsed;
            }
            _commandPhrase = phrase.Text;
            _commandText = command.Text;
            hint.Text = Explanation(phrase.Text, command.Text);
            hint.Visibility = hint.Text.Length == 0 ? Visibility.Collapsed : Visibility.Visible;
        }
        phrase.TextChanged += (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            Explain();
        };
        command.TextChanged += (_, _) =>
        {
            error.Visibility = Visibility.Collapsed;
            Explain();
        };
        phrase.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            command.Focus();
            command.CaretIndex = command.Text.Length;
        };
        command.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter)
                return;
            e.Handled = true;
            SaveShortcut();
        };
        Explain();

        var buttons = new List<(string Text, Action Click)> { (_commandEditing is null ? "AGGIUNGI SCORCIATOIA" : "SALVA MODIFICHE", SaveShortcut) };
        if (_commandEditing is not null || _commandPhrase.Length > 0 || _commandText.Length > 0)
            buttons.Add(("ANNULLA", () =>
            {
                ResetCommandForm();
                Rebuild();
            }));
        ButtonRow(true, [.. buttons]);

        if (learned.Count == 0)
            return;
        Section("ULTIME", learned.Count > RecentShortcuts ? $"{RecentShortcuts} di {learned.Count}" : null);
        foreach (var shortcut in learned.Take(RecentShortcuts))
            ShortcutRow(shortcut);
        ButtonRow(("VEDI TUTTE LE SCORCIATOIE", () => Show(SettingsPage.Shortcuts)));
    }

    /// <summary>Cosa farà la scorciatoia mentre la si scrive: descrizione, quando parte e cosa sostituisce.</summary>
    private string Explanation(string phrase, string command)
    {
        command = command.Trim();
        if (command.Length == 0)
            return string.Empty;
        var lines = new List<string>();
        if (CommandExplainer.Describe(command) is { Length: > 0 } description)
            lines.Add(description);
        lines.Add(CommandExplainer.Effect(new Shortcut([], command, ShortcutMemory.TrustedAfter, 0, DateTimeOffset.Now)) +
                  (CommandSafety.Classify(command) == CommandRisk.ReadOnly ? " Nel notch parte da solo." : " Nel notch chiede un clic."));
        if (phrase.Trim().Length > 0 && _shell.FindShortcut(phrase) is { } existing &&
            !(_commandEditing is { } editing && _shell.FindShortcut(editing.Phrase) == existing) &&
            !string.Equals(existing.Command, command, StringComparison.Ordinal))
            lines.Add($"Prende il posto di {(existing.Seed ? "quella di serie" : "quella che c'è")}: {existing.Command}");
        return string.Join("\n", lines);
    }

    private void SaveShortcut()
    {
        var phrase = _commandPhrase.Trim();
        var command = _commandText.Trim();
        _commandError = ShortcutMemory.WhyNot(phrase, command);
        if (_commandError is null && _commandEditing is { } editing && _shell.FindShortcut(editing.Phrase) is { } old && _shell.FindShortcut(phrase) != old)
        {
            // Frase cambiata: la vecchia se ne va, e se la nuova non si salva torna com'era.
            _shell.ForgetShortcut(editing.Phrase);
            _commandError = _shell.AddShortcut(phrase, command);
            if (_commandError is not null)
                _shell.AddShortcut(editing.Phrase, editing.Command);
        }
        else if (_commandError is null)
        {
            _commandError = _shell.AddShortcut(phrase, command);
        }
        if (_commandError is null)
        {
            var saved = _commandEditing is null ? $"Salvata: \"{phrase}\" ora esegue {command}" : $"Modificata: \"{phrase}\" ora esegue {command}";
            ResetCommandForm();
            _commandSaved = saved;
        }
        Rebuild();
        if (_commandError is not null)
            FocusCommandForm();
    }
}
