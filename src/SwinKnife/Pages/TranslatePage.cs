using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using ListBox = System.Windows.Controls.ListBox;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Traduce documenti interi (Word, PowerPoint, Excel, PDF, testi, sottotitoli) con Gemini, mantenendo la formattazione.</summary>
public sealed class TranslatePage : UserControl, IToolPage
{
    private static readonly string[] LanguageCodes =
        ["en", "it", "de", "fr", "es", "pt", "pt-BR", "nl", "pl", "ro", "cs", "hu", "el", "sv", "da", "nb", "fi", "ru", "uk", "tr", "ar", "he", "hi", "zh-Hans", "zh-Hant", "ja", "ko"];

    private readonly ComboBox _lang = new() { Width = 240 };
    private readonly TextBox _context = new() { PlaceholderText = L.T("Facoltativo: di cosa parla il documento (es. contratto di affitto, manuale tecnico) o il tono da usare"), Margin = new Thickness(0, 10, 0, 0) };
    private readonly ListBox _files = new() { MinHeight = 120, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly PasswordBox _key = new() { PlaceholderText = L.T("Incolla qui la chiave API"), Width = 380 };
    private readonly TextBlock _keyStatus = Ui.Hint();
    private readonly TextBlock _status = Ui.Hint();
    private readonly ProgressBar _progress = new() { Maximum = 1, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 8, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _go, _stop;
    private readonly Border _keyCard;
    private Wpf.Ui.Controls.Button? _changeKey;
    private readonly List<string> _paths = new();
    private CancellationTokenSource? _cts;

    public TranslatePage(MainWindow main)
    {
        foreach (var code in LanguageCodes)
        {
            var c = CultureInfo.GetCultureInfo(code);
            var native = c.NativeName.Length > 0 ? char.ToUpper(c.NativeName[0], c) + c.NativeName[1..] : code;
            _lang.Items.Add(new ComboBoxItem { Content = native, Tag = code });
        }
        var saved = Settings.Get("translate.lang") ?? (L.Code == "en" ? "it" : "en");
        _lang.SelectedIndex = Math.Max(0, Array.IndexOf(LanguageCodes, saved));

        // ---- chiave API
        var link = Ui.Btn(L.T("Crea una chiave gratuita"), SymbolRegular.Open24, (_, _) => Util.OpenUrl(GeminiApi.KeyPage));
        var save = Ui.Btn(L.T("Salva e prova"), SymbolRegular.Checkmark24, async (_, _) => await SaveKey(), primary: true);
        var steps = new TextBlock
        {
            Text = L.T("La traduzione usa Gemini di Google tramite la sua API ufficiale. Serve una chiave gratuita (un minuto, con il tuo account Google):\n1. clicca \"Crea una chiave gratuita\" e poi \"Create API key\";\n2. copia la chiave e incollala qui sotto.\nLa chiave resta sul PC, cifrata con il tuo account di Windows."),
            TextWrapping = TextWrapping.Wrap, LineHeight = 21, Margin = new Thickness(0, 0, 0, 10), Foreground = Ui.Res("TextFillColorSecondaryBrush"),
        };
        _keyCard = Ui.Card(L.T("Chiave di Gemini"), null, steps, Ui.Row(link, _key, new Border { Width = 8 }, save), _keyStatus);
        var remove = _changeKey = Ui.Btn(L.T("Cambia chiave"), SymbolRegular.Key24, (_, _) =>
        {
            GeminiApi.Key = null;
            UpdateKey();
        });

        // ---- documenti
        _go = Ui.Btn(L.T("Traduci"), SymbolRegular.Translate24, async (_, _) => await Run(), primary: true);
        _stop = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stop.Visibility = Visibility.Collapsed;
        var add = Ui.Btn(L.T("Aggiungi documenti…"), SymbolRegular.DocumentAdd24, (_, _) =>
            AddFiles(Dlg.OpenFiles(L.T("Documenti da tradurre"), L.T("Documenti|*.docx;*.pptx;*.xlsx;*.pdf;*.txt;*.md;*.srt|") + Dlg.AllFiles, "translate")));
        var clear = Ui.Btn(L.T("Svuota"), SymbolRegular.Delete24, (_, _) => { _paths.Clear(); Fill(); });
        var docsCard = Ui.Card(L.T("Documenti"), L.T("Word (.docx), PowerPoint (.pptx), Excel (.xlsx), PDF (con Word installato), testo, Markdown e sottotitoli (.srt). Il file tradotto viene salvato accanto all'originale, con il codice della lingua nel nome."),
            Ui.Row(add, clear),
            new Border { BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6), Margin = new Thickness(0, 10, 0, 10), Child = _files },
            Ui.Row(Ui.Label(L.T("Traduci in")), new Border { Width = 8 }, _lang, new Border { Width = 16 }, _go, _stop, remove),
            _context);

        var note = Ui.Hint(L.T("Il testo dei documenti viene inviato a Google per la traduzione: non usare questa funzione per documenti riservati. Con la chiave gratuita Google può usare i dati per migliorare i suoi servizi."));
        note.TextWrapping = TextWrapping.Wrap;
        var bottom = new StackPanel();
        bottom.Children.Add(_progress);
        bottom.Children.Add(_status);
        Content = Ui.ScrollPage(Ui.Header(L.T("Traduci documenti"), L.T("Traduci documenti interi in un'altra lingua mantenendo impaginazione, stili e immagini.")),
            _keyCard, docsCard, bottom, note);
        UpdateKey();
        Fill();
    }

    public bool Accepts(string path) => DocTranslator.IsSupported(path);
    public void OpenFile(string path) => AddFiles([path]);
    public void AddFiles(IReadOnlyList<string> paths) => AddFiles(paths.ToArray());

    private void AddFiles(string[] paths)
    {
        foreach (var p in paths.Where(DocTranslator.IsSupported))
            if (!_paths.Contains(p, StringComparer.OrdinalIgnoreCase)) _paths.Add(p);
        Fill();
    }

    private void Fill()
    {
        _files.Items.Clear();
        foreach (var p in _paths)
            _files.Items.Add(new ListBoxItem { Tag = p, Content = Ui.Row(new Image { Source = ShellIcons.For(p), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, Ui.Label(Path.GetFileName(p))) });
        if (_paths.Count == 0) _files.Items.Add(new ListBoxItem { Content = Ui.Hint(L.T("Trascina qui i documenti.")), IsEnabled = false });
        _go.IsEnabled = _paths.Count > 0 && GeminiApi.HasKey;
    }

    private void UpdateKey()
    {
        _keyCard.Visibility = GeminiApi.HasKey ? Visibility.Collapsed : Visibility.Visible;
        if (_changeKey != null) _changeKey.Visibility = GeminiApi.HasKey ? Visibility.Visible : Visibility.Collapsed;
        _go.IsEnabled = _paths.Count > 0 && GeminiApi.HasKey;
    }

    private async Task SaveKey()
    {
        var key = _key.Password.Trim();
        if (key.Length < 20)
        {
            _keyStatus.Text = L.T("La chiave sembra incompleta.");
            return;
        }
        GeminiApi.Key = key;
        _keyStatus.Text = L.T("Verifica della chiave…");
        try
        {
            await GeminiApi.TestAsync(CancellationToken.None);
            _keyStatus.Text = "";
            _key.Password = "";
            MainWindow.Notify(L.T("Chiave di Gemini salvata"));
            UpdateKey();
        }
        catch (Exception ex)
        {
            GeminiApi.Key = null;
            _keyStatus.Text = L.T("Chiave non accettata: ") + ex.Message;
        }
    }

    private async Task Run()
    {
        if (_lang.SelectedItem is not ComboBoxItem { Tag: string code }) return;
        Settings.Set("translate.lang", code);
        var language = CultureInfo.GetCultureInfo(code).EnglishName;
        var context = _context.Text.Trim();
        _cts = new CancellationTokenSource();
        _progress.Visibility = _stop.Visibility = Visibility.Visible;
        _go.IsEnabled = false;
        var outputs = new List<string>();
        var errors = new List<string>();
        var files = _paths.ToList();
        try
        {
            for (var i = 0; i < files.Count; i++)
            {
                var f = files[i];
                var n = i;
                try
                {
                    outputs.AddRange(await Task.Run(() => DocTranslator.TranslateFile(f, code.ToLowerInvariant(), (texts, ct) => GeminiApi.TranslateAsync(texts, language, context, ct),
                        (p, msg) => Dispatcher.InvokeAsync(() =>
                        {
                            _progress.Value = (n + p) / files.Count;
                            _status.Text = $"{Path.GetFileName(f)} · {msg}";
                        }), _cts.Token)));
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex)
                {
                    AppInfo.Log(ex, "Traduzione " + f);
                    errors.Add($"{Path.GetFileName(f)}: {ex.Message}");
                    if (ex is UnauthorizedAccessException)
                    {
                        GeminiApi.Key = null;
                        UpdateKey();
                        break;
                    }
                }
            }
            _status.Text = L.T($"{outputs.Count} file creati.");
            if (errors.Count > 0) Dlg.Error(L.T("Alcuni documenti non sono stati tradotti:\n\n") + string.Join("\n", errors));
            if (outputs.Count > 0 && Dlg.Confirm(L.T($"Traduzione completata: {string.Join(", ", outputs.Select(Path.GetFileName))}"), AppInfo.Name, L.T("Apri"), L.T("Chiudi")))
                Util.OpenExternal(outputs[0]);
        }
        catch (OperationCanceledException)
        {
            _status.Text = L.T("Traduzione interrotta.");
        }
        finally
        {
            _progress.Visibility = _stop.Visibility = Visibility.Collapsed;
            _go.IsEnabled = _paths.Count > 0 && GeminiApi.HasKey;
        }
    }
}
