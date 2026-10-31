using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NothingShell.App.Services;

/// <summary>
/// Esegue uno script PowerShell fisso e consegna le righe appena arrivano (docs/16-assistente-notch.md). Solo per gli
/// script di <c>RemoteDesktop</c>: i comandi dell'utente e del modello passano dal cancello dell'assistente.
/// </summary>
public static class ScriptRunner
{
    /// <summary>
    /// Prima dello script: niente barre di avanzamento, testo in UTF-8, ogni errore si ferma qui sotto. Dopo: il motivo vero
    /// in chiaro, perché con la riga di comando codificata PowerShell scriverebbe gli errori in XML.
    /// </summary>
    private const string Prelude = "$ProgressPreference='SilentlyContinue';$ErrorActionPreference='Stop';[Console]::OutputEncoding=[Text.UTF8Encoding]::new($false);$global:LASTEXITCODE=0\ntry {\n";
    private const string Epilogue = "\n} catch { $e = $_.Exception; while ($e.InnerException) { $e = $e.InnerException }; [Console]::Error.WriteLine($e.Message); exit 1 }\nif ($LASTEXITCODE) { exit $LASTEXITCODE }";

    /// <param name="line">La riga, e se viene dal flusso degli errori. Può arrivare da un thread qualsiasi.</param>
    /// <returns>L'exit code; se <paramref name="token"/> scade il processo viene chiuso e si lancia OperationCanceledException.</returns>
    public static async Task<int> RunAsync(string script, Action<string, bool> line, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(script);
        ArgumentNullException.ThrowIfNull(line);
        var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(Prelude + script + Epilogue));
        var start = new ProcessStartInfo("powershell.exe",
            $"-NoLogo -NoProfile -NonInteractive -ExecutionPolicy RemoteSigned -OutputFormat Text -EncodedCommand {encoded}")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false),
            StandardErrorEncoding = new UTF8Encoding(false),
        };

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is { } text)
                line(text, false);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is { } text)
                line(text, true);
        };
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            line(ex.Message, true);
            return -1;
        }
        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(token);
        }
        catch (OperationCanceledException)
        {
            Kill(process);
            throw;
        }
        return process.ExitCode;
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception)
        {
            // già chiuso
        }
    }
}
