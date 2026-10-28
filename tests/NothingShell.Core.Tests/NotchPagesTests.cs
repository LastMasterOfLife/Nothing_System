using NothingShell.Core.Notch;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class NotchAlertTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);

    [Fact]
    public void AlertShowsThenReturnsToPreviousState()
    {
        var sm = new NotchStateMachine();
        sm.OnActivityChanged(true);
        Assert.True(sm.OnAlert(T0));
        Assert.Equal(NotchState.Alert, sm.State);
        Assert.Equal(T0 + NotchStateMachine.AlertDuration, sm.NextDeadline);

        Assert.False(sm.Tick(T0 + TimeSpan.FromSeconds(5)));
        Assert.True(sm.Tick(T0 + NotchStateMachine.AlertDuration));
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void ActivityChangeDuringAlertDoesNotCutIt()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0);
        Assert.False(sm.OnActivityChanged(true));
        Assert.Equal(NotchState.Alert, sm.State);
        sm.Tick(T0 + NotchStateMachine.AlertDuration);
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void HoverDuringAlertExpandsAndCancelsAlert()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0);
        Assert.True(sm.OnPointerEnter());
        Assert.Null(sm.AlertDeadline);
        Assert.False(sm.Tick(T0 + TimeSpan.FromMinutes(1)));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void AlertDoesNotInterruptExpanded()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        Assert.False(sm.OnAlert(T0));
        Assert.Equal(NotchState.Expanded, sm.State);
    }

    [Fact]
    public void NextDeadlineIsTheEarliest()
    {
        var sm = new NotchStateMachine();
        sm.OnPointerEnter();
        sm.OnPointerLeave(T0);
        Assert.Equal(T0 + NotchStateMachine.LeaveDelay, sm.NextDeadline);
    }
}

public class NotchPagerTests
{
    [Fact]
    public void SwipesMoveOnePageAndStopAtEdges()
    {
        var pager = new NotchPager();
        Assert.False(pager.Apply(SwipeDirection.Previous)); // già alla prima
        Assert.True(pager.Apply(SwipeDirection.Next));
        Assert.Equal(NotchPage.Agenda, pager.Current);
        pager.Show(NotchPage.System);
        Assert.False(pager.Apply(SwipeDirection.Next)); // già all'ultima
        Assert.Equal(4, pager.Index);
    }
}

public class SwipeDetectorTests
{
    private static TimeSpan Ms(int ms) => TimeSpan.FromMilliseconds(ms);

    [Fact]
    public void OneWheelNotchIsOneSwipe()
    {
        var s = new SwipeDetector();
        Assert.Equal(SwipeDirection.Next, s.OnVerticalWheel(-120, Ms(1000)));
        Assert.Equal(SwipeDirection.Previous, s.OnVerticalWheel(120, Ms(2000)));
    }

    [Fact]
    public void TouchpadDeltasAccumulate()
    {
        var s = new SwipeDetector();
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(40, Ms(1000)));
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(40, Ms(1020)));
        Assert.Equal(SwipeDirection.Next, s.OnHorizontalWheel(40, Ms(1040)));
    }

    [Fact]
    public void OneLongSwipeMovesOnlyOnePage()
    {
        var s = new SwipeDetector();
        var fired = 0;
        for (var t = 0; t < 300; t += 10) // 30 eventi da 40 = un gesto lungo e veloce
            if (s.OnHorizontalWheel(40, Ms(1000 + t)) != SwipeDirection.None)
                fired++;
        Assert.Equal(1, fired);
    }

    [Fact]
    public void PausesResetTheSum()
    {
        var s = new SwipeDetector();
        s.OnHorizontalWheel(100, Ms(1000));
        Assert.Equal(SwipeDirection.None, s.OnHorizontalWheel(100, Ms(1000 + 400)));
    }

    [Fact]
    public void DragFiresOnceAndSwallowsTheClick()
    {
        var s = new SwipeDetector();
        s.OnDragStart(200);
        Assert.Equal(SwipeDirection.None, s.OnDragMove(180));
        Assert.Equal(SwipeDirection.Next, s.OnDragMove(150));
        Assert.Equal(SwipeDirection.None, s.OnDragMove(50));
        Assert.True(s.OnDragEnd());

        s.OnDragStart(100);
        Assert.Equal(SwipeDirection.Previous, s.OnDragMove(150));
        s.OnDragEnd();

        s.OnDragStart(100);
        Assert.False(s.OnDragEnd()); // semplice clic
    }
}

