# 19 — Ore di lavoro per progetto e gruppo

La shell conta il tempo reale passato su ogni progetto, lo somma per gruppi di progetti e lo rende disponibile all'assistente
del notch ([docs/16](16-assistente-notch.md)): "quante ore ho lavorato a ottobre sul gruppo Cliente A?".

Si accende e spegne in Impostazioni › Sviluppo › *Ore di lavoro* (`LiveActivity.WorkHours`); spenta, il contatore è fermo e la
pagina Ore sparisce dallo swipe.

## Come si conta
Ogni 5 secondi (`WorkClock`) si guarda la finestra in primo piano. Se il suo **titolo contiene una parola chiave** di un progetto
(senza badare alle maiuscole), il tempo va a quel progetto; con più progetti vince la parola chiave più lunga, così
`sinapsi_web` batte `sinapsi`. VS Code, i terminali e i browser mettono nome della cartella o del sito nel titolo.

- **Solo tempo vero**: se mouse e tastiera sono fermi da 5 minuti non si conta. Un buco fra due campioni oltre 30 s (PC in
  sospensione, shell chiusa) non si conta mai: meglio perdere un minuto che inventarlo.
- Le finestre di Nothing Shell non contano: si scrive qui, non si lavora al progetto.
- A mezzanotte l'intervallo si divide fra i due giorni.
- **Privacy**: dei titoli non resta niente. Si salvano solo nome del progetto, giorno e secondi.

## Nelle impostazioni
Impostazioni › *Ore* (dopo Sviluppo): le **ore calcolate** per Oggi, Settimana, Mese, Mese scorso o Anno, con il totale, il progetto
in corso e una riga per ogni gruppo e progetto; i **gruppi**, dove si spuntano i progetti che ne fanno parte (un interruttore per
progetto) e si creano o eliminano; i **progetti** con le loro parole chiave, da creare (lo stesso nome ne sostituisce le parole
chiave) o eliminare. Sono le stesse cose dei comandi del notch, e le modifiche valgono da subito in entrambi i posti.

## La pagina Ore
A vista: gruppi e progetti con le ore di oggi e del mese. In alto a destra il progetto in corso con le ore di oggi che scorrono
dal vivo (`● sinapsi_web 2:14:07`). Nel campo:

| Scrivi | Fa |
|---|---|
| `progetto sinapsi_web = sinapsi, elfin` | crea il progetto con quelle parole chiave (senza `=`, il nome stesso) |
| `gruppo Cliente A = sinapsi_web, printer` | crea il gruppo; i progetti che mancano si creano |
| `aggiungi printer a Cliente A` / `togli printer da Cliente A` | cambia i membri |
| `togli sinapsi_web` / `togli Cliente A` | toglie un progetto (le ore contate restano nel registro) o un gruppo |
| `ore ottobre gruppo Cliente A` | le ore di un periodo, di un gruppo o di un progetto (`ore settimana progetto printer`) |

Periodi: `oggi`, `ieri`, `settimana`, `mese`, `mese scorso`, `anno`, un mese per nome (`ottobre`, `febbraio 2025`: senza anno è
il più recente non nel futuro) o `2026-10`. Invio a campo vuoto torna alla vista di oggi. Un progetto può stare in più gruppi;
le ore di un gruppo sono la somma dei suoi membri, senza doppi conteggi perché ogni campione va a un solo progetto.

## Calendario e dettaglio
Impostazioni › Ore › *Calendario* mostra il mese con i **giorni colorati dove ci sono ore** (più ore, colore più pieno; dentro la
cella le ore del giorno). Le frecce ‹ › cambiano mese; un clic su un giorno ne mostra il **dettaglio**: il totale e una riga per
gruppo e per progetto. Il giorno di oggi ha il numero in grassetto.

