using NothingShell.Core.Notch;

namespace NothingShell.Core.Tests;

public class PhoneLinkTests
{
    private static readonly DateTimeOffset Now = new(2026, 10, 29, 21, 0, 0, TimeSpan.FromHours(1));

    [Theory]
    [InlineData("Microsoft.YourPhone_8wekyb3d8bbwe!App", true)]
    [InlineData("MicrosoftWindows.CrossDevice_cw5n1h2txyewy!CrossDeviceService", true)]
    [InlineData("Microsoft.OutlookForWindows_8wekyb3d8bbwe!Microsoft.OutlookforWindows", false)]
    [InlineData(null, false)]
    public void RecognizesPhoneLink(string? appId, bool expected) => Assert.Equal(expected, PhoneLink.IsPhoneLink(appId));

    [Theory]
    [InlineData("Mario Rossi", "Chiamata in arrivo")]
    [InlineData("Chiamata in arrivo", "Mario Rossi")]
    [InlineData("Incoming call", "Mario Rossi")]
    public void IncomingCallInEitherOrder(string first, string second)
    {
        var call = PhoneLink.Parse([first, second], Now)!;
        Assert.Equal(PhoneEventKind.IncomingCall, call.Kind);
        Assert.Equal("Mario Rossi", call.Title);
    }

    [Fact]
    public void CallerInsideTheSameLine()
    {
        var call = PhoneLink.Parse(["Mario Rossi sta chiamando"], Now)!;
        Assert.Equal(PhoneEventKind.IncomingCall, call.Kind);
        Assert.Equal("Mario Rossi", call.Title);
    }

    [Fact]
    public void MissedCall()
    {
        var call = PhoneLink.Parse(["Chiamata persa", "+39 333 1234567"], Now)!;
        Assert.Equal(PhoneEventKind.MissedCall, call.Kind);
        Assert.Equal("+39 333 1234567", call.Title);
    }

    [Fact]
    public void MessageWithAppAttribution()
    {
        var message = PhoneLink.Parse(["Giulia", "Ci vediamo alle 8\nporta le chiavi", "WhatsApp"], Now)!;
        Assert.Equal(PhoneEventKind.Message, message.Kind);
        Assert.Equal("Giulia", message.Title);
        Assert.Equal("Ci vediamo alle 8", message.Text);
        Assert.Equal("WhatsApp", message.App);
        Assert.Equal("Giulia: Ci vediamo alle 8", PhoneLink.Summary(message));
    }

    [Fact]
    public void SingleLineAndEmpty()
    {
        Assert.Equal("Backup completato", PhoneLink.Summary(PhoneLink.Parse(["  Backup completato "], Now)!));
        Assert.Null(PhoneLink.Parse(["", "  "], Now));
    }
}
