using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Shell;
using NothingShell.App.Interop;
using NothingShell.App.Rendering;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Cosa serve al lettore di posta (implementato da ShellController).</summary>
public interface IMailReaderActions
{
    /// <summary>Mail della casella collegata e delle notifiche, la più recente per prima.</summary>
    IReadOnlyList<MailItem> Mails { get; }

    int UnreadCount { get; }

    event Action? MailsChanged;

    /// <summary>Mail intera dal server; solo per le mail della casella collegata.</summary>
    Task<(MailContent? Content, string? Error)> LoadAsync(MailItem mail, CancellationToken cancellation);

    /// <summary>Segna come letta sul server; null se riesce, altrimenti il motivo.</summary>
    Task<string?> MarkReadAsync(MailItem mail);

    /// <summary>Dove si apre la webmail (nome della web app o del sito); null senza casella collegata.</summary>
    string? WebmailName { get; }

    void OpenWebmail();

    /// <summary>Apre l'app che ha mandato la notifica; false se non si può.</summary>
    bool OpenSourceApp(MailItem mail);

    /// <summary>Misure e posizione del lettore, ricordate nelle impostazioni.</summary>
    MailReaderLayout ReaderLayout { get; set; }
}

/// <summary>
/// Lettore di posta in stile Nothing (docs/08-notch-mail-pages.md): si apre con un clic su una mail del notch.
/// A sinistra le ultime mail divise per giorno, con ricerca e filtro delle non lette; a destra quella scelta con
/// rispondi, webmail, segna come letta e il codice di verifica a punti. Si ridimensiona dai bordi, si ingrandisce
/// col doppio clic sull'intestazione, il divisore sposta l'elenco; sotto i 700 DIP mostra un riquadro alla volta.
/// </summary>
public sealed class MailReaderWindow : Window
{
    private const double SegmentAll = 66;
    private const double SegmentUnread = 96;

    private readonly IMailReaderActions _mail;
    private readonly Dictionary<ImapMessageRef, MailContent> _loaded = [];
    private readonly Border _frame = new();
    private readonly Grid _panes = new();
    private readonly ColumnDefinition _listColumn = new();
    private readonly ColumnDefinition _splitterColumn = new() { Width = new GridLength(17) };
    private readonly ColumnDefinition _detailColumn = new() { Width = new GridLength(1, GridUnitType.Star) };
    private readonly StackPanel _list = new();
    private readonly ScrollViewer _listScroll = new();
    private readonly Border _detail = new();
    private readonly DotMatrixView _unread = new() { Pitch = 2, DotColor = Theme.White, VerticalAlignment = VerticalAlignment.Center };
    private readonly Border _unreadPill = new();
    private readonly TextBox _search = new();
    private readonly Border _segmentIndicator = new();
    private readonly TranslateTransform _segmentShift = new();
    private readonly TextBlock[] _segmentLabels = new TextBlock[2];
    private readonly Border _back = new();
    private readonly Rectangle _maximizeIcon = new() { Stroke = Theme.WhiteBrush, StrokeThickness = 1.5, RadiusX = 2, RadiusY = 2 };
    private readonly TextBlock _searchHint = Text(string.Empty, 12, Theme.GreyBrush);
    private Border? _searchBox;
    private MailReaderLayout _layout;
    private MailItem? _selected;
    private TextBox? _body;
    private bool _unreadOnly;
    private bool _compact;
    private bool _showList = true; // solo in modalità compatta: elenco o mail
    private bool _maximized;
    private bool _firstList = true;
    private Rect _restore;
    private bool _closing;
    private int _generation; // cresce a ogni mail mostrata: i caricamenti superati si ignorano

