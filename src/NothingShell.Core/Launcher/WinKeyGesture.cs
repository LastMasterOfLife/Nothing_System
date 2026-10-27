namespace NothingShell.Core.Launcher;

public enum KeyDecision
{
    /// <summary>Lascia passare l'evento.</summary>
    Pass,

    /// <summary>Blocca il rilascio di Win, invia la maschera e il rilascio, apri il launcher.</summary>
    OpenLauncher,
}

/// <summary>
/// Riconosce il tasto Win premuto e rilasciato da solo (docs/06-launcher.md).
/// Le combinazioni con altri tasti e gli eventi iniettati passano intatti.
/// </summary>
public sealed class WinKeyGesture
{
    public const int LeftWin = 0x5B;
    public const int RightWin = 0x5C;

    /// <summary>Tasto virtuale non assegnato, inviato fra Win giù e Win su per non far aprire lo Start.</summary>
    public const byte MaskKey = 0xE8;

    private bool _winDown;
    private bool _chord;
    private volatile bool _enabled = true;

    /// <summary>Scritto dal thread UI, letto dal thread dell'hook.</summary>
    public bool Enabled
    {
        get => _enabled;
        set => _enabled = value;
    }

    public KeyDecision OnKey(int virtualKey, bool down, bool injected)
    {
        if (!Enabled || injected)
            return KeyDecision.Pass;

        var isWin = virtualKey is LeftWin or RightWin;
        if (down)
        {
            if (isWin)
            {
                if (!_winDown) // l'autorepeat di Win non azzera la combinazione
                {
                    _winDown = true;
                    _chord = false;
                }
            }
            else if (_winDown)
            {
                _chord = true;
            }
            return KeyDecision.Pass;
        }

        if (!isWin || !_winDown)
            return KeyDecision.Pass;

        _winDown = false;
        return _chord ? KeyDecision.Pass : KeyDecision.OpenLauncher;
    }

    /// <summary>Un clic con Win premuto (es. Win+clic) conta come combinazione.</summary>
    public void OnMouseButton()
    {
        if (_winDown)
            _chord = true;
    }
}
