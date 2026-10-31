namespace NothingShell.Core.Brain;

/// <summary>
/// Scorciatoie di serie: le domande più comuni sul sistema hanno già una risposta il primo giorno, senza modello. Sono
/// tutte di sola lettura; provate una per una da <c>--brain-check</c>.
/// </summary>
public static class ShortcutSeeds
{
    private const string Memory = "Get-CimInstance Win32_OperatingSystem | Select-Object TotalVisibleMemorySize, FreePhysicalMemory";
    private const string Disk = "Get-PSDrive -PSProvider FileSystem";
    private const string Address = "Get-NetIPAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, IPAddress";
    private const string Processes = "Get-Process | Sort-Object WorkingSet64 -Descending | Select-Object -First 15 Name, Id, CPU, WorkingSet64";
    private const string Cpu = "Get-Process | Sort-Object CPU -Descending | Select-Object -First 15 Name, Id, CPU";
    private const string Services = "Get-Service | Where-Object Status -eq Running";
    private const string Ports = "Get-NetTCPConnection -State Listen | Sort-Object LocalPort | Select-Object LocalAddress, LocalPort, OwningProcess";
    private const string Version = "Get-CimInstance Win32_OperatingSystem | Select-Object Caption, Version, BuildNumber";
    private const string Uptime = "Get-CimInstance Win32_OperatingSystem | Select-Object LastBootUpTime";
    private const string Battery = "Get-CimInstance Win32_Battery | Select-Object EstimatedChargeRemaining, BatteryStatus";
    private const string Files = "Get-ChildItem";
    private const string BigFiles = "Get-ChildItem -File | Sort-Object Length -Descending | Select-Object -First 15 Name, Length";
    private const string Folder = "Get-Location";

    public static IReadOnlyList<Shortcut> All { get; } =
    [
        Seed("ram", Memory), Seed("ram libera", Memory), Seed("memoria", Memory), Seed("memoria libera", Memory), Seed("free memory", Memory),
        Seed("disco", Disk), Seed("spazio disco", Disk), Seed("spazio libero", Disk), Seed("disk space", Disk),
        Seed("ip", Address), Seed("indirizzo ip", Address), Seed("ip address", Address),
        Seed("processi", Processes), Seed("processes", Processes), Seed("processi memoria", Processes),
        Seed("processi cpu", Cpu), Seed("processes cpu", Cpu),
        Seed("servizi", Services), Seed("servizi esecuzione", Services), Seed("services", Services), Seed("running services", Services),
        Seed("porte", Ports), Seed("porte ascolto", Ports), Seed("porte aperte", Ports), Seed("open ports", Ports), Seed("listening ports", Ports),
        Seed("versione windows", Version), Seed("windows version", Version),
        Seed("acceso da", Uptime), Seed("uptime", Uptime),
        Seed("batteria", Battery), Seed("battery", Battery),
        Seed("file", Files), Seed("file cartella", Files), Seed("files", Files),
        Seed("file grandi", BigFiles), Seed("file piu grandi", BigFiles), Seed("biggest files", BigFiles), Seed("largest files", BigFiles),
        Seed("cartella", Folder), Seed("current folder", Folder),
        Seed("stato git", "git status"), Seed("git status", "git status"), Seed("modifiche git", "git status"),
        Seed("ultimi commit", "git log --oneline -15"), Seed("last commits", "git log --oneline -15"), Seed("recent commits", "git log --oneline -15"),
    ];

    private static Shortcut Seed(string phrase, string command) =>
        new(IntentText.Tokens(phrase), command, ShortcutMemory.TrustedAfter, 0, DateTimeOffset.UnixEpoch, Seed: true);
}
