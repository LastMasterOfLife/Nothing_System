using NothingShell.App.Interop;
using NothingShell.App.Notch;
using NothingShell.Core.RemoteAccess;

namespace NothingShell.App;

/// <summary>Il campo sotto la data nel notch e i computer remoti (docs/16-assistente-notch.md).</summary>
public sealed partial class ShellController : IRemoteConnectActions
{
    private const string CredentialComment = "Nothing Shell: desktop remoto";

    private readonly RemoteComputerStore _computers = new(RemoteComputerStore.DefaultPath);
    private RemoteConnectWindow? _remoteWindow;

    public IReadOnlyList<RemoteComputer> RemoteComputers => _computers.All;

    public void AddRemoteComputer() => RemoteWindow().With(w => w.ShowForm((RemoteComputer?)null));

    public void EditRemoteComputer(RemoteComputer computer) => RemoteWindow().With(w => w.ShowForm(computer));

    /// <summary>Toglie la scorciatoia e, se nessun altro computer usa quell'indirizzo, la sua password.</summary>
    public void RemoveRemoteComputer(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        if (!_computers.Remove(computer))
            return;
        if (!_computers.All.Any(c => c.Host == computer.Host))
            CredentialStore.Delete(RemoteDesktop.CredentialTarget(computer));
        BrainChanged?.Invoke();
    }

    public string? SaveComputer(RemoteComputer computer, RemoteComputer? replacing, string password)
    {
        ArgumentNullException.ThrowIfNull(computer);
        ArgumentNullException.ThrowIfNull(password);
        if (_computers.Problem(computer, replacing) is { } problem)
            return problem;

        var target = RemoteDesktop.CredentialTarget(computer);
        var previous = replacing is null ? null : RemoteDesktop.CredentialTarget(replacing);
        // Indirizzo o utente cambiati senza riscrivere la password: quella salvata segue il computer.
        var kept = password.Length == 0 && previous is not null && (previous != target || computer.UserName != replacing!.UserName)
            ? CredentialStore.Read(previous)
            : null;
        var secret = password.Length > 0 ? password : kept;
        if (secret is not null && computer.UserName is not null && !CredentialStore.Write(target, computer.UserName, secret, CredentialComment))
            return "Impossibile salvare la password nel Gestore credenziali di Windows.";

        _computers.Save(computer, replacing);
        if (previous is not null && previous != target && !_computers.All.Any(c => RemoteDesktop.CredentialTarget(c) == previous))
            CredentialStore.Delete(previous);
        BrainChanged?.Invoke();
        return null;
    }

    public void ComputerConnected(RemoteComputer computer, string? phrase)
    {
        ArgumentNullException.ThrowIfNull(computer);
        _computers.RecordSuccess(computer, phrase, DateTimeOffset.Now);
    }

    public RemoteComputer? Current(RemoteComputer computer)
    {
        ArgumentNullException.ThrowIfNull(computer);
        return _computers.All.FirstOrDefault(c => string.Equals(c.Name, computer.Name, StringComparison.OrdinalIgnoreCase));
    }

    private void WireRemoteAccess()
    {
        _notch.AskPreview = text => Route(text).Preview;
        _notch.AskSubmitted += OnAskSubmitted;
    }

    private RequestRoute Route(string text) => RequestRouter.Route(text, _computers.All, _brain.Memory);

    private void OnAskSubmitted(string text, bool choose)
    {
        var route = Route(text);
        switch (route.ActionFor(choose))
        {
            case RequestAction.Use when route.Sure?.Computer is { } computer:
                RemoteWindow().With(w => w.Connect(computer, null));
                break;
            case RequestAction.Choose:
                RemoteWindow().With(w => w.ShowChoice(route));
                break;
            case RequestAction.NewComputer:
                RemoteWindow().With(w => w.ShowForm(route));
                break;
            default:
                AskAssistant(text); // anche le scorciatoie dei comandi: da lì passa ogni comando
                break;
        }
    }

    /// <summary>Una finestra per richiesta; quella con una connessione in corso resta aperta finché non finisce.</summary>
    private RemoteWindowOpener RemoteWindow()
    {
        if (_remoteWindow is { IsBusy: false } previous)
            previous.Close();
        var window = new RemoteConnectWindow(this);
        window.Closed += (_, _) =>
        {
            if (ReferenceEquals(_remoteWindow, window))
                _remoteWindow = null;
        };
        _remoteWindow = window;
        return new RemoteWindowOpener(window);
    }

    /// <summary>Riempie la finestra prima di mostrarla, così non si vede vuota.</summary>
    private readonly record struct RemoteWindowOpener(RemoteConnectWindow Window)
    {
        public void With(Action<RemoteConnectWindow> fill)
        {
            fill(Window);
            Window.Present();
        }
    }
}
