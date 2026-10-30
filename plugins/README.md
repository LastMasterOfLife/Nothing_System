# Plugin d'esempio

Per installarne uno, copia la sua cartella in `%USERPROFILE%\.nothing-shell\plugins\` e premi
*Ricarica* nelle impostazioni (sezione Plugin). Il formato è descritto in
[docs/07-settings-plugins.md](../docs/07-settings-plugins.md).

| Plugin | Sorgente | Cosa mostra |
|---|---|---|
| `meteo` | http (Open-Meteo, senza chiave) | temperatura e umidità di Roma — cambia latitudine/longitudine nell'URL |
| `disco` | comando (`disco.ps1`) | spazio libero sul disco C:, rosso sotto il 10% |
| `git` | comando (`git.ps1`) | ramo, file modificati e commit da inviare/ricevere di un repository; rosso con commit da ricevere o conflitti — cambia il percorso negli `args` |
| `cestino` | comando (`cestino.ps1`) | elementi e spazio del cestino, rosso oltre 5 GB (secondo argomento) |

Gli script dei plugin vanno scritti in **solo ASCII** (o salvati in UTF-8 con BOM): PowerShell 5.1 legge i `.ps1` senza
BOM come ANSI e le lettere accentate rompono lo script.
