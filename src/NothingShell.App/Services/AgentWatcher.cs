using System.IO;
using System.Text;
using System.Windows.Threading;
using NothingShell.Core.Agents;

namespace NothingShell.App.Services;

/// <summary>
/// Agenti al lavoro (docs/14-agenti-connettori.md). Claude Code scrive ogni risposta dell'API nel transcript della
/// sessione (<c>~/.claude/projects/&lt;progetto&gt;/&lt;sessione&gt;.jsonl</c>, i sotto-agenti in
/// <c>&lt;sessione&gt;/subagents/</c>): si leggono solo le righe nuove, in background, e se ne tengono token, modello e
/// stato, mai il testo. Gli altri agenti mandano i loro totali dalla pipe dei comandi (<see cref="Report"/>).
/// </summary>
public sealed class AgentWatcher : IDisposable
{
    private const int MaxShown = 12;
    private static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan Rescan = TimeSpan.FromSeconds(10);

    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private readonly object _gate = new();
    private readonly Dictionary<string, FileCursor> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, AgentSession> _sessions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, External> _external = new(StringComparer.Ordinal);
    private readonly HashSet<string> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _debounce = new() { Interval = Debounce };
    private readonly DispatcherTimer _rescan = new() { Interval = Rescan };
    private FileSystemWatcher? _watcher;
    private IReadOnlyList<AgentSnapshot> _claude = [];
    private double _claudeToday;
    private bool _busy;
    private bool _again;

