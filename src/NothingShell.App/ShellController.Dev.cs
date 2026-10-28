using System.Globalization;
using System.IO;
using System.Windows.Media;
using NothingShell.App.Interop;
using NothingShell.App.Notch;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Design;
using NothingShell.Core.Dev;
using NothingShell.Core.Notch;

namespace NothingShell.App;

/// <summary>Lavoro nel notch, appunti intelligenti e server locali (docs/12-sviluppo.md).</summary>
public sealed partial class ShellController
{
    private readonly TaskWatcher _tasks = new();
    private readonly CiWatcher _ci = new();
    private readonly ClipboardWatcher _clipWatcher = new();
    private readonly ClipboardHistory _clipHistory = new();
    private readonly PortWatcher _ports = new();

    private DevPageActions DevActions => new(OpenTask, OpenServer, KillServer, CopyClip, ClipActionFor);

    private void WireDev()
    {
        _tasks.Changed += RefreshTasks;
        _tasks.Finished += OnTaskFinished;
        _tasks.Pushed += folder =>
        {
            if (ActivityOn(LiveActivity.Ci))
                _ci.Watch(folder);
        };
        _ci.Changed += () => _notch.SetCi(_ci.Current, _ci.Problem);
        _ci.Finished += status =>
        {
            if (!CanShow(LiveActivity.Ci))
                return;
            var url = status.Runs.FirstOrDefault(r => r.Failed)?.Url ?? (status.Runs.Count > 0 ? status.Runs[0].Url : null);
            _notch.ShowCard(NotchCards.CiFinished(status,
                () =>
                {
                    if (url is { Length: > 0 })
                        OpenUri(url);
                },
                status.State == CiState.Failure ? () => _ = _ci.RerunAsync(status) : null,
                _notch.DismissCard), CardDuration);
        };
        _clipWatcher.Copied += OnCopied;
        _ports.Changed += _notch.SetServers;
        _ports.Started += added =>
        {
            if (CanShow(LiveActivity.Servers))
                _notch.ShowHint(DotIcons.Plug, Theme.White,
                    string.Join("   ", added.Select(s => $"{s.Address} - {s.Service ?? s.Process}".ToUpperInvariant())), NotchPage.Servers);
        };
    }

    /// <summary>Avvia o ferma i servizi di sviluppo; le pagine delle funzioni spente spariscono dallo swipe.</summary>
    private void ApplyDevActivities()
    {
        var tasks = ActivityOn(LiveActivity.Tasks);
        var ci = ActivityOn(LiveActivity.Ci);
        if (tasks || ci)
            _tasks.Start(); // anche il push arriva dalla pipe
        else
            _tasks.Stop();
        if (!ci)
            _ci.Stop();
        RefreshTasks();

        var history = ActivityOn(LiveActivity.Clipboard);
        if (history || ActivityOn(LiveActivity.ClipboardHints) || ActivityOn(LiveActivity.ClipboardSecrets))
            _clipWatcher.Start();
        else
            _clipWatcher.Stop();
        if (!history)
            _clipHistory.Clear();
        _notch.SetClipboard(_clipHistory.Entries);

        Toggle(LiveActivity.Servers, _ports.Start, _ports.Stop);

        var hidden = new List<NotchPage>();
        if (!tasks && !ci)
            hidden.Add(NotchPage.Work);
        if (!ActivityOn(LiveActivity.Servers))
            hidden.Add(NotchPage.Servers);
        if (!history)
            hidden.Add(NotchPage.Clipboard);
        _notch.SetAvailablePages(NotchPager.Pages.Except(hidden));
    }

    private void DisposeDev()
    {
        _tasks.Dispose();
        _ci.Dispose();
        _clipWatcher.Dispose();
        _ports.Dispose();
    }

    private void RefreshTasks()
    {
        var on = ActivityOn(LiveActivity.Tasks);
        _notch.SetTasks(on ? _tasks.Running : [], on ? _tasks.Recent : []);
    }

    /// <summary>Scheda alla fine: dopo <c>ns</c> sempre, dopo l'hook del prompt solo se il terminale non è in primo piano.</summary>
    private void OnTaskFinished(FinishedTask task)
    {
        if (!CanShow(LiveActivity.Tasks))
            return;
        var terminal = task.Pid is { } pid ? DevTools.TerminalWindow(pid) : 0;
        if (task.Quiet && DevTools.IsForeground(terminal))
            return;
        _notch.ShowCard(NotchCards.TaskFinished(task,
            task.Error is { } error ? () => DevTools.OpenInEditor(error, _settings.Editor) : null,
            task.Log is { } log && File.Exists(log) ? () => DevTools.OpenPath(log) : null,
            terminal != 0 ? () => WindowNative.BringToFront(terminal) : null,
            _notch.DismissCard), CardDuration);
    }

