using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using NothingShell.App.Rendering;
using NothingShell.App.Services;
using NothingShell.Core.Design;
using NothingShell.Core.DotMatrix;
using NothingShell.Core.Notch;

namespace NothingShell.App.Notch;

/// <summary>Contenuto delle schede del notch (docs/10-live-activities.md): screenshot e download completato.</summary>
internal static class NotchCards
{
    private const double ContentHeight = 84;

    /// <summary>Anteprima da trascinare dove serve (file e immagine), Apri e Cartella; la cattura è già negli appunti.</summary>
    public static UIElement Screenshot(ScreenshotCapture capture, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(capture);
        ArgumentNullException.ThrowIfNull(dismiss);
        var aspect = capture.Image.PixelHeight > 0 ? (double)capture.Image.PixelWidth / capture.Image.PixelHeight : 1;
        var thumbnail = new Border
        {
            Width = Math.Clamp(ContentHeight * aspect, 60, 150),
            Height = ContentHeight,
            CornerRadius = new CornerRadius(10),
            BorderBrush = Theme.OutlineBrush,
            BorderThickness = new Thickness(1),
            Background = new ImageBrush(capture.Image) { Stretch = Stretch.UniformToFill },
            Cursor = Cursors.Hand,
            ToolTip = "Trascina dove vuoi",
        };
        Point? pressed = null;
        thumbnail.MouseLeftButtonDown += (_, e) =>
        {
            pressed = e.GetPosition(thumbnail);
            e.Handled = true;
        };
        thumbnail.MouseLeftButtonUp += (_, _) => pressed = null;
        thumbnail.MouseMove += (_, e) =>
        {
            if (pressed is not { } start || e.LeftButton != MouseButtonState.Pressed)
                return;
            if ((e.GetPosition(thumbnail) - start).Length < 4)
                return;
            pressed = null;
            if (Save(capture) is not { } path)
                return;
            var data = new DataObject();
            data.SetFileDropList([path]);
            data.SetImage(capture.Image);
            DragDrop.DoDragDrop(thumbnail, data, DragDropEffects.Copy);
        };

        var actions = Actions(
            ("APRI", () =>
            {
                if (Save(capture) is { } path)
                    Open(path);
                dismiss();
            }),
            ("CARTELLA", () =>
            {
                if (Save(capture) is { } path)
                    ShowInFolder(path);
                dismiss();
            }));
        return Layout(thumbnail, "SCREENSHOT", "Negli appunti · trascina l'anteprima", actions);
    }

    public static UIElement Download(string path, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(path);
        ArgumentNullException.ThrowIfNull(dismiss);
        var icon = new Border
        {
            Width = 60,
            Height = ContentHeight,
            CornerRadius = new CornerRadius(10),
            Background = Theme.SurfaceBrush,
            Child = new DotMatrixView
            {
                Pitch = 4,
                Dots = DotGrid.From(DotIcons.Download),
                DotColor = Theme.Red,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            },
        };
        long size = 0;
        try
        {
            size = new FileInfo(path).Length;
        }
        catch (IOException)
        {
            // dimensione non nota
        }
        var detail = size > 0 ? $"{Path.GetFileName(path)} · {DownloadTracker.FormatBytes(size)}" : Path.GetFileName(path);
        var actions = Actions(
            ("APRI", () =>
            {
                Open(path);
                dismiss();
            }),
            ("CARTELLA", () =>
            {
                ShowInFolder(path);
                dismiss();
            }));
        return Layout(icon, "SCARICATO", detail, actions);
    }

    /// <summary>Chiamata in arrivo sul telefono (docs/13-telefono.md): si risponde da Collegamento al telefono.</summary>
    public static UIElement Call(PhoneEvent call, Action openPhoneLink, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(call);
        ArgumentNullException.ThrowIfNull(openPhoneLink);
        ArgumentNullException.ThrowIfNull(dismiss);
        var tile = Tile(new DotMatrixView
        {
            Pitch = 4,
            Dots = DotGrid.From(DotIcons.Phone),
            DotColor = Theme.Red,
            HorizontalAlignment = HorizontalAlignment.Center,
        });
        var actions = Actions(
            ("RISPONDI", () =>
            {
                openPhoneLink();
                dismiss();
            }),
            ("CHIUDI", dismiss));
        return Layout(tile, "CHIAMATA", [call.Title.Length > 0 ? call.Title : "Numero sconosciuto"], actions, Theme.Red);
    }

