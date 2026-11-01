using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Media;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Home: musica in corso (titolo che scorre, controlli) oppure ora grande e data, poi il campo per chiedere
/// (docs/16-assistente-notch.md) e sotto il prossimo appuntamento (docs/11-agenda-pannello-rapido.md) o, mentre si scrive,
/// quale scorciatoia userebbe Invio.
/// </summary>
public sealed class HomePage : NotchPageView
{
    private const double TitlePitch = 3;
    private static readonly int TitleViewportColumns = (int)(NotchUi.PageWidth / TitlePitch);

    private readonly IMediaService _media;
    private readonly StackPanel _mediaPanel = new();
    private readonly TextBlock _source = NotchUi.Text(11, Theme.MutedBrush);
    private readonly DotMatrixView _headerClock = new() { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DotMatrixView _title = new() { Pitch = TitlePitch, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _artist = NotchUi.Text(13, Theme.MutedBrush);
    private readonly DotMatrixView _playPauseIcon = new() { Pitch = 3, Dots = DotGrid.From(DotIcons.Play) };

    private readonly StackPanel _idlePanel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _bigClock = new() { Pitch = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _date = new() { Pitch = 2, DotColor = Theme.Muted, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly TextBlock _next = NotchUi.Text(12, Theme.MutedBrush);

    private readonly Border _askBox = new()
    {
        Background = Theme.SurfaceBrush,
        CornerRadius = new CornerRadius(4),
        Height = 32,
        Padding = new Thickness(14, 0, 14, 0),
        Margin = new Thickness(0, 10, 0, 0),
    };
    private readonly TextBox _ask = new()
    {
        Background = Brushes.Transparent,
        BorderThickness = new Thickness(0),
        Foreground = Theme.InkBrush,
        CaretBrush = Theme.InkBrush,
        FontFamily = Theme.BodyFont,
        FontSize = 13,
        VerticalContentAlignment = VerticalAlignment.Center,
    };
    private readonly TextBlock _askPlaceholder = NotchUi.Text(13, Theme.MutedBrush, "Chiedi qualcosa, es. collegati al computer di Luca");
    private readonly TextBlock _preview = NotchUi.Text(12, Theme.InkBrush);
    private bool _hasNext;

    private readonly DispatcherTimer _marqueeTimer = new() { Interval = TimeSpan.FromMilliseconds(45) };
    private MediaSnapshot? _snapshot;
    private int _marqueeOffset;
    private bool _shown;

    public HomePage(IMediaService media)
    {
        ArgumentNullException.ThrowIfNull(media);
        _media = media;

        var header = new Grid { Margin = new Thickness(0, 0, 0, 12) };
        _source.VerticalAlignment = VerticalAlignment.Center;
        header.Children.Add(_source);
        header.Children.Add(_headerClock);
        _artist.Margin = new Thickness(0, 6, 0, 0);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
        };
        controls.Children.Add(NotchUi.Button(Icon(DotIcons.Previous), () => _media.PreviousAsync()));
        controls.Children.Add(NotchUi.Button(_playPauseIcon, () => _media.PlayPauseAsync()));
        controls.Children.Add(NotchUi.Button(Icon(DotIcons.Next), () => _media.NextAsync()));

        _mediaPanel.Children.Add(header);
        _mediaPanel.Children.Add(_title);
        _mediaPanel.Children.Add(_artist);
        _mediaPanel.Children.Add(controls);

        _idlePanel.Margin = new Thickness(0, 8, 0, 0);
        _date.Margin = new Thickness(0, 14, 0, 0);
        _idlePanel.Children.Add(_bigClock);
        _idlePanel.Children.Add(_date);

        RowDefinitions.Add(new RowDefinition());
        RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        RowDefinitions.Add(new RowDefinition { Height = new GridLength(26) });
        Children.Add(_mediaPanel);
        Children.Add(_idlePanel);

        _askPlaceholder.VerticalAlignment = VerticalAlignment.Center;
        _askPlaceholder.IsHitTestVisible = false;
        _askPlaceholder.Margin = new Thickness(2, 0, 0, 0); // dove comincia il testo nella casella
        var askContent = new Grid();
        askContent.Children.Add(_ask);
        askContent.Children.Add(_askPlaceholder);
        _askBox.Child = askContent;
        Grid.SetRow(_askBox, 1);
        Children.Add(_askBox);

        foreach (var line in new[] { _next, _preview })
        {
            line.HorizontalAlignment = HorizontalAlignment.Center;
            line.VerticalAlignment = VerticalAlignment.Center;
            line.MaxWidth = NotchUi.PageWidth;
            line.Visibility = Visibility.Collapsed;
            Grid.SetRow(line, 2);
            Children.Add(line);
        }

        _ask.PreviewMouseLeftButtonDown += (_, _) => AskClicked?.Invoke();
        _ask.TextChanged += (_, _) => OnAskChanged();
        _ask.PreviewKeyDown += OnAskKey;
        ShowMedia(null);
        _marqueeTimer.Tick += (_, _) => RenderTitle(++_marqueeOffset);
    }

    public override NotchPage Page => NotchPage.Home;

    /// <summary>Clic sul campo: il notch non prende il focus da solo, va chiesto prima che si possa scrivere.</summary>
    public event Action? AskClicked;

    /// <summary>Invio nel campo: il testo e se si è premuto Ctrl per scegliere fra le scorciatoie.</summary>
    public event Action<string, bool>? AskSubmitted;

    public event Action? AskCancelled;

    /// <summary>La riga sotto il campo, per il testo scritto finora.</summary>
    public Func<string, string>? AskPreview { get; set; }

    /// <summary>Il mouse è sul campo: trascinando si seleziona il testo, non si cambia pagina.</summary>
    public bool IsMouseOverAsk => _askBox.IsMouseOver;

    /// <summary>Scrive nel campo senza dargli il focus (per <c>--snapshot</c>).</summary>
    public void SetAskText(string text) => _ask.Text = text;

    public void FocusAsk() => Keyboard.Focus(_ask);

    public bool IsAskFocused => _ask.IsKeyboardFocused;

    public void ClearAsk() => _ask.Clear();

    public void SetTime(DateTime now)
    {
        var hhmm = now.ToString("HH:mm", NotchUi.Italian);
        _headerClock.Dots = DotText.Render(hhmm);
        _bigClock.Dots = DotText.Render(hhmm);
        _date.Dots = DotText.Render(now.ToString("dddd d MMMM", NotchUi.Italian));
    }

    /// <summary>Sotto la data: "14:30 Riunione · tra 25 min", in rosso quando manca poco o è in corso.</summary>
    public void SetNext(CalendarEvent? next, DateTimeOffset now)
    {
        _hasNext = next is not null;
        UpdateLine();
        if (next is null)
            return;
        _next.Text = $"{Agenda.When(next, now)}  {next.Title} · {Agenda.Relative(next, now)}";
        _next.Foreground = next.Start - now <= Agenda.ReminderLead ? Theme.AccentBrush : Theme.MutedBrush;
    }

    public void ShowMedia(MediaSnapshot? snapshot)
    {
        if (snapshot?.Title != _snapshot?.Title)
            _marqueeOffset = 0;
        _snapshot = snapshot;

        _mediaPanel.Visibility = snapshot is null ? Visibility.Collapsed : Visibility.Visible;
        _idlePanel.Visibility = snapshot is null ? Visibility.Visible : Visibility.Collapsed;
        if (snapshot is not null)
        {
            _source.Text = snapshot.SourceApp;
            _artist.Text = snapshot.Artist;
            _playPauseIcon.Dots = DotGrid.From(snapshot.IsPlaying ? DotIcons.Pause : DotIcons.Play);
        }
        UpdateLine();
        UpdateMarquee();
    }

    public override void OnShown()
    {
        _shown = true;
        UpdateMarquee();
    }

    public override void OnHidden()
    {
        _shown = false;
        UpdateMarquee();
    }

    private void OnAskChanged()
    {
        var text = _ask.Text.Trim();
        _askPlaceholder.Visibility = _ask.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        _preview.Text = text.Length == 0 ? string.Empty : AskPreview?.Invoke(text) ?? string.Empty;
        UpdateLine();
    }

    private void OnAskKey(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            e.Handled = true;
            AskCancelled?.Invoke();
        }
        else if (e.Key == Key.Enter && _ask.Text.Trim() is { Length: > 0 } text)
        {
            e.Handled = true;
            AskSubmitted?.Invoke(text, Keyboard.Modifiers.HasFlag(ModifierKeys.Control));
        }
    }

    /// <summary>Una riga sola sotto il campo: mentre si scrive conta la scorciatoia, altrimenti l'appuntamento.</summary>
    private void UpdateLine()
    {
        var asking = _preview.Text.Length > 0;
        _preview.Visibility = asking ? Visibility.Visible : Visibility.Collapsed;
        _next.Visibility = !asking && _hasNext && _snapshot is null ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdateMarquee()
    {
        var scroll = _shown && _snapshot is { } s && DotText.NeedsMarquee(s.Title, TitleViewportColumns);
        if (scroll)
        {
            _marqueeTimer.Start();
        }
        else
        {
            _marqueeTimer.Stop();
            _marqueeOffset = 0;
        }
        RenderTitle(_marqueeOffset);
    }

    private void RenderTitle(int offset)
    {
        var title = _snapshot?.Title ?? string.Empty;
        _title.Dots = DotText.RenderMarquee(title, Math.Min(TitleViewportColumns, DotText.Measure(title)), offset);
    }

    private static DotMatrixView Icon(DotGlyph glyph) => new() { Pitch = 3, Dots = DotGrid.From(glyph) };
}
