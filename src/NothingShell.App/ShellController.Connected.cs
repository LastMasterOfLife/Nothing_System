using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using NothingShell.App.Notch;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Agents;
using NothingShell.Core.Connectors;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;
using NothingShell.Core.Phone;
using NothingShell.Core.Storage;
using NothingShell.Core.Widgets;

namespace NothingShell.App;

/// <summary>Agenti al lavoro e connettori verso dispositivi e sistemi (docs/14-agenti-connettori.md).</summary>
public sealed partial class ShellController
{
    private const int PhoneNotificationsShown = 10;

    private readonly AgentWatcher _agentWatcher = new();
    private readonly ConnectorHost _connectors = new();
    private readonly ReceivedFiles _phoneFiles = new(ReceivedFiles.DefaultPath);
    private readonly List<PhoneEvent> _phoneNotifications = [];

    private ConnectedPageActions ConnectedActions => new(OpenFile, ShowFile, () => _kde.FindPhone(), (m, a) => _connectors.Run(m, a))
    {
        SendFile = () => _ = SendFileToPhoneAsync(),
        SendClipboard = SendClipboardToPhone,
    };

    public IReadOnlyList<ConnectorLoadResult> ConnectorResults => _connectors.Results;

    public string ConnectorFolder => ConnectorHost.Folder;

    public bool IsConnectorGranted(ConnectorManifest manifest) => _settings.IsConnectorGranted(manifest.Id, manifest.Hash);

