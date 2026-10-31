using NothingShell.Core.Notch;
using NothingShell.Core.Settings;

namespace NothingShell.Core.Tests;

public class NotchCardTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(10);

    [Fact]
    public void PointerKeepsTheCardOpenInsteadOfExpanding()
    {
        var sm = new NotchStateMachine();
        Assert.True(sm.OnCard(T0, TimeSpan.FromSeconds(6)));
        Assert.False(sm.OnPointerEnter());
        Assert.Equal(NotchState.Card, sm.State);
        Assert.False(sm.Tick(T0 + TimeSpan.FromMinutes(1))); // col mouse sopra non scade

        sm.OnPointerLeave(T0 + TimeSpan.FromMinutes(1));
        Assert.True(sm.Tick(T0 + TimeSpan.FromMinutes(1) + NotchStateMachine.CardLeaveDelay));
        Assert.Equal(NotchState.Idle, sm.State);
    }

    [Fact]
    public void CardExpiresAndCanBeDismissed()
    {
        var sm = new NotchStateMachine();
        sm.OnCard(T0, TimeSpan.FromSeconds(6));
        Assert.True(sm.Tick(T0 + TimeSpan.FromSeconds(6)));
        Assert.Equal(NotchState.Idle, sm.State);

        sm.OnActivityChanged(true);
        sm.OnCard(T0, TimeSpan.FromSeconds(6));
        Assert.True(sm.DismissCard());
        Assert.Equal(NotchState.Activity, sm.State);
    }

    [Fact]
    public void ShortAlertForVolume()
    {
        var sm = new NotchStateMachine();
        sm.OnAlert(T0, TimeSpan.FromSeconds(1.5));
        Assert.False(sm.Tick(T0 + TimeSpan.FromSeconds(1)));
        Assert.True(sm.Tick(T0 + TimeSpan.FromSeconds(1.5)));
    }

    [Fact]
    public void CardFitsTheNotchWindow() =>
        Assert.True(NotchLayout.For(NotchState.Card).Height <= NotchLayout.Host.Height);
}

public class PrivacyUsageTests
{
    [Fact]
    public void InUseWhenStartedAndNotStopped()
    {
        var uses = new[]
        {
            new CapabilityUse(@"C:#Users#a#AppData#Roaming#Zoom#bin#Zoom.exe", 134363556418783186, 0, IsPackaged: false),
            new CapabilityUse(@"C:#Program Files#Google#Chrome#Application#chrome.exe", 134333321221793875, 134333380842347658, false),
            new CapabilityUse("Microsoft.WindowsCamera_8wekyb3d8bbwe", 134363556418783186, 0, IsPackaged: true),
            new CapabilityUse("Clipchamp.Clipchamp_yxz26nhyzhsrt", 0, 0, IsPackaged: true), // mai usata
        };

        var inUse = PrivacyUsage.InUse(uses);

        Assert.Equal(["Zoom", "WindowsCamera"], inUse.Select(PrivacyUsage.FallbackName));
        Assert.Equal(@"C:\Users\a\AppData\Roaming\Zoom\bin\Zoom.exe", PrivacyUsage.ExePath(inUse[0]));
        Assert.Null(PrivacyUsage.ExePath(inUse[1]));
    }
}

public class DownloadTrackerTests
{
    private static readonly TimeSpan T0 = TimeSpan.FromSeconds(100);
    private const string PhoneVideo = @"C:\D\Mobile Devices\Ponyo sulla scogliera.mp4";

    [Theory]
    [InlineData(PhoneVideo, true)]
    [InlineData(@"C:\D\mobile devices\a.jpg", true)]
    [InlineData(@"C:\D\a.mp4", false)]
    [InlineData(@"C:\D\Mobile Devices\sotto\a.mp4", false)]
    public void PhoneFilesAreInTheirFolder(string path, bool expected) =>
        Assert.Equal(expected, DownloadTracker.IsPhoneFile(path));

