using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using NothingShell.App.Services;
using NothingShell.Core.Brain;

namespace NothingShell.App;

/// <summary>Terminale PowerShell e cervello dei comandi (docs/15-comandi-cervello.md).</summary>
public sealed partial class ShellController
{
    private readonly BrainService _brain = new();

    public string BrainStatus => _brain.Status;

    public int BrainLearned => _brain.Memory.LearnedCount;

    public bool BrainModelInstalled => _brain.ModelInstalled;

    public bool BrainInstalling => _brain.Installing;

    public event Action? BrainChanged;

    public void InstallBrainModel() => _ = InstallBrainModelAsync();

    public void CancelBrainInstall() => _brain.CancelInstall();

    public void ClearBrainMemory() => _brain.ClearMemory();

    public void OpenTerminal() => OpenTerminal(null, null);

    private void WireBrain()
    {
        _brain.Changed += () => BrainChanged?.Invoke();
        _brain.Start();
        _launcher.Decide = async (input, token) => await _brain.DecideAsync(input, forced: false, null, token);
        _launcher.CommandLineRequested += OpenTerminal;
    }

    private async Task InstallBrainModelAsync()
    {
        if (await _brain.InstallModelAsync() is { } problem)
            MessageBox.Show(problem, "Nothing Shell", MessageBoxButton.OK, MessageBoxImage.Warning);
    }

    /// <summary>
    /// Una console vera (Windows Terminal se c'è) con il PowerShell dell'utente, profilo compreso: così funziona ogni
    /// comando, anche i programmi interattivi. <paramref name="command"/> è già confermato da chi l'ha scelto.
    /// </summary>
    private static void OpenTerminal(string? phrase, string? command)
    {
        var module = Path.Combine(AppContext.BaseDirectory, "tools", "NothingShell.psm1");
        var script = $"Import-Module {PowerShellText.Quote(module)}; Enable-NothingShellBrain";
        if (command is not null)
        {
            script += $"; Invoke-NothingShellProposal -Command {PowerShellText.Quote(command)}";
            if (phrase is not null)
                script += $" -Phrase {PowerShellText.Quote(phrase)}";
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