    public AgentWatcher()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Process(full: false);
        };
        _rescan.Tick += (_, _) => Process(full: true);
    }

    /// <summary>Cartella dei transcript; <c>CLAUDE_CONFIG_DIR</c> la sposta come in Claude Code.</summary>
    public static string ProjectsFolder { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } dir
            ? dir
            : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude"),
        "projects");

    /// <summary>Le sessioni delle ultime ore, quelle al lavoro per prime.</summary>
    public IReadOnlyList<AgentSnapshot> Sessions { get; private set; } = [];

    /// <summary>Costo di oggi (da mezzanotte) di tutte le sessioni con un prezzo noto.</summary>
    public double Today { get; private set; }

    public event Action? Changed;

    /// <summary>Un agente ha finito il lavoro richiesto (fine del turno).</summary>
    public event Action<AgentTurn>? Finished;

    public bool Running => _watcher is not null || _rescan.IsEnabled;

    public void Start()
    {
        if (Running)
            return;
        try
        {
            Directory.CreateDirectory(ProjectsFolder);
            _watcher = new FileSystemWatcher(ProjectsFolder, "*.jsonl")
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
                InternalBufferSize = 64 * 1024,
            };
            _watcher.Changed += OnFileEvent;
            _watcher.Created += OnFileEvent;
            _watcher.Renamed += OnFileEvent;
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
        {
            _watcher?.Dispose();
            _watcher = null; // resta il controllo periodico
        }
        _rescan.Start();
        Process(full: true);
    }

    public void Stop()
    {
        _rescan.Stop();
        _debounce.Stop();
        _watcher?.Dispose();
        _watcher = null;
        lock (_gate)
        {
            _files.Clear();
            _sessions.Clear();
            _dirty.Clear();
        }
        _external.Clear();
        _claude = [];
        _claudeToday = 0;
        Sessions = [];
        Today = 0;
        Changed?.Invoke();
    }

    public void Dispose() => Stop();

    /// <summary>Totali di un agente qualsiasi dalla pipe (sul thread UI).</summary>
    public void Report(AgentReport report)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (!Running)
            return;
        var now = DateTimeOffset.Now;
        _external.TryGetValue(report.Id, out var previous);
        // i totali sono della sessione: il turno è la differenza da quando l'agente si è messo al lavoro
        var external = report.State == AgentState.Working && previous?.Report.State != AgentState.Working
            ? new External(report, now, now, previous?.Report.Usage ?? default, previous?.Report.Cost ?? 0)
            : new External(report, previous?.TurnStarted ?? now, now, previous?.Baseline ?? default, previous?.BaselineCost ?? 0);
        _external[report.Id] = external;
        var (started, baseline, baselineCost) = (external.TurnStarted, external.Baseline, external.BaselineCost);
        Publish();
        if (previous?.Report.State == AgentState.Working && report.State == AgentState.Done)
            Finished?.Invoke(new AgentTurn(report.Id, report.Title, report.Model, now - started, report.Usage - baseline,
                report.Cost is { } c ? c - baselineCost : null));
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e)
    {
        lock (_dirty)
            _dirty.Add(e.FullPath);
        _ = _dispatcher.BeginInvoke(() =>
        {
            if (!_debounce.IsEnabled)
                _debounce.Start();
        });
    }

    /// <summary>Un giro alla volta in background; con <paramref name="full"/> si cercano anche i file nuovi.</summary>
    private void Process(bool full)
    {
        if (_busy)
        {
            _again = true;
            return;
        }
        _busy = true;
        _ = Task.Run(() => Read(full)).ContinueWith(t =>
        {
            _ = _dispatcher.BeginInvoke(() =>
            {
                _busy = false;
                if (!Running || t.Result is not { } result)
                    return;
                (_claude, _claudeToday) = (result.Sessions, result.Today);
                Publish();
                foreach (var turn in result.Finished)
                    Finished?.Invoke(turn);
                if (_again)
                {
                    _again = false;
                    Process(full: false);
                }
            });
        }, TaskScheduler.Default);
    }

    /// <summary>
    /// Sul pool, un giro alla volta: legge le righe nuove e restituisce le sessioni recenti, il costo di oggi e i turni
    /// appena finiti. File e sessioni li tocca solo questo giro (e <see cref="Stop"/>, con lo stesso lucchetto).
    /// </summary>
    private ReadResult? Read(bool full)
    {
        lock (_gate)
            return ReadLocked(full);
    }

    private ReadResult? ReadLocked(bool full)
    {
        string[] paths;
        lock (_dirty)
        {
            paths = [.. _dirty];
            _dirty.Clear();
        }
        var since = DateTime.Now - AgentRules.Recent;
        var midnight = DateTime.Today;
        if (full)
        {
            try
            {
                var found = Directory.EnumerateFiles(ProjectsFolder, "*.jsonl", SearchOption.AllDirectories)
                    .Where(p => !_files.ContainsKey(p) && File.GetLastWriteTime(p) >= (since < midnight ? since : midnight));
                paths = [.. paths, .. found];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // cartella sparita o non leggibile: si riprova al prossimo giro
            }
        }

        var finished = new List<AgentTurn>();
        foreach (var path in paths.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (SessionId(path) is not { } id)
                continue;
            var known = _files.TryGetValue(path, out var cursor);
            cursor ??= new FileCursor();
            if (!known)
            {
                _files[path] = cursor;
                cursor.Announce = false; // il primo giro su un file legge la storia: nessuna scheda
            }
            if (!_sessions.TryGetValue(id, out var session))
                _sessions[id] = session = new AgentSession(id);
            foreach (var line in cursor.ReadNew(path))
            {
                if (ClaudeTranscript.Parse(line) is not { } entry)
                    continue;
                if (IsSubagent(path))
                    entry = entry with { Sidechain = true };
                if (session.Apply(entry) is { } turn && cursor.Announce)
                    finished.Add(turn);
            }
            cursor.Announce = true;
        }
        var now = DateTimeOffset.Now;
        var today = new DateTimeOffset(DateTime.Today);
        return new ReadResult(
            _sessions.Values.Where(s => now - s.LastActivity < AgentRules.Recent).Select(s => s.Snapshot()).ToArray(),
            _sessions.Values.Sum(s => s.CostSince(today) ?? 0),
            finished);
    }

    private void Publish()
    {
        var now = DateTimeOffset.Now;
        var all = _claude.ToList();
        var today = _claudeToday;
        foreach (var e in _external.Values.Where(e => now - e.LastReport < AgentRules.Recent))
        {
            var r = e.Report;
            all.Add(new AgentSnapshot(r.Id, "Agente", r.Title, r.Cwd, r.Model, r.State, r.Usage, r.Cost, r.Usage - e.Baseline,
                r.Cost is { } c ? c - e.BaselineCost : null, e.TurnStarted, e.LastReport));
            if (r.Cost is { } cost && e.LastReport.Date == DateTime.Today)
                today += cost;
        }
        // in corso ma muta da troppo: chiusa senza fine turno (terminale chiuso, crash)
        Sessions = all
            .Select(s => s.State == AgentState.Working && now - s.LastActivity > AgentRules.Stale ? s with { State = AgentState.Idle } : s)
            .OrderByDescending(s => s.State == AgentState.Working)
            .ThenByDescending(s => s.LastActivity)
            .Take(MaxShown)
            .ToArray();
        Today = today;
        Changed?.Invoke();
    }

    /// <summary>Sessione = nome del file; un sotto-agente appartiene alla cartella della sessione che lo ha avviato.</summary>
    private static string? SessionId(string path)
    {
        if (!IsSubagent(path))
            return Path.GetFileNameWithoutExtension(path);
        var parent = Path.GetDirectoryName(Path.GetDirectoryName(path));
        return parent is null ? null : Path.GetFileName(parent);
    }

    private static bool IsSubagent(string path) =>
        string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), "subagents", StringComparison.OrdinalIgnoreCase);

    private sealed record ReadResult(IReadOnlyList<AgentSnapshot> Sessions, double Today, List<AgentTurn> Finished);

    private sealed record External(AgentReport Report, DateTimeOffset TurnStarted, DateTimeOffset LastReport, TokenUsage Baseline, double BaselineCost);

    /// <summary>Fin dove è stato letto un file; l'ultima riga a metà resta da parte fino al giro dopo.</summary>
    private sealed class FileCursor
    {
        private long _offset;
        private string _partial = string.Empty;

        public bool Announce { get; set; }

        public string[] ReadNew(string path)
        {
            string chunk;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length < _offset)
                {
                    _offset = 0; // riscritto da capo
                    _partial = string.Empty;
                }
                if (stream.Length == _offset)
                    return [];
                stream.Seek(_offset, SeekOrigin.Begin);
                using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
                chunk = reader.ReadToEnd();
                _offset = stream.Length;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                return [];
            }
            var text = _partial + chunk;
            var last = text.LastIndexOf('\n');
            if (last < 0)
            {
                _partial = text;
                return [];
            }
            _partial = text[(last + 1)..];
            return text[..last].Split('\n');
        }
    }
}
