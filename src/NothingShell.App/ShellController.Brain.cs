using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.Core.Brain;

namespace NothingShell.App;

/// <summary>Terminale PowerShell e cervello dei comandi (docs/15-comandi-cervello.md).</summary>
public sealed partial class ShellController : IAssistantPanelActions
{
    private readonly BrainService _brain = new();
    private AssistantWindow? _assistantWindow;
    private CommandAssistant? _assistant;
    private OllamaAssistantModel? _assistantModel;
    private IReadOnlyList<string> _assistantModels = [];

    public string BrainStatus => _brain.Status;

    public int BrainLearned => _brain.Memory.LearnedCount;

    public bool BrainModelInstalled => _brain.ModelInstalled;

    public bool BrainInstalling => _brain.Installing;

    public event Action? BrainChanged;

    public void InstallBrainModel() => _ = InstallBrainModelAsync();

    public void CancelBrainInstall() => _brain.CancelInstall();

    public void ClearBrainMemory() => _brain.ClearMemory();

    public void OpenTerminal() => OpenTerminal(null, null, null);

    CommandAssistant IAssistantPanelActions.Assistant => _assistant ??= new CommandAssistant(_brain.Memory, new AssistantRunner(),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), _brain.ProposeAsync);

    /// <summary>Chi risponde nel pannello, per le impostazioni.</summary>
    public string AssistantStatus => _assistantModel is { } model
        ? $"Risponde {model.Name}, con Ollama su questo PC"
        : "Senza Ollama il pannello usa le scorciatoie e il modello piccolo, che propone comandi ma non scrive risposte";

    public IReadOnlyList<string> AssistantModels => _assistantModels;

    public void OpenAssistant()
    {
        _assistantWindow ??= new AssistantWindow(this);
        _assistantWindow.Toggle();
    }

    /// <summary>Manda una richiesta all'assistente: ogni comando passa dal suo cancello, anche se viene da altre parti della shell.</summary>
    public void AskAssistant(string text)
    {
        _assistantWindow ??= new AssistantWindow(this);
        _assistantWindow.Ask(text);
    }

    /// <summary>Passa al modello successivo fra quelli installati in Ollama.</summary>
    public async Task NextAssistantModelAsync()
    {
        await RefreshAssistantModelAsync(CancellationToken.None);
        if (_assistantModels.Count < 2 || _assistantModel is null)
            return;
        var next = _assistantModels[(_assistantModels.ToList().IndexOf(_assistantModel.Name) + 1) % _assistantModels.Count];
        Update(_settings with { AssistantModel = next });
        await RefreshAssistantModelAsync(CancellationToken.None);
        BrainChanged?.Invoke();
    }

    public void OpenCriticalCommands()
    {
        var path = CriticalCommands.DefaultUserPath;
        try
        {
            if (!File.Exists(path))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                File.WriteAllText(path, CriticalCommands.UserFileTemplate);
            }
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or Win32Exception)
        {
            MessageBox.Show(ex.Message, "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    async Task<string> IAssistantPanelActions.PrepareAsync(CancellationToken token)
    {
        await RefreshAssistantModelAsync(token);
        return _assistantModel is { } model ? $"{model.Name} · su questo PC" : "scorciatoie e modello piccolo · Ollama non trovato";
    }

    void IAssistantPanelActions.OpenInTerminal(string? phrase, string command, string? criticalReason) => OpenTerminal(phrase, command, criticalReason);

    /// <summary>Quello scelto nelle impostazioni, altrimenti il primo installato. Ollama può accendersi o spegnersi in ogni momento.</summary>
    private async Task RefreshAssistantModelAsync(CancellationToken token)
    {
        _assistantModels = await OllamaAssistantModel.InstalledAsync(token);
        var name = _assistantModels.Contains(_settings.AssistantModel ?? string.Empty) ? _settings.AssistantModel : _assistantModels.Count > 0 ? _assistantModels[0] : null;
        if (_assistantModel?.Name != name)
        {
            _assistantModel?.Dispose();
            _assistantModel = name is null ? null : new OllamaAssistantModel(name);
        }
        var assistant = ((IAssistantPanelActions)this).Assistant;
        assistant.Model = _assistantModel;
    }

    private void WireBrain()
    {
        _brain.Changed += () => BrainChanged?.Invoke();
        _brain.Start();
        _launcher.Decide = async (input, token) => await _brain.DecideAsync(input, forced: false, null, token);
        _launcher.CommandLineRequested += (phrase, command) => OpenTerminal(phrase, command, CommandSafety.Reason(command));
        _notch.AssistantRequested += OpenAssistant;
        _ = FindAssistantModelAsync();
    }

    private async Task FindAssistantModelAsync()
    {
        await RefreshAssistantModelAsync(CancellationToken.None);
        BrainChanged?.Invoke();
    }

    private async Task InstallBrainModelAsync()
    {
        if (await _brain.InstallModelAsync() is { } problem)
            MessageBox.Show(problem, "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Una console vera (Windows Terminal se c'è) con il PowerShell dell'utente, profilo compreso: così funziona ogni
    /// comando, anche i programmi interattivi. <paramref name="command"/> è già confermato da chi l'ha scelto, tranne se
    /// c'è <paramref name="critical"/>: allora il terminale chiede di scrivere SI.
    /// </summary>
    private static void OpenTerminal(string? phrase, string? command, string? critical)
    {
        var module = Path.Combine(AppContext.BaseDirectory, "tools", "NothingShell.psm1");
        var script = $"Import-Module {PowerShellText.Quote(module)}; Enable-NothingShellBrain";
        if (command is not null)
        {
            script += $"; Invoke-NothingShellProposal -Command {PowerShellText.Quote(command)}";
            if (phrase is not null)
                script += $" -Phrase {PowerShellText.Quote(phrase)}";
            if (critical is not null)
                script += $" -Critical {PowerShellText.Quote(critical)}";
        }

        var shell = FindOnPath("pwsh.exe") ?? "powershell.exe";
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
        var arguments = $"-NoLogo -NoExit -ExecutionPolicy RemoteSigned -EncodedCommand {encoded}";
        var folder = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var terminal = FindOnPath("wt.exe");
        var start = terminal is null
            ? new ProcessStartInfo(shell, arguments)
            : new ProcessStartInfo(terminal, $"-d \"{folder}\" {shell} {arguments}");
        start.UseShellExecute = true;
        start.WorkingDirectory = folder;
        try
        {
            Process.Start(start)?.Dispose();
        }
        catch (Win32Exception ex)
        {
            MessageBox.Show(ex.Message, "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private static string? FindOnPath(string program) =>
        (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(folder => Path.Combine(folder, program)).FirstOrDefault(File.Exists);
}
