using System.Security.Cryptography;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Xps.Packaging;
using ICSharpCode.AvalonEdit;
using Microsoft.Web.WebView2.Wpf;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using SwinKnife.Pdf;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

public partial class ViewerPage : UserControl, IToolPage
{
    private const long MaxText = 20 * 1024 * 1024;

    private readonly MainWindow _main;
    private string? _path;
    private int _generation;

    // viste create su richiesta
    private ImageView? _imageView;
    private PdfView? _pdfView;
    private DockPanel? _textPane;
    private TextEditor? _editor;
    private WebView2? _preview;
    private ToggleSwitch? _previewToggle;
    private Wpf.Ui.Controls.Button? _saveBtn;
    private TextBlock? _encodingLabel;
    private Encoding _encoding = new UTF8Encoding(false);
    private FileKind _textKind;
    private TableView? _tableView;
    private MediaView? _mediaView;
    private ArchiveView? _archiveView;
    private FontView? _fontView;
    private DockPanel? _hexPane;
    private TextEditor? _hex;
    private TextBlock? _hexNote;
    private long _hexOffset;
    private WebView2? _web;
    private DocumentViewer? _xps;
    private XpsDocument? _xpsDoc;
    private readonly FrameworkElement _empty;
    private readonly FrameworkElement _busyView;
    private readonly TextBlock _busyText;
    private readonly FrameworkElement _message;
    private readonly TextBlock _messageText;

