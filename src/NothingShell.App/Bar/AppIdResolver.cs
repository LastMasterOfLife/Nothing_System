using System.IO;
using NothingShell.App.Interop;
using NothingShell.Core.Bar;
using NothingShell.Core.Launcher;

namespace NothingShell.App.Bar;

/// <summary>
/// AppUserModelID di una finestra, la chiave con cui Windows raggruppa le app, le lega alle icone appuntate
/// e tiene i recenti (docs/05-bar.md). In ordine: quello della finestra (Chrome, PWA, app UWP); quello del
/// collegamento appuntato o dello Start che avvia lo stesso eseguibile (VS Code e le app Electron lo
/// impostano solo sul processo, invisibile da fuori); quello predefinito che Windows ricava dal percorso.
/// </summary>
public sealed class AppIdResolver
{
    private const string ExplorerId = "Microsoft.Windows.Explorer";

    private Dictionary<string, string> _byExe = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Cambia quando cambiano le fonti: gli ID già calcolati vanno ricalcolati.</summary>
    public int Version { get; private set; }

    public void SetSources(IReadOnlyList<PinnedApp> pins, IReadOnlyList<AppEntry> apps)
    {
        ArgumentNullException.ThrowIfNull(pins);
        ArgumentNullException.ThrowIfNull(apps);
        var byExe = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var app in apps)
            if (app.TargetPath?.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) == true)
                byExe.TryAdd(app.TargetPath, app.Id);
        // Un collegamento con argomenti (es. pythonw.exe script.py) non rappresenta tutto l'eseguibile.
        foreach (var pin in pins)
            if (pin.ExePath is not null && !pin.HasArguments)
                byExe[pin.ExePath] = pin.AppId;

        if (byExe.Count == _byExe.Count && byExe.All(p => _byExe.TryGetValue(p.Key, out var id) && id == p.Value))
            return;
        _byExe = byExe;
        Version++;
    }

    /// <summary>Null se non si può stabilire (processo inaccessibile, app UWP senza ID sulla cornice).</summary>
    public string? Resolve(nint hwnd, string exePath)
    {
        ArgumentNullException.ThrowIfNull(exePath);
        if (ShellNative.GetWindowAppId(hwnd) is { } own)
            return own;
        if (exePath.StartsWith("pid:", StringComparison.Ordinal))
            return null;
        if (_byExe.TryGetValue(exePath, out var id))
            return id;

        var file = Path.GetFileName(exePath);
        if (file.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase))
            return ExplorerId;
        // ApplicationFrameHost ospita tutte le app UWP: l'ID del suo percorso le fonderebbe in una sola icona.
        if (file.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase))
            return null;
        return AppIds.Default(exePath, ShellNative.KnownFolders);
    }
}
