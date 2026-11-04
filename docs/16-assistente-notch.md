# 16 — Il campo nel notch e i computer remoti

Si passa col mouse sul notch, si clicca il campo subito sotto la data e si scrive cosa serve: "collegati al computer di
Luca". Invio apre la finestra che mostra dal vivo i comandi veri, gli stessi passi che si farebbero a mano aprendo Desktop
remoto e scrivendo indirizzo, utente e password.

Il campo gestisce i computer remoti e la scelta fra scorciatoie. Ogni comando, anche scelto da qui, passa dall'assistente e
dal suo unico controllo sui comandi critici: vedi [docs/15-comandi-cervello.md](15-comandi-cervello.md).

## Il campo
- **Passarci sopra non prende la tastiera**, così non si interrompe chi sta scrivendo altrove: la prende il clic sul campo.
- Finché si scrive, il notch resta aperto anche col mouse altrove, e non cambia pagina né per uno swipe né per una mail.
- **La riga sotto il campo dice cosa farebbe Invio, prima di premerlo.** Si aggiorna a ogni tasto, senza modello.
- Esc butta il testo e rende la tastiera alla finestra di prima. Cliccando altrove il testo resta per la prossima apertura.
- C'è anche con la musica in riproduzione: la Home è alta 190 con l'ora e 232 con la musica.

## Cosa fa Invio
| La richiesta | Riga sotto il campo | Invio |
|---|---|---|
| Una sola scorciatoia con le stesse parole | `→ Computer di Luca · 192.168.1.50` | la usa subito |
| Più scorciatoie possibili, o una solo simile | `Computer di Luca, Computer di Marta · Invio per scegliere` | apre la scelta: nulla parte da solo |
| Chiede un computer che non c'è | `Nuovo computer: Computer di Marco · Invio` | modulo, oppure scelta se ci sono computer simili |
| Altro | `Nessuna scorciatoia · Invio per chiedere all'assistente` | passa all'assistente |

Ctrl+Invio apre sempre la scelta, anche quando la risposta sarebbe sicura.

## Simile non vuol dire uguale
`RequestMatcher` confronta le parole della richiesta con quelle della scorciatoia, senza badare all'ordine, a verbi come
"collegati" né ad articoli e preposizioni. "PC" e "computer" sono la stessa parola.

| Corrispondenza | Esempio con "Computer di Luca" | Invio |
|---|---|---|
| Esatta | "connettiti al pc di luca" | parte, se è l'unica |
| Contenuta | "collegati a luca" | si sceglie |
| Quasi uguale | "collegati al compiuter di luca", "computer di luna" | si sceglie |
| Diversa | "collegati al computer di marco" | in fondo, sotto *Simili, ma non uguali*: prima viene *Nuovo computer* |

Solo la corrispondenza esatta può partire con un Invio, e "Paola" non è mai "Paolo": per questo il confronto esatto non usa la
radice delle parole di `IntentText`. Le cifre contano sempre: `server1` non è `server2`.

Una frase scelta dall'elenco, se la connessione riesce, diventa un altro nome di quel computer e la volta dopo non si chiede
più. Si dimentica da sola se un computer aggiunto dopo potrebbe corrispondere anch'esso: "luca" vale per "Computer di Luca"
finché non arriva "Portatile di Luca". Non si impara una frase che contiene una password o una chiave.

## La connessione
La finestra mostra e, in quest'ordine, esegue:
```powershell
$tcp = New-Object Net.Sockets.TcpClient; if ($tcp.ConnectAsync('192.168.1.50', 3389).Wait(3000)) { 'raggiungibile' } else { 'nessuna risposta'; exit 1 }
cmdkey /list:TERMSRV/192.168.1.50
mstsc /v:192.168.1.50
1..30 | ForEach-Object { if (Get-NetTCPConnection -OwningProcess 24760 -RemotePort 3389 -State Established -ErrorAction Ignore) { 'connessione aperta'; exit 0 }; Start-Sleep -Milliseconds 500 }; exit 1
```
Le righe compaiono appena arrivano. Intorno agli script c'è solo ciò che serve a far uscire testo leggibile (`ScriptRunner`).

- **Collegato**: il computer ha accettato il collegamento. Non vuol dire che l'accesso è riuscito: quello si vede nella
  finestra di Desktop remoto. La finestra si chiude da sola dopo 2,5 s, se il mouse non è sopra.
- **Non risponde**: *Riprova*, *Correggi* (l'indirizzo era sbagliato) o *Apri comunque* (se il controllo sbaglia, per
  esempio con certe VPN). `mstsc` non parte da solo verso un computer che non risponde.
- **Da vedere**: `mstsc` è aperto ma entro 15 s non c'è la connessione. Il motivo è nella sua finestra; non si impara nulla.
- Esc ferma lo script in corso. Non chiude Desktop remoto, se è già aperto.

## Le password
Non sono mai in un file né nel registro della finestra. Stanno nel Gestore credenziali di Windows, nella voce
`TERMSRV/indirizzo`, cifrata per l'utente: è il posto da cui `mstsc` le legge, lo stesso di *Ricorda le credenziali*. Si
scrivono con `CredWrite` e non con `cmdkey /pass:`, che lascerebbe la password nella riga di comando, visibile agli altri
programmi. Il registro mostra `password ••••••••`.

Senza password il computer si salva lo stesso, e la password si chiede al collegamento: se `cmdkey` non trova la voce, la
finestra mostra utente e password al posto del registro (Esc ferma). La password vale solo per quel collegamento: va nella
voce `TERMSRV/indirizzo` col commento *solo per un collegamento*, perché `mstsc` la legga, e si cancella quando il processo di
Desktop remoto si chiude (serve ancora per riconnettersi se la rete cade). Se la shell si chiude prima, la voce si cancella
al prossimo avvio. Con *Ricordala su questo PC* si salva invece come dal modulo. `--remote-demo` non la chiede. Cambiando indirizzo o utente senza riscriverla,
quella salvata segue il computer. *Rimuovi* cancella anche la voce, se nessun altro computer usa quell'indirizzo.

`%USERPROFILE%\.nothing-shell\brain\computers.json` contiene nome, indirizzo, porta, utente e frasi imparate. Un indirizzo
che non è un vero indirizzo (spazi, `/admin`, virgolette) si rifiuta anche se il file è stato modificato a mano: finisce nella
riga di comando di `mstsc`.

I computer si gestiscono anche da Impostazioni › Sviluppo › *Computer remoti*.

## Diagnostica
- `NothingShell.exe --snapshot <cartella>`: `notch-ask-*.png` e `remote-*.png`.
- `NothingShell.exe --ask-demo <file>`: apre il notch, chiede la tastiera e prova a scrivere nel campo. I tasti partono solo se
  il notch è davvero in primo piano, così non possono finire in un'altra finestra.
- `NothingShell.exe --remote-demo <indirizzo> <immagine>`: una connessione vera con `mstsc` vero e il registro in PNG. Non salva
  nulla.

## Limiti
- **Provato con `mstsc` vero ma non con un computer remoto vero**: su Windows Home non c'è il servizio Desktop remoto, quindi
  la prova usa una porta in ascolto sullo stesso PC. L'accesso con la password salvata è da provare su un PC reale.
- Il computer remoto deve avere Desktop remoto attivo, cosa che Windows Home non permette.
- Con Credential Guard attivo Windows non usa le password salvate per Desktop remoto: la chiede ogni volta.
- Il certificato di un computer sconosciuto lo chiede `mstsc`, come sempre: non si salta.
- Le frasi nel terminale e nel launcher non conoscono i computer remoti.
