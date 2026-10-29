using System.Diagnostics;
using NothingShell.App.Notch;
using NothingShell.App.Services;
using NothingShell.Core.Design;
using NothingShell.Core.Notch;

namespace NothingShell.App;

/// <summary>
/// Telefono nel notch (docs/13-telefono.md): da Collegamento al telefono (notifiche di Windows) e, se acceso, dal
/// collegamento diretto KDE Connect. Con un telefono collegato direttamente le notifiche di Collegamento al
/// telefono si ignorano, altrimenti arriverebbe tutto due volte.
/// </summary>
public sealed partial class ShellController
{
    private static readonly TimeSpan CallDuration = TimeSpan.FromSeconds(20); // il telefono squilla più a lungo
    private static readonly TimeSpan PairDuration = TimeSpan.FromSeconds(30);

    private readonly KdeConnectService _kde = new();
    private DateTime _callShownAt = DateTime.MinValue;

    public bool PhoneDirect
    {
        get => _settings.PhoneDirect;
        set
        {
            Update(_settings with { PhoneDirect = value });
            if (value)
                _kde.Start();
            else
                _kde.Stop();
            ApplyDevActivities(); // la pagina Telefono c'è anche solo col collegamento diretto
            RefreshPhonePage();
        }
    }

    public IPhoneActions Phone => _kde;

    private void WirePhone()
    {
        _notifications.PhoneArrived += phone =>
        {
            if (_kde.Current is null)
                ShowPhoneEvent(phone, direct: false);
        };
        _kde.EventArrived += phone => ShowPhoneEvent(phone, direct: true);
        _kde.PairRequested += device =>
        {
            // anche col notch nascosto: senza conferma l'associazione scade
            _notch.ShowCard(NotchCards.PairRequest(device,
                () => _kde.AcceptPair(device.DeviceId),
                () => _kde.RejectPair(device.DeviceId),
                _notch.DismissCard), PairDuration);
        };
        _kde.PhoneConnected += device =>
        {
            if (CanShow(LiveActivity.Phone))
                _notch.ShowNotice(DotIcons.Phone, Rendering.Theme.White, $"{device.Name} COLLEGATO");
        };
        _kde.BatteryLow += device =>
        {
            if (CanShow(LiveActivity.Phone) && device.Battery is { } battery)
                _notch.ShowNotice(DotIcons.Phone, Rendering.Theme.Red, $"{device.Name}  {battery.Charge}%");
        };
        _kde.ClipboardReceived += text =>
        {
            if (_clipWatcher.SetText(text) && CanShow(LiveActivity.Phone))
                _notch.ShowNotice(DotIcons.Clip, Rendering.Theme.White, "APPUNTI DAL TELEFONO");
        };
        _kde.FileReceived += path =>
        {
            if (CanShow(LiveActivity.Phone))
                _notch.ShowCard(NotchCards.Download(path, _notch.DismissCard), CardDuration);
        };
        _kde.UrlReceived += uri =>
        {
            Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true })?.Dispose();
            if (CanShow(LiveActivity.Phone))
                _notch.ShowNotice(DotIcons.Phone, Rendering.Theme.White, uri.Host);
        };
    }

    private void ShowPhoneEvent(PhoneEvent phone, bool direct)
    {
        if (phone.Kind == PhoneEventKind.CallEnded)
        {
            if (DateTime.Now - _callShownAt < CallDuration)
                _notch.DismissCard();
            _callShownAt = DateTime.MinValue;
            return;
        }
        RememberPhoneEvent(phone);
        if (!CanShow(LiveActivity.Phone))
            return;
        switch (phone.Kind)
        {
            case PhoneEventKind.IncomingCall:
                // da Collegamento al telefono si risponde; col collegamento diretto si può solo silenziare
                _notch.ShowCard(direct
                    ? NotchCards.Call(phone, "SILENZIA", _kde.MuteRinger, _notch.DismissCard)
                    : NotchCards.Call(phone, "RISPONDI", OpenPhoneLink, _notch.DismissCard), CallDuration);
                _callShownAt = DateTime.Now;
                break;
            case PhoneEventKind.MissedCall:
                _notch.ShowNotice(DotIcons.Phone, Rendering.Theme.Red, $"PERSA: {phone.Title}");
                break;
            default:
                _notch.ShowNotice(DotIcons.Phone, Rendering.Theme.White, PhoneLink.Summary(phone));
                break;
        }
    }

    /// <summary>Collegamento al telefono: da lì si risponde alle chiamate e ai messaggi.</summary>
    private static void OpenPhoneLink() =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"shell:AppsFolder\\{PhoneLink.AppId}") { UseShellExecute = true })?.Dispose();
}
