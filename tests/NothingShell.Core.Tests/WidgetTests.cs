using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Sensors;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Tests;

public class DotBarTests
{
    [Theory]
    [InlineData(0.0, 0)]
    [InlineData(-1.0, 0)]
    [InlineData(double.NaN, 0)]
    [InlineData(0.001, 1)] // un valore positivo accende sempre almeno un punto
    [InlineData(0.5, 10)]
    [InlineData(1.0, 20)]
    [InlineData(3.0, 20)]
    public void LitColumns(double fraction, int expected) => Assert.Equal(expected, DotBar.LitColumns(fraction, 20));

    [Fact]
    public void RenderFillsFromTheLeftOnEveryRow()
    {
        var bar = DotBar.Render(0.5, 4, rows: 2);
        Assert.Equal("##..\n##..", bar.ToString());
    }

    [Fact]
    public void PercentGlyphExists() => Assert.True(DotFont.Supports('%'));
}

public class CpuUsageTests
{
    [Fact]
    public void ComputesBusyFraction()
    {
        var a = new CpuTimes(Idle: 1000, Kernel: 1500, User: 500);
        var b = new CpuTimes(Idle: 1300, Kernel: 2000, User: 1000); // totale 1000, idle 300
        Assert.Equal(0.7, CpuUsage.Between(a, b), 6);
    }

    [Fact]
    public void ZeroIntervalIsZero()
    {
        var a = new CpuTimes(1, 2, 3);
        Assert.Equal(0, CpuUsage.Between(a, a));
    }

    [Fact]
    public void CounterResetIsZero() =>
        Assert.Equal(0, CpuUsage.Between(new CpuTimes(500, 900, 100), new CpuTimes(10, 20, 5)));
}

public class BatteryStatusTests
{
    [Fact]
    public void NoBatteryShowsAcAndFullBar()
    {
        Assert.Equal("AC", BatteryStatus.NoBattery.Label);
        Assert.Equal(1, BatteryStatus.NoBattery.BarFraction);
        Assert.False(BatteryStatus.NoBattery.IsLow);
    }

    [Fact]
    public void LowOnlyWhenDischarging()
    {
        Assert.True(new BatteryStatus(true, 15, false).IsLow);
        Assert.False(new BatteryStatus(true, 15, true).IsLow);
        Assert.False(new BatteryStatus(true, 20, false).IsLow);
    }

    [Theory]
    [InlineData(1, 128, 255, false, null, false, true, "ALIMENTAZIONE DI RETE")] // desktop senza batteria
    [InlineData(255, 255, 255, false, null, false, true, "ALIMENTAZIONE DI RETE")] // stato sconosciuto
    [InlineData(0, 1, 76, true, 76, false, false, "SU BATTERIA")]
    [InlineData(1, 8, 40, true, 40, true, true, "IN CARICA")]
    [InlineData(1, 1, 100, true, 100, false, true, "COLLEGATO")]                 // collegato e pieno
    [InlineData(0, 1, 255, true, null, false, false, "SU BATTERIA")]             // percentuale sconosciuta
    public void FromWin32(byte ac, byte flag, byte pct, bool has, int? percent, bool charging, bool plugged, string text)
    {
        var status = BatteryStatus.FromWin32(ac, flag, pct);
        Assert.Equal(new BatteryStatus(has, percent, charging, plugged), status);
        Assert.Equal(text, status.StateText);
    }

    [Fact]
    public void NotLowWhenPluggedIn() => Assert.False(new BatteryStatus(true, 10, false, PluggedIn: true).IsLow);

    [Fact]
    public void UnknownPercent()
    {
        var s = new BatteryStatus(true, null, false);
        Assert.Equal("--", s.Label);
        Assert.Equal(0, s.BarFraction);
    }
}

public class MonthCalendarTests
{
    [Fact]
    public void October2026StartsOnThursday()
    {
        var weeks = MonthCalendar.Weeks(2026, 10);
        Assert.Equal(5, weeks.Length);
        Assert.Equal([0, 0, 0, 1, 2, 3, 4], weeks[0]);
        Assert.Equal([26, 27, 28, 29, 30, 31, 0], weeks[4]);
    }

    [Fact]
    public void February2027FitsInFourWeeks()
    {
        // 1 febbraio 2027 è lunedì, 28 giorni
        var weeks = MonthCalendar.Weeks(2027, 2);
        Assert.Equal(4, weeks.Length);
        Assert.Equal(1, weeks[0][0]);
        Assert.Equal(28, weeks[3][6]);
    }