    /// <summary>
    /// Prossimo appuntamento (docs/11-agenda-pannello-rapido.md): dieci minuti prima "TRA 10 MIN", all'inizio
    /// "INIZIA ORA"; Partecipa apre la riunione online (Teams, Meet, Zoom…), Calendario l'app del calendario.
    /// </summary>
    public static UIElement Appointment(CalendarEvent appointment, ReminderKind kind, DateTimeOffset now,
        Action<CalendarEvent> open, Action openCalendar, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(appointment);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(openCalendar);
        ArgumentNullException.ThrowIfNull(dismiss);
        var tile = Tile(
            new DotMatrixView { Pitch = 3, Dots = DotGrid.From(DotIcons.Calendar), DotColor = Theme.Red, HorizontalAlignment = HorizontalAlignment.Center },
            new DotMatrixView
            {
                Pitch = 1.5,
                Dots = DotText.Render(appointment.Start.LocalDateTime.ToString("HH:mm", NotchUi.Italian)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
        var title = kind == ReminderKind.Starting ? "INIZIA ORA"
            : $"TRA {Math.Max(1, (int)Math.Ceiling((appointment.Start - now).TotalMinutes))} MIN";
        var buttons = new List<(string, Action)>();
        if (appointment.MeetingLink is not null)
            buttons.Add(("PARTECIPA", () =>
            {
                open(appointment);
                dismiss();
            }));
        buttons.Add(("CALENDARIO", () =>
        {
            openCalendar();
            dismiss();
        }));
        var lines = appointment.Location is { } where && appointment.MeetingLink is null
            ? new[] { appointment.Title, where }
            : [appointment.Title];
        return Layout(tile, title, lines, Actions([.. buttons]), Theme.Red);
    }

    /// <summary>Riepilogo del mattino: giorno a punti, appuntamenti di oggi e mail da leggere; Agenda e Posta aprono il notch.</summary>
    public static UIElement MorningBrief(Core.Notch.MorningBrief brief, DateTime now, Action showAgenda, Action? showMail, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(brief);
        ArgumentNullException.ThrowIfNull(showAgenda);
        ArgumentNullException.ThrowIfNull(dismiss);
        var tile = Tile(
            new DotMatrixView { Pitch = 4, Dots = DotText.Render(now.Day.ToString(NotchUi.Italian)), HorizontalAlignment = HorizontalAlignment.Center },
            new DotMatrixView
            {
                Pitch = 1.5,
                Dots = DotText.Render(now.ToString("MMM", NotchUi.Italian).ToUpperInvariant()),
                DotColor = Theme.Red,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
        var buttons = new List<(string, Action)>
        {
            ("AGENDA", () =>
            {
                dismiss();
                showAgenda();
            }),
        };
        if (showMail is not null)
            buttons.Add(("POSTA", () =>
            {
                dismiss();
                showMail();
            }));
        var lines = brief.MailLine is { } mail ? new[] { brief.AgendaLine, mail } : [brief.AgendaLine];
        return Layout(tile, brief.Greeting, lines, Actions([.. buttons]), Theme.White);
    }

    /// <summary>
    /// Comando finito (docs/12-sviluppo.md): FATTO o ERRORE con l'exit code, titolo, primo errore o durata;
    /// Errore apre il file alla riga, Log l'output, Terminale porta davanti la finestra da cui è partito.
    /// </summary>
    public static UIElement TaskFinished(Core.Dev.FinishedTask task, Action? openError, Action? openLog, Action? showTerminal, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(dismiss);
        var color = task.Succeeded ? Theme.White : Theme.Red;
        var tile = Tile(
            new DotMatrixView { Pitch = 3, Dots = DotGrid.From(DotIcons.Terminal), DotColor = color, HorizontalAlignment = HorizontalAlignment.Center },
            new DotMatrixView
            {
                Pitch = 1.5,
                Dots = DotText.Render(Core.Dev.TaskTracker.FormatElapsed(task.Duration)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 8, 0, 0),
            });
        var buttons = new List<(string, Action)>();
        void Add(string label, Action? action)
        {
            if (action is not null)
                buttons.Add((label, () =>
                {
                    action();
                    dismiss();
                }));
        }
        Add("ERRORE", openError);
        Add("LOG", openLog);
        Add("TERMINALE", showTerminal);
        var detail = task.Error is { } error
            ? $"{error.Label} · {error.Message}"
            : $"{(task.Succeeded ? "Finito" : "Fallito")} in {Core.Dev.TaskTracker.FormatDuration(task.Duration)}";
        var title = task.Succeeded ? "FATTO" : $"ERRORE {task.ExitCode}";
        return Layout(tile, title, [task.Title, detail], Actions([.. buttons]), color);
    }

    /// <summary>CI conclusa: CI OK o CI FALLITA con i workflow falliti; Apri la pagina dell'esecuzione, Rilancia i job falliti.</summary>
    public static UIElement CiFinished(Core.Dev.CiStatus status, Action open, Action? rerun, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(open);
        ArgumentNullException.ThrowIfNull(dismiss);
        var failed = status.State == Core.Dev.CiState.Failure;
        var tile = Tile(new DotMatrixView { Pitch = 4, Dots = DotText.Render("CI"), DotColor = failed ? Theme.Red : Theme.White, HorizontalAlignment = HorizontalAlignment.Center });
        var buttons = new List<(string, Action)>
        {
            ("APRI", () =>
            {
                open();
                dismiss();
            }),
        };
        if (rerun is not null)
            buttons.Add(("RILANCIA", () =>
            {
                rerun();
                dismiss();
            }));
        return Layout(tile, failed ? "CI FALLITA" : "CI OK", [$"{status.Repository} · {status.Branch}", Core.Dev.CiRuns.Summary(status.Runs)],
            Actions([.. buttons]), failed ? Theme.Red : Theme.White);
    }

    /// <summary>Segreto o token appena copiato: nome in rosso, scadenza del JWT, Cancella e Tieni (o Copia payload).</summary>
    public static UIElement Secret(string name, string? detail, Action clearNow, Action keep, Action? copyPayload, Action dismiss)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(clearNow);
        ArgumentNullException.ThrowIfNull(keep);
        ArgumentNullException.ThrowIfNull(dismiss);
        var tile = Tile(new DotMatrixView { Pitch = 4, Dots = DotGrid.From(DotIcons.Lock), DotColor = Theme.Red, HorizontalAlignment = HorizontalAlignment.Center });
        var buttons = new List<(string, Action)>
        {
            ("CANCELLA", () =>
            {
                clearNow();
                dismiss();
            }),
        };
        if (copyPayload is not null)
            buttons.Add(("COPIA PAYLOAD", () =>
            {
                copyPayload();
                dismiss();
            }));
        buttons.Add(("TIENI", () =>
        {
            keep();
            dismiss();
        }));
        var lines = detail is null
            ? new[] { "Si cancella dagli appunti tra 30 s" }
            : [detail, "Si cancella dagli appunti tra 30 s"];
        return Layout(tile, name, lines, Actions([.. buttons]), Theme.Red);
    }

    private static Border Tile(params UIElement[] content)
    {
        var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        foreach (var item in content)
            stack.Children.Add(item);
        return new Border
        {
            Width = 72,
            Height = ContentHeight,
            CornerRadius = new CornerRadius(10),
            Background = Theme.SurfaceBrush,
            Child = stack,
        };
    }

    private static Grid Layout(FrameworkElement preview, string title, string detail, UIElement actions) =>
        Layout(preview, title, [detail], actions, Theme.White, emphasizeFirst: false);

    /// <summary>
    /// Anteprima a sinistra; a destra titolo a punti, una o due righe di testo e i pulsanti. Con
    /// <paramref name="emphasizeFirst"/> la prima riga è bianca (l'appuntamento, l'agenda), le altre grigie.
    /// </summary>
    private static Grid Layout(FrameworkElement preview, string title, string[] lines, UIElement actions, Color titleColor, bool emphasizeFirst = true)
    {
        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(16, 0, 0, 0) };
        text.Children.Add(new DotMatrixView { Pitch = 2, Dots = DotText.Render(title), DotColor = titleColor, HorizontalAlignment = HorizontalAlignment.Left });
        for (var i = 0; i < lines.Length; i++)
        {
            var line = NotchUi.Text(12, i == 0 && emphasizeFirst ? Theme.WhiteBrush : Theme.GreyBrush, lines[i]);
            line.Margin = new Thickness(0, i == 0 ? (lines.Length > 1 ? 4 : 6) : 0, 0, i == lines.Length - 1 ? (lines.Length > 1 ? 5 : 8) : 0);
            text.Children.Add(line);
        }
        text.Children.Add(actions);

        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        Grid.SetColumn(text, 1);
        grid.Children.Add(preview);
        grid.Children.Add(text);
        return grid;
    }

    private static StackPanel Actions(params (string Text, Action Click)[] buttons)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var (label, click) in buttons)
        {
            var button = new Border
            {
                BorderBrush = Theme.GreyBrush,
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(13),
                Padding = new Thickness(12, 4, 12, 4),
                Margin = new Thickness(0, 0, 8, 0),
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                Child = NotchUi.Text(11, Theme.WhiteBrush, label),
            };
            button.MouseEnter += (_, _) => button.Background = Theme.SurfaceBrush;
            button.MouseLeave += (_, _) => button.Background = Brushes.Transparent;
            button.MouseLeftButtonUp += (_, e) =>
            {
                e.Handled = true;
                click();
            };
            row.Children.Add(button);
        }
        return row;
    }

    private static string? Save(ScreenshotCapture capture)
    {
        try
        {
            return capture.Save();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void Open(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception)
        {
            ShowInFolder(path); // nessuna app associata: almeno si vede dov'è
        }
    }

    private static void ShowInFolder(string path) =>
        Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true })?.Dispose();
}
