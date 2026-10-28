using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using NothingShell.App.Rendering;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>
/// Pagina Posta: le ultime 3 mail (mittente grigio, oggetto bianco, punto rosso se non letta) o il motivo per
/// cui mancano. Un clic su una mail la apre.
/// </summary>
public sealed class MailPage : NotchPageView
{
    private const int Shown = 3;

    private readonly StackPanel _list = new();

    public MailPage()
    {
        Children.Add(_list);
        Update([], "Nessuna mail recente");
    }

    public override NotchPage Page => NotchPage.Mail;

    public event Action<MailItem>? Opened;

    /// <param name="mails">Dalla più recente.</param>
    /// <param name="emptyMessage">Testo da mostrare se non ci sono mail (es. accesso alle notifiche mancante).</param>
    public void Update(IReadOnlyList<MailItem> mails, string emptyMessage)
    {
        ArgumentNullException.ThrowIfNull(mails);
        _list.Children.Clear();
        if (mails.Count == 0)
        {
            var empty = NotchUi.Text(13, Theme.GreyBrush, emptyMessage);
            empty.TextWrapping = TextWrapping.Wrap;
            empty.Margin = new Thickness(0, 30, 0, 0);
            empty.TextAlignment = TextAlignment.Center;
            _list.Children.Add(empty);
            return;
        }

        foreach (var mail in mails.Take(Shown))
            _list.Children.Add(Row(mail));
    }

    private Border Row(MailItem mail)
    {
        var header = new DockPanel();
        if (mail.IsUnread)
        {
            var dot = new Ellipse { Width = 6, Height = 6, Fill = Theme.RedBrush, Margin = new Thickness(0, 0, 6, 0) };
            DockPanel.SetDock(dot, Dock.Left);
            header.Children.Add(dot);
        }
        var time = NotchUi.Text(11, Theme.GreyBrush, mail.Received.ToLocalTime().ToString("HH:mm", NotchUi.Italian));
        time.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(time, Dock.Right);
        header.Children.Add(time);
        header.Children.Add(NotchUi.Text(11, Theme.GreyBrush, mail.Sender.Length > 0 ? mail.Sender : mail.App));

        var content = new StackPanel();
        content.Children.Add(header);
        content.Children.Add(NotchUi.Text(13, Theme.WhiteBrush, mail.Subject));
        var row = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(-8, 0, -8, 4),
            Cursor = Cursors.Hand,
            ToolTip = mail.Imap is not null ? "Apri la mail" : mail.AppId is not null ? $"Apri {mail.App}" : null,
        };
        row.MouseEnter += (_, _) => row.Background = Theme.SurfaceBrush;
        row.MouseLeave += (_, _) => row.Background = Brushes.Transparent;
        row.MouseLeftButtonUp += (_, e) =>
        {
            e.Handled = true;
            Opened?.Invoke(mail);
        };
        return row;
    }
}