    [Fact]
    public void EveryDayAppearsOnce()
    {
        var days = MonthCalendar.Weeks(2026, 3).SelectMany(w => w).Where(d => d > 0).ToArray();
        Assert.Equal(Enumerable.Range(1, 31), days);
    }
}

public class WidgetLayoutTests
{
    private static readonly Rect2 WorkArea = new(0, 0, 1536, 912);
    private static readonly Rect2 Screen = new(0, 0, 1536, 960);

    [Fact]
    public void DefaultLayoutIsInsideWorkAreaAndHasNoOverlaps()
    {
        var layout = WidgetLayout.Default(WorkArea);
        Assert.Equal(Enum.GetValues<WidgetKind>().Length, layout.Count);
        foreach (var p in layout)
        {
            Assert.True(p.Bounds.X >= WorkArea.X && p.Bounds.Right <= WorkArea.Right, p.Kind.ToString());
            Assert.True(p.Bounds.Y >= WorkArea.Y && p.Bounds.Bottom <= WorkArea.Bottom, p.Kind.ToString());
            Assert.Equal(0, p.X % WidgetLayout.SnapGrid);
        }
        for (var i = 0; i < layout.Count; i++)
            for (var j = i + 1; j < layout.Count; j++)
                Assert.False(layout[i].Bounds.IntersectsWith(layout[j].Bounds), $"{layout[i].Kind} / {layout[j].Kind}");
    }

    [Fact]
    public void SettleSnapsAndClamps()
    {
        var settled = WidgetLayout.Settle(new WidgetPlacement(WidgetKind.Battery, 1500, -30), Screen);
        Assert.Equal(1536 - 168, settled.X);
        Assert.Equal(0, settled.Y);

        var snapped = WidgetLayout.Settle(new WidgetPlacement(WidgetKind.Battery, 101, 203), Screen);
        Assert.Equal(104, snapped.X);
        Assert.Equal(200, snapped.Y);
    }

    [Fact]
    public void NormalizeFillsMissingDropsDuplicatesAndRescuesOffscreen()
    {
        var saved = new[]
        {
            new WidgetPlacement(WidgetKind.Clock, 5000, 5000), // monitor scollegato
            new WidgetPlacement(WidgetKind.Clock, 10, 10),     // duplicato: ignorato
            new WidgetPlacement((WidgetKind)99, 0, 0),         // tipo sconosciuto
        };
        var result = WidgetLayout.Normalize(saved, WorkArea, Screen);

        Assert.Equal(Enum.GetValues<WidgetKind>(), result.Select(p => p.Kind));
        var clock = result.Single(p => p.Kind == WidgetKind.Clock);
        Assert.Equal(Screen.Right - WidgetSizes.For(WidgetKind.Clock).Width, clock.X);
        Assert.Equal(Screen.Bottom - WidgetSizes.For(WidgetKind.Clock).Height, clock.Y);
    }
}

public class WidgetSettingsStoreTests
{
    [Fact]
    public void RoundTrip()
    {
        var settings = new WidgetSettings(true, [new(WidgetKind.Calendar, 16, 32)]);
        var back = WidgetSettingsStore.Deserialize(WidgetSettingsStore.Serialize(settings));
        Assert.True(back.Locked);
        Assert.Equal(settings.Widgets, back.Widgets);
    }

    [Fact]
    public void JsonUsesReadableNames()
    {
        var json = WidgetSettingsStore.Serialize(new WidgetSettings(false, [new(WidgetKind.Clock, 1, 2)]));
        Assert.Contains("\"kind\": \"Clock\"", json, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("non json")]
    [InlineData("{\"locked\": true}")]
    [InlineData("null")]
    public void InvalidJsonIsEmpty(string json) =>
        Assert.Empty(WidgetSettingsStore.Deserialize(json).Widgets);

    [Fact]
    public void SaveAndLoadFromDisk()
    {
        var dir = Path.Combine(Path.GetTempPath(), "nothing-shell-tests", Guid.NewGuid().ToString("N"));
        var store = new WidgetSettingsStore(Path.Combine(dir, "widgets.json"));
        try
        {
            Assert.Empty(store.Load().Widgets);
            store.Save(new WidgetSettings(false, [new(WidgetKind.System, 8, 8)]));
            Assert.Equal(WidgetKind.System, store.Load().Widgets.Single().Kind);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
