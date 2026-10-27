using NothingShell.Core.DotMatrix;
using NothingShell.Core.Media;

namespace NothingShell.Core.Tests;

public class SourceNamesTests
{
    [Theory]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData("Microsoft.ZuneMusic_8wekyb3d8bbwe!Microsoft.ZuneMusic", "Media Player")]
    [InlineData("chrome.exe", "Chrome")]
    [InlineData("MSEdge", "Edge")]
    [InlineData(@"C:\Program Files\VLC\vlc.exe", "VLC")]
    [InlineData("SomeVendor.CoolPlayer", "CoolPlayer")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void FriendlyName(string? id, string expected) => Assert.Equal(expected, SourceNames.Friendly(id));
}

public class DotEqualizerTests
{
    [Fact]
    public void BarsStayInsideGridAndAreNeverEmpty()
    {
        var eq = new DotEqualizer(bars: 3, height: 7);
        for (var i = 0; i < 50; i++)
        {
            var frame = eq.NextFrame();
            Assert.Equal(5, frame.Width);
            for (var b = 0; b < 3; b++)
                Assert.Equal(1f, frame[b * 2, 6]); // il punto di base è sempre acceso
            for (var y = 0; y < 7; y++)
            {
                Assert.Equal(0f, frame[1, y]); // colonne di separazione vuote
                Assert.Equal(0f, frame[3, y]);
            }
        }
    }
}
