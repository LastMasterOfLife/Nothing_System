# Nothing Shell

Strato grafico in stile Nothing OS sopra Windows 11: notch interattivo, widget a punti, barra.
Non sostituisce `explorer.exe`. Le spec sono in [docs/](docs/).

## Avvio
```
dotnet run --project src/NothingShell.App
```
All'avvio la taskbar di Windows viene nascosta e sostituita dalla barra Nothing; torna visibile
all'uscita. Per chiudere: tasto destro sul notch, su un widget o sulla barra → *Esci da Nothing Shell*,
oppure da terminale:
```
NothingShell.exe --exit
```
Se il processo viene terminato di forza e la taskbar resta nascosta:
```
NothingShell.exe --restore-taskbar
```

Quando arriva una mail il notch mostra per qualche secondo la busta e l'oggetto (da qualsiasi
client che manda notifiche di Windows: Outlook, Posta, Thunderbird, Gmail nel browser).
Sul notch aperto, uno swipe (touchpad a due dita, rotellina o trascinamento) scorre le pagine
Home · Posta · Timer · Sistema. Dettagli in [docs/08-notch-mail-pages.md](docs/08-notch-mail-pages.md).

Il launcher si apre con il tasto Win (da solo) o con il pulsante ⁘ della barra: si scrive per
cercare, ↑↓ e Invio per avviare. Le combinazioni Win+qualcosa restano a Windows; lo Start di
Windows è nel menu col tasto destro della barra, dove si può anche disattivare l'uso del tasto Win.
Uso e preferenze in `%USERPROFILE%\.nothing-shell\launcher.json`.

Le impostazioni (tasto destro su barra, notch o widget → *Impostazioni…*, oppure "Impostazioni
Nothing Shell" nel launcher) accendono e spengono subito notch, barra, tasto Win, singoli widget e
l'avvio con Windows.

**Plugin**: widget aggiuntivi descritti da una cartella con `plugin.json` in
`%USERPROFILE%\.nothing-shell\plugins\`; i dati arrivano da una URL JSON o da un comando qualsiasi
(Python, PowerShell…) che stampa JSON. Formato in [docs/07-settings-plugins.md](docs/07-settings-plugins.md),
esempi pronti in [plugins/](plugins/).

I widget si trascinano col tasto sinistro; dal menu col tasto destro si bloccano o si
riportano alla disposizione iniziale. Le posizioni sono in `%USERPROFILE%\.nothing-shell\widgets.json`.

Per controllare la grafica (widget, launcher, plugin con dati veri, impostazioni) senza avviare la shell:
```
dotnet run --project src/NothingShell.App -- --snapshot out
```

## Test
```
dotnet test
```

## Struttura
- `src/NothingShell.Core`: logica pura e testata (font a punti, layout testo, macchina a stati del notch, molle,
  disposizione e salvataggio widget, calendario, CPU/batteria)
- `src/NothingShell.App`: WPF (notch, widget, barra, launcher, impostazioni, host dei plugin, interop Win32);
  `ShellController` collega i componenti e applica le impostazioni dal vivo
- `plugins/`: plugin d'esempio (`meteo` http, `disco` comando)
- `tests/NothingShell.Core.Tests`: xUnit

## Note tecniche
- Rendering WPF in sola modalità software (`RenderMode.SoftwareOnly`): evita di caricare il driver GPU
  (memoria privata da ~265 MB a ~62 MB), con costo trascurabile su superfici così piccole.
- La finestra del notch è `WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW`: non ruba il focus e non compare in Alt+Tab.
- Media tramite `GlobalSystemMediaTransportControlsSessionManager` (Spotify, browser, Media Player…).
- La barra nasconde `Shell_TrayWnd` (nessuna modifica permanente): l'area di lavoro resta riservata,
  e la barra occupa esattamente quella fascia. Finestre elencate e aggiornate con `SetWinEventHook`, senza polling;
  `--dump-windows <file>` spiega quali finestre entrano nella barra e perché.
- Launcher: app da `shell:AppsFolder` (COM della shell su thread STA), icone da
  `IShellItemImageFactory` lette con `GetDIBits` per non perdere l'alfa, avvio tramite
  `explorer.exe shell:AppsFolder\<id>`. Tasto Win: hook `WH_KEYBOARD_LL` su un thread dedicato;
  il rilascio di Win da solo viene sostituito da maschera `0xE8` + rilascio, così lo Start non si apre.
- I widget non usano il trucco `WorkerW` (lì le icone del desktop rubano i click): sono finestre
  di proprietà di `Progman`, forzate in fondo allo z-order con `WM_WINDOWPOSCHANGING`.
