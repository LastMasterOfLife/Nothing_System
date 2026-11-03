using System.ComponentModel;
using System.Globalization;
using System.Diagnostics;
using System.IO;
using System.Text;
using NothingShell.Core.Brain;

namespace NothingShell.App.Services;

/// <summary>
/// Il pulsante Aggiorna delle impostazioni: chiude la shell, ricompila dal sorgente se si sta girando da una cartella di
/// sviluppo e la riapre. Chiudere e riaccendere a mano non serve più dopo ogni modifica. Un processo PowerShell a parte
/// aspetta che la shell sia uscita (finché gira, il suo file è bloccato e non si può ricompilare). Se la compilazione fallisce
/// riparte comunque la versione di prima, così non si resta senza shell, e si apre il log degli errori.
/// </summary>
public static class SelfUpdate
{
    public static string Log { get; } = Path.Combine(Path.GetTempPath(), "nothing-shell", "aggiorna.log");

    /// <summary>Il progetto da ricompilare, se questo eseguibile sta nella cartella <c>bin</c> di una copia del sorgente.</summary>
    public static (string Project, string Configuration)? FindSource(string? exe = null)
    {
        exe ??= Environment.ProcessPath;
        if (exe is null)
            return null;
        for (var folder = Path.GetDirectoryName(exe); folder is not null; folder = Path.GetDirectoryName(folder))
        {
            var project = Path.Combine(folder, "NothingShell.App.csproj");
            if (File.Exists(project) && exe.StartsWith(Path.Combine(folder, "bin") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                var parts = exe[(folder.Length + 1)..].Split(Path.DirectorySeparatorChar);
                return (project, parts.Length > 2 ? parts[1] : "Debug"); // bin\<Configurazione>\...
            }
        }
        return null;
    }

    /// <summary>Il testo dello script che gira dopo l'uscita della shell.</summary>
    public static string Script(int processId, string exe, (string Project, string Configuration)? source)
    {
        var script = new StringBuilder();
        script.Append(CultureInfo.InvariantCulture, $"Wait-Process -Id {processId} -Timeout 30 -ErrorAction SilentlyContinue\n");
        script.Append("$failed = $false\n");
        if (source is { } s)
        {
            script.Append(CultureInfo.InvariantCulture, $"New-Item -ItemType Directory -Force {PowerShellText.Quote(Path.GetDirectoryName(Log)!)} | Out-Null\n");
            script.Append(CultureInfo.InvariantCulture, $"dotnet build {PowerShellText.Quote(s.Project)} -c {PowerShellText.Quote(s.Configuration)} -v q --nologo *> {PowerShellText.Quote(Log)}\n");
            script.Append("if ($LASTEXITCODE -ne 0) { $failed = $true }\n");
        }
        script.Append(CultureInfo.InvariantCulture, $"Start-Process {PowerShellText.Quote(exe)}\n");
        script.Append(CultureInfo.InvariantCulture, $"if ($failed) {{ Start-Process notepad {PowerShellText.Quote(Log)} }}\n");
        return script.ToString();
    }

    /// <summary>Prepara il riavvio; null se è partito (chi chiama deve chiudere la shell), altrimenti il problema.</summary>
    public static string? Prepare()
    {
        var exe = Environment.ProcessPath;
        if (exe is null)
            return "Non trovo l'eseguibile della shell.";
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Convert.ToBase64String(Encoding.Unicode.GetBytes(Script(Environment.ProcessId, exe, FindSource(exe)))) })
            start.ArgumentList.Add(argument);
        try
        {
            Process.Start(start)?.Dispose();
            return null;
        }
        catch (Win32Exception ex)
        {
            return ex.Message;
        }
    }
}
