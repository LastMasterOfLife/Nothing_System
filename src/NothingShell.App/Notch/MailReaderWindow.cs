using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
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
}

/// <summary>
/// Lettore di posta in stile Nothing (docs/08-notch-mail-pages.md): si apre con un clic su una mail del notch.
/// A sinistra le ultime mail, a destra quella scelta con rispondi, webmail, segna come letta e copia codice.
/// </summary>
public sealed class MailReaderWindow : Window
{
    private const double PanelWidth = 880;
    private const double PanelHeight = 600;
    private const double ListWidth = 280;

    private readonly IMailReaderActions _mail;
    private readonly Dictionary<ImapMessageRef, MailContent> _loaded = [];
    private readonly StackPanel _list = new();
    private readonly StackPanel _detail = new();
    private readonly DotMatrixView _unread = new() { Pitch = 2, DotColor = Theme.Red, VerticalAlignment = VerticalAlignment.Center };
    private MailItem? _selected;
    private int _generation; // cresce a ogni mail mostrata: i caricamenti superati si ignorano

    public MailReaderWindow(IMailReaderActions mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _mail = mail;

        Width = PanelWidth;
        Height = PanelHeight;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Title = "Posta · Nothing Shell";
        Content = BuildVisualTree();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
                Close();
            else if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                Move(e.Key == Key.Down ? 1 : -1);
            }
        };
        _mail.MailsChanged += OnMailsChanged;
        Closed += (_, _) =>
        {
            _mail.MailsChanged -= OnMailsChanged;
            _generation++;
        };
        RebuildList();
    }

    public void Select(MailItem mail)
    {
        ArgumentNullException.ThrowIfNull(mail);
        _selected = Find(mail) ?? mail;
        RebuildList();
        ShowDetail(_selected);
    }

    private MailItem? Find(MailItem mail) =>
        _mail.Mails.FirstOrDefault(m => mail.Imap is { } r ? m.Imap == r : m == mail);

    private void Move(int step)
    {
        var mails = _mail.Mails;
        if (mails.Count == 0)
            return;
        var index = _selected is null ? -1 : mails.ToList().FindIndex(m => _selected.Imap is { } r ? m.Imap == r : m == _selected);
        Select(mails[Math.Clamp(index + step, 0, mails.Count - 1)]);
    }

    private void OnMailsChanged()
    {
        // La stessa mail può cambiare (letta altrove): si tiene la scelta e si aggiorna lo stato.
        if (_selected is not null && Find(_selected) is { } current && current.IsUnread != _selected.IsUnread)
        {
            _selected = current;
            ShowDetail(current);
        }
        RebuildList();
    }

    private void RebuildList()
    {
        _unread.Dots = DotText.Render(_mail.UnreadCount > 0 ? $"{_mail.UnreadCount} NON LETTE" : string.Empty);
        _list.Children.Clear();
        var mails = _mail.Mails;
        if (mails.Count == 0)
        {
            _list.Children.Add(Text("Nessuna mail recente.", 12, Theme.GreyBrush));
            if (_selected is null)
                _detail.Children.Clear();
            return;
        }
        foreach (var mail in mails)
            _list.Children.Add(ListRow(mail));
    }

    private Border ListRow(MailItem mail)
    {
        var isSelected = _selected is not null && (_selected.Imap is { } r ? mail.Imap == r : mail == _selected);
        var header = new DockPanel();
        if (mail.IsUnread)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Theme.RedBrush, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            header.Children.Add(dot);
        }
        var when = Text(ShortDate(mail.Received), 11, Theme.GreyBrush);
        when.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(when, Dock.Right);
        header.Children.Add(when);
        header.Children.Add(Text(mail.Sender.Length > 0 ? mail.Sender : mail.App, 12, mail.IsUnread ? Theme.WhiteBrush : Theme.GreyBrush));

        var subject = Text(mail.Subject, 13, Theme.WhiteBrush);
        subject.Margin = new Thickness(0, 2, 0, 0);
        if (mail.IsUnread)
            subject.FontWeight = FontWeights.SemiBold;
        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(subject);

        var row = new Border
        {
            Child = content,
            Background = isSelected ? Theme.SurfaceBrush : Brushes.Transparent,
            BorderBrush = isSelected ? Theme.OutlineBrush : Brushes.Transparent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(12, 8, 12, 8),
            Margin = new Thickness(0, 0, 0, 4),
            Cursor = Cursors.Hand,
        };
        if (!isSelected)
        {
            row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
            row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        }
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Select(mail);
        };
        return row;
    }

    private void ShowDetail(MailItem mail)
    {
        var generation = ++_generation;
        _detail.Children.Clear();

        var subject = Text(mail.Subject, 20, Theme.WhiteBrush);
        subject.FontWeight = FontWeights.SemiBold;
        subject.TextWrapping = TextWrapping.Wrap;
        subject.TextTrimming = TextTrimming.None;
        _detail.Children.Add(subject);

        if (mail.Imap is not { } message)
        {
            Line(mail.Sender.Length > 0 ? mail.Sender : mail.App, 13, Theme.WhiteBrush, top: 10);
            Line(LongDate(mail.Received), 11, Theme.GreyBrush, top: 2);
            Line(mail.App == "Nothing Shell"
                    ? "Questa è la mail di prova delle impostazioni."
                    : $"Questa mail arriva da una notifica di {(mail.App.Length > 0 ? mail.App : "un'app")}: il testo completo è lì.",
                12, Theme.GreyBrush, top: 18);
            var actions = Actions();
            if (mail.AppId is not null && mail.App.Length > 0)
                AddButton(actions, $"APRI {mail.App.ToUpperInvariant()}", () =>
                {
                    if (_mail.OpenSourceApp(mail))
                        Close();
                });
            AddCodeButton(actions, MailCodes.Find(mail.Subject));
            return;
        }

        if (_loaded.TryGetValue(message, out var cached))
        {
            ShowContent(mail, cached);
            return;
        }
        Line(mail.Sender, 13, Theme.WhiteBrush, top: 10);
        Line("Apertura dal server…", 12, Theme.GreyBrush, top: 18);
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
        if (content is null)
        {
            _detail.Children.RemoveAt(_detail.Children.Count - 1);
            Line(error ?? "Impossibile aprire la mail.", 12, Theme.RedBrush, top: 18);
            var actions = Actions();
            AddButton(actions, "RIPROVA", () => ShowDetail(mail));
            AddWebmailButton(actions);
            return;
        }
        ShowContent(mail, content);
    }

    private void ShowContent(MailItem mail, MailContent content)
    {
        _detail.Children.RemoveRange(1, _detail.Children.Count - 1);
        Line(content.From, 13, Theme.WhiteBrush, top: 10);
        if (content.To.Length > 0)
            Line($"A: {content.To}", 11, Theme.GreyBrush, top: 2);
        Line(LongDate(content.Date), 11, Theme.GreyBrush, top: 2);

        var actions = Actions();
        if (MailLinks.Reply(content.ReplyAddress, content.Subject) is { } reply)
            AddButton(actions, "RISPONDI", () => OpenUri(reply));
        AddWebmailButton(actions);
        if (mail.IsUnread)
            AddButton(actions, "SEGNA COME LETTA", async () =>
            {
                if (await _mail.MarkReadAsync(mail) is { } error)
                    Line(error, 12, Theme.RedBrush, top: 6);
            });
        AddCodeButton(actions, MailCodes.Find(content.Subject, content.Body));

        if (content.Attachments.Count > 0)
            Line($"Allegati: {string.Join(" · ", content.Attachments)}  (si aprono dalla webmail)", 11, Theme.GreyBrush, top: 10);

        var body = new TextBox
        {
            Text = content.Body.Length > 0 ? content.Body : "(nessun testo)",
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Foreground = Theme.WhiteBrush,
            CaretBrush = Theme.WhiteBrush,
            SelectionBrush = Theme.RedBrush,
            FontFamily = Theme.BodyFont,
            FontSize = 13,
            Padding = new Thickness(0),
            Margin = new Thickness(0, 16, 0, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden, // scorre con la rotellina, senza barra chiara
            Height = BodyHeight(),
        };
        _detail.Children.Add(body);
    }

    /// <summary>Il corpo occupa lo spazio che resta sotto intestazione e azioni.</summary>
    private double BodyHeight()
    {
        _detail.Measure(new Size(PanelWidth - ListWidth - 90, double.PositiveInfinity));
        return Math.Max(120, PanelHeight - 110 - _detail.DesiredSize.Height);
    }

    private WrapPanel Actions()
    {
        var row = new WrapPanel { Margin = new Thickness(0, 14, 0, 0) };
        _detail.Children.Add(row);
        return row;
    }

    private void AddWebmailButton(WrapPanel actions)
    {
        if (_mail.WebmailName is { } name)
            AddButton(actions, $"APRI IN {name.ToUpperInvariant()}", _mail.OpenWebmail);
    }

    private static void AddCodeButton(WrapPanel actions, string? code)
    {
        if (code is null)
            return;
        Border? button = null;
        button = AddButton(actions, $"COPIA CODICE {code}", () =>
        {
            try
            {
                Clipboard.SetText(code);
                ((TextBlock)button!.Child).Text = "CODICE COPIATO";
            }
            catch (COMException)
            {
                // appunti occupati da un'altra app: si può riprovare
            }
        });
        button.BorderBrush = Theme.RedBrush;
    }

    private static Border AddButton(WrapPanel actions, string text, Action click) =>
        AddButton(actions, text, () =>
        {
            click();
            return Task.CompletedTask;
        });

    private static Border AddButton(WrapPanel actions, string text, Func<Task> click)
    {
        var button = new Border
        {
            BorderBrush = Theme.GreyBrush,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(14, 7, 14, 7),
            Margin = new Thickness(0, 0, 8, 8),
            Background = Brushes.Transparent,
            Cursor = Cursors.Hand,
            Child = Text(text, 11, Theme.WhiteBrush),
        };
        button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
        button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
        button.MouseLeftButtonUp += async (_, e) =>
        {
            e.Handled = true;
            await click();
        };
        actions.Children.Add(button);
        return button;
    }

    private void Line(string text, double size, Brush brush, double top)
    {
        var line = Text(text, size, brush);
        line.TextWrapping = TextWrapping.Wrap;
        line.TextTrimming = TextTrimming.None;
        line.Margin = new Thickness(0, top, 0, 0);
        _detail.Children.Add(line);
    }

    private Border BuildVisualTree()
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        title.Children.Add(new DotMatrixView { Pitch = 3, Dots = DotText.Render("POSTA"), VerticalAlignment = VerticalAlignment.Center });
        _unread.Margin = new Thickness(16, 0, 0, 0);
        title.Children.Add(_unread);

        var header = new Grid { Margin = new Thickness(0, 0, 0, 18), Background = Brushes.Transparent };
        header.Children.Add(title);
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
        close.MouseLeftButtonDown += (_, e) => e.Handled = true; // altrimenti DragMove cattura il mouse
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

        var list = new ScrollViewer
        {
            Width = ListWidth,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            Content = _list,
        };
        var divider = new Rectangle { Width = 1, Fill = Theme.OutlineBrush, Margin = new Thickness(16, 0, 24, 0) };
        var body = new DockPanel();
        DockPanel.SetDock(list, Dock.Left);
        DockPanel.SetDock(divider, Dock.Left);
        body.Children.Add(list);
        body.Children.Add(divider);
        body.Children.Add(_detail);

        var dock = new DockPanel();
        DockPanel.SetDock(header, Dock.Top);
        dock.Children.Add(header);
        dock.Children.Add(body);

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

    private static string ShortDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? local.ToString("HH:mm", NotchUi.Italian)
            : local.Date == DateTime.Today.AddDays(-1) ? "ieri"
            : local.ToString("d MMM", NotchUi.Italian);
    }

    private static string LongDate(DateTimeOffset date)
    {
        var local = date.ToLocalTime();
        return local.Date == DateTime.Today ? $"oggi, {local:HH:mm}"
            : local.ToString("dddd d MMMM yyyy, HH:mm", NotchUi.Italian);
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
