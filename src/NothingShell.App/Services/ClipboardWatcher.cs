using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Threading;
using NothingShell.Core.Dev;

namespace NothingShell.App.Services;

/// <summary>
/// Testo copiato negli appunti (docs/12-sviluppo.md), con <c>AddClipboardFormatListener</c> su una finestra di soli
/// messaggi. Non si guarda nulla di ciò che i gestori di password chiedono di ignorare. Per i segreti c'è la
/// cancellazione dopo 30 s, solo se gli appunti non sono cambiati nel frattempo.
/// </summary>
public sealed class ClipboardWatcher : IDisposable
{
    private const int WmClipboardUpdate = 0x031D;
    public static readonly TimeSpan SecretLifetime = TimeSpan.FromSeconds(30);

    private static readonly string[] IgnoredFormats = ["ExcludeClipboardContentFromMonitorProcessing", "Clipboard Viewer Ignore"];

    private readonly DispatcherTimer _debounce = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private readonly DispatcherTimer _clear = new() { Interval = SecretLifetime };
    private HwndSource? _window;
    private uint _ownSequence;
    private uint _clearSequence;

    public ClipboardWatcher()
    {
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Read();
        };
        _clear.Tick += (_, _) =>
        {
            _clear.Stop();
            ClearIfUnchanged(_clearSequence);
        };
    }

    /// <summary>Testo copiato da un'altra app: testo e numero di sequenza degli appunti.</summary>
    public event Action<string, uint>? Copied;

    public void Start()
    {
        if (_window is not null)
            return;
        _window = new HwndSource(new HwndSourceParameters("NothingShell.Clipboard")
        {
            ParentWindow = -3, // HWND_MESSAGE
            WindowStyle = 0,
        });
        _window.AddHook(OnMessage);
        AddClipboardFormatListener(_window.Handle);
    }

    public void Stop()
    {
        if (_window is not null)
        {
            RemoveClipboardFormatListener(_window.Handle);
            _window.Dispose();
            _window = null;
        }
        _debounce.Stop();
        _clear.Stop();
    }

    public void Dispose() => Stop();

    /// <summary>Scrive negli appunti (Formatta, Copia HEX…): la copia nostra non torna come <see cref="Copied"/>.</summary>
    public bool SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                _ownSequence = GetClipboardSequenceNumber();
                return true;
            }
            catch (Exception ex) when (ex is COMException or ExternalException)
            {
                Thread.Sleep(30); // appunti aperti da un'altra app
            }
        }
        return false;
    }

    /// <summary>Cancella gli appunti tra 30 s se contengono ancora la copia <paramref name="sequence"/>.</summary>
    public void ScheduleClear(uint sequence)
    {
        _clearSequence = sequence;
        _clear.Stop();
        _clear.Start();
    }

    public void CancelClear() => _clear.Stop();

    public void ClearNow(uint sequence)
    {
        _clear.Stop();
        ClearIfUnchanged(sequence);
    }

    public static uint Sequence => GetClipboardSequenceNumber();

    private static void ClearIfUnchanged(uint sequence)
    {
        if (GetClipboardSequenceNumber() != sequence)
            return;
        try
        {
            Clipboard.Clear();
        }
        catch (Exception ex) when (ex is COMException or ExternalException)
        {
            // appunti occupati: resta com'è
        }
    }

    private nint OnMessage(nint hwnd, int msg, nint wParam, nint lParam, ref bool handled)
    {
        if (msg == WmClipboardUpdate)
        {
            // Alcune app scrivono gli appunti più volte di fila: si legge quando si calmano.
            _debounce.Stop();
            _debounce.Start();
        }
        return 0;
    }

    private void Read()
    {
        var sequence = GetClipboardSequenceNumber();
        if (sequence == _ownSequence)
            return;
        try
        {
            if (IgnoredFormats.Any(Clipboard.ContainsData) || !CanIncludeInHistory() || !Clipboard.ContainsText())
                return;
            var text = Clipboard.GetText();
            if (text.Length is 0 or > ClipClassifier.MaxLength)
                return;
            Copied?.Invoke(text, sequence);
        }
        catch (Exception ex) when (ex is COMException or ExternalException or OutOfMemoryException)
        {
            // appunti occupati o troppo grandi
        }
    }

    /// <summary><c>CanIncludeInClipboardHistory</c> = 0: l'app chiede di non tenerne traccia (Windows lo usa per le password).</summary>
    private static bool CanIncludeInHistory()
    {
        if (!Clipboard.ContainsData("CanIncludeInClipboardHistory"))
            return true;
        return Clipboard.GetData("CanIncludeInClipboardHistory") is not MemoryStream { Length: >= 4 } stream
               || BitConverter.ToInt32(stream.ToArray(), 0) != 0;
    }

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(nint hwnd);

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();
}
