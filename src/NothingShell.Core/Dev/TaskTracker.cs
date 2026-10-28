using System.Globalization;
using System.Text.Json;

namespace NothingShell.Core.Dev;

public enum TaskEvent
{
    Start,
    End,
    Done,
    Push,
}

/// <summary>Un messaggio della named pipe dei comandi (docs/12-sviluppo.md): un oggetto JSON su una riga.</summary>
public sealed record TaskMessage(
    TaskEvent Event,
    string? Id = null,
    string? Title = null,
    string? Cwd = null,
    int? Pid = null,
    int? ExitCode = null,
    double? Seconds = null,
    string? Log = null,
    bool Quiet = false);

public static class TaskProtocol
{
    public const string PipeName = "NothingShell.Tasks";

    /// <summary>Più di così non è un messaggio della shell: si smette di leggere.</summary>
    public const int MaxMessageLength = 64 * 1024;

    /// <summary>Il messaggio, o null se non è valido (JSON rotto, evento sconosciuto, campi mancanti).</summary>
    public static TaskMessage? Parse(string line)
    {
        ArgumentNullException.ThrowIfNull(line);
        try
        {
            using var doc = JsonDocument.Parse(line);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return null;
            var fields = doc.RootElement.EnumerateObject()
                .GroupBy(p => p.Name, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.Last().Value, StringComparer.OrdinalIgnoreCase);
            if (!Enum.TryParse<TaskEvent>(Text(fields, "event"), ignoreCase: true, out var kind) || !Enum.IsDefined(kind))
                return null;
            var message = new TaskMessage(kind, Text(fields, "id"), Text(fields, "title"), Text(fields, "cwd"),
                Integer(fields, "pid"), Integer(fields, "exitCode"), Number(fields, "seconds"), Text(fields, "log"),
                fields.TryGetValue("quiet", out var quiet) && quiet.ValueKind == JsonValueKind.True);
            return kind switch
            {
                TaskEvent.Start when message.Id is null || message.Title is null => null,
                TaskEvent.End when message.Id is null => null,
                TaskEvent.Done when message.Title is null => null,
                TaskEvent.Push when message.Cwd is null => null,
                _ => message,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public static string Serialize(TaskMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        var fields = new Dictionary<string, object?> { ["event"] = message.Event.ToString().ToLowerInvariant() };
        void Add(string name, object? value)
        {
            if (value is not null)
                fields[name] = value;
        }
        Add("id", message.Id);
        Add("title", message.Title);
        Add("cwd", message.Cwd);
        Add("pid", message.Pid);
        Add("exitCode", message.ExitCode);
        Add("seconds", message.Seconds);
        Add("log", message.Log);
        if (message.Quiet)
            fields["quiet"] = true;
        return JsonSerializer.Serialize(fields);
    }

    private static string? Text(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String && v.GetString() is { Length: > 0 } s ? s : null;

    private static int? Integer(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n)
            ? (int)Math.Clamp(n, int.MinValue, int.MaxValue)
            : null;

    private static double? Number(Dictionary<string, JsonElement> fields, string name) =>
        fields.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.Number ? Math.Max(0, v.GetDouble()) : null;
}

/// <summary>Un comando in corso, avviato da <c>ns</c> o da <c>--task</c>.</summary>
public sealed record RunningTask(string Id, string Title, string? Cwd, int? Pid, DateTimeOffset StartedAt);

/// <summary>Un comando finito; <see cref="Error"/> è il primo errore riconosciuto nel log, se c'è.</summary>
public sealed record FinishedTask(string Title, string? Cwd, int? Pid, int ExitCode, TimeSpan Duration, DateTimeOffset FinishedAt,
    string? Log, bool Quiet)
{
    public bool Succeeded => ExitCode == 0;

    public CodeLocation? Error { get; init; }
}

/// <summary>
/// Comandi in corso e ultimi finiti (docs/12-sviluppo.md). Il tempo arriva da fuori; chi è ancora vivo lo dice
/// chi chiama <see cref="Prune"/> (il terminale esiste ancora).
/// </summary>
public sealed class TaskTracker
{
    public const int HistoryCapacity = 10;
    public static readonly TimeSpan MaxRunning = TimeSpan.FromHours(6);

    private readonly List<RunningTask> _running = [];
    private readonly List<FinishedTask> _recent = [];

    /// <summary>I più recenti per primi.</summary>
    public IReadOnlyList<RunningTask> Running => _running.AsEnumerable().Reverse().ToArray();

    /// <summary>I più recenti per primi.</summary>
    public IReadOnlyList<FinishedTask> Recent => _recent.AsEnumerable().Reverse().ToArray();

    public bool Start(TaskMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message.Event != TaskEvent.Start || message.Id is null || message.Title is null)
            return false;
        _running.RemoveAll(t => t.Id == message.Id);
        _running.Add(new RunningTask(message.Id, message.Title, message.Cwd, message.Pid, now));
        return true;
    }

    /// <summary><c>end</c> di un comando avviato, oppure <c>done</c> già finito: il comando finito, o null.</summary>
    public FinishedTask? Finish(TaskMessage message, DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(message);
        FinishedTask? finished = null;
        if (message.Event == TaskEvent.End && _running.FirstOrDefault(t => t.Id == message.Id) is { } running)
        {
            _running.Remove(running);
            finished = new FinishedTask(running.Title, running.Cwd, running.Pid, message.ExitCode ?? 0,
                now - running.StartedAt, now, message.Log, message.Quiet);
        }
        else if (message.Event == TaskEvent.Done && message.Title is not null)
        {
            finished = new FinishedTask(message.Title, message.Cwd, message.Pid, message.ExitCode ?? 0,
                TimeSpan.FromSeconds(message.Seconds ?? 0), now, message.Log, message.Quiet);
        }
        if (finished is null)
            return null;
        _recent.Add(finished);
        if (_recent.Count > HistoryCapacity)
            _recent.RemoveAt(0);
        return finished;
    }

    /// <summary>Il primo errore arriva dopo (si legge il log): sostituisce il comando nella cronologia.</summary>
    public void Replace(FinishedTask previous, FinishedTask updated)
    {
        var index = _recent.IndexOf(previous);
        if (index >= 0)
            _recent[index] = updated;
    }

    /// <summary>Toglie i comandi in corso da troppo o il cui terminale non c'è più.</summary>
    public bool Prune(DateTimeOffset now, Func<RunningTask, bool> alive)
    {
        ArgumentNullException.ThrowIfNull(alive);
        return _running.RemoveAll(t => now - t.StartedAt > MaxRunning || !alive(t)) > 0;
    }

    /// <summary>"12 s", "1:23", "1:02:03".</summary>
    public static string FormatDuration(TimeSpan duration)
    {
        var total = (int)Math.Max(0, Math.Floor(duration.TotalSeconds));
        return total switch
        {
            < 60 => $"{total} s",
            < 3600 => $"{total / 60}:{total % 60:00}",
            _ => $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}",
        };
    }

    /// <summary>Per la pillola compatta: "0:07", "1:23", "1:02:03".</summary>
    public static string FormatElapsed(TimeSpan elapsed)
    {
        var total = (int)Math.Max(0, Math.Floor(elapsed.TotalSeconds));
        return total < 3600 ? $"{total / 60}:{total % 60:00}" : $"{total / 3600}:{total / 60 % 60:00}:{total % 60:00}";
    }

    /// <summary>"adesso", "5 min fa", "2 h fa", "ieri".</summary>
    public static string FormatAgo(DateTimeOffset at, DateTimeOffset now)
    {
        var ago = now - at;
        return ago.TotalMinutes switch
        {
            < 1 => "adesso",
            < 60 => string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalMinutes} min fa"),
            < 24 * 60 => string.Create(CultureInfo.InvariantCulture, $"{(int)ago.TotalHours} h fa"),
            _ => "ieri",
        };
    }
}