public class CountdownTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromMinutes(10);

    [Fact]
    public void CountsDownAndFinishesOnce()
    {
        var c = new Countdown();
        c.Start(TimeSpan.FromMinutes(5), T0);
        Assert.Equal(TimeSpan.FromMinutes(4), c.Remaining(T0 + TimeSpan.FromMinutes(1)));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromMinutes(4)));
        Assert.True(c.CheckFinished(T0 + TimeSpan.FromMinutes(5)));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromMinutes(6)));
        Assert.False(c.IsRunning);
    }

    [Fact]
    public void StoppedTimerHasNothingLeft()
    {
        var c = new Countdown();
        c.Start(TimeSpan.FromMinutes(5), T0);
        c.Stop();
        Assert.Equal(TimeSpan.Zero, c.Remaining(T0));
        Assert.False(c.CheckFinished(T0 + TimeSpan.FromHours(1)));
    }

    [Theory]
    [InlineData(1500, "25:00")]
    [InlineData(59.2, "01:00")]
    [InlineData(0, "00:00")]
    [InlineData(-3, "00:00")]
    [InlineData(3725, "1:02:05")]
    public void Format(double seconds, string expected) =>
        Assert.Equal(expected, Countdown.Format(TimeSpan.FromSeconds(seconds)));
}

public class MailDetectorTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 27, 23, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData("Outlook", true)]
    [InlineData("Posta", true)]
    [InlineData("Mozilla Thunderbird", true)]
    [InlineData("Spotify", false)]
    [InlineData("Brave", false)] // browser senza sito di posta
    public void KnownApps(string app, bool expected) =>
        Assert.Equal(expected, MailDetector.IsMail(app, ["Mario Rossi", "Ciao"]));

    [Fact]
    public void BrowserWithMailSiteIsMail() =>
        Assert.True(MailDetector.IsMail("Brave", ["Mario Rossi", "Riunione\nci vediamo alle 10", "mail.google.com"]));

    [Fact]
    public void ExtraAppsFromSettings() =>
        Assert.True(MailDetector.IsMail("Windows PowerShell", ["x"], ["PowerShell"]));

    [Fact]
    public void ExtractsSenderAndFirstLineOfSubject()
    {
        var mail = MailDetector.Extract("Brave", ["Mario Rossi", "Riunione di domani\nanteprima del testo", "mail.google.com"], Now);
        Assert.Equal(new MailItem("Mario Rossi", "Riunione di domani", "Brave", Now), mail);
    }

    [Fact]
    public void SingleLineIsTheSubject() =>
        Assert.Equal(new MailItem("", "3 nuovi messaggi", "Thunderbird", Now),
            MailDetector.Extract("Thunderbird", ["3 nuovi messaggi"], Now));

    [Fact]
    public void EmptyNotificationIsNull() => Assert.Null(MailDetector.Extract("Outlook", ["  ", ""], Now));
}

public class MailSettingsTests
{
    [Fact]
    public void OldSettingsFileGetsMailDefaults()
    {
        var s = ShellSettingsStore.Deserialize("""{ "notch": true, "bar": false, "disabledWidgets": [] }""");
        Assert.True(s.MailInNotch);
        Assert.False(s.Bar);
    }
}

public class MailSetupTests
{
    [Fact]
    public void CandidatesListExtrasFirstThenUnknownNotifyingApps()
    {
        var candidates = MailDetector.SetupCandidates(
            ["Outlook", "Slack", "Aruba Mail Desktop", "", "slack", "Teams"],
            ["Aruba Mail Desktop"]);

        Assert.Equal([("Aruba Mail Desktop", true), ("Slack", false), ("Teams", false)], candidates);
    }

    [Fact]
    public void KnownClientsNeedNoSetup()
    {
        Assert.True(MailDetector.IsKnownMailApp("Microsoft Outlook"));
        Assert.True(MailDetector.IsKnownMailApp("Mozilla Thunderbird"));
        Assert.False(MailDetector.IsKnownMailApp("Slack"));
    }