Nel notch, nella pagina Ore, ogni gruppo mostra `totale <ore del mese>`. **Un clic sul nome del gruppo** apre un widget con lo
stesso calendario e dettaglio, ma solo di quel gruppo (i suoi progetti e le ore messe direttamente sul gruppo): si sposta
trascinando il titolo, si chiude con × o Esc, si aggiorna da solo, e se è già aperto si porta davanti (uno per gruppo).
Codice: Core `WorkCalendar`, controllo `HoursCalendarView` (lo stesso nelle due posizioni) e `HoursDetailWindow`.

## Ore dette a parole
In una frase sola, nel campo della Home, nella pagina Ore o all'assistente:

> aggiungi 7h a plainest e 1h al gruppo wpweb che ho fatto ieri e aggiungi 4h su plainest perché ho fatto questa mattina

Non passa dal modello (un modello piccolo sbaglierebbe giorni e somme, e qui si scrivono dati): `WorkPhrase` la capisce da sola.
- La frase comincia con un verbo (`aggiungi`, `metti`, `segna`, `registra`, `inserisci`; `togli`, `rimuovi`, `sottrai` per togliere;
  anche `ho lavorato` e `ho fatto`) e si divide a ogni verbo e a ogni punto.
- Ogni **durata** (`7h`, `2h30`, `2h 30m`, `1,5h`, `90m`, `2 ore e 30 minuti`, `2:30`; al massimo 24 ore) ha il suo **progetto o
  gruppo** (`a plainest`, `su plainest`, `al gruppo wpweb`, `sul progetto x`). Le ore date a un gruppo restano sul gruppo, senza
  finire in un suo progetto; il totale del gruppo le somma a quelle dei suoi progetti.
- Il **giorno** detto in una frase vale per tutte le sue voci: `ieri`, `avantieri`, `oggi`, `questa mattina`, `14/10`,
  `2026-10-14`, `lunedì` (l'ultimo passato). Senza, è oggi; i giorni futuri si rifiutano.
- **Tutto o niente**: se una voce non si capisce (progetto che non c'è, durata impossibile) non si scrive nulla e si dice quale.
- Dopo la registrazione il notch mostra ogni voce (`+7 h 00 min ieri`) e il **totale di ogni gruppo** nel mese, più quello dei
  progetti toccati. `togli 30m da plainest` corregge un errore: toglie al massimo le ore di quel giorno, mai sotto zero.

Le ore a mano finiscono nello stesso registro: contano nei gruppi, nei periodi e nei file dell'assistente come quelle del PC.

## L'assistente
Un modello piccolo sbaglia le somme, quindi non le fa: `WorkStore` scrive per ogni mese `~/.nothing-shell/work/ore-AAAA-MM.txt`,
già sommato, e l'assistente lo legge con `Get-Content` (comando di sola lettura che parte da solo):

```
Ore lavorate ottobre 2026 (tempo reale davanti al PC, senza pause)
TOTALE: 85 h 30 min (85,50 ore)
GRUPPO Cliente A: 40 h 15 min (40,25 ore) (progetti: sinapsi_web 30 h 00 min; printer 10 h 15 min)
PROGETTO sinapsi_web: 30 h 00 min (30,00 ore)
```
Il prompt dell'assistente indica la cartella e il formato del nome. Per più mesi legge più file. I file si riscrivono ogni
minuto se le ore cambiano e a ogni modifica di progetti e gruppi.

## File
`~/.nothing-shell/work/`: `projects.json` (progetti e gruppi, modificabile a mano), `time.json` (secondi per giorno e progetto),
`ore-AAAA-MM.txt` (per l'assistente). Un file illeggibile fa ripartire da vuoto, senza bloccare la shell.

## Diagnostica
`NothingShell.exe --snapshot <cartella>`: `notch-ore.png` e `notch-ore-gruppo.png`, con ore di prova in una cartella temporanea.
Codice: Core `Work/` (testato in `WorkHoursTests`), `WorkClock` e `HoursPage` nell'App.
