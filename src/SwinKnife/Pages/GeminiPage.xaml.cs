using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Clipboard = System.Windows.Clipboard;

namespace SwinKnife.Pages;

public partial class GeminiPage : UserControl, IToolPage
{
    private const string GeminiUrl = "https://gemini.google.com/app";
    private static readonly string[] InternalHosts = ["google.com", "gstatic.com", "googleusercontent.com", "youtube.com"];

    private readonly MainWindow _main;
    private readonly string? _chrome = ChromeHost.FindChrome();
    private readonly DispatcherTimer _poll = new() { Interval = TimeSpan.FromMilliseconds(300) };
    private readonly DispatcherTimer _watch = new() { Interval = TimeSpan.FromMilliseconds(1500) };
    private HashSet<IntPtr> _before = new();
    private int _tries;
    private ChromeHost? _host;
    private bool _webReady;

    public GeminiPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        ChromeIntroText.Text = _chrome != null
            ? L.T("Questa modalità usa Google Chrome con il tuo profilo: se in Chrome hai già fatto l'accesso a Google, Gemini si apre subito con il tuo account, senza inserire di nuovo la password.\n\n“Apri Gemini dentro SwinKnife” aggancia la finestra di Chrome qui sotto (funzione sperimentale: se qualcosa non va, usa la finestra separata).")
            : L.T("Google Chrome non è installato su questo PC: usa il pannello integrato.");
        EmbedBtn.IsEnabled = WindowBtn.IsEnabled = _chrome != null;
        _poll.Tick += (_, _) => FindChromeWindow();
        _watch.Tick += (_, _) =>
        {
            if (_host != null && !ChromeHost.IsWindow(_host.Child))
            {
                Release(false);
                ChromeStatus.Text = L.T("La finestra di Chrome è stata chiusa.");
            }
        };
        Loaded += async (_, _) => await InitWeb();
        if (Settings.Get("gemini.mode") == "chrome" && _chrome != null) ModeChrome.IsChecked = true;
    }

    // ------------------------------------------------------------------ pannello integrato (WebView2)
    private async Task InitWeb()
    {
        if (_webReady) return;
        _webReady = true;
        try
        {
            await Web.EnsureCoreWebView2Async(await HtmlPdf.EnvironmentAsync());
            var core = Web.CoreWebView2;
            core.Settings.IsStatusBarEnabled = false;
            core.NewWindowRequested += (_, e) =>
            {
                var host = new Uri(e.Uri).Host;
                if (host.Contains("accounts.google.com")) return; // finestra di accesso: lasciala aprire
                e.Handled = true;
                if (InternalHosts.Any(h => host == h || host.EndsWith("." + h))) core.Navigate(e.Uri);
                else Util.OpenUrl(e.Uri); // fonti e link esterni nel browser predefinito
            };
            core.PermissionRequested += (_, e) =>
            {
                if (new Uri(e.Uri).Host.EndsWith("google.com")) e.State = CoreWebView2PermissionState.Allow;
            };
            core.SourceChanged += (_, _) =>
            {
                var host = Web.Source?.Host ?? "";
                UrlText.Text = host;
                LoginHint.Visibility = host.Contains("accounts.google.com") ? Visibility.Visible : Visibility.Collapsed;
            };
            core.Navigate(GeminiUrl);
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "WebView2");
            Dlg.Error(L.T("Impossibile avviare il pannello web (Microsoft Edge WebView2).\n") + ex.Message);
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CanGoBack) Web.GoBack();
    }

    private void Forward_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CanGoForward) Web.GoForward();
    }

    private void Reload_Click(object sender, RoutedEventArgs e) => Web.Reload();
    private void Home_Click(object sender, RoutedEventArgs e) => Web.CoreWebView2?.Navigate(GeminiUrl);
    private void ZoomIn_Click(object sender, RoutedEventArgs e) => Web.ZoomFactor = Math.Min(2, Web.ZoomFactor + 0.1);
    private void ZoomOut_Click(object sender, RoutedEventArgs e) => Web.ZoomFactor = Math.Max(0.5, Web.ZoomFactor - 0.1);

    private async void Logout_Click(object sender, RoutedEventArgs e)
    {
        if (Web.CoreWebView2 == null) return;
        if (!Dlg.Confirm(L.T("Uscire dall'account Google nel pannello integrato?\nVerranno cancellati cookie e dati di navigazione di questo pannello."))) return;
        await Web.CoreWebView2.Profile.ClearBrowsingDataAsync();
        Web.CoreWebView2.Navigate(GeminiUrl);
    }

    // ------------------------------------------------------------------ domande sui file
    /// <summary>Domanda pronta: testo da usare con un documento e con un'immagine.</summary>
    public sealed record Prompt(string Label, string Text, string Image);

    /// <summary>Le domande sono nella lingua dell'interfaccia: Gemini risponde nella stessa lingua.</summary>
    public static Prompt[] Prompts
    {
        get
        {
            var list = new List<Prompt>
            {
                new(L.T("Riassumi"), L.T("Riassumi in italiano il contenuto qui sotto, con i punti principali in un elenco puntato."),
                    L.T("Riassumi in italiano il contenuto di questa immagine, con i punti principali in un elenco puntato.")),
                new(L.T("Spiega in modo semplice"), L.T("Spiegami in modo semplice, in italiano, di cosa parla il contenuto qui sotto."),
                    L.T("Spiegami in modo semplice, in italiano, cosa mostra questa immagine.")),
                new(L.T("Traduci in italiano"), L.T("Traduci in italiano il testo qui sotto, mantenendo la formattazione."),
                    L.T("Traduci in italiano il testo di questa immagine, mantenendo la formattazione.")),
            };
            if (L.Code != "en")
                list.Add(new(L.T("Traduci in inglese"), "Translate the text below into English, keeping the formatting.",
                    "Translate the text in this image into English, keeping the formatting."));
            list.Add(new(L.T("Correggi errori"), L.T("Correggi gli errori di grammatica e ortografia del testo qui sotto e mostrami la versione corretta."),
                L.T("Correggi gli errori di grammatica e ortografia del testo di questa immagine e mostrami la versione corretta.")));
            return list.ToArray();
        }
    }

    /// <summary>Prepara in Gemini una domanda sul file (testo estratto, oppure l'immagine stessa).</summary>
    public async Task AskAboutFile(string path, Prompt prompt)
    {
        var isImage = Formats.KindOf(path) is FileKind.Image or FileKind.Raw;
        MainWindow.Notify(L.T("Preparo il contenuto per Gemini…"), 0);
        string text;
        if (isImage && ModeChrome.IsChecked != true)
        {
            text = prompt.Image;
        }
        else
        {
            var body = await Task.Run(() => FileText.Extract(path));
            if (body.Length == 0)
            {
                Dlg.Info(L.T("Non sono riuscito a estrarre del testo da questo file."));
                return;
            }
            text = L.T($"{prompt.Text}\n\nFile: {Path.GetFileName(path)}\n---\n{body}");
        }
        if (ModeChrome.IsChecked == true)
        {
            Clipboard.SetText(text);
            MainWindow.Notify(L.T("Richiesta copiata negli appunti: incollala in Gemini (Ctrl+V)"), 12);
            return;
        }
        await SendToGemini(text, isImage ? path : null);
    }

    public async Task SendToGemini(string text, string? imagePath)
    {
        ModeWeb.IsChecked = true;
        await InitWeb();
        for (var i = 0; i < 100 && Web.CoreWebView2 == null; i++) await Task.Delay(100);
        var core = Web.CoreWebView2;
        if (core == null) return;
        if (!(Web.Source?.Host ?? "").Contains("gemini.google.com")) core.Navigate(GeminiUrl);
        const string find = "(document.querySelector('rich-textarea .ql-editor') || document.querySelector('div[contenteditable=true]'))";
        var ready = false;
        for (var i = 0; i < 60 && !ready; i++)
        {
            ready = await core.ExecuteScriptAsync($"!!{find}") == "true";
            if (!ready) await Task.Delay(250);
        }
        if (!ready)
        {
            Clipboard.SetText(text);
            MainWindow.Notify(L.T("Gemini non è pronto (serve l'accesso?): la richiesta è negli appunti, incollala con Ctrl+V"), 12);
            return;
        }
        if (imagePath != null)
        {
            try
            {
                using var img = ImageIO.Load(imagePath);
                if (img.Width > 3000 || img.Height > 3000) img.Resize(new ImageMagick.MagickGeometry(3000, 3000));
                Clipboard.SetImage(ImageIO.ToBitmapSource(img));
                await core.ExecuteScriptAsync($"{find}.focus()");
                foreach (var type in new[] { "keyDown", "keyUp" })
                    await core.CallDevToolsProtocolMethodAsync("Input.dispatchKeyEvent",
                        $"{{\"type\":\"{type}\",\"modifiers\":2,\"key\":\"v\",\"code\":\"KeyV\",\"windowsVirtualKeyCode\":86{(type == "keyDown" ? ",\"commands\":[\"paste\"]" : "")}}}");
                await Task.Delay(1500);
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, "Incolla immagine in Gemini");
            }
        }
        var json = System.Text.Json.JsonSerializer.Serialize(text);
        await core.ExecuteScriptAsync($"(function(t){{const e={find}; e.focus(); document.execCommand('insertText', false, t); }})({json})");
        Web.Focus();
        MainWindow.Notify(L.T("Richiesta pronta in Gemini: controllala e premi Invio"), 10);
    }

    // ------------------------------------------------------------------ modalità
    private void Mode_Checked(object sender, RoutedEventArgs e)
    {
        if (WebPanel == null) return;
        var chrome = ModeChrome.IsChecked == true;
        WebPanel.Visibility = chrome ? Visibility.Collapsed : Visibility.Visible;
        ChromePanel.Visibility = chrome ? Visibility.Visible : Visibility.Collapsed;
        WebNav.Visibility = LogoutBtn.Visibility = chrome ? Visibility.Collapsed : Visibility.Visible;
        if (chrome) LoginHint.Visibility = Visibility.Collapsed;
        Settings.Set("gemini.mode", chrome ? "chrome" : "web");
    }

    // ------------------------------------------------------------------ Chrome
    private void Launch()
    {
        _before = ChromeHost.ChromeWindows().Keys.ToHashSet();
        var psi = new ProcessStartInfo(_chrome!) { UseShellExecute = false };
        psi.ArgumentList.Add($"--app={GeminiUrl}");
        psi.ArgumentList.Add("--new-window");
        Process.Start(psi);
    }

    private void Embed_Click(object sender, RoutedEventArgs e)
    {
        if (_chrome == null) return;
        if (_host != null)
        {
            ChromeStatus.Text = L.T("Gemini è già aperto qui sotto.");
            return;
        }
        Launch();
        _tries = 0;
        EmbedBtn.IsEnabled = false;
        ChromeStatus.Text = L.T("Avvio di Chrome…");
        _poll.Start();
    }

    private void Window_Click(object sender, RoutedEventArgs e)
    {
        if (_chrome == null) return;
        Launch();
        ChromeStatus.Text = L.T("Gemini aperto in una finestra di Chrome.");
    }

    private void FindChromeWindow()
    {
        _tries++;
        var wins = ChromeHost.ChromeWindows();
        var fresh = wins.Keys.Where(h => !_before.Contains(h)).ToList();
        var pick = fresh.FirstOrDefault(h => wins[h].Contains("Gemini", StringComparison.OrdinalIgnoreCase));
        if (pick == IntPtr.Zero && fresh.Count > 0 && _tries > 8) pick = fresh[0];
        if (pick != IntPtr.Zero)
        {
            _poll.Stop();
            _host = new ChromeHost(pick);
            ChromeIntro.Visibility = Visibility.Collapsed;
            ChromeArea.Children.Add(_host);
            DetachBtn.Visibility = Visibility.Visible;
            EmbedBtn.IsEnabled = true;
            ChromeStatus.Text = L.T("Gemini con il tuo account Chrome.");
            _watch.Start();
        }
        else if (_tries > 60)
        {
            _poll.Stop();
            EmbedBtn.IsEnabled = true;
            ChromeStatus.Text = L.T("Non ho trovato la finestra di Chrome: è rimasta separata.");
        }
    }

    private void Release(bool close)
    {
        _watch.Stop();
        if (_host == null) return;
        if (!close) _host.Detach();
        ChromeArea.Children.Remove(_host);
        _host.Dispose();
        _host = null;
        ChromeIntro.Visibility = Visibility.Visible;
        DetachBtn.Visibility = Visibility.Collapsed;
    }

    private void Detach_Click(object sender, RoutedEventArgs e)
    {
        Release(false);
        ChromeStatus.Text = L.T("Finestra di Chrome staccata.");
    }

    public void Shutdown()
    {
        _poll.Stop();
        Release(true);
    }
}
