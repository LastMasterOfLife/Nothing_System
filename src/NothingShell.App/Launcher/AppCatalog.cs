using System.Diagnostics;
using NothingShell.Core.Launcher;

namespace NothingShell.App.Launcher;

/// <summary>
/// App dello Start lette da <c>shell:AppsFolder</c> (desktop e UWP) e avvio tramite Explorer
/// (docs/06-launcher.md). La lettura usa COM della shell su un thread STA dedicato.
/// </summary>
public sealed class AppCatalog
{
    /// <summary>Voci interne della shell: si riconoscono dal prefisso e non passano da Explorer.</summary>
    public const string InternalPrefix = "nothing-shell:";

    public static readonly AppEntry SettingsEntry = new($"{InternalPrefix}settings", "Impostazioni Nothing Shell");

    public IReadOnlyList<AppEntry> Apps { get; private set; } = [SettingsEntry];

    /// <summary>Incrementata a ogni ricarica che cambia l'elenco.</summary>
    public int Version { get; private set; }

    /// <summary>Ricarica in background; va chiamata e attesa dal thread UI.</summary>
    public async Task RefreshAsync()
    {
        var apps = await RunOnStaThread(ReadAppsFolder);
        apps.Add(SettingsEntry);
        apps.Sort((a, b) => StringComparer.CurrentCultureIgnoreCase.Compare(a.Name, b.Name));
        if (apps.Count == Apps.Count && apps.SequenceEqual(Apps))
            return;
        Apps = apps;
        Version++;
    }

    public static void Launch(AppEntry app)
    {
        ArgumentNullException.ThrowIfNull(app);
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{app.Id}") { UseShellExecute = true })?.Dispose();
    }

    private static List<AppEntry> ReadAppsFolder()
    {
        var shellType = Type.GetTypeFromProgID("Shell.Application")
                        ?? throw new InvalidOperationException("Shell.Application non disponibile");
        dynamic shell = Activator.CreateInstance(shellType)!;
        dynamic folder = shell.NameSpace("shell:AppsFolder");
        var apps = new List<AppEntry>();
        foreach (var item in folder.Items())
        {
            string name = item.Name;
            string id = item.Path;
            if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(id))
                continue;
            var entry = new AppEntry(id, name);
            if (!AppSearch.IsNoise(entry))
                apps.Add(entry);
        }
        return apps
            .DistinctBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .OrderBy(a => a.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static Task<T> RunOnStaThread<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                completion.SetResult(work());
            }
#pragma warning disable CA1031 // l'eccezione non va persa: passa al chiamante tramite il Task
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
#pragma warning restore CA1031
        })
        {
            IsBackground = true,
            Name = "AppCatalog",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
