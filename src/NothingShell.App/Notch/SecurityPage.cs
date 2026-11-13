using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.Core.Notch;
using NothingShell.Core.Security;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Sicurezza (docs/26-sicurezza.md): si scrive l'indirizzo di un <b>sito proprio</b>, si conferma di esserne
/// autorizzati (il cancello), e parte una sessione non distruttiva con feedback dal vivo — fase in corso, avanzamento,
/// conteggio per gravità — e, alla fine, l'elenco delle debolezze con <i>Apri report</i>. Un clic su una debolezza la copia.
/// </summary>
public sealed class SecurityPage : NotchPageView
{
    private static readonly Brush Amber = Freeze(new SolidColorBrush(Color.FromRgb(0xE6, 0xA2, 0x3C)));

    private readonly StackPanel _list = new();
    private readonly TextBlock _count;
    private readonly TextBox _input = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.InkBrush,
        CaretBrush = Theme.InkBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _placeholder = NotchUi.Text(13, Theme.MutedBrush, "https://tuo-sito-di-test — solo un sito tuo");

    private SecurityTarget? _pending;
    private SecuritySnapshot? _snapshot;
    private string? _reportPath;

    public SecurityPage()
    {
        var (header, _, count) = DevRows.Header("SICUREZZA");
        _count = count;

        _placeholder.VerticalAlignment = VerticalAlignment.Center;
        _placeholder.IsHitTestVisible = false;
        _placeholder.Margin = new Thickness(2, 0, 0, 0);
        var content = new Grid();
        content.Children.Add(_input);
        content.Children.Add(_placeholder);
        var box = new Border
        {
            Background = Theme.SurfaceBrush,
            CornerRadius = new CornerRadius(4),
            Height = 32,
            Padding = new Thickness(14, 0, 14, 0),
            Margin = new Thickness(0, 8, 0, 0),
            Child = content,
        };

        var scroll = new NotchScrollList(_list) { Margin = new Thickness(0, 6, 0, 0) };
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(box, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(box);
        dock.Children.Add(scroll);
        Children.Add(dock);

        _input.PreviewMouseLeftButtonDown += (_, _) => InputClicked?.Invoke();
        _input.TextChanged += (_, _) => _placeholder.Visibility = _input.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _input.PreviewKeyDown += OnKey;
        Render();
    }

    public override NotchPage Page => NotchPage.Security;

    /// <summary>Clic sul campo: il notch non prende il focus da solo, va chiesto prima che si possa scrivere.</summary>
    public event Action? InputClicked;

    /// <summary>Esc nel campo: la tastiera torna alla finestra di prima.</summary>
    public event Action? InputCancelled;

    /// <summary>L'utente ha confermato l'autorizzazione e chiede di eseguire la sessione su questo bersaglio.</summary>
    public event Action<SecurityTarget>? ScanRequested;

    /// <summary>Ferma la sessione in corso.</summary>
    public event Action? StopRequested;

    /// <summary>Apri il report dell'ultima sessione.</summary>
    public event Action? OpenReportRequested;

    public bool IsMouseOverInput => _input.IsMouseOver;

    public bool IsInputFocused => _input.IsKeyboardFocused;

    public void FocusInput() => Keyboard.Focus(_input);

    public void ClearInput() => _input.Clear();

    /// <summary>Scrive nel campo e propone (senza passare dalla tastiera, per <c>--snapshot</c>).</summary>
    public void Propose(string text)
    {
        _input.Text = text;
        Submit(text);
    }

    /// <summary>Nuova istantanea della sessione (dal vivo o finale) e dov'è il report.</summary>
    public void Update(SecuritySnapshot? snapshot, string? reportPath)
    {
        _snapshot = snapshot;
        _reportPath = reportPath;
        if (snapshot?.IsRunning == true)
            _pending = null; // in corso: via la richiesta di conferma
        Render();
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            InputCancelled?.Invoke();
        }
        else if (e.Key == Key.Enter)
        {
            e.Handled = true;
            Submit(_input.Text);
        }
    }

    private void Submit(string text)
    {
        _pending = ScopeGuard.Parse(text);
        Render();
    }

