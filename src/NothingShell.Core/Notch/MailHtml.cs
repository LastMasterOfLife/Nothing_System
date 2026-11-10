using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace NothingShell.Core.Notch;

/// <summary>Un pezzo del testo di una mail: testo semplice o un link da aprire.</summary>
public readonly record struct MailTextRun(string Text, Uri? Link);

/// <summary>
/// HTML delle mail nel lettore (docs/08-notch-mail-pages.md): la mail si vede com'è, pulsanti compresi, ma senza
/// script, moduli né riquadri, e senza scaricare nulla da internet finché non si chiede di mostrare le immagini (i
/// pixel di tracciamento dicono al mittente che la mail è stata aperta). I link si aprono nel browser.
/// </summary>
public static partial class MailHtml
{
    /// <summary>Oltre questa lunghezza la mail si mostra come testo (WebView2 accetta al massimo 2 MB di HTML).</summary>
    public const int MaxLength = 1_500_000;

    /// <summary>Solo questi link si aprono, nel browser o nell'app di posta.</summary>
    public static Uri? Openable(string? uri) =>
        Uri.TryCreate(uri?.Trim(), UriKind.Absolute, out var parsed) && parsed.Scheme is "http" or "https" or "mailto" ? parsed : null;

    /// <summary>Immagini, sfondi o caratteri presi da internet (non incorporati nella mail).</summary>
    public static bool HasRemoteContent(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        return RemoteReference().IsMatch(html);
    }

    /// <summary>
    /// Pagina da mostrare: la mail senza script, moduli, riquadri e redirect, con una Content-Security-Policy che
    /// lascia passare solo stili e immagini incorporate (e quelle remote con <paramref name="remoteContent"/>).
    /// I link si aprono in una nuova finestra, cioè nel browser.
    /// </summary>
    public static string Document(string html, bool remoteContent)
    {
        ArgumentNullException.ThrowIfNull(html);
        var clean = DangerousBlocks().Replace(html, string.Empty);
        clean = DangerousTags().Replace(clean, string.Empty);
        clean = EventHandlers().Replace(clean, string.Empty);
        clean = ScriptLinks().Replace(clean, "${attr}=\"#\"");
        var remote = remoteContent ? " https: http:" : string.Empty;
        var csp = $"default-src 'none'; style-src 'unsafe-inline'{remote}; img-src data:{remote}; font-src data:{remote}; " +
                  "script-src 'none'; form-action 'none'; frame-src 'none'; base-uri 'none'";
        return "<!DOCTYPE html><meta charset=\"utf-8\">" +
               $"<meta http-equiv=\"Content-Security-Policy\" content=\"{csp}\">" +
               "<base target=\"_blank\">" +
               "<style>html{background:#fff}body{margin:0;padding:16px;color:#111;font-family:'Segoe UI',sans-serif;" +
               "font-size:14px;line-height:1.45;overflow-wrap:anywhere}img{max-width:100%}a{color:#0b57d0}</style>" +
               clean;
    }

    /// <summary>
    /// Le immagini incorporate (<c>cid:</c>, a cui l'HTML rimanda) sostituite dai loro dati, così si vedono senza
    /// internet. <paramref name="images"/>: Content-ID (senza parentesi angolari) → URI <c>data:</c>.
    /// </summary>
    public static string InlineImages(string html, IReadOnlyDictionary<string, string> images)
    {
        ArgumentNullException.ThrowIfNull(html);
        ArgumentNullException.ThrowIfNull(images);
        if (images.Count == 0)
            return html;
        var byId = new Dictionary<string, string>(images, StringComparer.OrdinalIgnoreCase);
        return CidReference().Replace(html, m =>
        {
            var id = WebUtility.UrlDecode(m.Groups["id"].Value).Trim('<', '>');
            return byId.TryGetValue(id, out var data) ? data : m.Value;
        });
    }

    /// <summary>
    /// Testo semplice diviso in pezzi: gli indirizzi web (<c>https://…</c>, <c>www.…</c>) e di posta diventano link.
    /// La punteggiatura in fondo (punto, virgola, parentesi chiusa senza aperta) resta fuori dal link.
    /// </summary>
    public static IReadOnlyList<MailTextRun> Linkify(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        var runs = new List<MailTextRun>();
        var plain = new StringBuilder();
        var at = 0;
        foreach (Match match in Links().Matches(text))
        {
            var value = TrimTrailing(match.Value);
            var link = match.Groups["mail"].Success
                ? Openable($"mailto:{value}")
                : Openable(value.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? $"https://{value}" : value);
            if (link is null)
                continue;
            plain.Append(text, at, match.Index - at);
            if (plain.Length > 0)
                runs.Add(new MailTextRun(plain.ToString(), null));
            plain.Clear();
            runs.Add(new MailTextRun(value, link));
            at = match.Index + value.Length;
        }
        plain.Append(text, at, text.Length - at);
        if (plain.Length > 0)
            runs.Add(new MailTextRun(plain.ToString(), null));
        return runs;
    }

    private static string TrimTrailing(string url)
    {
        while (url.Length > 0)
        {
            var last = url[^1];
            if (".,;:!?'\"".Contains(last, StringComparison.Ordinal)
                || (last == ')' && url.Count(c => c == '(') < url.Count(c => c == ')'))
                || (last == ']' && !url.Contains('[', StringComparison.Ordinal)))
                url = url[..^1];
            else
                break;
        }
        return url;
    }

    [GeneratedRegex(@"<(script|iframe|object|embed|noscript|template)\b[^>]*>.*?</\1\s*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex DangerousBlocks();

    /// <summary>Tag rimasti soli (senza chiusura) e quelli che portano altrove: redirect, base, moduli.</summary>
    [GeneratedRegex(@"</?(script|iframe|object|embed|frame|frameset|applet|base|form|link)\b[^>]*>|<meta\b[^>]*http-equiv[^>]*>", RegexOptions.IgnoreCase)]
    private static partial Regex DangerousTags();

    [GeneratedRegex(@"\s+on[a-z]+\s*=\s*(""[^""]*""|'[^']*'|[^\s>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex EventHandlers();

    [GeneratedRegex(@"(?<attr>href|src|action)\s*=\s*(?<q>[""']?)\s*(?:javascript|vbscript|data:text/html)[^""'>]*\k<q>", RegexOptions.IgnoreCase)]
    private static partial Regex ScriptLinks();

    [GeneratedRegex(@"(src|background|srcset)\s*=\s*[""']?\s*(https?:)?//|url\(\s*[""']?\s*(https?:)?//|@import", RegexOptions.IgnoreCase)]
    private static partial Regex RemoteReference();

    [GeneratedRegex(@"cid:(?<id>[^""'\s)>]+)", RegexOptions.IgnoreCase)]
    private static partial Regex CidReference();

    [GeneratedRegex(@"(?<web>\b(?:https?://|www\.)[^\s<>""]+)|(?<mail>\b[\w.+-]+@[\w-]+(?:\.[\w-]+)+)", RegexOptions.IgnoreCase)]
    private static partial Regex Links();
}
