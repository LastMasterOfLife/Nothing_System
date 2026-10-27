# 01 — Architettura

## Stack
- .NET 9, C#; UI in WPF (`net9.0-windows10.0.19041.0` per l'accesso alle API WinRT).
- Nessuna dipendenza esterna a runtime.

## Progetti
```
src/NothingShell.Core     net9.0, puro, testabile: nessuna dipendenza da Windows/WPF
  Design/                 token (colori, metriche), DotFont (glifi 5x7)
  DotMatrix/              DotGrid (bitmap di punti), DotText (layout testo → griglia)
  Notch/                  NotchStateMachine (stati + transizioni), NotchLayout (dimensioni per stato)
  Animation/              Easing, Spring
src/NothingShell.App      WPF: finestre, rendering, servizi di sistema
  Rendering/              DotMatrixView (FrameworkElement che disegna una DotGrid)
  Notch/                  NotchWindow (finestra topmost, trasparente, no-activate)
  Services/               ClockService, MediaService (GSMTC)
  Interop/                Win32 (stili finestra, DPI)
tests/NothingShell.Core.Tests   xUnit sul Core
```

## Regole
1. **Il Core non conosce Windows.** Tutta la logica (layout, stati, animazioni) è pura e testata.
2. **L'App è sottile:** traduce eventi di sistema in input per il Core e disegna l'output.
3. **I servizi espongono snapshot immutabili** (`record`) tramite eventi; la UI non interroga mai le API di sistema direttamente.
4. **Fallimenti morbidi:** se un'API di sistema non è disponibile (es. nessuna sessione media) il componente degrada allo stato base, senza eccezioni visibili.

## Contratto servizi
```csharp
public sealed record MediaSnapshot(string Title, string Artist, bool IsPlaying, string SourceApp);
interface IMediaService { MediaSnapshot? Current { get; } event Action<MediaSnapshot?> Changed;
                          Task PlayPauseAsync(); Task NextAsync(); Task PreviousAsync(); }
```
