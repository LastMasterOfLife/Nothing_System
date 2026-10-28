namespace NothingShell.Core.Dev;

/// <summary>Una porta TCP in ascolto, dalla tabella di Windows.</summary>
public readonly record struct PortListener(int Port, int Pid, bool IPv6);

/// <summary>Il processo dietro una porta: nome dell'eseguibile senza estensione e se è un servizio (sessione 0).</summary>
public readonly record struct PortOwner(string Name, bool IsService);

/// <summary>Un server da mostrare nella pagina Server.</summary>
public sealed record DevServer(int Port, int Pid, string Process, string? Service, bool IsBackgroundService)
{
    /// <summary>Database e code non sono pagine web.</summary>
    public bool CanOpen => Service is null;

    /// <summary>Docker, WSL e i servizi di Windows non si terminano da qui: spegnerebbe tutto.</summary>
    public bool CanKill => !IsBackgroundService && !DevServers.IsInfrastructure(Process);

    public string Address => $"localhost:{Port}";
}

/// <summary>Quali porte in ascolto sono server di sviluppo (docs/12-sviluppo.md).</summary>
public static class DevServers
{
    public const int EphemeralStart = 49152;

    private static readonly HashSet<string> SystemProcesses = new(StringComparer.OrdinalIgnoreCase)
    {
        "System", "Idle", "svchost", "lsass", "wininit", "services", "spoolsv", "smss", "csrss", "NothingShell",
        "Registry", "MsMpEng", "SearchHost", "explorer", "sihost", "ctfmon",
    };

    /// <summary>App in background che tengono porte aperte e non sono lavoro.</summary>
    private static readonly HashSet<string> BackgroundApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "steam", "steamwebhelper", "Spotify", "Discord", "OneDrive", "Teams", "ms-teams", "Dropbox", "AnyDesk",
        "TeamViewer", "TeamViewer_Service", "nvcontainer", "NVIDIA Web Helper", "lghub_agent", "iCUE",
        "jetbrains-toolbox", "AdobeIPCBroker", "CCLibrary", "Code", "msedge", "chrome", "firefox", "brave",
        "ShareX", "Greenshot", "PhoneExperienceHost", "EpicGamesLauncher", "Battle.net", "Origin", "EADesktop",
        "qbittorrent", "OneDrive.Sync.Service", "ollama app", "uTorrent", "Plex Media Server", "Zoom",
    };

    private static readonly HashSet<int> WindowsPorts = [135, 139, 445, 5040, 5357, 7680];

    /// <summary>Runtime di sviluppo: le loro porte contano anche nella zona effimera.</summary>
    private static readonly HashSet<string> Runtimes = new(StringComparer.OrdinalIgnoreCase)
    {
        "node", "python", "python3", "pythonw", "py", "dotnet", "java", "javaw", "ruby", "php", "php-cgi", "go",
        "deno", "bun", "iisexpress", "nginx", "httpd", "caddy", "postgres", "mysqld", "redis-server", "mongod",
        "ollama", "com.docker.backend", "wslrelay", "docker", "docker-proxy", "vpnkit", "adb", "sqlservr",
    };

    private static readonly HashSet<string> Infrastructure = new(StringComparer.OrdinalIgnoreCase)
    {
        "com.docker.backend", "wslrelay", "wslservice", "vmmem", "vmmemWSL", "docker", "docker-proxy", "vpnkit", "dockerd",
    };

    private static readonly Dictionary<int, string> Services = new()
    {
        [5432] = "Postgres",
        [3306] = "MySQL",
        [1433] = "SQL Server",
        [6379] = "Redis",
        [27017] = "MongoDB",
        [9200] = "Elasticsearch",
        [5672] = "RabbitMQ",
        [11434] = "Ollama",
    };

    public static string? ServiceName(int port) => Services.GetValueOrDefault(port);

    public static bool IsInfrastructure(string process)
    {
        ArgumentNullException.ThrowIfNull(process);
        return Infrastructure.Contains(process);
    }

    /// <summary>Perché una porta non si mostra, o null se è un server di sviluppo.</summary>
    public static string? Exclusion(PortListener listener, PortOwner? owner)
    {
        if (owner is not { } o)
            return "processo non leggibile";
        if (listener.Pid <= 4 || SystemProcesses.Contains(o.Name))
            return "sistema";
        if (WindowsPorts.Contains(listener.Port))
            return "porta di Windows";
        if (BackgroundApps.Contains(o.Name))
            return "app in background";
        if (o.IsService && !Runtimes.Contains(o.Name) && ServiceName(listener.Port) is null)
            return "servizio in background"; // VPN, driver, utilità del produttore
        if (listener.Port >= EphemeralStart && !Runtimes.Contains(o.Name))
            return "porta effimera";
        return null;
    }

    /// <summary>I server da mostrare, una volta per porta e processo, in ordine di porta.</summary>
    public static IReadOnlyList<DevServer> Select(IEnumerable<PortListener> listeners, Func<int, PortOwner?> owner)
    {
        ArgumentNullException.ThrowIfNull(listeners);
        ArgumentNullException.ThrowIfNull(owner);
        return listeners
            .DistinctBy(l => (l.Port, l.Pid))
            .Select(l => (Listener: l, Owner: owner(l.Pid)))
            .Where(x => Exclusion(x.Listener, x.Owner) is null)
            .Select(x => new DevServer(x.Listener.Port, x.Listener.Pid, x.Owner!.Value.Name, ServiceName(x.Listener.Port), x.Owner.Value.IsService))
            .OrderBy(s => s.Port)
            .ToArray();
    }

    /// <summary>I server che non c'erano nella lettura precedente.</summary>
    public static IReadOnlyList<DevServer> Added(IReadOnlyList<DevServer> before, IReadOnlyList<DevServer> after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        var known = before.Select(s => (s.Port, s.Pid)).ToHashSet();
        return after.Where(s => !known.Contains((s.Port, s.Pid))).ToArray();
    }

    /// <summary>Stessi server di prima (per non ridisegnare la pagina a ogni lettura).</summary>
    public static bool Same(IReadOnlyList<DevServer> a, IReadOnlyList<DevServer> b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Count == b.Count && a.Zip(b).All(p => p.First == p.Second);
    }
}

