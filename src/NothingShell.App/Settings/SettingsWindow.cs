using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;
using NothingShell.Core.Plugins;

namespace NothingShell.App.Settings;

/// <summary>Operazioni che la finestra delle impostazioni chiede alla shell (implementate da ShellController).</summary>
public interface IShellSettingsActions
{
    bool NotchEnabled { get; set; }

    bool BarEnabled { get; set; }

    bool UseWinKey { get; set; }

    bool WidgetsLocked { get; set; }

    bool MailInNotch { get; set; }

    bool AccessAnimations { get; set; }

    string NotificationAccessText { get; }

    bool CanRequestNotificationAccess { get; }

    bool NotificationAccessAllowed { get; }

    Task RequestNotificationAccessAsync();

    /// <summary>App proposte come "app di posta" e se lo sono già (<c>extraMailApps</c>).</summary>
    IReadOnlyList<(string App, bool IsMail)> MailAppCandidates { get; }

    void SetMailApp(string app, bool isMail);

    /// <summary>Ultima mail rilevata dalle notifiche; null se nessuna finora.</summary>
    MailItem? LastMail { get; }

    /// <summary>Avviso mail di prova nel notch, per verificare che si veda.</summary>
    void ShowTestMailAlert();

    /// <summary>Casella IMAP collegata (null se nessuna); la password è nel Gestore credenziali.</summary>
    MailAccount? MailAccount { get; }

    string MailAccountStatus { get; }

    bool MailAccountConnected { get; }

    event Action? MailAccountChanged;

    /// <summary>Prova l'accesso e, se riesce, collega la casella; altrimenti restituisce il motivo.</summary>
    Task<string?> ConnectMailAccountAsync(MailAccount account, string password);

    void DisconnectMailAccount();

    void ReconnectMailAccount();

    IEnumerable<(string Id, string Name)> Widgets { get; }

    IReadOnlyList<PluginLoadResult> PluginResults { get; }

    string DataFolder { get; }

    string PluginFolder { get; }

    bool IsWidgetEnabled(string id);

    void SetWidgetEnabled(string id, bool enabled);

    void ResetWidgetLayout();

    void ReloadPlugins();
}

/// <summary>Impostazioni in stile Nothing (docs/07-settings-plugins.md). Ogni modifica si applica subito.</summary>
public sealed class SettingsWindow : Window
{
    private const double PanelWidth = 520;
    private const double PanelHeight = 640;

    private readonly IShellSettingsActions _shell;
    private readonly StackPanel _sections = new() { Margin = new Thickness(0, 0, 8, 0) };
    private TextBlock? _mailStatus;

