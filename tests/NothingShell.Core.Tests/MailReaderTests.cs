using NothingShell.Core.Notch;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class MailReaderTests
{
    private static readonly DateTime Today = new(2026, 10, 30);

    private static MailItem Mail(string sender, string subject, DateTime received, string? address = null) =>
        new(sender, subject, "Outlook", new DateTimeOffset(received)) { SenderAddress = address };

    [Fact]
    public void LayoutStaysInsideLimitsAndScreen()
    {
        var huge = new MailReaderLayout(5000, 100, 900, Left: 4000, Top: -300, BodySize: 40).Fit(0, 0, 1920, 1040);
        Assert.Equal(1920, huge.Width);
        Assert.Equal(MailReaderLayout.MinHeight, huge.Height);
        Assert.Equal(MailReaderLayout.MaxList, huge.ListWidth);
        Assert.Equal(0, huge.Left); // non esce a destra
        Assert.Equal(0, huge.Top);
        Assert.Equal(MailReaderLayout.MaxBody, huge.BodySize);

        // l'elenco lascia almeno 320 DIP alla mail
        var narrow = new MailReaderLayout(600, 500, 400).Fit(0, 0, 1920, 1040);
        Assert.Equal(280, narrow.ListWidth, 3);
        Assert.Null(narrow.Left);

        // un monitor a sinistra (coordinate negative) resta valido
        var left = MailReaderLayout.Default with { Left = -1500, Top = 100 };
        Assert.Equal(-1500, left.Fit(-1920, 0, 3840, 1080).Left);
    }

    [Fact]
    public void LayoutSurvivesSettingsRoundTrip()
    {
        var settings = ShellSettings.Default with { MailReader = new MailReaderLayout(1000, 700, 320, 50, 60, 15) };
        var back = ShellSettingsStore.Deserialize(ShellSettingsStore.Serialize(settings));
        Assert.Equal(settings.MailReader, back.MailReader);
        Assert.Null(ShellSettingsStore.Deserialize(ShellSettingsStore.Serialize(ShellSettings.Default)).MailReader);
    }

    [Fact]
    public void MailsAreGroupedByDayInOrder()
    {
        MailItem[] mails =
        [
            Mail("A", "oggi", Today.AddHours(9)),
            Mail("B", "ieri", Today.AddDays(-1).AddHours(20)),
            Mail("C", "martedì", Today.AddDays(-3)),
            Mail("D", "mese scorso", Today.AddDays(-40)),
            Mail("E", "oggi presto", Today.AddHours(1)),
        ];
        var groups = MailGroups.Of(mails, Today);
        Assert.Equal(["OGGI", "IERI", "QUESTA SETTIMANA", "PRIMA"], groups.Select(g => g.Label));
        Assert.Equal(["A", "E"], groups[0].Mails.Select(m => m.Sender)); // dentro il gruppo resta l'ordine dato
        Assert.Empty(MailGroups.Of([], Today));
    }

    [Theory]
    [InlineData("Mario Rossi", "MR")]
    [InlineData("Mario Rossi <mario@x.it>", "MR")]
    [InlineData("\"Studio Bianchi Srl\"", "SS")]
    [InlineData("amazon.it", "A")]
    [InlineData("info@banca.it", "I")]
    [InlineData("GitHub", "G")]
    [InlineData("Ufficio 3", "U")]
    [InlineData("", "?")]
    [InlineData("  ", "?")]
    public void InitialsComeFromTheName(string sender, string expected) => Assert.Equal(expected, MailSearch.Initials(sender));

    [Fact]
    public void SearchMatchesEveryWordIgnoringCaseAndAccents()
    {
        var mail = Mail("Città di Milano", "Avviso di pagamento TARI", Today, "tributi@comune.milano.it");
        Assert.True(MailSearch.Matches(mail, null));
        Assert.True(MailSearch.Matches(mail, "  "));
        Assert.True(MailSearch.Matches(mail, "citta tari"));
        Assert.True(MailSearch.Matches(mail, "TRIBUTI@"));
        Assert.True(MailSearch.Matches(mail, "outlook")); // anche l'app
        Assert.False(MailSearch.Matches(mail, "tari roma"));
    }
}