    public void SetConnectorGranted(ConnectorManifest manifest, bool granted)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        Update(_settings.WithConnectorGrant(manifest.Id, manifest.Hash, granted));
        if (_connectors.Running)
            _connectors.Start();
    }

    public bool HasConnectorSecret(ConnectorManifest manifest) => ConnectorHost.HasSecret(manifest);

    public void SetConnectorSecret(ConnectorManifest manifest, string secret) => _connectors.SetSecret(manifest, secret);

    public void ReloadConnectors()
    {
        if (ActivityOn(LiveActivity.Connectors))
            _connectors.Start();
    }

    /// <summary>
    /// Connettore dalla libreria: scrive il file, mette il token nel Gestore credenziali e, se l'utente l'ha chiesto, dà
    /// il permesso per questo contenuto. Accende i connettori se erano spenti. Con <paramref name="replace"/> riscrive
    /// quel connettore (Modifica): stesso id, stesso token se non se ne scrive uno nuovo, stessi costi LLM.
    /// </summary>
    public string? AddConnector(ConnectorTemplate connector, string? name, IReadOnlyDictionary<string, string> values, bool grant,
        ConnectorManifest? replace = null)
    {
        ArgumentNullException.ThrowIfNull(connector);
        var existing = Directory.Exists(ConnectorHost.Folder)
            ? Directory.EnumerateFiles(ConnectorHost.Folder, "*.json").Select(Path.GetFileNameWithoutExtension).OfType<string>().ToArray()
            : [];
        var result = ConnectorCatalog.Create(connector, name, values, existing, replace?.Id, keepSecret: replace?.Secret == true);
        if (result.Error is { } error)
            return error;
        var json = result.Json!;
        // il blocco "llm" aggiunto dalle impostazioni resta; quello del connettore della libreria vince se c'è
        if (replace?.Llm is { } kept && ConnectorParser.Parse(result.Id!, json).Manifest?.Llm is null)
            json = ConnectorCatalog.WithLlm(json, kept);
        try
        {
            AtomicFile.WriteAllText(Path.Combine(ConnectorHost.Folder, result.Id + ".json"), json);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"file non scritto: {ex.Message}";
        }
        var manifest = ConnectorParser.Parse(result.Id!, json).Manifest!;
        if (result.Secret is { } secret)
            ConnectorHost.StoreSecret(manifest, secret);
        if (manifest.NeedsGrant)
            Update(_settings.WithConnectorGrant(manifest.Id, manifest.Hash, grant));
        if (!ActivityOn(LiveActivity.Connectors))
            SetActivityEnabled(LiveActivity.Connectors, true);
        else
            _connectors.Start();
        return null;
    }

    /// <summary>Toglie il file, il segreto e il permesso del connettore.</summary>
    public void RemoveConnector(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        try
        {
            File.Delete(Path.Combine(ConnectorHost.Folder, manifest.Id + ".json"));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return;
        }
        if (manifest.Secret)
            ConnectorHost.StoreSecret(manifest, string.Empty);
        if (IsConnectorGranted(manifest))
            Update(_settings.WithConnectorGrant(manifest.Id, manifest.Hash, false));
        _connectors.ForgetLlm(manifest.Id);
        var widget = ConnectorWidgetLayout.WidgetId(manifest.Id);
        if (_settings.IsWidgetEnabled(widget))
            Update(_settings.WithWidget(widget, false)); // un connettore nuovo con lo stesso nome non ricompare da solo
        if (_connectors.Running)
            _connectors.Start();
    }

    public double? UsdToEur => _connectors.UsdToEur;

    public LlmSummary ConnectorCosts(ConnectorManifest manifest) => _connectors.LlmSummaryOf(manifest);

    public IReadOnlyList<LlmCall> ConnectorCalls(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        return _connectors.LlmRecent(manifest.Id);
    }

    /// <summary>
    /// Accende, cambia o (con null) spegne i costi LLM di un connettore: riscrive il blocco <c>"llm"</c> del file.
    /// Il blocco non esegue nulla, quindi un permesso già dato resta valido per il file nuovo.
    /// </summary>
    public string? SetConnectorLlm(ConnectorManifest manifest, LlmSpec? llm)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var path = Path.Combine(ConnectorHost.Folder, manifest.Id + ".json");
        if (AtomicFile.TryReadAllText(path) is not { } json)
            return "file del connettore non leggibile";
        string updated;
        try
        {
            updated = ConnectorCatalog.WithLlm(json, llm);
        }
        catch (System.Text.Json.JsonException ex)
        {
            return $"JSON non valido: {ex.Message}";
        }
        var parsed = ConnectorParser.Parse(manifest.Id, updated);
        if (parsed.Manifest is not { } next)
            return parsed.Error;
        try
        {
            AtomicFile.WriteAllText(path, updated);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return $"file non scritto: {ex.Message}";
        }
        if (IsConnectorGranted(manifest))
            Update(_settings.WithConnectorGrant(next.Id, next.Hash, true));
        if (llm is null)
            _connectors.ForgetLlm(manifest.Id);
        if (_connectors.Running)
            _connectors.Start();
        return null;
    }

    public void ResetConnectorCosts(ConnectorManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        _connectors.ForgetLlm(manifest.Id);
        if (_connectors.Running)
            _connectors.Start();
    }

    /// <summary>Tutti i giorni di tutti i connettori in un CSV (separato da punto e virgola, per Excel) e lo mostra.</summary>
    public void ExportConnectorCosts()
    {
        var names = _connectors.Results.Where(r => r.Manifest is not null).ToDictionary(r => r.Manifest!.Id, r => r.Manifest!.Name, StringComparer.Ordinal);
        var path = Path.Combine(AtomicFile.DataFolder, $"costi-llm-{DateTime.Now:yyyyMMdd-HHmm}.csv");
        try
        {
            File.WriteAllText(path, _connectors.LlmCsv(id => names.GetValueOrDefault(id, id)), new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _notch.ShowNotice(DotIcons.Device, Theme.Red, $"CSV NON SCRITTO: {ex.Message}");
            return;
        }
        ShowFile(path);
    }

    /// <summary>Copia gli esempi in <c>connectors\esempi</c>: lì non partono, si spostano su quando sono pronti.</summary>
    public void CopyConnectorExamples()
    {
        var source = Path.Combine(AppContext.BaseDirectory, "connectors");
        var target = Path.Combine(ConnectorHost.Folder, "esempi");
        Directory.CreateDirectory(target);
        if (Directory.Exists(source))
            foreach (var file in Directory.EnumerateFiles(source, "*.json"))
                File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        OpenFile(target);
    }

    private void WireConnected()
    {
        _phoneFiles.Load();
        _agentWatcher.Changed += () => _notch.SetAgents(_agentWatcher.Sessions, _agentWatcher.Today);
        _agentWatcher.Finished += turn =>
        {
            if (!CanShow(LiveActivity.Agents) || !AgentRules.ShouldAnnounce(turn))
                return;
            var cwd = _agentWatcher.Sessions.FirstOrDefault(s => s.Id == turn.SessionId)?.Cwd;
            _notch.ShowCard(NotchCards.AgentFinished(turn,
                () => _notch.OpenPage(NotchPage.Agents),
                cwd is { Length: > 0 } && Directory.Exists(cwd) ? () => OpenFile(cwd) : null,
                _notch.DismissCard), CardDuration);
        };
        _tasks.AgentReported += _agentWatcher.Report;

        _connectors.IsGranted = IsConnectorGranted;
        _connectors.Changed += () =>
        {
            _notch.SetConnectors(_connectors.States);
            _widgets.SyncConnectors(_connectors.States);
        };
        _clock.MinuteChanged += _ => _widgets.RefreshConnectors();
        _connectors.Alert += (manifest, reading) =>
        {
            if (CanShow(LiveActivity.Connectors))
                _notch.ShowHint(DotIcons.Device, Theme.Red,
                    $"{manifest.Name}: {reading.Label} {reading.Text}{(reading.Unit is { Length: > 0 } u ? " " + u : string.Empty)}", NotchPage.Devices);
        };
        _connectors.ActionFinished += (manifest, action, error) =>
            _notch.ShowNotice(DotIcons.Device, error is null ? Theme.White : Theme.Red,
                error is null ? $"{manifest.Name}: {action.Label} FATTO" : $"{manifest.Name}: {action.Label} - {error}");

        _kde.Changed += RefreshPhonePage;
        _kde.FileReceived += path =>
        {
            long size = 0;
            try
            {
                size = new FileInfo(path).Length;
            }
            catch (IOException)
            {
                // dimensione non nota
            }
            _phoneFiles.Add(new ReceivedFile(path, _kde.Current?.Name ?? "telefono", size, DateTimeOffset.Now));
            RefreshPhonePage();
        };
        if (_phoneFiles.Prune(File.Exists))
            RefreshPhonePage();
        RefreshPhonePage();
    }

    /// <summary>Avvia o ferma agenti e connettori; la pagina Telefono c'è col telefono acceso in una delle due forme.</summary>
    private void ApplyConnected(List<NotchPage> hidden)
    {
        Toggle(LiveActivity.Agents, _agentWatcher.Start, _agentWatcher.Stop);
        if (ActivityOn(LiveActivity.Connectors))
        {
            if (!_connectors.Running)
                _connectors.Start();
        }
        else
        {
            _connectors.Stop();
        }
        if (!ActivityOn(LiveActivity.Agents))
            hidden.Add(NotchPage.Agents);
        if (!ActivityOn(LiveActivity.Connectors))
            hidden.Add(NotchPage.Devices);
        if (!ActivityOn(LiveActivity.Phone) && !_settings.PhoneDirect)
            hidden.Add(NotchPage.Phone);
    }

    private void DisposeConnected()
    {
        _agentWatcher.Dispose();
        _connectors.Dispose();
    }

    /// <summary>Notifiche del telefono per la pagina Telefono: le ultime, solo in memoria.</summary>
    private void RememberPhoneEvent(PhoneEvent phone)
    {
        if (phone.Kind is not (PhoneEventKind.Message or PhoneEventKind.MissedCall))
            return;
        _phoneNotifications.Insert(0, phone);
        if (_phoneNotifications.Count > PhoneNotificationsShown)
            _phoneNotifications.RemoveAt(_phoneNotifications.Count - 1);
        RefreshPhonePage();
    }

    private void RefreshPhonePage() =>
        _notch.SetPhone(new PhonePageData(_settings.PhoneDirect, _kde.Current ?? (_settings.PhoneDirect ? _kde.Devices.FirstOrDefault(d => d.Paired) : null),
            _kde.Status, _kde.Incoming, _phoneFiles.Files, [.. _phoneNotifications]));

    /// <summary>Dalla pagina Telefono: sceglie un file e lo manda; l'esito arriva come avviso nel notch.</summary>
    private async Task SendFileToPhoneAsync()
    {
        if (_kde.Current is not { } phone)
            return;
        var dialog = new Microsoft.Win32.OpenFileDialog { Title = $"Invia a {phone.Name}" };
        if (dialog.ShowDialog() != true)
            return;
        var error = await _kde.SendFileAsync(dialog.FileName);
        _notch.ShowNotice(DotIcons.Download, error is null ? Theme.White : Theme.Red,
            error is null ? $"{Path.GetFileName(dialog.FileName)} INVIATO" : $"NON INVIATO: {error}");
    }

    /// <summary>Gli appunti partono solo con un clic, mai da soli (docs/13-telefono.md).</summary>
    private void SendClipboardToPhone()
    {
        if (_kde.Current is null)
            return;
        try
        {
            if (!System.Windows.Clipboard.ContainsText())
            {
                _notch.ShowNotice(DotIcons.Clip, Theme.Grey, "APPUNTI VUOTI");
                return;
            }
            _kde.SendClipboard(System.Windows.Clipboard.GetText());
            _notch.ShowNotice(DotIcons.Clip, Theme.White, "APPUNTI AL TELEFONO");
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            _notch.ShowNotice(DotIcons.Clip, Theme.Red, "APPUNTI OCCUPATI");
        }
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            ShowFile(path); // nessuna app associata: almeno si vede dov'è
        }
    }

    private static void ShowFile(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
}
