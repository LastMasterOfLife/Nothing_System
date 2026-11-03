using System.Windows.Threading;
using NothingShell.App.Interop;
using NothingShell.Core.Work;

namespace NothingShell.App.Services;

/// <summary>
/// Ogni 5 secondi guarda la finestra in primo piano e dà il tempo al progetto il cui titolo la riconosce, solo se l'utente ha
/// toccato mouse o tastiera da meno di 5 minuti (docs/19-ore-di-lavoro.md). Le finestre di Nothing Shell non contano: si sta
/// scrivendo qui, non lavorando a un progetto. Del titolo non resta traccia, solo il nome del progetto.
/// </summary>
public sealed class WorkClock : IDisposable
{
    private const int SavePeriod = 12; // campioni: un minuto

    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(5) };
    private readonly uint _ownProcess = (uint)Environment.ProcessId;
    private int _samples;

    public WorkClock(WorkStore store)
    {
        ArgumentNullException.ThrowIfNull(store);
        Store = store;
        _timer.Tick += (_, _) => Sample();
    }

    public WorkStore Store { get; }

    /// <summary>Un campione preso: il progetto in corso può essere cambiato.</summary>
    public event Action? Sampled;

    public void Start()
    {
        if (_timer.IsEnabled)
            return;
        Store.WriteReports(); // l'assistente trova subito i file, anche prima del primo minuto
        _timer.Start();
    }

    public void Stop()
    {
        if (!_timer.IsEnabled)
            return;
        _timer.Stop();
        Store.Sample(DateTime.Now, null, TimeSpan.Zero);
        Store.Save();
    }

    public void Dispose() => Stop();

    private void Sample()
    {
        var window = WindowNative.GetForegroundWindow();
        string? title = null;
        if (window != 0 && WindowNative.GetWindowThreadProcessId(window, out var process) != 0 && process != _ownProcess)
            title = WindowNative.GetTitle(window);
        Store.Sample(DateTime.Now, title, NativeMethods.IdleTime());
        if (++_samples % SavePeriod == 0)
            Store.Save();
        Sampled?.Invoke();
    }
}
