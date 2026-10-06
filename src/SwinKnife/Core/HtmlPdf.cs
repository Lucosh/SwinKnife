using System.Net;
using System.Windows;
using System.Windows.Interop;
using Markdig;
using Microsoft.Web.WebView2.Core;

namespace SwinKnife.Core;

/// <summary>Conversione HTML/Markdown/testo in PDF con il motore di Edge (WebView2).</summary>
public static class HtmlPdf
{
    public static readonly string WebDataDir = Path.Combine(AppInfo.DataDir, "WebView2");

    private static CoreWebView2Environment? _env;

    public static async Task<CoreWebView2Environment> EnvironmentAsync()
    {
        _env ??= await CoreWebView2Environment.CreateAsync(null, WebDataDir);
        return _env;
    }

    public const string Css = """
        html { background: #ffffff; }
        body { font-family: "Segoe UI", Arial, sans-serif; font-size: 11pt; line-height: 1.5; color: #1f2328; max-width: 820px; margin: 0 auto; padding: 8px 16px; }
        h1, h2 { border-bottom: 1px solid #d0d7de; padding-bottom: .3em; }
        code, pre { font-family: "Cascadia Mono", Consolas, monospace; font-size: 9.5pt; background: #f6f8fa; border-radius: 4px; }
        code { padding: .15em .35em; }
        pre { padding: 12px; white-space: pre-wrap; word-wrap: break-word; }
        pre code { padding: 0; background: none; }
        table { border-collapse: collapse; } th, td { border: 1px solid #d0d7de; padding: 5px 10px; } th { background: #f6f8fa; }
        blockquote { color: #59636e; border-left: 4px solid #d0d7de; margin: 0; padding: 0 1em; }
        img { max-width: 100%; }
        """;

    public static string MarkdownToHtml(string markdown, string title = "")
    {
        var pipeline = new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();
        var body = Markdown.ToHtml(markdown, pipeline);
        return $"<!doctype html><html><head><meta charset='utf-8'><title>{WebUtility.HtmlEncode(title)}</title><style>{Css}</style></head><body>{body}</body></html>";
    }

    public static string TextToHtml(string text) =>
        $"<!doctype html><html><head><meta charset='utf-8'><style>{Css} pre {{ background: none; padding: 0; }}</style></head><body><pre>{WebUtility.HtmlEncode(text)}</pre></body></html>";

    public static string HtmlToText(string html)
    {
        var s = System.Text.RegularExpressions.Regex.Replace(html, "(?is)<(script|style).*?</\\1>", "");
        s = System.Text.RegularExpressions.Regex.Replace(s, "(?i)<br\\s*/?>|</p>|</div>|</h\\d>|</li>|</tr>", "\n");
        s = System.Text.RegularExpressions.Regex.Replace(s, "<[^>]+>", "");
        s = WebUtility.HtmlDecode(s);
        return System.Text.RegularExpressions.Regex.Replace(s, "\n{3,}", "\n\n").Trim() + "\n";
    }

    /// <summary>Salva l'HTML in un file temporaneo con &lt;base&gt; verso la cartella indicata (per immagini relative).</summary>
    public static string WriteTempHtml(string html, string? baseDir)
    {
        Directory.CreateDirectory(AppInfo.TempDir);
        var temp = Path.Combine(AppInfo.TempDir, $"page_{Guid.NewGuid():N}.html");
        if (baseDir != null)
        {
            var baseTag = $"<base href=\"{new Uri(Path.GetFullPath(baseDir) + Path.DirectorySeparatorChar).AbsoluteUri}\">";
            html = System.Text.RegularExpressions.Regex.IsMatch(html, "(?i)<head>")
                ? System.Text.RegularExpressions.Regex.Replace(html, "(?i)<head>", "<head>" + baseTag)
                : baseTag + html;
        }
        File.WriteAllText(temp, html, new System.Text.UTF8Encoding(false));
        return temp;
    }

    /// <summary>Stampa in PDF un file HTML (o una stringa HTML). Va chiamato sul thread dell'interfaccia.</summary>
    public static async Task ToPdfAsync(string? htmlFile, string? html, string dst, string? baseDir = null)
    {
        var env = await EnvironmentAsync();
        var parameters = new HwndSourceParameters("swk-print") { Width = 800, Height = 600, WindowStyle = unchecked((int)0x80000000) };
        using var host = new HwndSource(parameters);
        var controller = await env.CreateCoreWebView2ControllerAsync(host.Handle);
        try
        {
            controller.IsVisible = false;
            var core = controller.CoreWebView2;
            string url;
            string? temp = null;
            if (htmlFile != null)
            {
                url = new Uri(Path.GetFullPath(htmlFile)).AbsoluteUri;
            }
            else
            {
                temp = WriteTempHtml(html ?? "", baseDir);
                url = new Uri(temp).AbsoluteUri;
            }
            var tcs = new TaskCompletionSource<bool>();
            core.NavigationCompleted += (_, e) => tcs.TrySetResult(e.IsSuccess);
            core.Navigate(url);
            await tcs.Task;
            await Task.Delay(300); // lascia caricare immagini e font
            var settings = env.CreatePrintSettings();
            settings.ShouldPrintBackgrounds = true;
            settings.ShouldPrintHeaderAndFooter = false;
            settings.MarginTop = settings.MarginBottom = 0.6;
            settings.MarginLeft = settings.MarginRight = 0.6;
            if (!await core.PrintToPdfAsync(dst, settings)) throw new IOException(L.T("Stampa in PDF non riuscita."));
            if (temp != null) File.Delete(temp);
        }
        finally
        {
            controller.Close();
        }
    }

    /// <summary>Esegue la stampa sul thread dell'interfaccia da un thread qualsiasi.</summary>
    public static void ToPdf(string? htmlFile, string? html, string dst, string? baseDir = null)
    {
        var dispatcher = Application.Current?.Dispatcher ?? throw new InvalidOperationException(L.T("Interfaccia non disponibile."));
        if (dispatcher.CheckAccess()) throw new InvalidOperationException(L.T("Usare ToPdfAsync dal thread dell'interfaccia."));
        dispatcher.InvokeAsync(() => ToPdfAsync(htmlFile, html, dst, baseDir)).Task.Unwrap().GetAwaiter().GetResult();
    }
}