    public ViewerPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        _empty = Ui.Placeholder(SymbolRegular.DocumentSearch24,
            L.T("Trascina qui un file oppure premi “Apri…”\n\nImmagini (anche HEIC, AVIF e RAW) · PDF e XPS · Word, Excel, PowerPoint · video e audio · ZIP, 7z, RAR, TAR · testo e codice · font · qualsiasi altro file in esadecimale"), out _);
        _busyView = Ui.Placeholder(SymbolRegular.HourglassHalf24, "", out _busyText);
        _message = Ui.Placeholder(SymbolRegular.Info24, "", out _messageText);
        ViewHost.Content = _empty;
    }

    // ------------------------------------------------------------------ IToolPage
    public bool Accepts(string path) => true;

    public bool CanClose()
    {
        if (ViewHost.Content == _textPane && _editor is { IsModified: true })
            return Dlg.Confirm(L.T("Il file di testo ha modifiche non salvate. Chiudere comunque?"));
        return true;
    }

    public void Shutdown()
    {
        _mediaView?.Stop();
        _pdfView?.Clear();
        _xpsDoc?.Close();
    }

    public void OpenFile(string path) => _ = OpenAsync(path);

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var p = Dlg.OpenFile(L.T("Apri un file"), key: "viewer");
        if (p != null) OpenFile(p);
    }

    // ------------------------------------------------------------------ apertura
    private void Reset()
    {
        _generation++;
        _imageView?.Clear();
        _mediaView?.Stop();
        _pdfView?.Clear();
        if (_xpsDoc != null)
        {
            _xps!.Document = null;
            _xpsDoc.Close();
            _xpsDoc = null;
        }
        Busy.Visibility = Visibility.Collapsed;
        HashText.Visibility = Visibility.Collapsed;
    }

    private void ShowBusy(string text)
    {
        _busyText.Text = text;
        ViewHost.Content = _busyView;
        Busy.Visibility = Visibility.Visible;
    }

    private void Show(FrameworkElement view)
    {
        Busy.Visibility = Visibility.Collapsed;
        ViewHost.Content = view;
    }

    public async Task OpenAsync(string path)
    {
        path = Path.GetFullPath(path);
        if (Directory.Exists(path))
        {
            _main.OpenFile(path, "disk");
            return;
        }
        if (!File.Exists(path))
        {
            Dlg.Error(L.T($"Il file non esiste:\n{path}"));
            return;
        }
        if (!CanClose()) return;
        Reset();
        var gen = _generation;
        _path = path;
        PathText.Text = path;
        PathText.ToolTip = path;
        var kind = Formats.KindOf(path);
        SetBasicInfo(path, kind);
        EditBtn.IsEnabled = kind is FileKind.Image or FileKind.Raw or FileKind.Pdf;
        ConvertBtn.IsEnabled = Converter.TargetsFor(path).Count > 0;
        ExternalBtn.IsEnabled = RevealBtn.IsEnabled = HashBtn.IsEnabled = GeminiBtn.IsEnabled = true;
        try
        {
            await Dispatch(path, kind, gen);
        }
        catch (Exception ex) when (gen == _generation)
        {
            AppInfo.Log(ex, $"Apertura {path}");
            ShowHex(path, L.T($"Impossibile mostrare l'anteprima: {ex.Message}\nEcco il contenuto grezzo del file. Puoi anche aprirlo con l'app predefinita."));
        }
    }

    private async Task Dispatch(string path, FileKind kind, int gen)
    {
        switch (kind)
        {
            case FileKind.Image or FileKind.Raw:
            {
                ShowBusy(L.T("Caricamento immagine…"));
                _imageView ??= new ImageView();
                if (Formats.Ext(path) is ".gif" or ".webp")
                {
                    var frames = await Task.Run(() => ImageIO.LoadAnimation(path));
                    if (gen != _generation) return;
                    if (frames != null)
                    {
                        _imageView.SetAnimation(frames);
                        AddInfo(new() { [L.T("Fotogrammi")] = frames.Count.ToString(), [L.T("Dimensioni")] = L.T($"{frames[0].frame.PixelWidth} × {frames[0].frame.PixelHeight} px") });
                        Show(_imageView);
                        return;
                    }
                }
                var (img, info) = await Task.Run(() => ImageIO.LoadForDisplay(path));
                if (gen != _generation) return;
                _imageView.SetImage(img);
                AddInfo(info);
                Show(_imageView);
                break;
            }
            case FileKind.Svg:
                await ShowWeb(new Uri(path));
                break;
            case FileKind.Pdf:
                OpenPdf(path, null);
                break;
            case FileKind.Xps:
                _xps ??= new DocumentViewer();
                _xpsDoc = new XpsDocument(path, FileAccess.Read);
                _xps.Document = _xpsDoc.GetFixedDocumentSequence();
                Show(_xps);
                break;
            case FileKind.Word or FileKind.PowerPoint:
            case FileKind.Excel when Formats.Ext(path) is not (".xlsx" or ".xlsm"):
                await OfficePreview(path, gen);
                break;
            case FileKind.Excel or FileKind.Table:
            {
                ShowBusy(L.T("Lettura della tabella…"));
                var sheets = await Task.Run(() => Tables.Read(path, TableView.MaxRows));
                if (gen != _generation) return;
                _tableView ??= new TableView();
                _tableView.SetSheets(sheets);
                AddInfo(new() { [L.T("Fogli")] = sheets.Count.ToString() });
                Show(_tableView);
                break;
            }
            case FileKind.Text or FileKind.Markdown or FileKind.Html or FileKind.Json:
                await ShowText(path, kind);
                break;
            case FileKind.Audio or FileKind.Video:
                _mediaView ??= CreateMediaView();
                _mediaView.Load(path);
                Show(_mediaView);
                break;
            case FileKind.Archive:
            {
                ShowBusy(L.T("Lettura dell'archivio…"));
                _archiveView ??= new ArchiveView();
                var info = _archiveView.Load(path);
                AddInfo(info);
                Show(_archiveView);
                break;
            }
            case FileKind.Font:
                _fontView ??= new FontView();
                AddInfo(_fontView.Load(path));
                Show(_fontView);
                break;
            case FileKind.Ebook:
                ShowMessage(L.T("Gli e-book (EPUB, MOBI) non sono ancora visualizzabili qui: usa “App predefinita”."));
                break;
            default:
            {
                var sample = new byte[8192];
                int n;
                await using (var fs = File.OpenRead(path)) n = await fs.ReadAsync(sample);
                if (Formats.LooksLikeText(sample.AsSpan(0, n))) await ShowText(path, FileKind.Text);
                else ShowHex(path, L.T("Tipo di file non riconosciuto: ecco il contenuto in esadecimale. Prova “App predefinita” per aprirlo con un altro programma."));
                break;
            }
        }
    }

    private MediaView CreateMediaView()
    {
        var mv = new MediaView();
        mv.DurationKnown += d => AddInfo(new() { [L.T("Durata")] = Util.HumanTime(d.TotalSeconds) });
        mv.Failed += msg => ShowMessage(L.T("Windows non riesce a riprodurre questo file (codec non supportato).\nPuoi convertirlo in MP4/MP3 con “Converti” oppure aprirlo con l'app predefinita.\n\n") + msg);
        return mv;
    }

    private void ShowMessage(string text)
    {
        _messageText.Text = text;
        Show(_message);
    }

    // ------------------------------------------------------------------ PDF e Office
    private void OpenPdf(string path, string? displayPath)
    {
        PdfDoc? doc = null;
        string? password = null;
        for (var attempt = 0; attempt < 3 && doc == null; attempt++)
        {
            try
            {
                doc = PdfDoc.Open(path, password);
            }
            catch (PdfPasswordException ex)
            {
                password = Dlg.Prompt(L.T("Documento protetto"), ex.Message + L.T("\nInserisci la password:"), password: true);
                if (password == null) break;
            }
        }
        if (doc == null)
        {
            ShowMessage(L.T("Il documento è protetto da password."));
            return;
        }
        _pdfView ??= new PdfView();
        _pdfView.SetDocument(doc);
        if (displayPath == null) AddInfo(new() { [L.T("Pagine")] = doc.PageCount.ToString() });
        Show(_pdfView);
    }

    private async Task OfficePreview(string path, int gen)
    {
        ShowBusy(L.T("Preparazione dell'anteprima con Office… (la prima volta può richiedere qualche secondo)"));
        var fi = new FileInfo(path);
        var key = Convert.ToHexString(MD5.HashData(Encoding.UTF8.GetBytes($"{path}|{fi.LastWriteTimeUtc.Ticks}|{fi.Length}")));
        var pdf = Path.Combine(AppInfo.TempDir, key + ".pdf");
        if (!File.Exists(pdf))
        {
            Directory.CreateDirectory(AppInfo.TempDir);
            await Sta.Run(() =>
            {
                using var office = new OfficeSession();
                office.Convert(path, pdf, "pdf");
            });
        }
        if (gen != _generation) return;
        OpenPdf(pdf, path);
        using var d = PdfDoc.Open(pdf);
        AddInfo(new() { [L.T("Pagine")] = d.PageCount.ToString() });
    }

    // ------------------------------------------------------------------ testo
    private async Task ShowText(string path, FileKind kind)
    {
        EnsureTextPane();
        var size = new FileInfo(path).Length;
        var truncated = size > MaxText;
        var (text, enc) = await Task.Run(() => Formats.ReadText(path, MaxText));
        _encoding = enc;
        _textKind = kind;
        _editor!.IsReadOnly = truncated;
        _editor.SyntaxHighlighting = CodeEditor.ForExtension(Formats.Ext(path));
        _editor.Text = text;
        _editor.IsModified = false;
        _editor.ScrollToHome();
        _saveBtn!.IsEnabled = false;
        _encodingLabel!.Text = L.T($"Codifica: {enc.WebName}") + (truncated ? L.T(" · file troncato (sola lettura)") : "");
        var hasPreview = kind is FileKind.Markdown or FileKind.Html;
        _previewToggle!.Visibility = hasPreview ? Visibility.Visible : Visibility.Collapsed;
        _previewToggle.IsChecked = hasPreview;
        AddInfo(new() { [L.T("Righe")] = Util.Number(_editor.Document.LineCount), [L.T("Codifica")] = enc.WebName });
        Show(_textPane!);
        await UpdatePreview();
    }

    private void EnsureTextPane()
    {
        if (_textPane != null) return;
        _editor = CodeEditor.Create();
        _saveBtn = Ui.Btn(L.T("Salva"), SymbolRegular.Save24, (_, _) => SaveText());
        var wrap = new ToggleSwitch { Content = L.T("A capo"), IsChecked = true, Margin = new Thickness(8, 0, 12, 0) };
        wrap.Click += (_, _) => _editor.WordWrap = wrap.IsChecked == true;
        _previewToggle = new ToggleSwitch { Content = L.T("Anteprima"), Margin = new Thickness(0, 0, 12, 0) };
        _previewToggle.Click += async (_, _) => await UpdatePreview();
        _encodingLabel = Ui.Hint();
        var bar = Ui.Row(_saveBtn, wrap, _previewToggle, _encodingLabel);
        bar.Margin = new Thickness(0, 0, 0, 8);
        _editor.TextChanged += (_, _) => _saveBtn.IsEnabled = _editor.IsModified && !_editor.IsReadOnly;
        _preview = NewWebView();
        var stack = new Grid();
        stack.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _editor });
        stack.Children.Add(_preview);
        _textPane = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        _textPane.Children.Add(bar);
        _textPane.Children.Add(stack);
        _editor.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.S && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control) SaveText();
        };
    }

    private async Task UpdatePreview()
    {
        var on = _previewToggle!.IsChecked == true && _textKind is FileKind.Markdown or FileKind.Html;
        _preview!.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (!on || _path == null) return;
        await _preview.EnsureCoreWebView2Async(await HtmlPdf.EnvironmentAsync());
        var html = _textKind == FileKind.Markdown
            ? HtmlPdf.MarkdownToHtml(_editor!.Text, Path.GetFileName(_path))
            : _editor!.Text;
        _preview.Source = new Uri(HtmlPdf.WriteTempHtml(html, Path.GetDirectoryName(_path)));
    }

    private void SaveText()
    {
        if (_path == null || _editor == null || _editor.IsReadOnly) return;
        try
        {
            File.WriteAllText(_path, _editor.Text, _encoding);
            _editor.IsModified = false;
            _saveBtn!.IsEnabled = false;
            MainWindow.Notify(L.T("File salvato"));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile salvare:\n") + ex.Message);
        }
    }

    // ------------------------------------------------------------------ web ed esadecimale
    private static WebView2 NewWebView() => new()
    {
        DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 32, 32, 36),
        Visibility = Visibility.Collapsed,
    };

    private async Task ShowWeb(Uri uri)
    {
        _web ??= NewWebView();
        _web.Visibility = Visibility.Visible;
        Show(_web);
        await _web.EnsureCoreWebView2Async(await HtmlPdf.EnvironmentAsync());
        _web.Source = uri;
    }

    private void ShowHex(string path, string note)
    {
        if (_hexPane == null)
        {
            _hex = CodeEditor.Create();
            _hex.IsReadOnly = true;
            _hex.WordWrap = false;
            _hex.ShowLineNumbers = false;
            _hexNote = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Ui.Res("TextFillColorPrimaryBrush") };
            var more = Ui.Btn(L.T("Carica altri 64 KB"), null, (_, _) => LoadHex(path));
            var top = new Border { Style = (Style)FindResource("InfoBanner"), Child = _hexNote, Margin = new Thickness(0, 0, 0, 8) };
            _hexPane = new DockPanel();
            DockPanel.SetDock(top, Dock.Top);
            DockPanel.SetDock(more, Dock.Bottom);
            more.Margin = new Thickness(0, 8, 0, 0);
            more.HorizontalAlignment = HorizontalAlignment.Left;
            _hexPane.Children.Add(top);
            _hexPane.Children.Add(more);
            _hexPane.Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _hex });
        }
        _hexNote!.Text = note;
        _hex!.Text = "";
        _hexOffset = 0;
        LoadHex(path);
        Show(_hexPane);
    }

    private void LoadHex(string path)
    {
        if (_path != path || _hexOffset >= 4 * 1024 * 1024) return;
        var buf = new byte[64 * 1024];
        int n;
        using (var fs = File.OpenRead(path))
        {
            fs.Seek(_hexOffset, SeekOrigin.Begin);
            n = fs.Read(buf);
        }
        var sb = new StringBuilder();
        for (var i = 0; i < n; i += 16)
        {
            var len = Math.Min(16, n - i);
            sb.Append((_hexOffset + i).ToString("X8")).Append("  ");
            for (var j = 0; j < 16; j++) sb.Append(j < len ? buf[i + j].ToString("X2") + " " : "   ");
            sb.Append(' ');
            for (var j = 0; j < len; j++) sb.Append(buf[i + j] is >= 32 and < 127 ? (char)buf[i + j] : '.');
            sb.Append('\n');
        }
        _hex!.AppendText(sb.ToString());
        _hexOffset += n;
    }

    // ------------------------------------------------------------------ informazioni
    private void SetBasicInfo(string path, FileKind kind)
    {
        InfoGrid.Children.Clear();
        InfoGrid.RowDefinitions.Clear();
        var fi = new FileInfo(path);
        AddInfo(new()
        {
            [L.T("Nome")] = fi.Name,
            [L.T("Cartella")] = fi.DirectoryName ?? "",
            [L.T("Tipo")] = $"{Formats.Labels[kind]} ({(fi.Extension.Length > 0 ? fi.Extension.ToLowerInvariant() : "senza estensione")})",
            [L.T("Dimensione")] = L.T($"{Util.HumanSize(fi.Length)} ({Util.Number(fi.Length)} byte)"),
            [L.T("Creato")] = Util.Date(fi.CreationTime),
            [L.T("Modificato")] = Util.Date(fi.LastWriteTime),
        });
    }

    private void AddInfo(Dictionary<string, string> info)
    {
        foreach (var (k, v) in info)
        {
            var row = InfoGrid.RowDefinitions.Count;
            InfoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var key = new TextBlock { Text = k, Margin = new Thickness(0, 4, 12, 4), Foreground = Ui.Res("TextFillColorTertiaryBrush") };
            var val = new TextBox
            {
                Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = System.Windows.Media.Brushes.Transparent,
                TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Left, Padding = new Thickness(0), Margin = new Thickness(0, 4, 0, 4),
                Foreground = Ui.Res("TextFillColorPrimaryBrush"),
            };
            Grid.SetRow(key, row);
            Grid.SetRow(val, row);
            Grid.SetColumn(val, 1);
            InfoGrid.Children.Add(key);
            InfoGrid.Children.Add(val);
        }
    }

    private async void Hash_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        var path = _path;
        HashBtn.IsEnabled = false;
        HashText.Visibility = Visibility.Visible;
        HashText.Text = L.T("Calcolo in corso…");
        var progress = new Progress<double>(f => HashText.Text = L.T($"Calcolo… {f * 100:0}%"));
        try
        {
            var (md5, sha) = await Task.Run(() =>
            {
                using var m = MD5.Create();
                using var s = SHA256.Create();
                using var fs = File.OpenRead(path);
                var buf = new byte[4 << 20];
                long done = 0;
                int n;
                while ((n = fs.Read(buf)) > 0)
                {
                    m.TransformBlock(buf, 0, n, null, 0);
                    s.TransformBlock(buf, 0, n, null, 0);
                    done += n;
                    ((IProgress<double>)progress).Report(done / (double)Math.Max(1, fs.Length));
                }
                m.TransformFinalBlock([], 0, 0);
                s.TransformFinalBlock([], 0, 0);
                return (Convert.ToHexString(m.Hash!).ToLowerInvariant(), Convert.ToHexString(s.Hash!).ToLowerInvariant());
            });
            if (_path == path) HashText.Text = $"MD5\n{md5}\n\nSHA-256\n{sha}";
        }
        catch (Exception ex)
        {
            HashText.Text = L.T("Errore: ") + ex.Message;
        }
        finally
        {
            HashBtn.IsEnabled = true;
        }
    }

    // ------------------------------------------------------------------ azioni
    private void Edit_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        _main.OpenFile(_path, Formats.KindOf(_path) == FileKind.Pdf ? "pdf" : "photo");
    }

    private void Convert_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null) ((IToolPage)_main.ShowPage("convert")).AddFiles([_path]);
    }

    private void Gemini_Click(object sender, RoutedEventArgs e)
    {
        if (_path == null) return;
        var path = _path;
        var menu = new ContextMenu();
        foreach (var prompt in GeminiPage.Prompts)
        {
            var item = new System.Windows.Controls.MenuItem { Header = prompt.Label };
            item.Click += async (_, _) => await ((GeminiPage)_main.ShowPage("gemini")).AskAboutFile(path, prompt);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var custom = new System.Windows.Controls.MenuItem { Header = L.T("Fai una domanda…") };
        custom.Click += async (_, _) =>
        {
            var q = Dlg.Prompt(L.T("Chiedi a Gemini"), L.T($"Cosa vuoi chiedere su “{Path.GetFileName(path)}”?"), multiline: true);
            if (!string.IsNullOrWhiteSpace(q)) await ((GeminiPage)_main.ShowPage("gemini")).AskAboutFile(path, new GeminiPage.Prompt("", q.Trim(), q.Trim()));
        };
        menu.Items.Add(custom);
        menu.PlacementTarget = GeminiBtn;
        menu.IsOpen = true;
    }

    private void External_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null) Util.OpenExternal(_path);
    }

    private void Reveal_Click(object sender, RoutedEventArgs e)
    {
        if (_path != null) Util.Reveal(_path);
    }
}