    [Fact]
    public void ExtraAppIsAddedOnceAndRemovedIgnoringCase()
    {
        var added = MailDetector.WithExtraApp(["A"], "Webmail", isMail: true);
        Assert.Equal(["A", "Webmail"], added);
        Assert.Equal(["A", "Webmail"], MailDetector.WithExtraApp(added, "webmail", isMail: true).Select(a => a == "webmail" ? "Webmail" : a));
        Assert.Equal(["A"], MailDetector.WithExtraApp(added, "WEBMAIL", isMail: false));
    }
}

public class ImapSetupTests
{
    [Theory]
    [InlineData("mario@gmail.com", "imap.gmail.com")]
    [InlineData("mario@Hotmail.it", "outlook.office365.com")]
    [InlineData("andrea@sinapsi.it", "imap.sinapsi.it")]
    [InlineData("non-un-indirizzo", null)]
    [InlineData("mario@", null)]
    public void SuggestsServerFromAddress(string address, string? server) =>
        Assert.Equal(server, ImapSetup.SuggestServer(address));

    [Fact]
    public void ValidationReportsFirstProblem()
    {
        var ok = new MailAccount("a@b.it", "mail.b.it");
        Assert.Null(ImapSetup.Validate(ok, "pw"));
        Assert.NotNull(ImapSetup.Validate(ok with { Address = "ab.it" }, "pw"));
        Assert.NotNull(ImapSetup.Validate(ok with { Server = " " }, "pw"));
        Assert.NotNull(ImapSetup.Validate(ok with { Port = 0 }, "pw"));
        Assert.NotNull(ImapSetup.Validate(ok, ""));
    }

    [Fact]
    public void LoginDefaultsToAddress()
    {
        Assert.Equal("a@b.it", new MailAccount("a@b.it", "mail.b.it").Login);
        Assert.Equal(@"DOMINIO\a", new MailAccount("a@b.it", "mail.b.it", UserName: @"DOMINIO\a").Login);
    }

    [Fact]
    public void SettingsKeepAccountButNeverPassword()
    {
        var settings = ShellSettings.Default with { MailAccount = new MailAccount("a@b.it", "mail.b.it", 993, "utente") };
        var json = ShellSettingsStore.Serialize(settings);

        Assert.DoesNotContain("password", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(settings.MailAccount, ShellSettingsStore.Deserialize(json).MailAccount);
        Assert.Null(ShellSettingsStore.Deserialize("""{"notch":true,"bar":true,"disabledWidgets":[]}""").MailAccount);
    }
}

public class ImapCursorTests
{
    [Fact]
    public void FirstCheckOnlyPrimes()
    {
        var cursor = new ImapCursor();
        Assert.Empty(cursor.Advance(7, [10, 11, 12]));
        Assert.Equal(12u, cursor.LastUid);
    }

    [Fact]
    public void NewUidsAreReturnedOnceInOrder()
    {
        var cursor = new ImapCursor();
        cursor.Prime(7, 12);
        Assert.Equal([13u, 15u], cursor.Advance(7, [15, 12, 13]));
        Assert.Empty(cursor.Advance(7, [15])); // "16:*" restituisce comunque l'ultimo
    }

