using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class MailHtmlTests
{
    private const string Consent =
        "<html><head><meta http-equiv=\"refresh\" content=\"0;url=https://evil.example\"><script>alert(1)</script></head>" +
        "<body onload=\"steal()\"><p>Autorizzi l'accesso?</p>" +
        "<a href=\"https://auth.example.com/allow?t=1\" style=\"background:#1a73e8\">Allow</a> " +
        "<a href=\"https://auth.example.com/deny?t=1\">Deny</a>" +
        "<a href=\"javascript:alert(2)\">x</a><iframe src=\"https://x.example\"></iframe>" +
        "<form action=\"https://x.example\"><input></form><img src=\"https://track.example/p.gif\"></body></html>";

    [Fact]
    public void DocumentKeepsLinksAndButtonsButDropsScriptsAndRedirects()
    {
        var doc = MailHtml.Document(Consent, remoteContent: false);
        Assert.Contains("href=\"https://auth.example.com/allow?t=1\"", doc);
        Assert.Contains(">Allow</a>", doc);
        Assert.Contains(">Deny</a>", doc);
        Assert.Contains("background:#1a73e8", doc);
        Assert.DoesNotContain("<script", doc, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("alert", doc);
        Assert.DoesNotContain("refresh", doc);
        Assert.DoesNotContain("onload", doc);
        Assert.DoesNotContain("<iframe", doc);
        Assert.DoesNotContain("<form", doc);
        Assert.Contains("<base target=\"_blank\">", doc);
        Assert.Contains("script-src 'none'", doc);
        Assert.Contains("img-src data:;", doc); // immagini remote bloccate
    }

    [Fact]
    public void RemoteContentOnlyWhenAsked()
    {
        Assert.True(MailHtml.HasRemoteContent(Consent));
        Assert.True(MailHtml.HasRemoteContent("<td style=\"background:url('https://x/y.png')\">"));
        Assert.False(MailHtml.HasRemoteContent("<a href=\"https://x\">link</a><img src=\"data:image/png;base64,AA\">"));
        Assert.Contains("img-src data: https: http:;", MailHtml.Document(Consent, remoteContent: true));
    }

    [Fact]
    public void InlineImagesReplaceCidReferences()
    {
        var html = MailHtml.InlineImages("<img src=\"cid:logo@x\"><img src='cid:missing'>",
            new Dictionary<string, string> { ["LOGO@x"] = "data:image/png;base64,AA" });
        Assert.Equal("<img src=\"data:image/png;base64,AA\"><img src='cid:missing'>", html);
    }

    [Theory]
    [InlineData("https://x.example/a", true)]
    [InlineData("mailto:a@b.it", true)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/system32/calc.exe", false)]
    [InlineData("data:text/html,hi", false)]
    [InlineData("relative/path", false)]
    public void OnlyWebAndMailLinksOpen(string uri, bool openable) =>
        Assert.Equal(openable, MailHtml.Openable(uri) is not null);

    [Fact]
    public void LinkifyFindsWebAndMailAddresses()
    {
        var runs = MailHtml.Linkify("Approva qui: https://auth.example.com/ok?id=7. Oppure (www.example.it) o scrivi a help@example.com!");
        var links = runs.Where(r => r.Link is not null).ToList();
        Assert.Equal(3, links.Count);
        Assert.Equal("https://auth.example.com/ok?id=7", links[0].Text);
        Assert.Equal("www.example.it", links[1].Text);
        Assert.Equal("https://www.example.it/", links[1].Link!.AbsoluteUri);
        Assert.Equal("mailto:help@example.com", links[2].Link!.OriginalString);
        Assert.Equal("Approva qui: https://auth.example.com/ok?id=7. Oppure (www.example.it) o scrivi a help@example.com!",
            string.Concat(runs.Select(r => r.Text))); // il testo resta identico
    }

    [Fact]
    public void LinkifyKeepsBalancedParentheses()
    {
        var link = MailHtml.Linkify("vedi https://en.wikipedia.org/wiki/Foo_(bar) ora").Single(r => r.Link is not null);
        Assert.Equal("https://en.wikipedia.org/wiki/Foo_(bar)", link.Text);
    }
}