    /// <summary>Clic su un comando della pagina Lavoro: il primo errore nell'editor, altrimenti il log.</summary>
    private void OpenTask(FinishedTask task)
    {
        if (task.Error is { } error)
            DevTools.OpenInEditor(error, _settings.Editor);
        else if (task.Log is { } log && File.Exists(log))
            DevTools.OpenPath(log);
    }

    private static void OpenServer(DevServer server) => OpenUri($"http://{server.Address}");

    private static bool KillServer(DevServer server) => PortWatcher.Kill(server);

    private void OnCopied(string text, uint sequence)
    {
        var now = DateTimeOffset.Now;
        var kind = ClipClassifier.Classify(text, PathExists);
        if (ClipClassifier.IsSensitive(kind))
        {
            if (ActivityOn(LiveActivity.ClipboardSecrets))
            {
                _clipWatcher.ScheduleClear(sequence);
                if (CanShow(LiveActivity.ClipboardSecrets))
                    ShowSecretCard(text.Trim(), kind, sequence, now);
            }
        }
        if (ActivityOn(LiveActivity.Clipboard))
        {
            _clipHistory.Add(text, kind, now);
            _notch.SetClipboard(_clipHistory.Entries);
        }
        if (!ClipClassifier.IsSensitive(kind) && CanShow(LiveActivity.ClipboardHints) && ClipClassifier.Hint(kind, text, now) is { } hint)
        {
            var color = kind == ClipKind.Color && ClipClassifier.Color(text.Trim()) is { } c ? Color.FromRgb(c.R, c.G, c.B) : Theme.White;
            _notch.ShowHint(kind == ClipKind.Color ? DotIcons.Start : DotIcons.Clip, color, hint, NotchPage.Clipboard);
        }
    }

    private void ShowSecretCard(string text, ClipKind kind, uint sequence, DateTimeOffset now)
    {
        var jwt = kind == ClipKind.Jwt ? ClipClassifier.Jwt(text) : null;
        var name = jwt is not null ? "TOKEN JWT" : ClipClassifier.SecretName(text) ?? "SEGRETO";
        var detail = jwt is not null ? ClipClassifier.Expiry(jwt, now) ?? "senza scadenza" : ClipClassifier.Mask(text);
        _notch.ShowCard(NotchCards.Secret(name, detail,
            () => _clipWatcher.ClearNow(sequence),
            _clipWatcher.CancelClear,
            jwt is not null ? () => ReplaceClipboard(jwt.Payload) : null,
            _notch.DismissCard), CardDuration);
    }

    /// <summary>Scrive negli appunti e lo segna nella cronologia (la copia nostra non torna dal watcher).</summary>
    private void ReplaceClipboard(string text)
    {
        if (!_clipWatcher.SetText(text) || !ActivityOn(LiveActivity.Clipboard))
            return;
        _clipHistory.Add(text, ClipClassifier.Classify(text, PathExists), DateTimeOffset.Now);
        _notch.SetClipboard(_clipHistory.Entries);
    }

    private void CopyClip(ClipEntry entry)
    {
        if (entry.Text is { } text)
            ReplaceClipboard(text);
    }

    /// <summary>L'azione principale della pagina Appunti per ogni tipo, o null.</summary>
    private ClipAction? ClipActionFor(ClipEntry entry)
    {
        if (entry.Text is not { } text)
            return null;
        var trimmed = text.Trim();
        switch (entry.Kind)
        {
            case ClipKind.Json:
                return trimmed.Contains('\n', StringComparison.Ordinal)
                    ? new ClipAction("MINIFICA", () => ReplaceClipboard(ClipClassifier.Minify(trimmed)))
                    : new ClipAction("FORMATTA", () => ReplaceClipboard(ClipClassifier.Format(trimmed)));
            case ClipKind.Code:
                return CodeLocations.Pick(trimmed, null, File.Exists) is { } location
                    ? new ClipAction("APRI", () => DevTools.OpenInEditor(location, _settings.Editor))
                    : null;
            case ClipKind.Color when ClipClassifier.Color(trimmed) is { } color:
                return trimmed.StartsWith('#')
                    ? new ClipAction("RGB", () => ReplaceClipboard(color.Rgb))
                    : new ClipAction("HEX", () => ReplaceClipboard(color.Hex));
            case ClipKind.Timestamp when ClipClassifier.Timestamp(trimmed) is { } at:
                return new ClipAction("ISO", () => ReplaceClipboard(at.ToLocalTime().ToString("yyyy-MM-ddTHH:mm:ssK", CultureInfo.InvariantCulture)));
            case ClipKind.Url:
                return new ClipAction("APRI", () => OpenUri(trimmed));
            case ClipKind.Path when ClipClassifier.PathOf(trimmed) is { } path:
                return new ClipAction("APRI", () => DevTools.OpenPath(path));
            default:
                return null;
        }
    }

    /// <summary>I percorsi di rete non si controllano: un server lento bloccherebbe la UI.</summary>
    private static bool PathExists(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) || File.Exists(path) || Directory.Exists(path);
}
