# 09 — Accesso Nothing (M7)

Animazioni Nothing intorno al blocco del PC: una card rotonda centrale con l'immagine
dell'account, il nome e il nome utente. **Il blocco vero resta quello di Windows**.

## Perché ibrido
La schermata di accesso di Windows (LogonUI) vive su un desktop protetto: nessuna app può
disegnarci sopra. Sostituirla davvero richiede un Credential Provider (DLL C++ installata come
amministratore), senza animazioni personalizzate e con il rischio di restare chiusi fuori.
Una schermata "finta" a tutto schermo si aggira con Ctrl+Alt+Canc. Quindi:
- la password, il PIN e Windows Hello restano a Windows; Nothing Shell non vede mai credenziali;
- Nothing Shell anima il **prima** (blocco) e il **dopo** (bentornato).

## Sequenze
```
Blocca (launcher, --lock) ─▶ [CHIUSURA 1,1 s] ─▶ LockWorkStation ─▶ [BLOCCATO: fermo]
Win+L o blocco automatico  ──────────────────────────────────────────▶ [BLOCCATO: fermo]
Sblocco (evento di sessione) ─▶ [BENTORNATO 1,7 s] ─▶ nascosto
```
| Fase | Cosa si vede |
|---|---|
| Chiusura | il nero si chiude a iride sul centro dello schermo; la card compare con un rimbalzo; i punti dell'anello arrivano da fuori e si spengono uno a uno; scritta "A presto" |
| Bloccato | nero, ora a punti in alto, card con anello spento, scritta "Bloccato". Nessun ridisegno tranne l'ora a ogni minuto |
| Bentornato | l'anello si accende in senso orario (punto di testa rosso); "Bentornato" o il saluto dell'ora; la card pulsa; poi l'iride si apre dalla card mostrando il desktop, mentre la card si rimpicciolisce e i punti dell'anello volano via |

Win+L non si può intercettare (lo gestisce Winlogon): in quel caso non c'è la chiusura animata,
ma lo sfondo Nothing è già pronto sotto la schermata di Windows e allo sblocco parte il bentornato.

## Card
- Cerchio di 176 DIP con l'immagine dell'account Windows (a colori, ritagliata a cerchio);
  senza immagine: iniziali a punti su `Surface`.
- Anello di 40 punti a 118 DIP dal centro.
- Sotto: nome visualizzato (28 DIP, bianco) e `nome utente` (14 DIP, grigio).
- Nome e immagine: API WinRT `Windows.System.User` (proprietà DisplayName, immagine 424×424);
  ripiego: `GetUserNameEx(NameDisplay)`, poi il nome utente.

## Sicurezza (vincoli)
- L'overlay è cosmetico: mai una barriera, mai credenziali, nessuna impostazione di sistema toccata.
- Non deve mai restare sopra il desktop sbloccato: con lo stato Bloccato un controllo ogni 1 s del
  desktop di input (`OpenInputDesktop` riesce = sessione sbloccata) avvia il bentornato anche se
  l'evento di sessione si perde; ogni sequenza animata ha un limite di 4 s; un clic la salta.
- Se `LockWorkStation` fallisce, l'overlay si nasconde.

## Monitor
La card è sul monitor principale; gli altri monitor ricevono un pannello nero semplice che
sparisce all'apertura dell'iride.

## Tendina di Windows
Per andare subito al PIN/viso senza trascinare la tendina serve il criterio "Non visualizzare la
schermata di blocco" (`HKLM\SOFTWARE\Policies\Microsoft\Windows\Personalization`, `NoLockScreen=1`).
È un'impostazione di sistema: Nothing Shell non la modifica, la imposta l'utente se vuole.

## Impostazioni
`settings.json` → `access: true` (sezione ACCESSO delle impostazioni: *Animazioni di blocco e
sblocco*). Spento: il pulsante Blocca chiama subito `LockWorkStation` e non c'è overlay.

## Logica pura (Core, testata)
- `AccessFlow`: stati Hidden / Locking / Locked / Welcoming, eventi e scadenze.
- `AccessTimeline`: per ogni istante delle due sequenze, i valori (iride, scala e opacità della
  card, punti accesi dell'anello, raggio di volo, testo).
- `AccessIdentity`: iniziali e saluto in base all'ora.

## Criteri di accettazione
- Blocca dal launcher: animazione di chiusura fluida, poi la schermata di Windows.
- Allo sblocco (PIN, viso, password) parte il bentornato e poi il desktop è subito usabile.
- Dopo Win+L: bentornato allo sblocco.
- `--snapshot` produce `access-locked.png` e i fotogrammi chiave delle due sequenze.

## Fuori portata
Sostituire LogonUI, sfondo personalizzato della schermata di Windows, più utenti.
