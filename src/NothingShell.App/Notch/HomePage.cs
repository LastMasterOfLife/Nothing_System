using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using NothingShell.App.Rendering;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Media;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Pagina Home: musica in corso (titolo che scorre, controlli) oppure ora grande e data.</summary>
public sealed class HomePage : NotchPageView
{
    private const double TitlePitch = 3;
    private static readonly int TitleViewportColumns = (int)(NotchUi.PageWidth / TitlePitch);

    private readonly IMediaService _media;
    private readonly StackPanel _mediaPanel = new();
    private readonly TextBlock _source = NotchUi.Text(11, Theme.GreyBrush);
    private readonly DotMatrixView _headerClock = new() { Pitch = 2, HorizontalAlignment = HorizontalAlignment.Right };
    private readonly DotMatrixView _title = new() { Pitch = TitlePitch, HorizontalAlignment = HorizontalAlignment.Left };
    private readonly TextBlock _artist = NotchUi.Text(13, Theme.GreyBrush);
    private readonly DotMatrixView _playPauseIcon = new() { Pitch = 3, Dots = DotGrid.From(DotIcons.Play) };

    private readonly StackPanel _idlePanel = new() { HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _bigClock = new() { Pitch = 6, HorizontalAlignment = HorizontalAlignment.Center };
    private readonly DotMatrixView _date = new() { Pitch = 2, DotColor = Theme.Grey, HorizontalAlignment = HorizontalAlignment.Center };

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

        _idlePanel.Margin = new Thickness(0, 22, 0, 0);
        _date.Margin = new Thickness(0, 14, 0, 0);
        _idlePanel.Children.Add(_bigClock);
        _idlePanel.Children.Add(_date);

        Children.Add(_mediaPanel);
        Children.Add(_idlePanel);
        ShowMedia(null);
        _marqueeTimer.Tick += (_, _) => RenderTitle(++_marqueeOffset);
    }

    public override NotchPage Page => NotchPage.Home;

    public void SetTime(DateTime now)
    {
        var hhmm = now.ToString("HH:mm", NotchUi.Italian);
        _headerClock.Dots = DotText.Render(hhmm);
        _bigClock.Dots = DotText.Render(hhmm);
        _date.Dots = DotText.Render(now.ToString("dddd d MMMM", NotchUi.Italian));
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
            _source.Text = snapshot.SourceApp.ToUpperInvariant();
            _artist.Text = snapshot.Artist;
            _playPauseIcon.Dots = DotGrid.From(snapshot.IsPlaying ? DotIcons.Pause : DotIcons.Play);
        }
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
