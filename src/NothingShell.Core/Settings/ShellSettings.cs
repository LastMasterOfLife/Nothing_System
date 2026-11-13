using System.Text.Json;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;
using NothingShell.Core.Storage;
using NothingShell.Core.Widgets;

namespace NothingShell.Core.Settings;

/// <summary>Preferenze generali della shell (docs/07-settings-plugins.md).</summary>
public sealed record ShellSettings(
    bool Notch,
    bool Bar,
    IReadOnlyList<string> DisabledWidgets,
    bool MailInNotch = true,
    IReadOnlyList<string>? ExtraMailApps = null,
    bool Access = true,
    MailAccount? MailAccount = null,
    IReadOnlyList<LiveActivity>? DisabledActivities = null,
    IReadOnlyList<string>? CalendarFeeds = null,
    DateOnly? MorningBriefShown = null,
    string? Editor = null,
    bool PhoneDirect = false,
    IReadOnlyDictionary<string, string>? ConnectorGrants = null,
    IReadOnlyList<string>? ConnectorWidgets = null,
    MailReaderLayout? MailReader = null,
    string? AssistantModel = null,
    AppearanceSettings? Appearance = null,
    bool PowerfulAssistant = false,
    IReadOnlyList<NotchOutfit>? Wardrobe = null,
    IReadOnlyList<NotchPage>? NotchPageOrder = null,
    IReadOnlyList<NotchPage>? HiddenNotchPages = null,
    NothingShell.Core.PortalDebug.DebugSettings? Debug = null,
    NothingShell.Core.Leisure.LeisureSettings? Leisure = null,
    bool Security = true)
{
    public static ShellSettings Default { get; } = new(true, true, []);

    /// <summary>Debug potenziato (docs/24-debug-potenziato.md): portali collegati e regole degli avvisi.</summary>
    public NothingShell.Core.PortalDebug.DebugSettings DebugOrDefault() => Debug ?? NothingShell.Core.PortalDebug.DebugSettings.Default;

    /// <summary>Svago (docs/25-svago.md): il gioco nel notch e il record.</summary>
    public NothingShell.Core.Leisure.LeisureSettings LeisureOrDefault() => Leisure ?? NothingShell.Core.Leisure.LeisureSettings.Default;

    /// <summary>
    /// Permesso di eseguire comandi e azioni dato a un connettore (docs/14-agenti-connettori.md): vale solo per il
    /// contenuto del file approvato (<paramref name="hash"/>), così un file cambiato va riapprovato.
    /// </summary>
    public bool IsConnectorGranted(string id, string hash) =>
        ConnectorGrants is not null && ConnectorGrants.TryGetValue(id, out var granted) && granted == hash;

    public ShellSettings WithConnectorGrant(string id, string hash, bool granted)
    {
        var grants = new Dictionary<string, string>(ConnectorGrants ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        if (granted)
            grants[id] = hash;
        else
            grants.Remove(id);
        return this with { ConnectorGrants = grants };
    }

    public bool IsActivityEnabled(LiveActivity activity) => DisabledActivities?.Contains(activity) != true;

    public ShellSettings WithActivity(LiveActivity activity, bool enabled)
    {
        var disabled = (DisabledActivities ?? []).Where(a => a != activity).ToList();
        if (!enabled)
            disabled.Add(activity);
        return this with { DisabledActivities = disabled };
    }

    /// <summary>Pagina del notch tenuta tra quelle da swipe (docs/03-notch.md); la Home c'è sempre.</summary>
    public bool IsNotchPageShown(NotchPage page) => page == NotchPage.Home || HiddenNotchPages?.Contains(page) != true;

    public ShellSettings WithNotchPage(NotchPage page, bool shown)
    {
        var hidden = (HiddenNotchPages ?? []).Where(p => p != page).ToList();
        if (!shown && page != NotchPage.Home)
            hidden.Add(page);
        return this with { HiddenNotchPages = hidden };
    }

    /// <summary>Le pagine nell'ordine scelto, la Home per prima.</summary>
    public IReadOnlyList<NotchPage> NotchPages => global::NothingShell.Core.Notch.NotchPageOrder.Resolve(NotchPageOrder);

    /// <summary>Sposta una pagina di un posto fra quelle in <paramref name="movable"/>: le altre restano dove sono.</summary>
    public ShellSettings WithNotchPageMoved(NotchPage page, int delta, IEnumerable<NotchPage> movable) =>
        this with { NotchPageOrder = global::NothingShell.Core.Notch.NotchPageOrder.Move(NotchPageOrder, page, delta, movable) };

    /// <summary>
    /// I widget integrati e dei plugin ci sono finché non si spengono; quelli dei connettori solo se accesi
    /// (<see cref="ConnectorWidgets"/>): un connettore non finisce sul desktop da solo.
    /// </summary>
    public bool IsWidgetEnabled(string widgetId) =>
        ConnectorWidgetLayout.IsConnectorWidget(widgetId)
            ? ConnectorWidgets?.Contains(widgetId, StringComparer.Ordinal) == true
            : !DisabledWidgets.Contains(widgetId, StringComparer.Ordinal);

    public ShellSettings WithWidget(string widgetId, bool enabled)
    {
        if (ConnectorWidgetLayout.IsConnectorWidget(widgetId))
        {
            var shown = (ConnectorWidgets ?? []).Where(w => w != widgetId).ToList();
            if (enabled)
                shown.Add(widgetId);
            return this with { ConnectorWidgets = shown };
        }
        var disabled = DisabledWidgets.Where(w => w != widgetId).ToList();
        if (!enabled)
            disabled.Add(widgetId);
        return this with { DisabledWidgets = disabled };
    }
}

public sealed class ShellSettingsStore(string path)
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public static string DefaultPath { get; } = Path.Combine(AtomicFile.DataFolder, "settings.json");

    public static string Serialize(ShellSettings settings) => JsonSerializer.Serialize(settings, Options);

    public static ShellSettings Deserialize(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<ShellSettings>(json, Options);
            return settings is null ? ShellSettings.Default
                : settings.DisabledWidgets is null ? settings with { DisabledWidgets = [] }
                : settings;
        }
        catch (JsonException)
        {
            return ShellSettings.Default;
        }
    }

    public ShellSettings Load() => AtomicFile.TryReadAllText(path) is { } json ? Deserialize(json) : ShellSettings.Default;

    public void Save(ShellSettings settings) => AtomicFile.WriteAllText(path, Serialize(settings));
}
