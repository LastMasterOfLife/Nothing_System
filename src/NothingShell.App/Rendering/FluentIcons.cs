using NothingShell.Core.Design;

namespace NothingShell.App.Rendering;

/// <summary>L'icona di Segoe Fluent Icons che fa le veci di ogni icona a punti nello stile Windows.</summary>
internal static class FluentIcons
{
    private static readonly Dictionary<DotGlyph, char> Map = new()
    {
        [DotIcons.Play] = '\uE768',
        [DotIcons.Pause] = '\uE769',
        [DotIcons.Next] = '\uE893',
        [DotIcons.Previous] = '\uE892',
        [DotIcons.Start] = '\uF0E2',
        [DotIcons.Wifi] = '\uE701',
        [DotIcons.Mail] = '\uE715',
        [DotIcons.Search] = '\uE721',
        [DotIcons.Lock] = '\uE72E',
        [DotIcons.Sleep] = '\uE708',
        [DotIcons.Restart] = '\uE777',
        [DotIcons.Power] = '\uE7E8',
        [DotIcons.Ethernet] = '\uE839',
        [DotIcons.Close] = '\uE711',
        [DotIcons.Microphone] = '\uE720',
        [DotIcons.Camera] = '\uE714',
        [DotIcons.Speaker] = '\uE767',
        [DotIcons.SpeakerMuted] = '\uE74F',
        [DotIcons.Sun] = '\uE706',
        [DotIcons.Download] = '\uE896',
        [DotIcons.Headphones] = '\uE7F6',
        [DotIcons.Screenshot] = '\uE722',
        [DotIcons.Bluetooth] = '\uE702',
        [DotIcons.Calendar] = '\uE787',
        [DotIcons.Display] = '\uE7F4',
        [DotIcons.Gear] = '\uE713',
        [DotIcons.Terminal] = '\uE756',
        [DotIcons.Check] = '\uE73E',
        [DotIcons.Plug] = '\uE71B',
        [DotIcons.Phone] = '\uE8EA',
        [DotIcons.Agent] = '\uE945',
        [DotIcons.Device] = '\uE772',
        [DotIcons.Clip] = '\uE77F',
    };

    /// <summary>Tutte le icone, per la tavola di controllo di <c>--snapshot</c>.</summary>
    public static IReadOnlyDictionary<DotGlyph, char> All => Map;

    public static char? For(DotGlyph glyph) => Map.TryGetValue(glyph, out var icon) ? icon : null;
}
