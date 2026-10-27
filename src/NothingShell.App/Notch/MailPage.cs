using System.Windows;
using System.Windows.Controls;
using NothingShell.App.Rendering;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Pagina Posta: le ultime 3 mail (mittente grigio, oggetto bianco) o il motivo per cui mancano.</summary>
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
        {
            var row = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            var header = new Grid();
            header.Children.Add(NotchUi.Text(11, Theme.GreyBrush, mail.Sender.Length > 0 ? mail.Sender : mail.App));
            var time = NotchUi.Text(11, Theme.GreyBrush, mail.Received.ToLocalTime().ToString("HH:mm", NotchUi.Italian));
            time.HorizontalAlignment = HorizontalAlignment.Right;
            header.Children.Add(time);
            row.Children.Add(header);
            row.Children.Add(NotchUi.Text(13, Theme.WhiteBrush, mail.Subject));
            _list.Children.Add(row);
        }
    }
}