/// <summary>Il comando che apre un file alla riga nell'editor: <c>editor</c> in settings.json o VS Code.</summary>
public static class EditorCommand
{
    /// <summary>Editor cercati nel PATH, nell'ordine, se <c>editor</c> non è impostato.</summary>
    public static readonly string[] Candidates = ["code", "code-insiders", "cursor"];

    public const string GotoArguments = "--goto \"{file}:{line}:{column}\"";

    /// <summary>
    /// Programma e argomenti dal modello: il primo elemento (anche fra virgolette) è il programma, nel resto
    /// <c>{file}</c>, <c>{line}</c> e <c>{column}</c> diventano la posizione (colonna 1 se non è nota).
    /// </summary>
    public static (string Program, string Arguments) Build(string template, CodeLocation location)
    {
        ArgumentNullException.ThrowIfNull(template);
        ArgumentNullException.ThrowIfNull(location);
        var trimmed = template.Trim();
        string program, rest;
        if (trimmed.StartsWith('"'))
        {
            var end = trimmed.IndexOf('"', 1);
            program = end > 0 ? trimmed[1..end] : trimmed.Trim('"');
            rest = end > 0 ? trimmed[(end + 1)..] : string.Empty;
        }
        else
        {
            var space = trimmed.IndexOf(' ', StringComparison.Ordinal);
            program = space > 0 ? trimmed[..space] : trimmed;
            rest = space > 0 ? trimmed[(space + 1)..] : string.Empty;
        }
        var arguments = rest.Trim()
            .Replace("{file}", location.File, StringComparison.OrdinalIgnoreCase)
            .Replace("{line}", location.Line.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase)
            .Replace("{column}", Math.Max(1, location.Column).ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.OrdinalIgnoreCase);
        return (program, arguments);
    }
}
