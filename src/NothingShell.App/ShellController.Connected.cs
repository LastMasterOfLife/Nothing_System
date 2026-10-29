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

namespace NothingShell.App;

/// <summary>Agenti al lavoro e connettori verso dispositivi e sistemi (docs/14-agenti-connettori.md).</summary>
public sealed partial class ShellController
{
    private const int PhoneNotificationsShown = 10;

    private readonly AgentWatcher _agentWatcher = new();
    private readonly ConnectorHost _connectors = new();
    private readonly ReceivedFiles _phoneFiles = new(ReceivedFiles.DefaultPath);
    private readonly List<PhoneEvent> _phoneNotifications = [];

    private ConnectedPageActions ConnectedActions => new(OpenFile, ShowFile, () => _kde.FindPhone(), (m, a) => _connectors.Run(m, a));

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
        _connectors.Changed += () => _notch.SetConnectors(_connectors.States);
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
