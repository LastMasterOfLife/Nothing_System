using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using NothingShell.App.Interop;
using NothingShell.Core.Dev;

namespace NothingShell.App.Services;

/// <summary>
/// Strumenti per le attività di sviluppo (docs/12-sviluppo.md): l'editor che apre un file alla riga, la finestra
/// del terminale di un processo, l'esecuzione nascosta di <c>git</c> e <c>gh</c>.
/// </summary>
public static class DevTools
{
    /// <summary>Programma dell'editor: dal modello in settings.json, o il primo fra VS Code, Insiders e Cursor nel PATH.</summary>
    public static string? EditorTemplate(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured))
            return configured;
        foreach (var candidate in EditorCommand.Candidates)
            if (FindInPath(candidate) is { } path)
                return $"\"{path}\" {EditorCommand.GotoArguments}";
        return null;
    }

    /// <summary>Apre il file alla riga nell'editor; senza editor, con l'app associata (senza riga).</summary>
    public static void OpenInEditor(CodeLocation location, string? configured)
    {
        ArgumentNullException.ThrowIfNull(location);
        if (EditorTemplate(configured) is { } template)
        {
            var (program, arguments) = EditorCommand.Build(template, location);
            try
            {
                Process.Start(new ProcessStartInfo(program, arguments)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true, // code.cmd è un batch: niente console che lampeggia
                    WorkingDirectory = Path.GetDirectoryName(location.File) ?? string.Empty,
                })?.Dispose();
                return;
            }
            catch (Win32Exception)
            {
                // editor configurato male: si prova con l'app associata
            }
        }
        OpenPath(location.File);
    }

    public static void OpenPath(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            // nessuna app associata o file sparito
        }
    }

    public static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// La finestra del terminale di una shell: il primo antenato con una finestra principale (conhost risponde col
    /// PID della shell stessa; Windows Terminal e VS Code sono più su).
    /// </summary>
    public static nint TerminalWindow(int pid)
    {
        var parents = ParentMap();
        var current = pid;
        for (var depth = 0; depth < 8 && current > 4; depth++)
        {
            try
            {
                using var process = Process.GetProcessById(current);
                if (process.MainWindowHandle != 0)
                    return process.MainWindowHandle;
            }
            catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
            {
                return 0; // processo chiuso
            }
            if (!parents.TryGetValue(current, out current))
                break;
        }
        return 0;
    }

    public static bool IsForeground(nint hwnd) =>
        hwnd != 0 && WindowNative.GetRootOwner(WindowNative.GetForegroundWindow()) == WindowNative.GetRootOwner(hwnd);

    public static bool ProcessAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or Win32Exception)
        {
            return ex is Win32Exception; // di un altro utente: esiste
        }
    }

    /// <summary>Esegue un programma senza finestra; null se non c'è o non risponde in tempo.</summary>
    public static async Task<(int ExitCode, string Output)?> RunAsync(string program, string arguments, string folder, TimeSpan timeout)
    {
        var info = new ProcessStartInfo(program, arguments)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = folder,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        info.Environment["GH_PROMPT_DISABLED"] = "1";
        info.Environment["NO_COLOR"] = "1";
        Process? process;
        try
        {
            process = Process.Start(info);
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
        {
            return null;
        }
        if (process is null)
            return null;
        using (process)
        using (var cancel = new CancellationTokenSource(timeout))
        {
            try
            {
                var output = process.StandardOutput.ReadToEndAsync(cancel.Token);
                var errors = process.StandardError.ReadToEndAsync(cancel.Token);
                await process.WaitForExitAsync(cancel.Token);
                return (process.ExitCode, (await output) + (await errors));
            }
            catch (OperationCanceledException)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                }
                catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
                {
                    // già finito
                }
                return null;
            }
        }
    }

    /// <summary>Le ultime righe di un log (al massimo 2 MB), con la codifica dal BOM (PowerShell 5 scrive in UTF-16).</summary>
    public static string? ReadLogTail(string path)
    {
        const long maxBytes = 2 * 1024 * 1024;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var bom = new byte[4];
            var bomLength = stream.Read(bom, 0, 4);
            var encoding = bomLength >= 2 && bom[0] == 0xFF && bom[1] == 0xFE ? System.Text.Encoding.Unicode : System.Text.Encoding.UTF8;
            stream.Position = Math.Max(0, stream.Length - maxBytes);
            if (encoding == System.Text.Encoding.Unicode && stream.Position % 2 == 1)
                stream.Position++;
            using var reader = new StreamReader(stream, encoding, detectEncodingFromByteOrderMarks: stream.Position == 0);
            return reader.ReadToEnd();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    public static string? FindInPath(string name)
    {
        var extensions = (Environment.GetEnvironmentVariable("PATHEXT") ?? ".COM;.EXE;.BAT;.CMD").Split(';', StringSplitOptions.RemoveEmptyEntries);
        foreach (var folder in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty).Split(';', StringSplitOptions.RemoveEmptyEntries))
        {
            foreach (var extension in extensions)
            {
                try
                {
                    var candidate = Path.Combine(folder.Trim('"'), name + extension);
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch (ArgumentException)
                {
                    // voce del PATH non valida
                }
            }
        }
        return null;
    }

    /// <summary>PID → PID del genitore per tutti i processi (un'istantanea ToolHelp, pochi millisecondi).</summary>
    private static Dictionary<int, int> ParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(0x00000002, 0); // TH32CS_SNAPPROCESS
        if (snapshot == -1)
            return map;
        try
        {
            var entry = new ProcessEntry { Size = (uint)Marshal.SizeOf<ProcessEntry>() };
            for (var ok = Process32First(snapshot, ref entry); ok; ok = Process32Next(snapshot, ref entry))
                map[(int)entry.ProcessId] = (int)entry.ParentProcessId;
        }
        finally
        {
            CloseHandle(snapshot);
        }
        return map;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct ProcessEntry
    {
        public uint Size;
        public uint Usage;
        public uint ProcessId;
        public nint DefaultHeapId;
        public uint ModuleId;
        public uint Threads;
        public uint ParentProcessId;
        public int PriorityClassBase;
        public uint Flags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        public string ExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint CreateToolhelp32Snapshot(uint flags, uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32FirstW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32First(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "Process32NextW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool Process32Next(nint snapshot, ref ProcessEntry entry);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(nint handle);
}