    [Fact]
    public void APhoneFileKeepsItsNameAndExtensionWhileGrowing()
    {
        var tracker = new DownloadTracker();
        Assert.True(tracker.OnPhoneFile(PhoneVideo, 0, T0));
        Assert.False(tracker.OnPhoneFile(PhoneVideo, 0, T0)); // lo stesso file non riparte

        tracker.OnSize(PhoneVideo, 2_500_000, T0 + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var file = Assert.Single(tracker.Active);
        Assert.Equal("Ponyo sulla scogliera.mp4", file.Name);
        Assert.True(file.FromPhone);
        Assert.Equal(2_500_000, file.Bytes);
        Assert.Equal(2_500_000, file.BytesPerSecond);
    }

    [Fact]
    public void WhatAPhoneFileAlreadyHadIsNotCountedAsSpeed()
    {
        var tracker = new DownloadTracker();
        tracker.OnPhoneFile(PhoneVideo, 200_000_000, T0);
        tracker.OnSize(PhoneVideo, 201_000_000, T0 + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        Assert.Equal(1_000_000, Assert.Single(tracker.Active).BytesPerSecond);
    }

    [Fact]
    public void APhoneFileArrivesWhenItStaysStill()
    {
        var tracker = new DownloadTracker();
        tracker.OnPhoneFile(PhoneVideo, 0, T0);
        tracker.OnSize(PhoneVideo, 1234, T0, TimeSpan.FromSeconds(1));

        // Le pause fra una raffica e l'altra non sono la fine
        Assert.Empty(tracker.TakeSettledPhoneFiles(T0 + DownloadTracker.PhoneSettleTime));
        tracker.OnSize(PhoneVideo, 596_682_366, T0 + DownloadTracker.PhoneSettleTime, TimeSpan.FromSeconds(1));
        Assert.Empty(tracker.TakeSettledPhoneFiles(T0 + DownloadTracker.PhoneSettleTime + TimeSpan.FromSeconds(10)));

        var later = T0 + DownloadTracker.PhoneSettleTime + DownloadTracker.PhoneSettleTime + TimeSpan.FromSeconds(1);
        Assert.Equal([PhoneVideo], tracker.TakeSettledPhoneFiles(later));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void AnEmptyPhoneFileHasNotArrivedAndIsDroppedWhenStalled()
    {
        var tracker = new DownloadTracker();
        tracker.OnPhoneFile(PhoneVideo, 0, T0);
        Assert.Empty(tracker.TakeSettledPhoneFiles(T0 + TimeSpan.FromMinutes(1)));
        Assert.Single(tracker.Active);
        Assert.True(tracker.Prune(T0 + DownloadTracker.StallTimeout + TimeSpan.FromSeconds(1)));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void APhoneFileStoppedHalfwayHasNotArrivedAndCanResume()
    {
        var tracker = new DownloadTracker();
        tracker.OnPhoneFile(PhoneVideo, 0, T0);
        tracker.OnSize(PhoneVideo, 325_300_000, T0, TimeSpan.FromSeconds(1));

        Assert.Empty(tracker.TakeSettledPhoneFiles(T0 + TimeSpan.FromMinutes(1)));
        Assert.True(tracker.Prune(T0 + DownloadTracker.StallTimeout + TimeSpan.FromSeconds(1)));
        Assert.Empty(tracker.Active);

        // Riparte: lo stesso file torna in elenco, e ciò che aveva già non conta come velocità
        Assert.True(tracker.OnPhoneFile(PhoneVideo, 325_400_000, T0 + TimeSpan.FromMinutes(5)));
        Assert.Equal(0, Assert.Single(tracker.Active).BytesPerSecond);
    }

    [Fact]
    public void ACancelledPhoneFileDoesNotArrive()
    {
        var tracker = new DownloadTracker();
        tracker.OnPhoneFile(PhoneVideo, 1234, T0);
        Assert.True(tracker.OnDeleted(PhoneVideo));
        Assert.Empty(tracker.TakeSettledPhoneFiles(T0 + TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void ChromeDownloadFromUnconfirmedToFinalFile()
    {
        var tracker = new DownloadTracker();
        Assert.True(tracker.OnCreated(@"C:\D\Unconfirmed 123456.crdownload", T0));
        Assert.Equal("Download", Assert.Single(tracker.Active).Name);

        Assert.Null(tracker.OnRenamed(@"C:\D\Unconfirmed 123456.crdownload", @"C:\D\report.pdf.crdownload", T0));
        tracker.OnSize(@"C:\D\report.pdf.crdownload", 2_000_000, T0 + TimeSpan.FromSeconds(1), TimeSpan.FromSeconds(1));
        var active = Assert.Single(tracker.Active);
        Assert.Equal("report.pdf", active.Name);
        Assert.Equal(2_000_000, active.Bytes);
        Assert.Equal(2_000_000, active.BytesPerSecond);

        Assert.Equal(@"C:\D\report.pdf", tracker.OnRenamed(@"C:\D\report.pdf.crdownload", @"C:\D\report.pdf", T0));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void FirefoxPartFile()
    {
        var tracker = new DownloadTracker();
        Assert.False(tracker.OnCreated(@"C:\D\foto.zip", T0)); // Firefox crea anche il file vuoto finale
        Assert.True(tracker.OnCreated(@"C:\D\foto.zip.part", T0));
        Assert.Equal(@"C:\D\foto.zip", tracker.OnRenamed(@"C:\D\foto.zip.part", @"C:\D\foto.zip", T0));
    }

    [Fact]
    public void CancelledAndStalledDownloadsDisappear()
    {
        var tracker = new DownloadTracker();
        tracker.OnCreated(@"C:\D\a.crdownload", T0);
        tracker.OnCreated(@"C:\D\b.crdownload", T0);
        Assert.True(tracker.OnDeleted(@"C:\D\a.crdownload"));

        Assert.False(tracker.Prune(T0 + TimeSpan.FromMinutes(1)));
        Assert.True(tracker.Prune(T0 + DownloadTracker.StallTimeout + TimeSpan.FromSeconds(1)));
        Assert.Empty(tracker.Active);
    }

    [Fact]
    public void RenamingAnUnrelatedFileIsNotACompletedDownload() =>
        Assert.Null(new DownloadTracker().OnRenamed(@"C:\D\a.txt", @"C:\D\b.txt", T0));

    [Theory]
    [InlineData(500, "1 KB")]
    [InlineData(870_000, "849 KB")]
    [InlineData(13_002_342, "12,4 MB")]
    [InlineData(734_003_200, "700 MB")]
    [InlineData(1_288_490_189, "1,2 GB")]
    public void BytesInItalian(long bytes, string text) => Assert.Equal(text, DownloadTracker.FormatBytes(bytes));

    [Theory]
    [InlineData(870_000, "849K")]
    [InlineData(4_404_019, "4,2M")]
    [InlineData(130_023_424, "124M")]
    [InlineData(1_288_490_189, "1,2G")]
    public void ShortBytesForTheCompactNotch(long bytes, string text) => Assert.Equal(text, DownloadTracker.FormatShort(bytes));
}

public class LiveActivitySettingsTests
{
    [Fact]
    public void ActivitiesAreOnByDefaultAndRoundTrip()
    {
        Assert.True(ShellSettings.Default.IsActivityEnabled(LiveActivity.Volume));
        var settings = ShellSettings.Default.WithActivity(LiveActivity.Volume, false);
        var json = ShellSettingsStore.Serialize(settings);

        Assert.Contains("\"volume\"", json, StringComparison.Ordinal);
        var loaded = ShellSettingsStore.Deserialize(json);
        Assert.False(loaded.IsActivityEnabled(LiveActivity.Volume));
        Assert.True(loaded.IsActivityEnabled(LiveActivity.Bluetooth));
    }
}