    public MailReaderWindow(IMailReaderActions mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _mail = mail;
        _layout = mail.ReaderLayout.Fit(SystemParameters.VirtualScreenLeft, SystemParameters.VirtualScreenTop,
            SystemParameters.VirtualScreenWidth, SystemParameters.VirtualScreenHeight);

        Width = _layout.Width;
        Height = _layout.Height;
        MinWidth = MailReaderLayout.MinWidth;
        MinHeight = MailReaderLayout.MinHeight;
        if (_layout is { Left: { } left, Top: { } top })
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            Left = left;
            Top = top;
        }
        else
            WindowStartupLocation = WindowStartupLocation.CenterScreen;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.CanResize;
        // bordi trascinabili senza la cornice di Windows: la finestra resta nera e arrotondata
        WindowChrome.SetWindowChrome(this, new WindowChrome
        {
            CaptionHeight = 0,
            ResizeBorderThickness = new Thickness(8),
            GlassFrameThickness = new Thickness(0),
            CornerRadius = new CornerRadius(0),
            UseAeroCaptionButtons = false,
        });
        Title = "Posta · Nothing Shell";
        Content = BuildVisualTree();
        _listColumn.Width = new GridLength(_layout.ListWidth);
        PreviewKeyDown += OnKey;
        PreviewMouseWheel += OnZoomWheel;
        SizeChanged += (_, _) => ApplyCompact();
        Loaded += (_, _) => Motion.Open(_frame);
        Closing += (_, _) => SaveLayout();
        _mail.MailsChanged += OnMailsChanged;
        Closed += (_, _) =>
        {
            _mail.MailsChanged -= OnMailsChanged;
            _generation++;
        };
        ApplyCompact();
        RebuildList();
        ShowEmptyDetail();
    }

    public void Select(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _selected = Find(mail) ?? mail;
        _showList = false;
        ApplyCompact();
        RebuildList();
        ShowDetail(_selected, animate: true);
    }

    /// <summary>Chiude sfumando (Esc, X): il contenuto si rimpicciolisce appena e sparisce.</summary>
    public void CloseAnimated()
    {
        if (_closing)
            return;
        _closing = true;
        if (!Motion.Enabled)
        {
            Close();
            return;
        }
        var fade = new DoubleAnimation(0, Motion.Fast) { EasingFunction = Motion.OutQuint };
        fade.Completed += (_, _) => Close();
        _frame.BeginAnimation(OpacityProperty, fade);
    }

    private static bool Same(MailItem a, MailItem b) => a.Imap is { } r ? b.Imap == r : a == b;

    private MailItem? Find(MailItem mail) => _mail.Mails.FirstOrDefault(m => Same(mail, m));

    private List<MailItem> Visible() =>
        _mail.Mails.Where(m => (!_unreadOnly || m.IsUnread || (_selected is not null && Same(_selected, m))) && MailSearch.Matches(m, _search.Text)).ToList();

    private void Move(int step)
    {
        var mails = Visible();
        if (mails.Count == 0)
            return;
        var index = _selected is null ? -1 : mails.FindIndex(m => Same(_selected, m));
        Select(mails[Math.Clamp(index + step, 0, mails.Count - 1)]);
        if (_list.Children.OfType<FrameworkElement>().FirstOrDefault(e => e.Tag is MailItem m && Same(m, _selected!)) is { } row)
            row.BringIntoView();
    }

    private void OnMailsChanged()
    {
        // La stessa mail può cambiare (letta altrove): si tiene la scelta e si aggiorna lo stato.
        if (_selected is not null && Find(_selected) is { } current && current.IsUnread != _selected.IsUnread)
        {
            _selected = current;
            ShowDetail(current, animate: false);
        }
        RebuildList();
    }

    // ── Elenco ──────────────────────────────────────────────────────────────────

    private void RebuildList()
    {
        var unread = _mail.UnreadCount;
        _unread.Dots = DotText.Render(unread > 0 ? $"{unread}" : string.Empty);
        _unreadPill.Visibility = unread > 0 ? Visibility.Visible : Visibility.Collapsed;
        _list.Children.Clear();
        var mails = Visible();
        if (mails.Count == 0)
        {
            _list.Children.Add(EmptyState(
                _mail.Mails.Count == 0 ? "NIENTE" : "NESSUNA",
                _mail.Mails.Count == 0 ? "Nessuna mail recente." : _unreadOnly && _search.Text.Length == 0 ? "Hai letto tutto." : "Nessuna mail trovata."));
            return;
        }
        var index = 0;
        foreach (var (label, group) in MailGroups.Of(mails, DateTime.Today))
        {
            var header = new DotMatrixView
            {
                Pitch = 1.6,
                DotColor = Theme.Grey,
                Dots = DotText.Render(label),
                HorizontalAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(12, _list.Children.Count == 0 ? 2 : 16, 0, 8),
            };
            _list.Children.Add(header);
            foreach (var mail in group)
            {
                var row = ListRow(mail);
                _list.Children.Add(row);
                if (_firstList)
                    Motion.Enter(row, 12, Math.Min(index, 10) * 28);
                index++;
            }
        }
        _firstList = false;
    }

    private Border ListRow(MailItem mail)
    {
        var isSelected = _selected is not null && Same(_selected, mail);
        var sender = mail.Sender.Length > 0 ? mail.Sender : mail.App;

        var avatar = Avatar(sender, 34, isSelected);
        var when = Text(ShortDate(mail.Received), 11, mail.IsUnread ? Theme.WhiteBrush : Theme.GreyBrush);
        when.Margin = new Thickness(8, 0, 0, 0);
        var header = new DockPanel();
        DockPanel.SetDock(when, Dock.Right);
        header.Children.Add(when);
        if (mail.IsUnread)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Theme.RedBrush, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            DockPanel.SetDock(dot, Dock.Right);
            header.Children.Add(dot);
        }
        var name = Text(sender, 12, mail.IsUnread ? Theme.WhiteBrush : Theme.GreyBrush);
        if (mail.IsUnread)
            name.FontWeight = FontWeights.SemiBold;
        header.Children.Add(name);

        var subject = Text(mail.Subject, 13, mail.IsUnread || isSelected ? Theme.WhiteBrush : Theme.GreyBrush);
        subject.Margin = new Thickness(0, 3, 0, 0);
        var text = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(header);
        text.Children.Add(subject);

        var content = new DockPanel();
        DockPanel.SetDock(avatar, Dock.Left);
        content.Children.Add(avatar);
        content.Children.Add(text);

        // la scelta corrente ha un trattino rosso a sinistra, come l'indicatore di pagina del notch
        var accent = new Border
        {
            Width = 3,
            Height = 22,
            CornerRadius = new CornerRadius(1.5),
            Background = Theme.RedBrush,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(-10, 0, 0, 0),
            Opacity = isSelected ? 1 : 0,
        };
        var layered = new Grid();
        layered.Children.Add(content);
        layered.Children.Add(accent);

        var row = new Border
        {
            Child = layered,
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 9, 12, 9),
            Margin = new Thickness(0, 0, 4, 3),
            Cursor = Cursors.Hand,
            Tag = mail,
        };
        Motion.StateLayer(row, isSelected ? Theme.Surface : Colors.Transparent, Theme.Surface, () => isSelected);
        Motion.Press(row, 0.985);
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Select(mail);
        };
        return row;
    }

    private static Border Avatar(string sender, double size, bool lit)
    {
        var initials = MailSearch.Initials(sender);
        return new Border
        {
            Width = size,
            Height = size,
            CornerRadius = new CornerRadius(size / 2),
            Background = lit ? Theme.BlackBrush : Theme.SurfaceBrush,
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
            Child = new DotMatrixView
            {
                Pitch = size >= 44 ? 2.4 : 1.6,
                DotColor = lit ? Theme.White : Theme.Grey,
                Dots = DotText.Render(initials),
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
    }

    private static StackPanel EmptyState(string dots, string text)
    {
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 60, 0, 0) };
        panel.Children.Add(new DotMatrixView { Pitch = 3, DotColor = Theme.Outline, Dots = DotText.Render(dots), HorizontalAlignment = HorizontalAlignment.Center });
        var line = Text(text, 12, Theme.GreyBrush);
        line.HorizontalAlignment = HorizontalAlignment.Center;
        line.Margin = new Thickness(0, 14, 0, 0);
        panel.Children.Add(line);
        Motion.Enter(panel, 8);
        return panel;
    }

    // ── Mail scelta ─────────────────────────────────────────────────────────────

    private void ShowEmptyDetail()
    {
        _body = null;
        var empty = EmptyState("POSTA", "Scegli una mail dall'elenco.");
        empty.VerticalAlignment = VerticalAlignment.Center;
        empty.Margin = new Thickness(0);
        _detail.Child = empty;
    }

    private void ShowDetail(MailItem mail, bool animate)
    {
        var generation = ++_generation;
        if (mail.Imap is not { } message)
        {
            var note = mail.App == "Nothing Shell"
                ? "Questa è la mail di prova delle impostazioni."
                : $"Questa mail arriva da una notifica di {(mail.App.Length > 0 ? mail.App : "un'app")}: il testo completo è lì.";
            Render(mail, null, note, null, animate);
            return;
        }
        if (_loaded.TryGetValue(message, out var cached))
        {
            Render(mail, cached, null, null, animate);
            return;
        }
        Render(mail, null, null, null, animate, loading: true);
        _ = LoadAsync(mail, message, generation);
    }

    private async Task LoadAsync(MailItem mail, ImapMessageRef message, int generation)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40));
        var (content, error) = await _mail.LoadAsync(mail, timeout.Token);
        if (content is not null)
            _loaded[message] = content;
        if (generation != _generation)
            return; // nel frattempo è stata scelta un'altra mail o il lettore è chiuso
        Render(mail, content, null, content is null ? error ?? "Impossibile aprire la mail." : null, animate: false);
    }

    /// <summary>
    /// Disegna la mail: intestazione con avatar, oggetto, azioni, codice e allegati, e il testo in una scheda che
    /// occupa lo spazio rimasto (segue il ridimensionamento). Senza contenuto: la nota, l'errore o il caricamento.
    /// </summary>
    private void Render(MailItem mail, MailContent? content, string? note, string? error, bool animate, bool loading = false)
    {
        var grid = new Grid();
        for (var i = 0; i < 4; i++)
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });

        // intestazione: avatar, mittente, indirizzo e destinatario, data a destra
        var sender = content?.From ?? (mail.Sender.Length > 0 ? mail.Sender : mail.App);
        var (name, address) = SplitAddress(sender, mail.SenderAddress);
        var meta = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        var who = Text(name, 15, Theme.WhiteBrush);
        who.FontWeight = FontWeights.SemiBold;
        meta.Children.Add(who);
        var details = address ?? string.Empty;
        if (content is { To.Length: > 0 })
            details += (details.Length > 0 ? "  ·  " : string.Empty) + $"a {content.To}";
        if (details.Length > 0)
        {
            var line = Text(details, 11, Theme.GreyBrush);
            line.Margin = new Thickness(0, 2, 0, 0);
            meta.Children.Add(line);
        }
        var date = Text(LongDate(content?.Date ?? mail.Received), 11, Theme.GreyBrush);
        date.VerticalAlignment = VerticalAlignment.Top;
        date.Margin = new Thickness(12, 4, 0, 0);
        var top = new DockPanel();
        var avatar = Avatar(name, 46, lit: false);
        DockPanel.SetDock(avatar, Dock.Left);
        DockPanel.SetDock(date, Dock.Right);
        top.Children.Add(avatar);
        top.Children.Add(date);
        top.Children.Add(meta);
        grid.Children.Add(top);

        var subject = Text(content?.Subject ?? mail.Subject, 22, Theme.WhiteBrush);
        subject.FontWeight = FontWeights.SemiBold;
        subject.TextWrapping = TextWrapping.Wrap;
        subject.TextTrimming = TextTrimming.None;
        subject.Margin = new Thickness(0, 18, 0, 0);
        Grid.SetRow(subject, 1);
        grid.Children.Add(subject);

        // azioni: la principale piena e bianca, le altre a contorno
        var actions = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        Grid.SetRow(actions, 2);
        grid.Children.Add(actions);
        if (content is not null)
        {
            if (MailLinks.Reply(content.ReplyAddress, content.Subject) is { } reply)
                AddButton(actions, "RISPONDI", () => OpenUri(reply), primary: true);
            AddWebmailButton(actions);
            if (mail.IsUnread)
                AddButton(actions, "SEGNA COME LETTA", async () =>
                {
                    if (await _mail.MarkReadAsync(mail) is { } failure)
                        ShowError(failure);
                });
        }
        else if (error is not null)
        {
            AddButton(actions, "RIPROVA", () => ShowDetail(mail, animate: false), primary: true);
            AddWebmailButton(actions);
        }
        else if (note is not null && mail.AppId is not null && mail.App.Length > 0)
            AddButton(actions, $"APRI {mail.App.ToUpperInvariant()}", () =>
            {
                if (_mail.OpenSourceApp(mail))
                    CloseAnimated();
            }, primary: true);

        // codice di verifica in grande, a punti, e allegati come gettoni
        var extras = new StackPanel();
        Grid.SetRow(extras, 3);
        grid.Children.Add(extras);
        if ((content is not null ? MailCodes.Find(content.Subject, content.Body) : MailCodes.Find(mail.Subject)) is { } code)
            extras.Children.Add(CodeCard(code));
        if (content is { Attachments.Count: > 0 })
            extras.Children.Add(Attachments(content.Attachments));
        if (error is not null)
            extras.Children.Add(ErrorLine(error));

        var card = new Border
        {
            Background = Theme.SurfaceBrush,
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(20, 16, 14, 16),
            Margin = new Thickness(0, 14, 0, 0),
        };
        Grid.SetRow(card, 4);
        grid.Children.Add(card);
        _body = null;
        if (content is not null)
            card.Child = Body(content.Body);
        else if (loading)
            card.Child = Loader("Apertura dal server");
        else
            card.Child = Note(note ?? error ?? string.Empty);

        _detail.Child = grid;
        if (animate)
            Motion.Enter(grid, 10);
        else if (content is not null)
            Motion.Enter(card, 6);
    }

    private void ShowError(string error)
    {
        if (_detail.Child is Grid grid && grid.Children.OfType<StackPanel>().FirstOrDefault(p => Grid.GetRow(p) == 3) is { } extras)
            extras.Children.Add(ErrorLine(error));
    }

    private static TextBlock ErrorLine(string error)
    {
        var line = Text(error, 12, Theme.RedBrush);
        line.TextWrapping = TextWrapping.Wrap;
        line.TextTrimming = TextTrimming.None;
        line.Margin = new Thickness(0, 10, 0, 0);
        Motion.Enter(line, 4);
        return line;
    }

    private static TextBlock Note(string text)
    {
        var line = Text(text, 13, Theme.GreyBrush);
        line.TextWrapping = TextWrapping.Wrap;
        line.TextTrimming = TextTrimming.None;
        return line;
    }

    /// <summary>Tre punti che respirano in sequenza, poi il testo.</summary>
    private static StackPanel Loader(string text)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Top };
        for (var i = 0; i < 3; i++)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Theme.WhiteBrush, Margin = new Thickness(0, 0, 5, 0), VerticalAlignment = VerticalAlignment.Center };
            Motion.Breathe(dot, 0.15, i * 170);
            row.Children.Add(dot);
        }
        var label = Text(text, 12, Theme.GreyBrush);
        label.Margin = new Thickness(8, 0, 0, 0);
        row.Children.Add(label);
        return row;
    }

    /// <summary>Il testo della mail: si seleziona e copia, scorre con la rotellina; Ctrl+rotellina lo ingrandisce.</summary>
    private Grid Body(string text)
    {
        var body = new TextBox
        {
            Text = text.Length > 0 ? text : "(nessun testo)",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.WhiteBrush,
            CaretBrush = Theme.WhiteBrush,
            SelectionBrush = Theme.RedBrush,
            FontFamily = Theme.BodyFont,
            FontSize = _layout.BodySize,
            Padding = new Thickness(0, 0, 10, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // scorre con la rotellina, senza barra chiara
        };
        TextOptions.SetTextFormattingMode(body, TextFormattingMode.Ideal);
        _body = body;
        var thumb = new Border
        {
            Width = 3,
            CornerRadius = new CornerRadius(1.5),
            Background = Theme.GreyBrush,
            Opacity = 0.5,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed,
        };
        body.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, _) =>
        {
            var total = body.ExtentHeight;
            var visible = body.ViewportHeight;
            if (total <= visible + 0.5 || visible <= 0)
            {
                thumb.Visibility = Visibility.Collapsed;
                return;
            }
            thumb.Visibility = Visibility.Visible;
            thumb.Height = Math.Max(20, visible * visible / total);
            thumb.RenderTransform = new TranslateTransform(0, (visible - thumb.Height) * (body.VerticalOffset / Math.Max(1, total - visible)));
        }));
        var grid = new Grid();
        grid.Children.Add(body);
        grid.Children.Add(thumb);
        return grid;
    }

    private static Border CodeCard(string code)
    {
        var label = Text("CODICE DI VERIFICA", 10, Theme.GreyBrush);
        var digits = new DotMatrixView { Pitch = 4, DotColor = Theme.White, Dots = DotText.Render(code), Margin = new Thickness(0, 8, 0, 0), HorizontalAlignment = HorizontalAlignment.Left };
        var left = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        left.Children.Add(label);
        left.Children.Add(digits);

        var actions = new WrapPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 8, 0, 0) };
        Border? copy = null;
        copy = AddButton(actions, "COPIA", () =>
        {
            try
            {
                Clipboard.SetText(code);
                ((TextBlock)copy!.Child).Text = "COPIATO";
                Motion.FadeTo(copy, Theme.Red, Motion.Normal);
            }
            catch (COMException)
            {
                // appunti occupati da un'altra app: si può riprovare
            }
        }, primary: true);
        var dock = new DockPanel();
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.Add(actions);
        dock.Children.Add(left);
        return new Border
        {
            BorderBrush = Theme.RedBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(18, 14, 14, 14),
            Margin = new Thickness(0, 6, 0, 0),
            Child = dock,
        };
    }

    private WrapPanel Attachments(IReadOnlyList<string> names)
    {
        var row = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        foreach (var name in names)
        {
            var chip = new Border
            {
                CornerRadius = new CornerRadius(12),
                BorderBrush = Theme.OutlineBrush,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10, 5, 12, 5),
                Margin = new Thickness(0, 0, 6, 6),
                ToolTip = _mail.WebmailName is { } w ? $"Gli allegati si aprono in {w}" : "Gli allegati si aprono dalla webmail",
            };
            var content = new StackPanel { Orientation = Orientation.Horizontal };
            content.Children.Add(new Rectangle { Width = 8, Height = 10, Stroke = Theme.GreyBrush, StrokeThickness = 1, RadiusX = 1.5, RadiusY = 1.5, VerticalAlignment = VerticalAlignment.Center });
            var label = Text(name, 11, Theme.WhiteBrush);
            label.Margin = new Thickness(7, 0, 0, 0);
            label.MaxWidth = 260;
            content.Children.Add(label);
            chip.Child = content;
            if (_mail.WebmailName is not null)
            {
                Motion.StateLayer(chip, Colors.Transparent, Theme.Surface);
                Motion.Clickable(chip);
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    e.Handled = true;
                    _mail.OpenWebmail();
                };
            }
            row.Children.Add(chip);
        }
        return row;
    }

    private void AddWebmailButton(WrapPanel actions)
    {
        if (_mail.WebmailName is { } name)
            AddButton(actions, $"APRI IN {name.ToUpperInvariant()}", _mail.OpenWebmail);
    }

    private static Border AddButton(WrapPanel actions, string text, Action click, bool primary = false) =>
        AddButton(actions, text, () =>
        {
            click();
            return Task.CompletedTask;
        }, primary);

    private static Border AddButton(WrapPanel actions, string text, Func<Task> click, bool primary = false)
    {
        var label = Text(text, 11, primary ? Theme.BlackBrush : Theme.WhiteBrush);
        label.FontWeight = primary ? FontWeights.SemiBold : FontWeights.Normal;
        var button = new Border
        {
            BorderBrush = primary ? Theme.WhiteBrush : Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(16, 8, 16, 8),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = Cursors.Hand,
            Child = label,
        };
        if (primary)
            Motion.StateLayer(button, Theme.White, Color.FromRgb(0xD9, 0xD9, 0xD9));
        else
            Motion.StateLayer(button, Colors.Transparent, Theme.Surface);
        Motion.Press(button);
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await click();
        };
        actions.Children.Add(button);
        return button;
    }

    // ── Finestra ────────────────────────────────────────────────────────────────

    private Border BuildVisualTree()
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        _back.Child = new Polyline
        {
            Points = [new Point(6, 0), new Point(0, 6), new Point(6, 12)],
            Stroke = Theme.WhiteBrush,
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 2, 0),
        };
        RoundButton(_back, "Torna all'elenco", () =>
        {
            _showList = true;
            ApplyCompact();
        });
        _back.Margin = new Thickness(0, 0, 10, 0);
        title.Children.Add(_back);
        title.Children.Add(new DotMatrixView { Pitch = 3, Dots = DotText.Render("POSTA"), VerticalAlignment = VerticalAlignment.Center });
        _unreadPill.Child = _unread;
        _unreadPill.Background = Theme.RedBrush;
        _unreadPill.CornerRadius = new CornerRadius(10);
        _unreadPill.Padding = new Thickness(8, 4, 8, 4);
        _unreadPill.Margin = new Thickness(14, 0, 0, 0);
        _unreadPill.VerticalAlignment = VerticalAlignment.Center;
        _unreadPill.ToolTip = "Da leggere";
        title.Children.Add(_unreadPill);

        var tools = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center };
        tools.Children.Add(SearchBox());
        tools.Children.Add(Segments());
        var maximize = new Border { Child = _maximizeIcon, Margin = new Thickness(12, 0, 0, 0) };
        _maximizeIcon.HorizontalAlignment = HorizontalAlignment.Center;
        _maximizeIcon.VerticalAlignment = VerticalAlignment.Center;
        RoundButton(maximize, "Ingrandisci (doppio clic sull'intestazione)", ToggleMaximize);
        tools.Children.Add(maximize);
        var close = new Border
        {
            Child = new DotMatrixView { Pitch = 2, Dots = DotText.Render("X"), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center },
            Margin = new Thickness(4, 0, 0, 0),
        };
        RoundButton(close, "Chiudi (Esc)", CloseAnimated);
        tools.Children.Add(close);
        UpdateMaximizeIcon();

        var header = new DockPanel { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent, LastChildFill = false };
        DockPanel.SetDock(tools, Dock.Right);
        header.Children.Add(tools);
        header.Children.Add(title);
        header.MouseLeftButtonDown += OnHeaderDown;

        _listScroll.VerticalScrollBarVisibility = ScrollBarVisibility.Hidden;
        _listScroll.HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled;
        _listScroll.CanContentScroll = false;
        _listScroll.Content = _list;

        // divisore: una riga sottile che si accende al passaggio e si trascina per allargare l'elenco
        var line = new Rectangle { Width = 1, Fill = Theme.OutlineBrush, HorizontalAlignment = HorizontalAlignment.Center, IsHitTestVisible = false };
        var splitter = new GridSplitter
        {
            Width = 17,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brushes.Transparent,
            ResizeBehavior = GridResizeBehavior.PreviousAndNext,
            ResizeDirection = GridResizeDirection.Columns,
            Cursor = Cursors.SizeWE,
            Focusable = false,
            ToolTip = "Trascina per allargare l'elenco",
        };
        splitter.MouseEnter += (_, _) => line.Fill = Theme.GreyBrush;
        splitter.MouseLeave += (_, _) => line.Fill = Theme.OutlineBrush;
        splitter.DragCompleted += (_, _) => _layout = _layout with { ListWidth = _listColumn.ActualWidth };
        Grid.SetColumn(line, 1);
        Grid.SetColumn(splitter, 1);
        _listColumn.MinWidth = MailReaderLayout.MinList;
        _listColumn.MaxWidth = MailReaderLayout.MaxList;
        _detailColumn.MinWidth = 300;
        _panes.ColumnDefinitions.Add(_listColumn);
        _panes.ColumnDefinitions.Add(_splitterColumn);
        _panes.ColumnDefinitions.Add(_detailColumn);
        Grid.SetColumn(_detail, 2);
        _panes.Children.Add(_listScroll);
        _panes.Children.Add(line);
        _panes.Children.Add(splitter);
        _panes.Children.Add(_detail);

        var grip = Grip();
        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(_panes);
        var layered = new Grid();
        layered.Children.Add(dock);
        layered.Children.Add(grip);

        _frame.Background = Theme.BlackBrush;
        _frame.BorderBrush = Theme.OutlineBrush;
        _frame.BorderThickness = new Thickness(1);
        _frame.CornerRadius = new CornerRadius(28);
        _frame.Padding = new Thickness(24, 20, 24, 20);
        _frame.Child = layered;
        return _frame;
    }

    /// <summary>Tre punti in diagonale nell'angolo in basso a destra: da lì si ridimensiona.</summary>
    private static Canvas Grip()
    {
        var grip = new Canvas
        {
            Width = 12,
            Height = 12,
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Bottom,
            Margin = new Thickness(0, 0, -14, -12),
            IsHitTestVisible = false,
        };
        foreach (var (x, y) in new[] { (8.0, 0.0), (4.0, 4.0), (8.0, 4.0), (0.0, 8.0), (4.0, 8.0), (8.0, 8.0) })
        {
            var dot = new Ellipse { Width = 2.4, Height = 2.4, Fill = Theme.OutlineBrush };
            Canvas.SetLeft(dot, x);
            Canvas.SetTop(dot, y);
            grip.Children.Add(dot);
        }
        return grip;
    }

    private Border SearchBox()
    {
        _search.Background = Brushes.Transparent;
        _search.BorderThickness = new Thickness(0);
        _search.Foreground = Theme.WhiteBrush;
        _search.CaretBrush = Theme.WhiteBrush;
        _search.SelectionBrush = Theme.RedBrush;
        _search.FontFamily = Theme.BodyFont;
        _search.FontSize = 12;
        _search.VerticalContentAlignment = VerticalAlignment.Center;
        _search.Padding = new Thickness(0);
        var hint = _searchHint;
        hint.IsHitTestVisible = false;
        hint.VerticalAlignment = VerticalAlignment.Center;
        _search.TextChanged += (_, _) =>
        {
            hint.Visibility = _search.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
            RebuildList();
        };
        var lens = new Canvas { Width = 12, Height = 12, Margin = new Thickness(0, 0, 8, 0), VerticalAlignment = VerticalAlignment.Center, IsHitTestVisible = false };
        lens.Children.Add(new Ellipse { Width = 8.5, Height = 8.5, Stroke = Theme.GreyBrush, StrokeThickness = 1.4 });
        lens.Children.Add(new Line { X1 = 7.5, Y1 = 7.5, X2 = 11.5, Y2 = 11.5, Stroke = Theme.GreyBrush, StrokeThickness = 1.4, StrokeEndLineCap = PenLineCap.Round });
        var field = new Grid();
        field.Children.Add(hint);
        field.Children.Add(_search);
        var row = new DockPanel();
        DockPanel.SetDock(lens, Dock.Left);
        row.Children.Add(lens);
        row.Children.Add(field);
        var box = _searchBox = new Border
        {
            Width = 190,
            Height = 32,
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(12, 0, 12, 0),
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 10, 0),
            Cursor = Cursors.IBeam,
            Child = row,
        };
        Motion.StateLayer(box, Colors.Transparent, Theme.Surface, () => _search.IsKeyboardFocused);
        _search.GotKeyboardFocus += (_, _) =>
        {
            Motion.FadeTo(box, Theme.Surface);
            box.BorderBrush = Theme.GreyBrush;
        };
        _search.LostKeyboardFocus += (_, _) =>
        {
            Motion.FadeTo(box, Colors.Transparent, Motion.Normal);
            box.BorderBrush = Theme.OutlineBrush;
        };
        box.MouseLeftButtonDown += (_, e) =>
        {
            e.Handled = true; // non trascina la finestra
            _search.Focus();
        };
        return box;
    }

    /// <summary>Controllo a segmenti TUTTE · NON LETTE: la pillola chiara scorre sulla scelta (come in Ski).</summary>
    private Border Segments()
    {
        _segmentIndicator.Width = SegmentAll - 4;
        _segmentIndicator.Height = 26;
        _segmentIndicator.CornerRadius = new CornerRadius(13);
        _segmentIndicator.Background = Theme.WhiteBrush;
        _segmentIndicator.HorizontalAlignment = HorizontalAlignment.Left;
        _segmentIndicator.Margin = new Thickness(2, 0, 0, 0);
        _segmentIndicator.RenderTransform = _segmentShift;
        var labels = new StackPanel { Orientation = Orientation.Horizontal };
        string[] names = ["TUTTE", "NON LETTE"];
        double[] widths = [SegmentAll, SegmentUnread];
        for (var i = 0; i < 2; i++)
        {
            var index = i;
            var label = Text(names[i], 10, i == 0 ? Theme.BlackBrush : Theme.GreyBrush);
            label.FontWeight = FontWeights.SemiBold;
            label.HorizontalAlignment = HorizontalAlignment.Center;
            label.VerticalAlignment = VerticalAlignment.Center;
            _segmentLabels[i] = label;
            var hit = new Border { Width = widths[i], Background = Brushes.Transparent, Cursor = Cursors.Hand, Child = label };
            hit.MouseLeftButtonDown += (_, e) => e.Handled = true;
            hit.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                SetFilter(index == 1);
            };
            labels.Children.Add(hit);
        }
        var grid = new Grid { Height = 30 };
        grid.Children.Add(_segmentIndicator);
        grid.Children.Add(labels);
        return new Border
        {
            CornerRadius = new CornerRadius(16),
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            Background = Theme.BlackBrush,
            Child = grid,
            ToolTip = "Tutte le mail o solo quelle da leggere",
        };
    }

    private void SetFilter(bool unreadOnly)
    {
        if (_unreadOnly == unreadOnly)
            return;
        _unreadOnly = unreadOnly;
        Motion.Slide(_segmentIndicator, _segmentShift, unreadOnly ? SegmentAll : 0, (unreadOnly ? SegmentUnread : SegmentAll) - 4);
        _segmentLabels[0].Foreground = unreadOnly ? Theme.GreyBrush : Theme.BlackBrush;
        _segmentLabels[1].Foreground = unreadOnly ? Theme.BlackBrush : Theme.GreyBrush;
        _firstList = true; // l'elenco filtrato entra di nuovo in sequenza
        RebuildList();
    }

    private static void RoundButton(Border button, string tooltip, Action click)
    {
        button.Width = 32;
        button.Height = 32;
        button.CornerRadius = new CornerRadius(16);
        button.Cursor = Cursors.Hand;
        button.ToolTip = tooltip;
        Motion.StateLayer(button, Colors.Transparent, Theme.Surface);
        Motion.Press(button, 0.9);
        button.MouseLeftButtonDown += (_, e) => e.Handled = true; // altrimenti DragMove cattura il mouse
        button.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            click();
        };
    }

    /// <summary>Sotto <see cref="MailReaderLayout.CompactWidth"/> un riquadro alla volta: elenco oppure mail, con "&lt;" per tornare.</summary>
    private void ApplyCompact()
    {
        _compact = ActualWidth > 0 ? ActualWidth < MailReaderLayout.CompactWidth : Width < MailReaderLayout.CompactWidth;
        var listOnly = _compact && (_showList || _selected is null);
        var detailOnly = _compact && !listOnly;
        _listScroll.Visibility = detailOnly ? Visibility.Collapsed : Visibility.Visible;
        _detail.Visibility = listOnly ? Visibility.Collapsed : Visibility.Visible;
        foreach (UIElement child in _panes.Children)
        {
            if (Grid.GetColumn(child) == 1)
                child.Visibility = _compact ? Visibility.Collapsed : Visibility.Visible;
        }
        if (_compact)
        {
            _listColumn.MaxWidth = double.PositiveInfinity;
            _listColumn.Width = listOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
            _listColumn.MinWidth = 0;
            _splitterColumn.Width = new GridLength(0);
            _detailColumn.MinWidth = 0;
            _detailColumn.Width = detailOnly ? new GridLength(1, GridUnitType.Star) : new GridLength(0);
        }
        else
        {
            _listColumn.MinWidth = MailReaderLayout.MinList;
            _listColumn.MaxWidth = MailReaderLayout.MaxList;
            if (_listColumn.Width.IsStar || _listColumn.Width.Value == 0)
                _listColumn.Width = new GridLength(_layout.ListWidth);
            _splitterColumn.Width = new GridLength(17);
            _detailColumn.MinWidth = 300;
            _detailColumn.Width = new GridLength(1, GridUnitType.Star);
        }
        _back.Visibility = detailOnly ? Visibility.Visible : Visibility.Collapsed;
        if (_searchBox is not null)
            _searchBox.Width = _compact ? 120 : 190;
        _searchHint.Text = _compact ? "Cerca" : "Cerca  (Ctrl+F)";
        _unreadPill.Margin = new Thickness(_compact ? 10 : 14, 0, 0, 0);
    }

    private void OnHeaderDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }
        if (e.ButtonState != MouseButtonState.Pressed)
            return;
        if (_maximized)
        {
            // trascinando da ingrandita torna alla misura di prima, sotto il puntatore
            var cursor = PointToScreen(e.GetPosition(this));
            var source = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
            var point = source.Transform(cursor);
            var ratio = e.GetPosition(this).X / Math.Max(1, ActualWidth);
            SetMaximized(false);
            Left = point.X - _restore.Width * ratio;
            Top = point.Y - 20;
        }
        DragMove();
    }

    private void ToggleMaximize() => SetMaximized(!_maximized);

    /// <summary>Ingrandita occupa l'area di lavoro del suo monitor (non copre la barra); senza angoli arrotondati.</summary>
    private void SetMaximized(bool maximized)
    {
        if (maximized == _maximized)
            return;
        var hwnd = new WindowInteropHelper(this).Handle;
        if (maximized)
        {
            var monitor = WindowNative.MonitorOf(hwnd);
            if (WindowNative.Monitors().FirstOrDefault(m => m.Handle == monitor) is not { } display)
                return;
            _restore = new Rect(Left, Top, Width, Height);
            _maximized = true;
            WindowNative.Place(hwnd, display.WorkArea);
        }
        else
        {
            _maximized = false;
            Left = _restore.Left;
            Top = _restore.Top;
            Width = _restore.Width;
            Height = _restore.Height;
        }
        _frame.CornerRadius = new CornerRadius(maximized ? 0 : 28);
        _frame.BorderThickness = new Thickness(maximized ? 0 : 1);
        UpdateMaximizeIcon();
        if (Motion.Enabled)
            _frame.BeginAnimation(OpacityProperty, new DoubleAnimation(0.6, 1, Motion.Normal) { EasingFunction = Motion.OutQuint });
    }

    /// <summary>Un quadrato pieno di spazio da ingrandire, più piccolo da ingrandita.</summary>
    private void UpdateMaximizeIcon()
    {
        _maximizeIcon.Width = _maximizeIcon.Height = _maximized ? 9 : 12;
    }

    private void SaveLayout()
    {
        var bounds = _maximized ? _restore : new Rect(Left, Top, Width, Height);
        if (!_compact && _listColumn.ActualWidth > 0)
            _layout = _layout with { ListWidth = _listColumn.ActualWidth };
        _layout = _layout with { Left = bounds.Left, Top = bounds.Top, Width = bounds.Width, Height = bounds.Height };
        _mail.ReaderLayout = _layout;
    }

    private void OnKey(object sender, KeyEventArgs e)
    {
        var ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        switch (e.Key)
        {
            case Key.Escape when _search.Text.Length > 0:
                _search.Clear();
                break;
            case Key.Escape when _compact && !_showList && _selected is not null:
                _showList = true;
                ApplyCompact();
                break;
            case Key.Escape:
                CloseAnimated();
                break;
            case Key.F when ctrl:
                _search.Focus();
                _search.SelectAll();
                break;
            case Key.OemPlus or Key.Add when ctrl:
                Zoom(1);
                break;
            case Key.OemMinus or Key.Subtract when ctrl:
                Zoom(-1);
                break;
            case Key.D0 or Key.NumPad0 when ctrl:
                Zoom(0);
                break;
            case Key.Up or Key.Down:
                Move(e.Key == Key.Down ? 1 : -1);
                break;
            case Key.Enter when _search.IsKeyboardFocused && Visible() is [var first, ..]:
                Select(first);
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void OnZoomWheel(object sender, MouseWheelEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0)
            return;
        e.Handled = true;
        Zoom(Math.Sign(e.Delta));
    }

    /// <summary>Testo della mail più grande o più piccolo di 1 punto; 0 torna a 13. Resta per le prossime aperture.</summary>
    private void Zoom(int step)
    {
        var size = step == 0 ? MailReaderLayout.Default.BodySize : Math.Clamp(_layout.BodySize + step, MailReaderLayout.MinBody, MailReaderLayout.MaxBody);
        _layout = _layout with { BodySize = size };
        if (_body is not null)
            _body.FontSize = size;
    }

    // ── Testo ───────────────────────────────────────────────────────────────────

    /// <summary>"Mario Rossi &lt;mario@x.it&gt;" → ("Mario Rossi", "mario@x.it").</summary>
    private static (string Name, string? Address) SplitAddress(string from, string? fallbackAddress)
    {
        var open = from.IndexOf('<', StringComparison.Ordinal);
        var close = from.IndexOf('>', StringComparison.Ordinal);
        if (open > 0 && close > open)
            return (from[..open].Trim().Trim('"'), from[(open + 1)..close].Trim());
        if (from.Contains('@', StringComparison.Ordinal) && !from.Contains(' ', StringComparison.Ordinal))
            return (from.Trim('<', '>'), null);
        return (from, fallbackAddress);
    }

    private static string ShortDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", NotchUi.Italian)
            : local.Date == DateTime.Today.AddDays(-1) ? "ieri"
            : local.Date > DateTime.Today.AddDays(-7) ? local.ToString("ddd", NotchUi.Italian)
            : local.ToString("d MMM", NotchUi.Italian);
    }

    private static string LongDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? $"oggi, {local:HH:mm}"
            : local.Date == DateTime.Today.AddDays(-1) ? $"ieri, {local:HH:mm}"
            : local.ToString("ddd d MMM yyyy, HH:mm", NotchUi.Italian);
    }

    private static TextBlock Text(string text, double size, Brush foreground) => new()
    {
        Text = text,
        FontFamily = Theme.BodyFont,
        FontSize = size,
        Foreground = foreground,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private static void OpenUri(string uri)
    {
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nessuna app associata ai link mailto:
        }
    }
}