    private void Render()
    {
        _list.Children.Clear();
        var snapshot = _snapshot;
        var running = snapshot?.IsRunning == true;

        if (running && snapshot is not null)
        {
            _count.Text = $"{snapshot.Done}/{snapshot.Total}";
            _list.Children.Add(Caption(snapshot.Activity.Length > 0 ? snapshot.Activity : "In corso…"));
            _list.Children.Add(SeverityBar(snapshot));
            _list.Children.Add(ActionButton("FERMA", () => StopRequested?.Invoke()));
            AddFindings(snapshot);
            return;
        }

        if (snapshot is { Status: SessionStatus.Completed or SessionStatus.Aborted or SessionStatus.Failed })
        {
            _count.Text = snapshot.Findings.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
            var head = snapshot.Status switch
            {
                SessionStatus.Completed => "Sessione completata",
                SessionStatus.Aborted => "Sessione interrotta",
                _ => "Interrotta da un errore" + (snapshot.Error is { Length: > 0 } err ? $": {err}" : string.Empty),
            };
            _list.Children.Add(Caption(head));
            _list.Children.Add(SeverityBar(snapshot));
            if (_reportPath is { Length: > 0 })
                _list.Children.Add(ActionButton("APRI REPORT", () => OpenReportRequested?.Invoke()));
            AddFindings(snapshot);
            return;
        }

        // In attesa: aiuto, oppure la riga di conferma se è stato scritto un indirizzo valido.
        _count.Text = string.Empty;
        if (_pending is { } target)
        {
            _list.Children.Add(Caption($"Confermi che «{target.Host}» è tuo o sei autorizzato a provarlo?"));
            _list.Children.Add(ActionButton("SÌ, È MIO — ESEGUI", () =>
            {
                var t = _pending;
                _pending = null;
                if (t is not null)
                    ScanRequested?.Invoke(t);
            }));
            _list.Children.Add(Caption("Scansione non distruttiva: niente DoS, brute-force o furto di dati. Solo su un sito tuo."));
            return;
        }

        var help = DevRows.Empty();
        help.Text = "Scrivi l'indirizzo di un sito tuo e premi Invio: dopo la conferma parte una sessione non distruttiva " +
                    "(intestazioni, TLS, cookie; nuclei e OWASP ZAP se installati) e a fine giro trovi il report con dove sta il problema e come si risolve.";
        _list.Children.Add(help);
    }

    private void AddFindings(SecuritySnapshot snapshot)
    {
        foreach (var finding in snapshot.Ordered)
            _list.Children.Add(FindingRow(finding));
    }

    private static TextBlock Caption(string text)
    {
        var caption = NotchUi.Text(12, Theme.MutedBrush, text);
        caption.TextWrapping = TextWrapping.Wrap;
        caption.TextTrimming = TextTrimming.None;
        caption.Margin = new Thickness(0, 6, 0, 2);
        return caption;
    }

    /// <summary>Una riga con il conteggio per gravità: CRIT · ALTA · MEDIA · BASSA · INFO.</summary>
    private static StackPanel SeverityBar(SecuritySnapshot snapshot)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 4, 0, 4) };
        foreach (var severity in new[] { Severity.Critical, Severity.High, Severity.Medium, Severity.Low, Severity.Info })
        {
            var n = snapshot.Count(severity);
            var chip = NotchUi.Text(12, n > 0 ? BrushOf(severity) : Theme.MutedBrush, $"{Severities.Label(severity)} {n}");
            chip.Margin = new Thickness(0, 0, 12, 0);
            row.Children.Add(chip);
        }
        return row;
    }

    private static Border FindingRow(Finding finding)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(64) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var tag = NotchUi.Text(11, BrushOf(finding.Severity), Severities.Label(finding.Severity));
        tag.VerticalAlignment = VerticalAlignment.Top;
        tag.Margin = new Thickness(0, 2, 0, 0);
        var body = new StackPanel();
        var title = NotchUi.Text(13, Theme.InkBrush, finding.Title);
        title.TextWrapping = TextWrapping.Wrap;
        title.TextTrimming = TextTrimming.None;
        body.Children.Add(title);
        if (finding.Where is { Length: > 0 })
        {
            var where = NotchUi.Text(10, Theme.MutedBrush, finding.Where);
            where.TextTrimming = TextTrimming.CharacterEllipsis;
            body.Children.Add(where);
        }
        Grid.SetColumn(body, 1);
        grid.Children.Add(tag);
        grid.Children.Add(body);

        var row = new Border
        {
            Padding = new Thickness(0, 4, 0, 4),
            CornerRadius = new CornerRadius(4),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            ToolTip = finding.Remediation,
            Child = grid,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            try
            {
                Clipboard.SetText($"[{Severities.Label(finding.Severity)}] {finding.Title} — {finding.Where}\n{finding.Remediation}");
            }
            catch (COMException)
            {
                // appunti occupati: si riprova con un altro clic
            }
        };
        return row;
    }

    private static Border ActionButton(string label, Action action)
    {
        var text = NotchUi.Text(12, Theme.InkBrush, label);
        var button = NotchUi.Button(text, action, width: 180);
        button.HorizontalAlignment = HorizontalAlignment.Left;
        button.Margin = new Thickness(0, 6, 0, 6);
        button.Background = Theme.SurfaceBrush;
        return button;
    }

    private static Brush BrushOf(Severity severity) => severity switch
    {
        Severity.Critical or Severity.High => Theme.CriticalBrush,
        Severity.Medium => Amber,
        Severity.Low => Theme.AccentBrush,
        _ => Theme.MutedBrush,
    };

    private static SolidColorBrush Freeze(SolidColorBrush brush)
    {
        brush.Freeze();
        return brush;
    }
}
