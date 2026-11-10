using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
using NothingShell.Core.Notch;
using NothingShell.Core.Storage;

namespace NothingShell.App.Notch;

/// <summary>
/// La mail in HTML nel lettore (docs/08-notch-mail-pages.md), con WebView2 in modalità composizione: il lettore è una
/// finestra trasparente, dove il WebView2 classico (una finestra figlia) non si vedrebbe. Script spenti, nessuna
/// navigazione dentro il lettore: ogni link (anche i pulsanti "Consenti"/"Rifiuta") si apre nel browser.
/// Una sola vista per lettore, riusata da una mail all'altra.
/// </summary>
internal sealed class MailHtmlView : Border, IDisposable
{
    private static Task<CoreWebView2Environment>? _environment;

    private readonly WebView2CompositionControl _web = new() { DefaultBackgroundColor = System.Drawing.Color.White };
    private Task? _ready;
    private string? _pending;
    private bool _allowNavigation;
    private double _zoom = 1;

    /// <summary>WebView2 non c'è o non parte: le mail si mostrano come testo per il resto della sessione.</summary>
    public static bool Unavailable { get; private set; }

    /// <summary>L'inizializzazione non è riuscita (vedi <see cref="Unavailable"/>).</summary>
    public event Action? Failed;

    public MailHtmlView()
    {
        // WebView2 in composizione disegna con Direct3D: col rendering software della shell resterebbe bianco.
        // Si accende l'hardware solo da qui (il driver GPU si carica alla prima mail in HTML) e si spegne in Dispose.
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.Default;
        Child = _web;
    }

    /// <summary>Mostra la pagina (<see cref="MailHtml.Document"/>) con l'ingrandimento dato (1 = 100%).</summary>
    public void Show(string document, double zoom)
    {
        _pending = document;
        _zoom = zoom;
        _ready ??= InitializeAsync();
        if (_ready.IsCompletedSuccessfully && _web.CoreWebView2 is not null)
            Navigate();
    }

    public void SetZoom(double zoom)
    {
        _zoom = zoom;
        if (_web.CoreWebView2 is not null)
            _web.ZoomFactor = zoom;
    }

    private async Task InitializeAsync()
    {
        try
        {
            _environment ??= CoreWebView2Environment.CreateAsync(null, Path.Combine(AtomicFile.DataFolder, "WebView2"));
            await _web.EnsureCoreWebView2Async(await _environment);
        }
        catch (Exception ex) when (ex is WebView2RuntimeNotFoundException or COMException or InvalidOperationException or IOException or UnauthorizedAccessException)
        {
            Unavailable = true;
            Failed?.Invoke();
            return;
        }

        var core = _web.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsWebMessageEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsZoomControlEnabled = false; // lo zoom lo fa il lettore (Ctrl+rotellina) e lo ricorda
        settings.IsStatusBarEnabled = true; // passando sopra un link si vede dove porta
        core.NewWindowRequested += (_, e) =>
        {
            e.Handled = true;
            Open(e.Uri);
        };
        core.NavigationStarting += (_, e) =>
        {
            if (_allowNavigation && e.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                _allowNavigation = false;
                return;
            }
            e.Cancel = true; // il lettore mostra solo la mail: i link vanno nel browser
            if (e.IsUserInitiated)
                Open(e.Uri);
        };
        if (_pending is not null)
            Navigate();
    }

    private void Navigate()
    {
        if (_pending is not { } document)
            return;
        _pending = null;
        _web.ZoomFactor = _zoom;
        _allowNavigation = true;
        _web.NavigateToString(document);
    }

    private static void Open(string uri)
    {
        if (MailHtml.Openable(uri) is not { } link)
            return;
        try
        {
            Process.Start(new ProcessStartInfo(link.AbsoluteUri) { UseShellExecute = true })?.Dispose();
        }
        catch (System.ComponentModel.Win32Exception)
        {
            // nessun browser o app di posta associati
        }
    }

    public void Dispose()
    {
        _web.Dispose();
        System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
    }
}
