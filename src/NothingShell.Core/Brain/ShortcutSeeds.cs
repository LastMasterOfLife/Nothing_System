namespace NothingShell.Core.Brain;

/// <summary>
/// Scorciatoie di serie: le domande più comuni sul sistema hanno già una risposta il primo giorno, senza modello. Sono
/// tutte di sola lettura, locali e accettate dall'esecutore limitato; provate una per una da <c>--brain-check</c>. Una
/// frase ambigua non entra: "aggiornamenti windows" può voler dire quelli da installare, e qui si leggono quelli installati.
/// Nemmeno una che somiglia a un'azione: "servizi fermi" ha la stessa chiave di "ferma i servizi".
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

    private const string Now = "Get-Date";
    private const string ComputerName = "hostname";
    private const string CurrentUser = "whoami";
    private const string Users = "Get-LocalUser | Select-Object Name, Enabled, LastLogon";
    private const string Processor = "Get-CimInstance Win32_Processor | Select-Object Name, NumberOfCores, NumberOfLogicalProcessors, MaxClockSpeed";
    private const string ProcessorLoad = "Get-CimInstance Win32_Processor | Select-Object Name, LoadPercentage";
    private const string Graphics = "Get-CimInstance Win32_VideoController | Select-Object Name, DriverVersion, AdapterRAM";
    private const string Resolution = "Get-CimInstance Win32_VideoController | Select-Object Name, CurrentHorizontalResolution, CurrentVerticalResolution, CurrentRefreshRate";
    private const string MemoryModules = "Get-CimInstance Win32_PhysicalMemory | Select-Object Manufacturer, Capacity, Speed, PartNumber";
    private const string Motherboard = "Get-CimInstance Win32_BaseBoard | Select-Object Manufacturer, Product, Version";
    private const string Bios = "Get-CimInstance Win32_BIOS | Select-Object Manufacturer, SMBIOSBIOSVersion, ReleaseDate";
    private const string Serial = "Get-CimInstance Win32_BIOS | Select-Object SerialNumber";
    private const string Model = "Get-CimInstance Win32_ComputerSystem | Select-Object Manufacturer, Model, TotalPhysicalMemory";
    private const string SystemInfo = "Get-ComputerInfo OsName, OsVersion, CsName, CsManufacturer, CsModel, CsNumberOfLogicalProcessors, CsTotalPhysicalMemory";
    private const string Audio = "Get-CimInstance Win32_SoundDevice | Select-Object Name, Status";
    private const string PhysicalDisks = "Get-PhysicalDisk | Select-Object FriendlyName, MediaType, Size, HealthStatus";
    private const string Volumes = "Get-Volume | Select-Object DriveLetter, FileSystemLabel, FileSystem, SizeRemaining, Size";
    private const string Partitions = "Get-Partition | Select-Object DiskNumber, PartitionNumber, DriveLetter, Size, Type";
    private const string Printers = "Get-Printer | Select-Object Name, DriverName, PortName, PrinterStatus";
    private const string Usb = "Get-PnpDevice -PresentOnly -Class USB | Select-Object FriendlyName, Status";
    private const string BadDevices = "Get-PnpDevice -PresentOnly | Where-Object Status -ne OK | Select-Object FriendlyName, Class, Status";

    private const string Updates = "Get-HotFix | Sort-Object InstalledOn -Descending | Select-Object -First 15 HotFixID, Description, InstalledOn";
    private const string TimeZone = "Get-TimeZone";
    private const string Language = "Get-Culture";
    private const string Programs = @"Get-ItemProperty HKLM:\Software\Microsoft\Windows\CurrentVersion\Uninstall\*, HKLM:\Software\Wow6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*, HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\* | Where-Object DisplayName | Sort-Object DisplayName | Select-Object DisplayName, DisplayVersion, Publisher";
    private const string Startup = "Get-CimInstance Win32_StartupCommand | Select-Object Name, Command, Location";
    private const string Tasks = @"Get-ScheduledTask | Where-Object TaskPath -NotLike '\Microsoft\*' | Select-Object TaskName, State";
    private const string StoppedServices = "Get-Service | Where-Object Status -eq Stopped";
    private const string FailedServices = "Get-Service | Where-Object StartType -eq Automatic | Where-Object Status -eq Stopped";
    private const string SystemErrors = "Get-WinEvent -LogName System -MaxEvents 200 | Where-Object Level -eq 2 | Select-Object -First 15 TimeCreated, Id, ProviderName, Message";
    private const string AppErrors = "Get-WinEvent -LogName Application -MaxEvents 200 | Where-Object Level -eq 2 | Select-Object -First 15 TimeCreated, Id, ProviderName, Message";
    private const string ProcessCount = "Get-Process | Measure-Object";
    private const string PowerShellVersion = "Get-Host | Select-Object Version";

    private const string Adapters = "Get-NetAdapter | Select-Object Name, InterfaceDescription, Status, LinkSpeed, MacAddress";
    private const string Mac = "Get-NetAdapter | Select-Object Name, MacAddress";
    private const string NetworkConfig = "ipconfig /all";
    private const string Dns = "Get-DnsClientServerAddress -AddressFamily IPv4 | Select-Object InterfaceAlias, ServerAddresses";
    private const string Gateway = "Get-NetRoute -DestinationPrefix 0.0.0.0/0 | Select-Object InterfaceAlias, NextHop, RouteMetric";
    private const string Routes = "Get-NetRoute -AddressFamily IPv4 | Select-Object DestinationPrefix, NextHop, InterfaceAlias";
    private const string Connection = "Get-NetConnectionProfile | Select-Object Name, InterfaceAlias, IPv4Connectivity, NetworkCategory";
    private const string Wifi = "Get-NetAdapter | Where-Object PhysicalMediaType -eq 'Native 802.11' | Select-Object Name, Status, LinkSpeed";
    private const string Connections = "Get-NetTCPConnection -State Established | Select-Object LocalPort, RemoteAddress, RemotePort, OwningProcess";
    private const string UdpPorts = "Get-NetUDPEndpoint | Sort-Object LocalPort | Select-Object LocalAddress, LocalPort, OwningProcess";
    private const string Firewall = "Get-NetFirewallProfile | Select-Object Name, Enabled, DefaultInboundAction, DefaultOutboundAction";
    private const string Antivirus = "Get-MpComputerStatus | Select-Object AntivirusEnabled, RealTimeProtectionEnabled, AntivirusSignatureLastUpdated, QuickScanEndTime";
    private const string Shares = "Get-SmbShare | Select-Object Name, Path, Description";

    private const string HiddenFiles = "Get-ChildItem -Hidden";
    private const string Subfolders = "Get-ChildItem -Directory";
    private const string RecentFiles = "Get-ChildItem -File | Sort-Object LastWriteTime -Descending | Select-Object -First 15 Name, LastWriteTime, Length";
    private const string FilesSize = "Get-ChildItem -File | Measure-Object -Property Length -Sum";
    private const string FilesCount = "Get-ChildItem -File | Measure-Object";
    private const string FileTypes = "Get-ChildItem -File | Group-Object Extension | Sort-Object Count -Descending | Select-Object Name, Count";
    private const string Downloads = @"Get-ChildItem $HOME\Downloads | Sort-Object LastWriteTime -Descending | Select-Object -First 15 Name, Length, LastWriteTime";
    private const string TempSize = "Get-ChildItem $env:TEMP -File -Recurse -ErrorAction SilentlyContinue | Measure-Object -Property Length -Sum";

    private const string Branches = "git branch -a";
    private const string CurrentBranch = "git branch --show-current";
    private const string Remotes = "git remote -v";
    private const string GitTags = "git tag --list";
    private const string GitDiff = "git diff --stat";
    private const string GitStaged = "git diff --staged --stat";
    private const string LastCommit = "git show --stat";
    private const string Authors = "git shortlog -sn HEAD";
    private const string GitGraph = "git log --oneline --graph --all -20";
    private const string TodayCommits = "git log --oneline --since=midnight";

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

        // Il PC e chi lo usa
        Seed("che ore sono", Now), Seed("che ora è", Now), Seed("orario", Now), Seed("data", Now), Seed("data oggi", Now), Seed("che giorno è", Now), Seed("che giorno è oggi", Now), Seed("current time", Now), Seed("what time is it", Now), Seed("today date", Now),
        Seed("nome computer", ComputerName), Seed("nome pc", ComputerName), Seed("computer name", ComputerName), Seed("pc name", ComputerName),
        Seed("chi sono", CurrentUser), Seed("utente corrente", CurrentUser), Seed("nome utente", CurrentUser), Seed("current user", CurrentUser),
        Seed("utenti", Users), Seed("account locali", Users), Seed("local users", Users), Seed("users", Users),
        Seed("modello pc", Model), Seed("modello computer", Model), Seed("pc model", Model), Seed("computer model", Model),
        Seed("numero di serie", Serial), Seed("seriale", Serial), Seed("serial number", Serial),
        Seed("informazioni sistema", SystemInfo), Seed("info sistema", SystemInfo), Seed("informazioni pc", SystemInfo), Seed("info pc", SystemInfo), Seed("system info", SystemInfo), Seed("system information", SystemInfo),
        Seed("fuso orario", TimeZone), Seed("time zone", TimeZone), Seed("timezone", TimeZone),
        Seed("lingua sistema", Language), Seed("lingua windows", Language), Seed("system language", Language),
        Seed("versione powershell", PowerShellVersion), Seed("powershell version", PowerShellVersion),

        // Hardware
        Seed("cpu", Processor), Seed("processore", Processor), Seed("processor", Processor),
        Seed("uso cpu", ProcessorLoad), Seed("carico cpu", ProcessorLoad), Seed("utilizzo cpu", ProcessorLoad), Seed("cpu usage", ProcessorLoad), Seed("cpu load", ProcessorLoad),
        Seed("gpu", Graphics), Seed("scheda video", Graphics), Seed("scheda grafica", Graphics), Seed("graphics card", Graphics), Seed("video card", Graphics),
        Seed("risoluzione", Resolution), Seed("risoluzione schermo", Resolution), Seed("frequenza schermo", Resolution), Seed("screen resolution", Resolution), Seed("refresh rate", Resolution),
        Seed("moduli ram", MemoryModules), Seed("banchi ram", MemoryModules), Seed("velocità ram", MemoryModules), Seed("ram modules", MemoryModules), Seed("ram speed", MemoryModules),
        Seed("scheda madre", Motherboard), Seed("motherboard", Motherboard),
        Seed("bios", Bios), Seed("versione bios", Bios), Seed("bios version", Bios),
        Seed("schede audio", Audio), Seed("dispositivi audio", Audio), Seed("audio devices", Audio), Seed("sound devices", Audio),
        Seed("dischi", PhysicalDisks), Seed("dischi fisici", PhysicalDisks), Seed("salute disco", PhysicalDisks), Seed("stato dischi", PhysicalDisks), Seed("ssd", PhysicalDisks), Seed("physical disks", PhysicalDisks), Seed("disk health", PhysicalDisks),
        Seed("volumi disco", Volumes), Seed("disk volumes", Volumes),
        Seed("partizioni", Partitions), Seed("partitions", Partitions),
        Seed("stampanti", Printers), Seed("printers", Printers), Seed("printer", Printers),
        Seed("usb", Usb), Seed("dispositivi usb", Usb), Seed("usb devices", Usb),
        Seed("dispositivi con problemi", BadDevices), Seed("driver con problemi", BadDevices), Seed("problemi hardware", BadDevices), Seed("device problems", BadDevices),

        // Windows e programmi
        Seed("aggiornamenti installati", Updates), Seed("ultimi aggiornamenti", Updates), Seed("hotfix", Updates), Seed("installed updates", Updates),
        Seed("programmi installati", Programs), Seed("app installate", Programs), Seed("applicazioni installate", Programs), Seed("software installato", Programs), Seed("installed programs", Programs), Seed("installed apps", Programs), Seed("installed software", Programs),
        Seed("programmi che partono all'avvio", Startup), Seed("avvio automatico", Startup), Seed("esecuzione automatica", Startup), Seed("startup programs", Startup), Seed("startup apps", Startup),
        Seed("attività pianificate", Tasks), Seed("task pianificati", Tasks), Seed("scheduled tasks", Tasks),
        Seed("servizi arrestati", StoppedServices), Seed("servizi non in esecuzione", StoppedServices), Seed("stopped services", StoppedServices),
        Seed("servizi non partiti", FailedServices), Seed("failed services", FailedServices),
        Seed("errori sistema", SystemErrors), Seed("errori windows", SystemErrors), Seed("system errors", SystemErrors), Seed("windows errors", SystemErrors),
        Seed("errori applicazioni", AppErrors), Seed("errori programmi", AppErrors), Seed("crash applicazioni", AppErrors), Seed("application errors", AppErrors), Seed("app errors", AppErrors),
        Seed("numero processi", ProcessCount), Seed("conta processi", ProcessCount), Seed("process count", ProcessCount),

        // Rete e sicurezza
        Seed("schede rete", Adapters), Seed("adattatori rete", Adapters), Seed("network adapters", Adapters), Seed("network cards", Adapters),
        Seed("mac", Mac), Seed("indirizzo mac", Mac), Seed("mac address", Mac),
        Seed("configurazione rete", NetworkConfig), Seed("dettagli rete", NetworkConfig), Seed("network configuration", NetworkConfig), Seed("network config", NetworkConfig),
        Seed("dns", Dns), Seed("server dns", Dns), Seed("dns servers", Dns), Seed("dns server", Dns),
        Seed("gateway", Gateway), Seed("router", Gateway), Seed("gateway predefinito", Gateway), Seed("default gateway", Gateway),
        Seed("tabella routing", Routes), Seed("rotte", Routes), Seed("routing table", Routes), Seed("routes", Routes),
        Seed("connessione internet", Connection), Seed("stato connessione", Connection), Seed("internet", Connection), Seed("stato rete", Connection), Seed("internet connection", Connection), Seed("network status", Connection),
        Seed("wifi", Wifi), Seed("wi-fi", Wifi), Seed("stato wifi", Wifi), Seed("velocità wifi", Wifi), Seed("wifi status", Wifi), Seed("wifi speed", Wifi),
        Seed("connessioni aperte", Connections), Seed("connessioni stabilite", Connections), Seed("active connections", Connections), Seed("established connections", Connections),
        Seed("porte udp", UdpPorts), Seed("udp", UdpPorts), Seed("udp ports", UdpPorts),
        Seed("firewall", Firewall), Seed("stato firewall", Firewall), Seed("firewall status", Firewall),
        Seed("antivirus", Antivirus), Seed("stato antivirus", Antivirus), Seed("defender", Antivirus), Seed("windows defender", Antivirus), Seed("antivirus status", Antivirus),
        Seed("cartelle condivise", Shares), Seed("condivisioni", Shares), Seed("shared folders", Shares), Seed("network shares", Shares),

        // File
        Seed("file nascosti", HiddenFiles), Seed("hidden files", HiddenFiles),
        Seed("sottocartelle", Subfolders), Seed("subfolders", Subfolders),
        Seed("file recenti", RecentFiles), Seed("ultimi file", RecentFiles), Seed("file modificati", RecentFiles), Seed("recent files", RecentFiles), Seed("latest files", RecentFiles),
        Seed("dimensione file", FilesSize), Seed("peso file", FilesSize), Seed("files size", FilesSize),
        Seed("numero file", FilesCount), Seed("conta file", FilesCount), Seed("count files", FilesCount), Seed("file count", FilesCount),
        Seed("tipi file", FileTypes), Seed("estensioni file", FileTypes), Seed("file types", FileTypes), Seed("file extensions", FileTypes),
        Seed("download", Downloads), Seed("ultimi download", Downloads), Seed("download recenti", Downloads), Seed("file scaricati", Downloads), Seed("downloads", Downloads), Seed("recent downloads", Downloads),
        Seed("file temporanei", TempSize), Seed("spazio temporanei", TempSize), Seed("temp files", TempSize), Seed("temp size", TempSize),

        // Git
        Seed("branch", Branches), Seed("branch git", Branches), Seed("rami git", Branches), Seed("branches", Branches), Seed("git branches", Branches),
        Seed("branch corrente", CurrentBranch), Seed("branch attuale", CurrentBranch), Seed("ramo corrente", CurrentBranch), Seed("current branch", CurrentBranch),
        Seed("remote git", Remotes), Seed("repository remoto", Remotes), Seed("url repository", Remotes), Seed("git remotes", Remotes),
        Seed("tag", GitTags), Seed("tag git", GitTags), Seed("git tags", GitTags),
        Seed("differenze git", GitDiff), Seed("diff git", GitDiff),
        Seed("modifiche staged", GitStaged), Seed("modifiche preparate", GitStaged), Seed("file staged", GitStaged), Seed("staged changes", GitStaged),
        Seed("dettagli commit", LastCommit), Seed("dettagli ultimo commit", LastCommit), Seed("last commit", LastCommit), Seed("commit details", LastCommit),
        Seed("autori commit", Authors), Seed("autori git", Authors), Seed("contributori", Authors), Seed("contributors", Authors), Seed("commit authors", Authors),
        Seed("grafico git", GitGraph), Seed("storia git", GitGraph), Seed("albero commit", GitGraph), Seed("git graph", GitGraph), Seed("git history", GitGraph),
        Seed("commit oggi", TodayCommits), Seed("today commits", TodayCommits),
    ];

    private static Shortcut Seed(string phrase, string command) =>
        new(IntentText.Tokens(phrase), command, ShortcutMemory.TrustedAfter, 0, DateTimeOffset.UnixEpoch, Seed: true);
}