    [Fact]
    public void RenumberedMailboxStartsOverWithoutAlerts()
    {
        var cursor = new ImapCursor();
        cursor.Prime(7, 12);
        Assert.Empty(cursor.Advance(8, [1, 2]));
        Assert.Equal(8u, cursor.UidValidity);
        Assert.Equal([3u], cursor.Advance(8, [3]));
    }
}

public class MailFeedTests
{
    private static readonly DateTimeOffset T = new(2026, 10, 28, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void SameMailFromTwoSourcesAppearsOnce()
    {
        var imap = new MailItem("Mario Rossi", "Preventivo", "mail.b.it", T);
        var toast = new MailItem("Mario Rossi", "preventivo", "Outlook", T.AddMinutes(1));
        var other = new MailItem("Anna", "Fattura", "Outlook", T.AddMinutes(-5));

        var merged = MailFeed.Merge([imap], [toast, other], 10);

        Assert.Equal(["preventivo", "Fattura"], merged.Select(m => m.Subject));
    }

    [Fact]
    public void SameSubjectHoursApartIsAnotherMail() =>
        Assert.False(MailFeed.IsSame(new MailItem("A", "Ciao", "x", T), new MailItem("A", "Ciao", "x", T.AddHours(2))));

    [Fact]
    public void DuplicateKeepsTheMailThatCanBeReopened()
    {
        var imap = new MailItem("Mario Rossi", "Preventivo", "mail.b.it", T) { Imap = new ImapMessageRef(7, 42) };
        var toast = new MailItem("Mario Rossi", "preventivo", "Outlook", T.AddMinutes(1));

        var merged = MailFeed.Merge([imap], [toast], 10);

        Assert.Equal(new ImapMessageRef(7, 42), Assert.Single(merged).Imap);
    }
}

public class MailTextTests
{
    [Fact]
    public void HtmlBecomesReadableLines()
    {
        const string html = """
            <html><head><title>x</title><style>p{color:red}</style></head>
            <body><p>Ciao&nbsp;Mario,</p><div>ecco il <b>preventivo</b> &amp; il listino.<br>A presto</div>
            <!-- tracking --><ul><li>uno</li><li>due</li></ul><script>alert(1)</script></body></html>
            """;

        Assert.Equal("Ciao Mario,\n\necco il preventivo & il listino.\nA presto\n\n• uno\n• due", MailText.FromHtml(html));
    }

    [Fact]
    public void NormalizeKeepsAtMostOneBlankLine() =>
        Assert.Equal("a\n\nb", MailText.Normalize("  a  \r\n\r\n\r\n\t\r\nb\n\n"));
}

public class MailCodesTests
{
    [Theory]
    [InlineData("Il tuo codice di verifica è 482913", "482913")]
    [InlineData("G-731204 è il tuo codice di verifica Google", "731204")]
    [InlineData("Codice OTP: 123 456. Scade tra 10 minuti", "123456")]
    [InlineData("Riunione 2026: codice sala 4821", "4821")]
    [InlineData("Your verification code is 90210", "90210")]
    public void FindsTheCode(string text, string code) => Assert.Equal(code, MailCodes.Find(text));

    [Theory]
    [InlineData("Fattura 2026 n. 123456 del 28/10")] // nessuna parola chiave
    [InlineData("Codice cliente: totale 1500 €")] // importo
    [InlineData("Conferma ordine del 28.10.2026")] // data
    public void IgnoresNumbersThatAreNotCodes(string text) => Assert.Null(MailCodes.Find(text));

    [Fact]
    public void SubjectFirstThenBody() =>
        Assert.Equal("777111", MailCodes.Find("Accesso al tuo account", "Usa il codice 777111 per entrare"));
}

public class MailLinksTests
{
    [Fact]
    public void WebmailOfKnownProviderOrServer()
    {
        Assert.Equal("https://mail.google.com/", MailLinks.Webmail(new MailAccount("a@gmail.com", "imap.gmail.com"))?.ToString());
        Assert.Equal("https://mail.b.it/", MailLinks.Webmail(new MailAccount("a@b.it", "mail.b.it"))?.ToString());
        Assert.Equal("https://posta.b.it/owa/",
            MailLinks.Webmail(new MailAccount("a@b.it", "mail.b.it", Webmail: "posta.b.it/owa/"))?.ToString());
    }

    [Fact]
    public void EdgeWebAppOfTheSiteIsPreferred()
    {
        var apps = new[]
        {
            new NothingShell.Core.Launcher.AppEntry("Microsoft.Office.OUTLOOK.EXE.15", "Outlook"),
            new NothingShell.Core.Launcher.AppEntry("mail.b.it-17472CD0_nb9wzx8vsex3w!App", "Outlook (PWA)"),
        };

        Assert.Equal("Outlook (PWA)", MailLinks.InstalledWebApp(new Uri("https://mail.b.it/owa/"), apps)?.Name);
        Assert.Null(MailLinks.InstalledWebApp(new Uri("https://b.it/"), apps));
    }

    [Fact]
    public void ReplyAddsReOnce()
    {
        Assert.Equal("mailto:mario@b.it?subject=Re%3A%20Preventivo%20%26%20listino", MailLinks.Reply("mario@b.it", "Preventivo & listino"));
        Assert.Equal("mailto:mario@b.it?subject=RE%3A%20x", MailLinks.Reply("mario@b.it", "RE: x"));
        Assert.Null(MailLinks.Reply(null, "x"));
    }
}
