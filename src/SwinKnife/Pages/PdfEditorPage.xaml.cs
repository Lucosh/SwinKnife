using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using SwinKnife.Pdf;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

public partial class PdfEditorPage : UserControl, IToolPage
{
    public sealed class Thumb : INotifyPropertyChanged
    {
        private BitmapSource? _image;
        public BitmapSource? Image
        {
            get => _image;
            set
            {
                _image = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Image)));
            }
        }
        public double W { get; init; }
        public double H { get; init; }
        public string Label { get; init; } = "";
        public event PropertyChangedEventHandler? PropertyChanged;
    }

    private readonly MainWindow _main;
    private PdfDoc? _doc;
    private string? _path;
    private string? _password;
    private int _cur;
    private double _zoom = 1.2;
    private bool _dirty;
    private Color _color = Colors.Black;
    private readonly List<byte[]> _undo = new();
    private readonly List<byte[]> _redo = new();
    private readonly ObservableCollection<Thumb> _thumbs = new();
    private readonly Queue<int> _thumbQueue = new();
    private readonly DispatcherTimer _thumbTimer = new() { Interval = TimeSpan.FromMilliseconds(1) };
    private List<(Rect display, TextLine line)> _lines = new();
    private (int page, int hit) _findPos = (0, -1);
    private readonly List<FrameworkElement> _needsDoc = new();
    private readonly TextBlock _pageLabel = Ui.Label("");
    private readonly TextBlock _zoomLabel = Ui.Hint();
    private readonly Border _colorSwatch = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(4), BorderThickness = new Thickness(1), BorderBrush = Brushes.Gray };
    private readonly ComboBox _width = new() { ItemsSource = new[] { 1, 2, 3, 4, 6, 8, 12 }, SelectedIndex = 1, Width = 76 };
    private Wpf.Ui.Controls.Button _undoBtn = null!, _redoBtn = null!;
    private Point? _dragStart;
    private int _dragIndex = -1;

    private static readonly Dictionary<PdfTool, string> Hints = new()
    {
        [PdfTool.Select] = L.T("Scorri con la rotellina (Ctrl+rotellina per lo zoom). Trascina le miniature a sinistra per riordinare le pagine."),
        [PdfTool.Text] = L.T("Fai clic sulla pagina nel punto in cui vuoi scrivere."),
        [PdfTool.EditText] = L.T("Le righe modificabili sono tratteggiate: fai clic su una riga per riscriverla."),
        [PdfTool.Highlight] = L.T("Trascina un rettangolo sul testo da evidenziare."),
        [PdfTool.Whiteout] = L.T("Trascina un rettangolo: l'area viene coperta di bianco e il testo interamente coperto viene rimosso."),
        [PdfTool.Redact] = L.T("Trascina un rettangolo: copre di nero e rimuove definitivamente ogni testo che tocca l'area."),
        [PdfTool.Sign] = L.T("Trascina il rettangolo dove inserire la firma o l'immagine (oppure fai clic)."),
        [PdfTool.Ink] = L.T("Disegna tenendo premuto il tasto sinistro. Colore e spessore dalla barra."),
        [PdfTool.Note] = L.T("Fai clic dove vuoi attaccare una nota adesiva."),
        [PdfTool.EraseAnnot] = L.T("Fai clic su un'annotazione (evidenziazione, nota, disegno) per eliminarla."),
    };

    public PdfEditorPage(MainWindow main)
    {
        _main = main;
        InitializeComponent();
        BuildBars();
        Thumbs.ItemsSource = _thumbs;
        Thumbs.SelectionChanged += (_, _) =>
        {
            if (Thumbs.SelectedIndex >= 0 && Thumbs.SelectedIndex != _cur) GoTo(Thumbs.SelectedIndex);
        };
        Thumbs.PreviewMouseLeftButtonDown += Thumbs_MouseDown;
        Thumbs.PreviewMouseMove += Thumbs_MouseMove;
        Thumbs.Drop += Thumbs_Drop;
        EmptyState.Drop += (_, e) =>
        {
            if (e.Data.GetData(DataFormats.FileDrop) is string[] f && f.Length > 0) OpenFile(f[0]);
            e.Handled = true;
        };
        _thumbTimer.Tick += (_, _) => RenderThumbs();
        Canvas.Clicked += OnClick;
        Canvas.Dragged += OnDrag;
        Canvas.Stroke += OnStroke;
        FindBox.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) FindNext();
        };
        PageScroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        };
        PreviewKeyDown += OnKey;
        SetTool(PdfTool.Select);
        UpdateUi();
    }

    // ------------------------------------------------------------------ barre
    private void BuildBars()
    {
        Wpf.Ui.Controls.Button Doc(SymbolRegular icon, string tip, Action action, string? text = null)
        {
            var b = Ui.IconBtn(icon, tip, (_, _) => Safe(action), text);
            _needsDoc.Add(b);
            DocBar.Children.Add(b);
            return b;
        }
        var open = Ui.Btn(L.T("Apri…"), SymbolRegular.Open24, (_, _) => Pick(), primary: true);
        open.Margin = new Thickness(0, 0, 10, 0);
        DocBar.Children.Add(open);
        Doc(SymbolRegular.Save24, L.T("Salva (Ctrl+S)"), Save);
        Doc(SymbolRegular.SaveEdit24, L.T("Salva con nome…"), () => SaveAs());
        DocBar.Children.Add(Ui.Separator());
        _undoBtn = Doc(SymbolRegular.ArrowUndo24, L.T("Annulla (Ctrl+Z)"), Undo);
        _redoBtn = Doc(SymbolRegular.ArrowRedo24, L.T("Ripristina (Ctrl+Y)"), Redo);
        DocBar.Children.Add(Ui.Separator());
        Doc(SymbolRegular.DocumentAdd24, L.T("Unisci: inserisci PDF o immagini dopo la pagina corrente"), InsertFiles, L.T("Unisci"));
        Doc(SymbolRegular.Add24, L.T("Inserisci una pagina vuota"), BlankPage);
        Doc(SymbolRegular.DocumentArrowRight24, L.T("Estrai pagine in un nuovo PDF"), ExtractPages, L.T("Estrai"));
        Doc(SymbolRegular.ArrowSplit24, L.T("Dividi il PDF in più file"), Split, L.T("Dividi"));
        DocBar.Children.Add(Ui.Separator());
        Doc(SymbolRegular.ArrowRotateCounterclockwise24, L.T("Ruota la pagina a sinistra"), () => Rotate(-1));
        Doc(SymbolRegular.ArrowRotateClockwise24, L.T("Ruota la pagina a destra"), () => Rotate(1));
        Doc(SymbolRegular.Delete24, L.T("Elimina la pagina"), DeletePage);
        DocBar.Children.Add(Ui.Separator());
        Doc(SymbolRegular.TextFont24, L.T("Filigrana su tutte le pagine"), Watermark, L.T("Filigrana"));
        Doc(SymbolRegular.NumberSymbol24, L.T("Numera le pagine"), NumberPages, L.T("Numeri"));
        Doc(SymbolRegular.ArrowMinimize24, L.T("Comprimi (riduce le dimensioni)"), Compress, L.T("Comprimi"));
        Doc(SymbolRegular.LockClosed24, L.T("Proteggi con password / rimuovi password"), Password, L.T("Password"));
        Doc(SymbolRegular.Scan24, L.T("Riconosci il testo delle pagine scansionate (OCR): il PDF diventa ricercabile"), RunOcr, "OCR");
        Doc(SymbolRegular.CheckboxChecked24, L.T("Compila i campi del modulo PDF"), FillForm, L.T("Moduli"));
        Doc(SymbolRegular.DocumentText24, L.T("Esporta tutto il testo in un file .txt"), ExportText);

        var tools = new (PdfTool tool, SymbolRegular icon, string label, bool showText)[]
        {
            (PdfTool.Select, SymbolRegular.CursorClick24, L.T("Naviga"), false),
            (PdfTool.Text, SymbolRegular.TextT24, L.T("Aggiungi testo"), true),
            (PdfTool.EditText, SymbolRegular.TextEditStyle24, L.T("Modifica testo"), true),
            (PdfTool.Highlight, SymbolRegular.Highlight24, L.T("Evidenzia"), false),
            (PdfTool.Whiteout, SymbolRegular.Eraser24, L.T("Bianchetto"), false),
            (PdfTool.Redact, SymbolRegular.ShieldLock24, L.T("Oscura"), false),
            (PdfTool.Sign, SymbolRegular.Signature24, L.T("Firma / immagine"), true),
            (PdfTool.Ink, SymbolRegular.Pen24, L.T("Disegno a mano libera"), false),
            (PdfTool.Note, SymbolRegular.NoteAdd24, L.T("Nota adesiva"), false),
            (PdfTool.EraseAnnot, SymbolRegular.DeleteDismiss24, L.T("Elimina annotazione"), false),
        };
        foreach (var (tool, icon, label, showText) in tools)
        {
            var content = Ui.Row(new SymbolIcon { Symbol = icon, FontSize = 18 });
            if (showText) content.Children.Add(new TextBlock { Text = label, Margin = new Thickness(6, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center });
            var rb = new RadioButton { Style = (Style)FindResource("ToolToggle"), Content = content, GroupName = "pdftool", ToolTip = label, IsChecked = tool == PdfTool.Select };
            rb.Checked += (_, _) => SetTool(tool);
            _needsDoc.Add(rb);
            ToolBar.Children.Add(rb);
        }
        ToolBar.Children.Add(Ui.Separator());
        var colorBtn = new Wpf.Ui.Controls.Button { Content = Ui.Row(_colorSwatch, new TextBlock { Text = L.T(" Colore"), VerticalAlignment = VerticalAlignment.Center }), Appearance = ControlAppearance.Transparent, Padding = new Thickness(8, 5, 8, 5) };
        var menu = new ContextMenu();
        foreach (var (name, col) in Palette.Colors)
        {
            var c = col;
            var item = new System.Windows.Controls.MenuItem { Header = name, Icon = new Border { Width = 14, Height = 14, Background = new SolidColorBrush(c), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) } };
            item.Click += (_, _) => SetColor(c);
            menu.Items.Add(item);
        }
        colorBtn.Click += (_, _) =>
        {
            menu.PlacementTarget = colorBtn;
            menu.IsOpen = true;
        };
        ToolBar.Children.Add(colorBtn);
        ToolBar.Children.Add(Ui.Row(Ui.Label(L.T("  Spessore  ")), _width));
        SetColor(Colors.Black);

        NavBar.Children.Add(Ui.IconBtn(SymbolRegular.ChevronLeft24, L.T("Pagina precedente (PagSu)"), (_, _) => GoTo(_cur - 1)));
        _pageLabel.Margin = new Thickness(8, 0, 8, 0);
        NavBar.Children.Add(_pageLabel);
        NavBar.Children.Add(Ui.IconBtn(SymbolRegular.ChevronRight24, L.T("Pagina successiva (PagGiù)"), (_, _) => GoTo(_cur + 1)));
        NavBar.Children.Add(new Border { Width = 24 });
        NavBar.Children.Add(Ui.IconBtn(SymbolRegular.ZoomOut24, L.T("Riduci"), (_, _) => SetZoom(_zoom / 1.2)));
        NavBar.Children.Add(_zoomLabel);
        NavBar.Children.Add(Ui.IconBtn(SymbolRegular.ZoomIn24, L.T("Ingrandisci"), (_, _) => SetZoom(_zoom * 1.2)));
        NavBar.Children.Add(Ui.IconBtn(SymbolRegular.ZoomFit24, L.T("Adatta alla larghezza"), (_, _) => FitWidth()));
    }

    private void SetColor(Color c)
    {
        _color = c;
        _colorSwatch.Background = new SolidColorBrush(c);
        Canvas.InkColor = c;
    }

    private void SetTool(PdfTool tool)
    {
        Canvas.Tool = tool;
        HintText.Text = Hints[tool];
        if (_doc != null && tool == PdfTool.EditText) LoadLines();
        Canvas.InvalidateVisual();
    }

    private static void Safe(Action a)
    {
        try { a(); }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Editor PDF");
            Dlg.Error(ex.Message);
        }
    }

    // ------------------------------------------------------------------ IToolPage
    public bool Accepts(string path) => Formats.KindOf(path) == FileKind.Pdf;

    public bool CanClose() => _doc == null || !_dirty || Dlg.Confirm(L.T("Il PDF ha modifiche non salvate. Vuoi chiudere comunque?"));

    public void Shutdown() => _doc?.Dispose();

    private void Pick()
    {
        var p = Dlg.OpenFile(L.T("Apri PDF"), L.T("Documenti PDF e immagini|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.heic;*.webp|Tutti i file|*.*"), "pdf");
        if (p != null) OpenFile(p);
    }

    public void OpenFile(string path)
    {
        if (!CanClose()) return;
        try
        {
            PdfDoc? doc = null;
            string? pw = null;
            var openPath = path;
            if (Formats.IsImageLike(Formats.KindOf(path)))
            {
                Directory.CreateDirectory(AppInfo.TempDir);
                var tmp = Path.Combine(AppInfo.TempDir, $"{Guid.NewGuid():N}.pdf");
                PdfTools.ImagesToPdf([path], tmp);
                openPath = tmp;
                path = Path.ChangeExtension(path, ".pdf");
            }
            var data = File.ReadAllBytes(openPath);
            while (doc == null)
            {
                try
                {
                    doc = PdfDoc.Open(data, pw);
                }
                catch (PdfPasswordException ex)
                {
                    pw = Dlg.Prompt(L.T("PDF protetto"), ex.Message + L.T("\nInserisci la password:"), password: true);
                    if (pw == null) return;
                }
            }
            if (pw != null) Dlg.Info(L.T("Il documento è protetto: verrà salvato con la stessa password.\nPer toglierla usa il pulsante “Password”."));
            _doc?.Dispose();
            _doc = doc;
            _password = pw;
            _path = path;
            _cur = 0;
            _dirty = false;
            _undo.Clear();
            _redo.Clear();
            RebuildThumbs();
            Dispatcher.BeginInvoke(FitWidth, DispatcherPriority.Loaded);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile aprire il documento:\n") + ex.Message);
        }
    }

    // ------------------------------------------------------------------ rendering e navigazione
    private void UpdateUi()
    {
        var has = _doc != null;
        EmptyState.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        PageArea.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        foreach (var e in _needsDoc) e.IsEnabled = has;
        _undoBtn.IsEnabled = has && _undo.Count > 0;
        _redoBtn.IsEnabled = has && _redo.Count > 0;
        if (!has)
        {
            TitleText.Text = "";
            _pageLabel.Text = "";
            return;
        }
        TitleText.Text = $"{Path.GetFileName(_path)}{(_dirty ? " •" : "")}{(_password != null ? "  🔒" : "")}";
        _pageLabel.Text = $"{_cur + 1} / {_doc!.PageCount}";
        _zoomLabel.Text = $"{_zoom * 100:0}%";
    }

    private void RenderPage()
    {
        if (_doc == null) return;
        _cur = Math.Clamp(_cur, 0, _doc.PageCount - 1);
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        Canvas.SetPage(_doc.Render(_cur, _zoom * dpi), _doc.PageSize(_cur), _zoom);
        Canvas.Hits.Clear();
        if (Canvas.Tool == PdfTool.EditText) LoadLines();
        UpdateUi();
    }

    private void LoadLines()
    {
        if (_doc == null) return;
        _lines = _doc.TextLines(_cur).Select(l => (_doc.LineDisplayRect(_cur, l), l)).ToList();
        Canvas.EditableLines.Clear();
        Canvas.EditableLines.AddRange(_lines.Select(l => l.display));
        Canvas.InvalidateVisual();
    }

    private void GoTo(int i)
    {
        if (_doc == null) return;
        _cur = Math.Clamp(i, 0, _doc.PageCount - 1);
        if (Thumbs.SelectedIndex != _cur) Thumbs.SelectedIndex = _cur;
        Thumbs.ScrollIntoView(Thumbs.SelectedItem);
        RenderPage();
        PageScroll.ScrollToTop();
    }

    private void SetZoom(double z)
    {
        _zoom = Math.Clamp(z, 0.25, 5);
        RenderPage();
    }

    private void FitWidth()
    {
        if (_doc == null) return;
        var w = _doc.PageSize(_cur).Width;
        SetZoom((PageScroll.ActualWidth - 70) / w);
    }

    private void RebuildThumbs()
    {
        _thumbs.Clear();
        _thumbQueue.Clear();
        if (_doc == null)
        {
            UpdateUi();
            return;
        }
        for (var i = 0; i < _doc.PageCount; i++)
        {
            var s = _doc.PageSize(i);
            var k = 120 / Math.Max(s.Width, s.Height);
            _thumbs.Add(new Thumb { W = s.Width * k, H = s.Height * k, Label = (i + 1).ToString() });
            _thumbQueue.Enqueue(i);
        }
        _thumbTimer.Start();
        _cur = Math.Clamp(_cur, 0, _doc.PageCount - 1);
        Thumbs.SelectedIndex = _cur;
        RenderPage();
    }

    private void RenderThumbs()
    {
        if (_doc == null || _thumbQueue.Count == 0)
        {
            _thumbTimer.Stop();
            return;
        }
        for (var n = 0; n < 3 && _thumbQueue.Count > 0; n++)
        {
            var i = _thumbQueue.Dequeue();
            if (i >= _thumbs.Count) continue;
            var s = _doc.PageSize(i);
            _thumbs[i].Image = _doc.Render(i, 2 * 120 / Math.Max(s.Width, s.Height), false);
        }
    }

    private void RefreshThumb(int i)
    {
        if (!_thumbQueue.Contains(i)) _thumbQueue.Enqueue(i);
        _thumbTimer.Start();
    }

    // ------------------------------------------------------------------ cronologia
    private void Snapshot()
    {
        _undo.Add(_doc!.Save());
        if (_undo.Count > 25) _undo.RemoveAt(0);
        _redo.Clear();
    }

    private void Restore(byte[] data)
    {
        _doc!.Dispose();
        _doc = PdfDoc.Open(data);
        RebuildThumbs();
    }

    private void Undo()
    {
        if (_doc == null || _undo.Count == 0) return;
        _redo.Add(_doc.Save());
        var last = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        Restore(last);
        _dirty = true;
        UpdateUi();
    }

    private void Redo()
    {
        if (_doc == null || _redo.Count == 0) return;
        _undo.Add(_doc.Save());
        var last = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        Restore(last);
        _dirty = true;
        UpdateUi();
    }

    private void Changed(bool structure = false)
    {
        _dirty = true;
        if (structure) RebuildThumbs();
        else
        {
            RenderPage();
            RefreshThumb(_cur);
        }
        UpdateUi();
    }

    // ------------------------------------------------------------------ strumenti sulla pagina
    private void OnClick(Point p)
    {
        if (_doc == null) return;
        Safe(() =>
        {
            switch (Canvas.Tool)
            {
                case PdfTool.Text:
                {
                    var d = new TextEditDialog();
                    if (!d.Show(L.T("Aggiungi testo"), "", 12, _color, 0, false, false) || string.IsNullOrWhiteSpace(d.Text)) return;
                    Snapshot();
                    _doc.AddText(_cur, p, d.Text, d.FontName, d.Size, d.Color);
                    Changed();
                    break;
                }
                case PdfTool.EditText:
                    EditLineAt(p);
                    break;
                case PdfTool.Note:
                {
                    var text = Dlg.Prompt(L.T("Nota adesiva"), L.T("Testo della nota:"), multiline: true);
                    if (string.IsNullOrWhiteSpace(text)) return;
                    Snapshot();
                    _doc.AddNote(_cur, p, text);
                    Changed();
                    break;
                }
                case PdfTool.EraseAnnot:
                {
                    var before = _doc.Save();
                    if (_doc.RemoveAnnotAt(_cur, p))
                    {
                        _undo.Add(before);
                        _redo.Clear();
                        Changed();
                    }
                    else MainWindow.Notify(L.T("Nessuna annotazione in quel punto"));
                    break;
                }
            }
        });
    }

    private void EditLineAt(Point p)
    {
        var hit = _lines.Where(l => { var r = l.display; r.Inflate(2, 2); return r.Contains(p); })
            .OrderBy(l => l.display.Width * l.display.Height).Select(l => l.line).FirstOrDefault();
        if (hit == null)
        {
            MainWindow.Notify(L.T("Nessun testo modificabile in quel punto"));
            return;
        }
        var effective = (float)(hit.FontSize * Math.Sqrt(hit.Matrix.a * hit.Matrix.a + hit.Matrix.b * hit.Matrix.b));
        var d = new TextEditDialog();
        if (!d.Show(L.T("Modifica testo"), hit.Text, effective, hit.Color, hit.Family, hit.Bold, hit.Italic,
                L.T("Il testo viene riscritto con un font standard simile all'originale."))) return;
        Snapshot();
        _doc!.ReplaceLine(_cur, hit, d.Text, d.FontName, d.Size, d.Color);
        Changed();
    }

    private void OnDrag(Rect r)
    {
        if (_doc == null) return;
        Safe(() =>
        {
            switch (Canvas.Tool)
            {
                case PdfTool.Highlight:
                    Snapshot();
                    _doc.AddHighlight(_cur, r, _color == Colors.Black ? Color.FromRgb(255, 230, 0) : _color);
                    Changed();
                    break;
                case PdfTool.Whiteout:
                    Snapshot();
                    _doc.Cover(_cur, r, Colors.White, false);
                    Changed();
                    break;
                case PdfTool.Redact:
                    if (!Dlg.Confirm(L.T("Il testo che tocca l'area verrà rimosso definitivamente e coperto di nero. Continuare?"))) return;
                    Snapshot();
                    var n = _doc.Cover(_cur, r, Colors.Black, true);
                    Changed();
                    MainWindow.Notify(L.T($"Rimossi {n} elementi di testo"));
                    break;
                case PdfTool.Sign:
                    var img = SignatureDialog.Show();
                    if (img == null) return;
                    Snapshot();
                    _doc.AddImage(_cur, r, img);
                    Changed();
                    break;
            }
        });
    }

    private void OnStroke(List<Point> pts)
    {
        if (_doc == null) return;
        Safe(() =>
        {
            Snapshot();
            _doc.AddInk(_cur, pts, _color, _width.SelectedItem is int w ? w : 2);
            Changed();
        });
    }

    private void FindNext()
    {
        var text = FindBox.Text.Trim();
        if (_doc == null || text.Length == 0) return;
        var n = _doc.PageCount;
        var (startPage, startHit) = _findPos;
        for (var k = 0; k <= n; k++)
        {
            var p = (startPage + k) % n;
            var hits = _doc.Search(p, text);
            var first = k == 0 ? startHit + 1 : 0;
            if (first < hits.Count)
            {
                _findPos = (p, first);
                if (p != _cur) GoTo(p);
                Canvas.Hits.Clear();
                Canvas.Hits.AddRange(hits);
                Canvas.InvalidateVisual();
                PageScroll.ScrollToVerticalOffset(Math.Max(0, hits[first].Y * _zoom - 120));
                MainWindow.Notify(L.T($"{hits.Count} risultati a pagina {p + 1}"));
                return;
            }
        }
        _findPos = (0, -1);
        MainWindow.Notify(L.T($"“{text}” non trovato"));
    }

    // ------------------------------------------------------------------ pagine
    private void Rotate(int quarter)
    {
        Snapshot();
        _doc!.SetRotation(_cur, _doc.Rotation(_cur) + quarter);
        Changed(true);
    }

    private void DeletePage()
    {
        if (_doc!.PageCount == 1)
        {
            Dlg.Info(L.T("Il documento deve avere almeno una pagina."));
            return;
        }
        Snapshot();
        _doc.DeletePage(_cur);
        Changed(true);
    }

    private void BlankPage()
    {
        Snapshot();
        var s = _doc!.PageSize(_cur);
        _doc.InsertBlankPage(_cur + 1, s.Width, s.Height);
        _cur++;
        Changed(true);
    }

    private void InsertFiles()
    {
        var files = Dlg.OpenFiles(L.T("Scegli i file da inserire"), L.T("PDF e immagini|*.pdf;*.png;*.jpg;*.jpeg;*.tif;*.tiff;*.bmp;*.heic;*.webp|Tutti i file|*.*"), "pdf_insert");
        if (files.Length > 0) InsertPaths(files);
    }

    public void AddFiles(IReadOnlyList<string> paths)
    {
        if (_doc == null) OpenFile(paths[0]);
        else InsertPaths(paths);
    }

    private void InsertPaths(IReadOnlyList<string> paths)
    {
        Snapshot();
        var at = _cur + 1;
        var added = 0;
        try
        {
            foreach (var p in paths)
            {
                string pdfPath = p;
                if (Formats.IsImageLike(Formats.KindOf(p)))
                {
                    pdfPath = Path.Combine(AppInfo.TempDir, $"{Guid.NewGuid():N}.pdf");
                    Directory.CreateDirectory(AppInfo.TempDir);
                    PdfTools.ImagesToPdf([p], pdfPath);
                }
                else if (Formats.KindOf(p) != FileKind.Pdf)
                {
                    throw new InvalidOperationException(L.T($"{Path.GetFileName(p)}: formato non supportato."));
                }
                using var src = PdfDoc.Open(pdfPath);
                _doc!.Import(src, at);
                at += src.PageCount;
                added += src.PageCount;
            }
        }
        catch (PdfPasswordException)
        {
            Dlg.Error(L.T("Uno dei file è protetto da password: rimuovila prima di unirlo."));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Inserimento non riuscito:\n") + ex.Message);
        }
        if (added > 0)
        {
            _cur++;
            Changed(true);
            MainWindow.Notify(L.T($"Inserite {added} pagine"));
        }
        else _undo.RemoveAt(_undo.Count - 1);
    }

    private int[]? AskPages(string title)
    {
        var text = Dlg.Prompt(title, L.T($"Pagine (es. 1-3, 5, 8-) su {_doc!.PageCount}:"), (_cur + 1).ToString());
        if (text == null) return null;
        var n = _doc.PageCount;
        var result = new List<int>();
        foreach (var part in Regex.Split(text.Trim(), @"[,;\s]+").Where(s => s.Length > 0))
        {
            var m = Regex.Match(part, @"^(\d*)\s*-\s*(\d*)$");
            int a, b;
            if (m.Success)
            {
                a = m.Groups[1].Value.Length > 0 ? int.Parse(m.Groups[1].Value) : 1;
                b = m.Groups[2].Value.Length > 0 ? int.Parse(m.Groups[2].Value) : n;
            }
            else if (int.TryParse(part, out var single)) a = b = single;
            else
            {
                Dlg.Error(L.T($"Intervallo non valido: {part}"));
                return null;
            }
            a = Math.Max(1, a);
            b = Math.Min(n, b);
            for (var i = a; i <= b; i++) result.Add(i - 1);
        }
        if (result.Count == 0) Dlg.Error(L.T("Nessuna pagina indicata."));
        return result.Count > 0 ? result.ToArray() : null;
    }

    private string Stem => _path != null ? Path.GetFileNameWithoutExtension(_path) : "documento";

    private void ExtractPages()
    {
        var pages = AskPages(L.T("Estrai pagine"));
        if (pages == null) return;
        var dst = Dlg.SaveFile(L.T("Salva le pagine estratte"), $"{Stem}_estratto.pdf", "PDF|*.pdf", "pdf");
        if (dst == null) return;
        using var nd = _doc!.ExtractPages(pages);
        File.WriteAllBytes(dst, nd.Save());
        MainWindow.Notify(L.T("Salvato ") + Path.GetFileName(dst));
        Util.Reveal(dst);
    }

    private void Split()
    {
        var n = Dlg.PromptInt(L.T("Dividi PDF"), L.T("Numero di pagine per ogni file:"), 1, 1, _doc!.PageCount);
        if (n == null) return;
        var folder = Dlg.PickFolder(L.T("Cartella in cui salvare i file"), "pdf_split");
        if (folder == null) return;
        var count = 0;
        for (var start = 0; start < _doc.PageCount; start += n.Value)
        {
            var end = Math.Min(start + n.Value, _doc.PageCount) - 1;
            using var nd = _doc.ExtractPages(Enumerable.Range(start, end - start + 1).ToArray());
            var label = start == end ? $"{start + 1}" : $"{start + 1}-{end + 1}";
            File.WriteAllBytes(Util.UniquePath(Path.Combine(folder, $"{Stem}_pag{label}.pdf")), nd.Save());
            count++;
        }
        MainWindow.Notify(L.T($"Creati {count} file"));
        Util.OpenFolder(folder);
    }

    // ------------------------------------------------------------------ strumenti documento
    private void Watermark()
    {
        var w = WatermarkDialog.Show();
        if (w == null) return;
        Snapshot();
        for (var i = 0; i < _doc!.PageCount; i++)
            _doc.AddCenteredText(i, w.Value.text, w.Value.size, w.Value.color, w.Value.alpha, w.Value.diagonal ? 45 : 0);
        Changed(true);
    }

    private void NumberPages()
    {
        string[] formats = ["1", "1 / N", L.T("Pagina 1 di N"), "- 1 -"];
        var choice = Dlg.Choose(L.T("Numera pagine"), L.T("Formato dei numeri (in fondo a ogni pagina):"), formats, 1);
        if (choice == null) return;
        Snapshot();
        var n = _doc!.PageCount;
        for (var i = 0; i < n; i++)
        {
            var label = choice switch { 0 => $"{i + 1}", 1 => $"{i + 1} / {n}", 2 => L.T($"Pagina {i + 1} di {n}"), _ => $"- {i + 1} -" };
            _doc.AddFooterText(i, label, 10, Color.FromRgb(64, 64, 64));
        }
        Changed(true);
    }

    private async void Compress()
    {
        var level = Dlg.Choose(L.T("Comprimi PDF"), L.T("Livello di compressione:"),
            [L.T("Leggera (solo pulizia, nessuna perdita)"), L.T("Media (immagini fino a 1800 px)"), L.T("Forte (immagini fino a 1200 px)")], 1);
        if (level == null) return;
        var before = _doc!.Save();
        try
        {
            MainWindow.Notify(L.T("Compressione in corso…"), 0);
            var after = await Task.Run(() => PdfTools.Compress(before, (PdfTools.CompressionLevel)level.Value));
            if (after.Length >= before.Length)
            {
                MainWindow.Notify(L.T("Il documento è già compatto: nessun risparmio possibile"));
                return;
            }
            _undo.Add(before);
            _redo.Clear();
            Restore(after);
            _dirty = true;
            UpdateUi();
            Dlg.Info(L.T($"Dimensione: {Util.HumanSize(before.Length)} → {Util.HumanSize(after.Length)} ({(1 - after.Length / (double)before.Length) * 100:0}% in meno).\nRicorda di salvare."));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Compressione non riuscita:\n") + ex.Message);
        }
    }

    private void Password()
    {
        if (_password != null)
        {
            if (Dlg.Confirm(L.T("Il documento è protetto da password. Vuoi rimuovere la protezione?\n(Diventa effettivo al salvataggio.)")))
            {
                _password = null;
                _dirty = true;
                UpdateUi();
            }
            return;
        }
        var pw = Dlg.Prompt(L.T("Proteggi con password"), L.T("Password che servirà per aprire il PDF:"), password: true);
        if (string.IsNullOrEmpty(pw)) return;
        var pw2 = Dlg.Prompt(L.T("Proteggi con password"), L.T("Ripeti la password:"), password: true);
        if (pw2 == null) return;
        if (pw != pw2)
        {
            Dlg.Error(L.T("Le password non coincidono."));
            return;
        }
        _password = pw;
        _dirty = true;
        UpdateUi();
        MainWindow.Notify(L.T("Protezione AES-256 impostata: verrà applicata al salvataggio"));
    }

    private async void RunOcr()
    {
        if (!Ocr.Available)
        {
            Dlg.Error(L.T("Il riconoscimento del testo non è disponibile: aggiungi una lingua in Impostazioni di Windows › Lingua."));
            return;
        }
        var before = _doc!.Save();
        MainWindow.Notify(L.T("Riconoscimento del testo in corso…"), 0);
        try
        {
            var pages = 0;
            var result = await Task.Run(async () =>
            {
                using var d = PdfDoc.Open(before);
                pages = await Ocr.MakeSearchable(d, (_, m) => Dispatcher.InvokeAsync(() => MainWindow.Notify(m, 0)), CancellationToken.None);
                return d.Save();
            });
            if (pages == 0)
            {
                MainWindow.Notify(L.T("Tutte le pagine hanno già testo selezionabile"));
                return;
            }
            _undo.Add(before);
            _redo.Clear();
            Restore(result);
            _dirty = true;
            UpdateUi();
            MainWindow.Notify(L.T($"Testo riconosciuto in {pages} pagine: ora puoi cercarlo e copiarlo. Ricorda di salvare."), 10);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("OCR non riuscito:\n") + ex.Message);
        }
    }

    private void FillForm()
    {
        var bytes = _doc!.Save();
        var fields = PdfForms.Read(bytes);
        if (fields.Count == 0)
        {
            Dlg.Info(L.T("Questo PDF non contiene campi compilabili."));
            return;
        }
        if (!FormFillDialog.Show(fields)) return;
        var filled = PdfForms.Fill(bytes, fields);
        _undo.Add(bytes);
        _redo.Clear();
        Restore(filled);
        _dirty = true;
        UpdateUi();
        MainWindow.Notify(L.T("Modulo compilato. Ricorda di salvare."));
    }

    private void ExportText()
    {
        var dst = Dlg.SaveFile(L.T("Esporta testo"), $"{Stem}.txt", "Testo|*.txt", "pdf");
        if (dst == null) return;
        var sb = new System.Text.StringBuilder();
        for (var i = 0; i < _doc!.PageCount; i++) sb.AppendLine(L.T($"===== Pagina {i + 1} =====")).AppendLine(_doc.PageText(i));
        File.WriteAllText(dst, sb.ToString());
        MainWindow.Notify(L.T("Testo esportato in ") + Path.GetFileName(dst));
    }

    // ------------------------------------------------------------------ salvataggio
    private bool Write(string path)
    {
        try
        {
            var bytes = _doc!.Save();
            if (_password != null) bytes = PdfTools.Encrypt(bytes, _password);
            var tmp = path + ".swk.tmp";
            File.WriteAllBytes(tmp, bytes);
            File.Move(tmp, path, true);
            _path = path;
            _dirty = false;
            UpdateUi();
            MainWindow.Notify(L.T("Salvato: ") + path);
            return true;
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Salvataggio non riuscito:\n") + ex.Message);
            return false;
        }
    }

    private void Save()
    {
        if (_doc == null) return;
        if (_path == null || !File.Exists(_path)) SaveAs();
        else Write(_path);
    }

    private bool SaveAs()
    {
        if (_doc == null) return false;
        var dst = Dlg.SaveFile(L.T("Salva PDF"), _path ?? "documento.pdf", "PDF|*.pdf", "pdf");
        if (dst == null) return false;
        if (!dst.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase)) dst += ".pdf";
        return Write(dst);
    }

    // ------------------------------------------------------------------ tastiera e miniature
    private void OnKey(object sender, KeyEventArgs e)
    {
        if (_doc == null || e.OriginalSource is System.Windows.Controls.TextBox) return;
        var ctrl = Keyboard.Modifiers == ModifierKeys.Control;
        if (ctrl && e.Key == Key.S) Save();
        else if (ctrl && e.Key == Key.Z) Undo();
        else if (ctrl && e.Key == Key.Y) Redo();
        else if (e.Key == Key.PageDown) GoTo(_cur + 1);
        else if (e.Key == Key.PageUp) GoTo(_cur - 1);
        else return;
        e.Handled = true;
    }

    private int IndexAt(Point p)
    {
        var el = Thumbs.InputHitTest(p) as DependencyObject;
        var item = el == null ? null : ItemsControl.ContainerFromElement(Thumbs, el) as ListBoxItem;
        return item == null ? -1 : Thumbs.ItemContainerGenerator.IndexFromContainer(item);
    }

    private void Thumbs_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragStart = e.GetPosition(Thumbs);
        _dragIndex = IndexAt(_dragStart.Value);
    }

    private void Thumbs_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragStart is not { } s || e.LeftButton != MouseButtonState.Pressed || _dragIndex < 0) return;
        if ((e.GetPosition(Thumbs) - s).Length < 8) return;
        var index = _dragIndex;
        _dragStart = null;
        DragDrop.DoDragDrop(Thumbs, new DataObject("swk-page", index), DragDropEffects.Move);
    }

    private void Thumbs_Drop(object sender, DragEventArgs e)
    {
        e.Handled = true;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files)
        {
            AddFiles(files);
            return;
        }
        if (_doc == null || e.Data.GetData("swk-page") is not int from) return;
        var to = IndexAt(e.GetPosition(Thumbs));
        if (to < 0) to = _doc.PageCount - 1;
        if (to == from) return;
        var order = Enumerable.Range(0, _doc.PageCount).ToList();
        order.RemoveAt(from);
        order.Insert(to, from);
        Safe(() =>
        {
            Snapshot();
            _doc.Reorder(order.ToArray());
            _cur = to;
            Changed(true);
        });
    }
}