    public SettingsWindow(IShellSettingsActions shell)
    {
        ArgumentNullException.ThrowIfNull(shell);
        _shell = shell;

        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Impostazioni Nothing Shell";
        Content = BuildVisualTree();
        Rebuild();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
        };
        // Lo stato della casella cambia da solo (connessione, errori): si aggiorna la riga senza rifare il modulo.
        _shell.MailAccountChanged += OnMailAccountChanged;
        Closed += (_, _) => _shell.MailAccountChanged -= OnMailAccountChanged;
    }

    /// <summary>Ridisegna le sezioni (dopo una ricarica dei plugin cambiano i widget e gli errori).</summary>
    public void Rebuild()
    {
        _sections.Children.Clear();
        _mailStatus = null;

        Section("GENERALE");
        ToggleRow("Avvia con Windows", "Solo per questo utente", Autostart.IsEnabled, Autostart.Set);

        Section("COMPONENTI");
        ToggleRow("Notch", "Orologio e musica in alto al centro", _shell.NotchEnabled, v => _shell.NotchEnabled = v);
        ToggleRow("Barra al posto della taskbar", "Spenta: torna la taskbar di Windows", _shell.BarEnabled, v => _shell.BarEnabled = v);
        ToggleRow("Tasto Win apre il launcher", "Le combinazioni Win+… restano a Windows", _shell.UseWinKey, v => _shell.UseWinKey = v);

        Section("NOTCH");
        Note("Swipe sul notch aperto (touchpad, rotellina o trascinamento) per Posta, Timer e Sistema.", Theme.GreyBrush);

        MailSection();

        Section("ACCESSO");
        ToggleRow("Animazioni di blocco e sblocco", "Card con il tuo account prima e dopo il blocco di Windows",
            _shell.AccessAnimations, v => _shell.AccessAnimations = v);
        Note("Password, PIN e Windows Hello restano a Windows: Nothing Shell anima solo il prima e il dopo.", Theme.GreyBrush);

        Section("WIDGET");
        foreach (var (id, name) in _shell.Widgets)
            ToggleRow(name, null, _shell.IsWidgetEnabled(id), v => _shell.SetWidgetEnabled(id, v));
        ToggleRow("Blocca posizione", "I widget non si trascinano", _shell.WidgetsLocked, v => _shell.WidgetsLocked = v);
        ButtonRow(("RIPRISTINA DISPOSIZIONE", _shell.ResetWidgetLayout));

        Section("PLUGIN");
        var valid = _shell.PluginResults.Count(r => r.Manifest is not null);
        Note(valid == 0 ? "Nessun plugin installato." : $"{valid} plugin attivi.", Theme.GreyBrush);
        foreach (var bad in _shell.PluginResults.Where(r => r.Error is not null))
            Note($"{bad.FolderName}: {bad.Error}", Theme.RedBrush);
        ButtonRow(
            ("APRI CARTELLA", () => OpenFolder(_shell.PluginFolder)),
            ("RICARICA", () =>
            {
                _shell.ReloadPlugins();
                Rebuild();
            }));

        Section("INFO");
        Note($"Nothing Shell {typeof(SettingsWindow).Assembly.GetName().Version?.ToString(2)}", Theme.GreyBrush);
        Note($"Dati: {_shell.DataFolder}", Theme.GreyBrush);
        ButtonRow(("APRI CARTELLA DEI DATI", () => OpenFolder(_shell.DataFolder)));
    }

    /// <summary>
    /// Come collegare la posta al notch (docs/08-notch-mail-pages.md). Due strade: la casella collegata
    /// direttamente (IMAP, consigliata) oppure le notifiche che un'app di posta mostra in Windows.
    /// </summary>
    private void MailSection()
    {
        Section("POSTA NEL NOTCH");
        ToggleRow("Mail nel notch", "Busta e oggetto per qualche secondo, elenco nella pagina Posta",
            _shell.MailInNotch, v =>
            {
                _shell.MailInNotch = v;
                Rebuild();
            });
        if (!_shell.MailInNotch)
            return;

        Heading("Collega la tua casella", "consigliato");
        Note("Nothing Shell si collega direttamente alla casella (IMAP) e avvisa appena arriva una mail, senza " +
             "altre app aperte. Legge solo mittente e oggetto della Posta in arrivo, non modifica né segna come " +
             "lette le mail. La password resta cifrata nel Gestore credenziali di Windows.", Theme.GreyBrush);
        if (_shell.MailAccount is { } account)
        {
            _mailStatus = Note(_shell.MailAccountStatus, _shell.MailAccountConnected ? Theme.WhiteBrush : Theme.RedBrush);
            Note($"Utente: {account.Login} · server {account.Server}:{account.Port.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                Theme.GreyBrush);
            ButtonRow(
                ("RICOLLEGA", _shell.ReconnectMailAccount),
                ("SCOLLEGA", () =>
                {
                    _shell.DisconnectMailAccount();
                    Rebuild();
                }));
        }
        else
            MailAccountForm();

        Heading("Oppure: le notifiche di un'app di posta", null);
        Note("Per Gmail, Outlook.com o un programma di posta già configurato sul PC: Nothing Shell legge le " +
             "notifiche che mostrano in Windows. Le webmail aziendali (Exchange) di solito non ne mandano: per " +
             "quelle usa la casella collegata qui sopra.", Theme.GreyBrush);

        var allowed = _shell.NotificationAccessAllowed;
        Step(1, "Consenti l'accesso alle notifiche", done: allowed);
        Note(_shell.NotificationAccessText, allowed ? Theme.GreyBrush : Theme.RedBrush);
        if (_shell.CanRequestNotificationAccess)
            ButtonRow(("CONSENTI ACCESSO", async () =>
            {
                await _shell.RequestNotificationAccessAsync();
                Rebuild();
            }));
        else if (!allowed)
            ButtonRow(("APRI PRIVACY › NOTIFICHE", () => OpenUri("ms-settings:privacy-notifications")));

        Step(2, "Usa un'app di posta con le notifiche attive", done: null);
        Note("Riconosciute da sole: Outlook (classico e nuovo), Posta, Thunderbird, eM Client, Mailbird, Spark.",
            Theme.GreyBrush);
        Note("Webmail nel browser (Gmail, Outlook.com, Yahoo, Proton): consenti le notifiche al sito " +
             "(lucchetto accanto all'indirizzo › Notifiche › Consenti) e lascia aperta una scheda.", Theme.GreyBrush);
        Note("In Windows l'app deve avere le notifiche attive e Non disturbare deve essere spento.", Theme.GreyBrush);
        ButtonRow(("APRI NOTIFICHE DI WINDOWS", () => OpenUri("ms-settings:notifications")));

        Step(3, "La tua app non è riconosciuta?", done: null);
        var candidates = _shell.MailAppCandidates;
        if (candidates.Count == 0)
            Note("Qui compaiono le app che hanno mandato notifiche da quando Nothing Shell è avviato. " +
                 "Mandati una mail di prova, poi premi Aggiorna.", Theme.GreyBrush);
        else
        {
            Note("Accendi l'app che ti avvisa delle mail: le sue notifiche diventano mail nel notch.", Theme.GreyBrush);
            foreach (var (app, isMail) in candidates)
                ToggleRow(app, null, isMail, v => _shell.SetMailApp(app, v));
        }

        var last = _shell.LastMail;
        Heading("Verifica", null);
        Note(last is null
                ? "Nessuna mail rilevata finora."
                : $"Ultima mail rilevata alle {last.Received.ToLocalTime():HH:mm} da {last.App}: «{last.Subject}»",
            Theme.GreyBrush);
        ButtonRow(("PROVA L'AVVISO", _shell.ShowTestMailAlert), ("AGGIORNA", Rebuild));
    }

    /// <summary>Modulo per collegare la casella: si salva solo se l'accesso di prova riesce.</summary>
    private void MailAccountForm()
    {
        var address = Field("Indirizzo email", "");
        var server = Field("Server IMAP (lo trovi nelle istruzioni del provider, es. mail.dominio.it)", "");
        var port = Field("Porta", MailAccount.DefaultPort.ToString(System.Globalization.CultureInfo.InvariantCulture));
        var user = Field(@"Utente: lascia vuoto se è l'indirizzo email (alcuni server Exchange vogliono DOMINIO\nome)", "");
        var password = new PasswordBox();
        AddField("Password", password);

        // Il server si propone dall'indirizzo, finché l'utente non ne scrive uno.
        address.LostFocus += (_, _) =>
        {
            if (server.Text.Length == 0 && ImapSetup.SuggestServer(address.Text) is { } suggested)
                server.Text = suggested;
        };

        var result = Note(string.Empty, Theme.GreyBrush);
        var busy = false;
        ButtonRow(("COLLEGA", async () =>
        {
            if (busy)
                return;
            busy = true;
            result.Foreground = Theme.GreyBrush;
            result.Text = "Prova di accesso in corso…";
            var account = new MailAccount(
                address.Text.Trim(),
                server.Text.Trim(),
                int.TryParse(port.Text.Trim(), System.Globalization.CultureInfo.InvariantCulture, out var p) ? p : 0,
                string.IsNullOrWhiteSpace(user.Text) ? null : user.Text.Trim());
            var error = await _shell.ConnectMailAccountAsync(account, password.Password);
            busy = false;
            if (error is null)
            {
                Rebuild();
                return;
            }
            result.Foreground = Theme.RedBrush;
            result.Text = error;
        }));
    }

    private TextBox Field(string label, string text)
    {
        var box = new TextBox { Text = text };
        AddField(label, box);
        return box;
    }

    /// <summary>Campo in stile Nothing: etichetta grigia, riquadro scuro con bordo Outline e angoli arrotondati.</summary>
    private void AddField(string label, Control input)
    {
        var caption = Label(label, 11, Theme.GreyBrush);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.Margin = new Thickness(0, 10, 0, 4);
        _sections.Children.Add(caption);

        input.Background = Brushes.Transparent;
        input.BorderThickness = new Thickness(0);
        input.Foreground = Theme.WhiteBrush;
        input.FontFamily = Theme.BodyFont;
        input.FontSize = 13;
        input.VerticalContentAlignment = VerticalAlignment.Center;
        switch (input)
        {
            case TextBox t:
                t.CaretBrush = Theme.WhiteBrush;
                break;
            case PasswordBox p:
                p.CaretBrush = Theme.WhiteBrush;
                break;
        }
        _sections.Children.Add(new Border
        {
            Background = Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(10, 6, 10, 6),
            Child = input,
        });
    }

    private void Heading(string title, string? tag)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 18, 0, 4) };
        var label = Label(title, 14, Theme.WhiteBrush);
        label.FontWeight = FontWeights.SemiBold;
        row.Children.Add(label);
        if (tag is not null)
        {
            var badge = Label(tag.ToUpperInvariant(), 10, Theme.RedBrush);
            badge.VerticalAlignment = VerticalAlignment.Center;
            badge.Margin = new Thickness(10, 1, 0, 0);
            row.Children.Add(badge);
        }
        _sections.Children.Add(row);
    }

    private void OnMailAccountChanged()
    {
        if (_mailStatus is null)
            return;
        _mailStatus.Text = _shell.MailAccountStatus;
        _mailStatus.Foreground = _shell.MailAccountConnected ? Theme.WhiteBrush : Theme.RedBrush;
    }

    /// <summary>Passo numerato: numero a punti rosso finché c'è da fare, grigio quando è a posto.</summary>
    private void Step(int number, string title, bool? done)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 14, 0, 4) };
        row.Children.Add(new DotMatrixView
        {
            Pitch = 2,
            Dots = DotText.Render(number.ToString("00", System.Globalization.CultureInfo.InvariantCulture)),
            DotColor = done == true ? Theme.Grey : Theme.Red,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 10, 0),
        });
        var label = Label(done == true ? $"{title}  ·  fatto" : title, 14, Theme.WhiteBrush);
        label.VerticalAlignment = VerticalAlignment.Center;
        row.Children.Add(label);
        _sections.Children.Add(row);
    }

    private Border BuildVisualTree()
    {
        var header = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent };
        header.Children.Add(new DotMatrixView
        {
            Pitch = 3,
            Dots = DotText.Render("IMPOSTAZIONI"),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
        });
        var close = new Border
        {
            Width = 32,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Background = Brushes.Transparent,
            HorizontalAlignment = HorizontalAlignment.Right,
            Cursor = Cursors.Hand,
            ToolTip = "Chiudi",
            Child = new DotMatrixView
            {
                Pitch = 2,
                Dots = DotText.Render("X"),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        close.MouseEnter += (_, _) => close.Background = Theme.SurfaceBrush;
        close.MouseLeave += (_, _) => close.Background = Brushes.Transparent;
        // La pressione va fermata qui: arrivando all'intestazione avvierebbe DragMove, che cattura il mouse
        // fino al rilascio, e il clic sulla X non arriverebbe mai.
        close.MouseLeftButtonDown += (_, e) => e.Handled = true;
        close.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        header.Children.Add(close);
        header.MouseLeftButtonDown += (_, e) =>
        {
            if (e.ButtonState == MouseButtonState.Pressed)
                DragMove();
        };

        var scroll = new ScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _sections,
        };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(scroll);

        return new Border
        {
            Background = Theme.BlackBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(28),
            Padding = new Thickness(24, 20, 24, 20),
            Child = dock,
        };
    }

    private void Section(string title)
    {
        var label = Label(title, 11, Theme.GreyBrush);
        label.Margin = new Thickness(0, _sections.Children.Count == 0 ? 0 : 22, 0, 8);
        _sections.Children.Add(label);
    }

    private void ToggleRow(string title, string? description, bool isOn, Action<bool> changed)
    {
        var row = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 60, 0) };
        text.Children.Add(Label(title, 14, Theme.WhiteBrush));
        if (description is not null)
        {
            var d = Label(description, 11, Theme.GreyBrush);
            d.Margin = new Thickness(0, 2, 0, 0);
            text.Children.Add(d);
        }
        var toggle = new DotToggle(isOn) { HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        toggle.Toggled += changed;
        row.Children.Add(text);
        row.Children.Add(toggle);
        _sections.Children.Add(row);
    }

    private void ButtonRow(params (string Text, Action Click)[] buttons) =>
        ButtonRow(buttons.Select(b => (b.Text, (Func<Task>)(() =>
        {
            b.Click();
            return Task.CompletedTask;
        }))).ToArray());

    private void ButtonRow(params (string Text, Func<Task> Click)[] buttons)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 8, 0, 4) };
        foreach (var (text, click) in buttons)
        {
            var button = new Border
            {
                BorderBrush = Theme.GreyBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(14, 7, 14, 7),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = Label(text, 11, Theme.WhiteBrush),
            };
            button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += async (_, e) =>
            {
                e.Handled = true;
                await click();
            };
            row.Children.Add(button);
        }
        _sections.Children.Add(row);
    }

    private TextBlock Note(string text, Brush brush)
    {
        var note = Label(text, 12, brush);
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 2, 0, 2);
        _sections.Children.Add(note);
        return note;
    }

    private static void OpenFolder(string path)
    {
        System.IO.Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true })?.Dispose();
    }

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // pagina delle impostazioni non disponibile in questa versione di Windows
        }
    }

    private static TextBlock Label(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
    };
}
