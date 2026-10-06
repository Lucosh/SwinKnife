using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Screenshot (area, finestra, schermo) con annotazioni e registrazione dello schermo in MP4 o GIF.</summary>
public sealed class CapturePage : UserControl, IToolPage
{
    private static readonly (string name, Color color)[] Palette =
    [
        (L.T("Rosso"), Color.FromRgb(0xE8, 0x2C, 0x2C)), (L.T("Giallo"), Color.FromRgb(0xFF, 0xD0, 0x00)), (L.T("Verde"), Color.FromRgb(0x22, 0xB5, 0x4A)),
        (L.T("Blu"), Color.FromRgb(0x1E, 0x7B, 0xFF)), (L.T("Nero"), Color.FromRgb(0x10, 0x10, 0x10)), (L.T("Bianco"), Colors.White),
    ];

    private readonly MainWindow _main;
    private readonly AnnotationEditor _editor = new();
    private readonly ComboBox _delay = new() { MinWidth = 120 };
    private readonly CheckBox _autoCopy = new() { Content = L.T("Copia subito negli appunti"), IsChecked = true, Margin = new Thickness(0, 10, 0, 0) };
    private readonly Border _editorPanel;
    private readonly FrameworkElement _placeholder;
    private readonly StackPanel _editBar = new() { Orientation = Orientation.Horizontal };
    private readonly Wpf.Ui.Controls.Button _undoBtn;
    private readonly TextBlock _imageInfo = new() { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(10, 0, 0, 0) };
    private readonly List<Border> _swatches = new();

    // ---- registrazione
    private readonly ComboBox _recArea = new() { Width = 230 };
    private readonly ComboBox _recFormat = new() { Width = 230 };
    private readonly ComboBox _recAudio = new() { Width = 230 };
    private readonly CheckBox _recCursor = new() { Content = L.T("Mostra il puntatore del mouse"), IsChecked = true };
    private readonly Wpf.Ui.Controls.Button _recBtn;
    private readonly StackPanel _recResult = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 10, 0, 0) };
    private ScreenRecorder? _recorder;
    private bool _audioLoaded;
    private string? _lastSaved;

    public CapturePage(MainWindow main)
    {
        _main = main;
        foreach (var s in new[] { L.T("Subito"), L.T("Tra 3 secondi"), L.T("Tra 5 secondi"), L.T("Tra 10 secondi") }) _delay.Items.Add(s);
        _delay.SelectedIndex = 0;
        _imageInfo.Style = (Style)Application.Current.FindResource("Hint");

        // ---- screenshot
        var shotButtons = Ui.Row(
            Ui.Btn(L.T("Area o finestra"), SymbolRegular.SelectObject24, (_, _) => _ = Screenshot(false), L.T("Trascina per un'area, clic per una finestra"), primary: true),
            Ui.Btn(L.T("Schermo intero"), SymbolRegular.FullScreenMaximize24, (_, _) => _ = Screenshot(true)));
        var shotPanel = new StackPanel();
        shotPanel.Children.Add(shotButtons);
        var delayRow = Ui.Row(Ui.Label(L.T("Ritardo")), new Border { Width = 10 }, _delay);
        delayRow.Margin = new Thickness(0, 10, 0, 0);
        shotPanel.Children.Add(delayRow);
        shotPanel.Children.Add(_autoCopy);
        var shotCard = Ui.Card(L.T("Screenshot"), L.T("Poi puoi aggiungere frecce, testo, evidenziare o nascondere dati sensibili."), shotPanel);
        shotCard.Margin = new Thickness(0, 0, 7, 14);

        // ---- registrazione
        foreach (var s in new[] { L.T("Schermo intero"), L.T("Scegli un'area o una finestra") }) _recArea.Items.Add(s);
        foreach (var s in new[] { L.T("Video MP4"), L.T("GIF animata (senza audio)") }) _recFormat.Items.Add(s);
        _recAudio.Items.Add(L.T("Nessun audio"));
        _recArea.SelectedIndex = _recFormat.SelectedIndex = _recAudio.SelectedIndex = 0;
        _recFormat.SelectionChanged += (_, _) => _recAudio.IsEnabled = _recFormat.SelectedIndex == 0;
        _recAudio.DropDownOpened += (_, _) => LoadAudioDevices();
        _recBtn = Ui.Btn(L.T("Avvia registrazione"), SymbolRegular.Record24, (_, _) => _ = Record(), primary: true);
        var recGrid = new Grid();
        recGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        recGrid.ColumnDefinitions.Add(new ColumnDefinition());
        void Row(int r, string label, FrameworkElement c)
        {
            recGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var l = Ui.Label(label);
            l.Margin = new Thickness(0, 4, 12, 4);
            Grid.SetRow(l, r);
            Grid.SetRow(c, r);
            Grid.SetColumn(c, 1);
            c.Margin = new Thickness(0, 4, 0, 4);
            c.HorizontalAlignment = HorizontalAlignment.Left;
            recGrid.Children.Add(l);
            recGrid.Children.Add(c);
        }
        Row(0, L.T("Cosa"), _recArea);
        Row(1, L.T("Formato"), _recFormat);
        Row(2, L.T("Audio"), _recAudio);
        var recPanel = new StackPanel();
        recPanel.Children.Add(recGrid);
        _recCursor.Margin = new Thickness(0, 6, 0, 10);
        recPanel.Children.Add(_recCursor);
        recPanel.Children.Add(_recBtn);
        recPanel.Children.Add(_recResult);
        var recCard = Ui.Card(L.T("Registra lo schermo"), L.T("Per tutorial e dimostrazioni. La barra di controllo non compare nel video."), recPanel);
        recCard.Margin = new Thickness(7, 0, 0, 14);

        var cards = new Grid();
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.ColumnDefinitions.Add(new ColumnDefinition());
        cards.Children.Add(shotCard);
        Grid.SetColumn(recCard, 1);
        cards.Children.Add(recCard);

        // ---- editor
        BuildEditBar();
        _undoBtn = (Wpf.Ui.Controls.Button)_editBar.Children.OfType<Wpf.Ui.Controls.Button>().First(b => (string?)b.Tag == "undo");
        var bar = new Border { Style = (Style)Application.Current.FindResource("Toolbar"), Child = new ScrollViewer { Content = _editBar, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled }, Margin = new Thickness(0, 0, 0, 8) };
        var editorDock = new DockPanel();
        DockPanel.SetDock(bar, Dock.Top);
        editorDock.Children.Add(bar);
        editorDock.Children.Add(new Border { Background = Ui.Res("CanvasBrush"), CornerRadius = new CornerRadius(8), Padding = new Thickness(10), Child = _editor });
        _editorPanel = new Border { Child = editorDock, Visibility = Visibility.Collapsed };
        _placeholder = Ui.Placeholder(SymbolRegular.Screenshot24, L.T("Fai uno screenshot per vederlo e annotarlo qui.\nPuoi anche trascinare un'immagine in questa pagina."), out _);
        _editor.Changed += (_, _) => UpdateEditor();

        var root = new Grid { Margin = new Thickness(24, 18, 24, 18) };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition());
        root.Children.Add(Ui.Header(L.T("Cattura schermo"), L.T("Screenshot con annotazioni e registrazione dello schermo in video o GIF.")));
        Grid.SetRow(cards, 1);
        root.Children.Add(cards);
        Grid.SetRow(_editorPanel, 2);
        Grid.SetRow(_placeholder, 2);
        root.Children.Add(_placeholder);
        root.Children.Add(_editorPanel);
        Content = root;
        UpdateEditor();
    }

    // ------------------------------------------------------------------ barra dell'editor
    private void BuildEditBar()
    {
        var tools = new (AnnotationTool tool, SymbolRegular icon, string label)[]
        {
            (AnnotationTool.Arrow, SymbolRegular.ArrowUpRight24, L.T("Freccia")),
            (AnnotationTool.Rectangle, SymbolRegular.RectangleLandscape24, L.T("Rettangolo")),
            (AnnotationTool.Ellipse, SymbolRegular.Circle24, L.T("Ellisse")),
            (AnnotationTool.Highlight, SymbolRegular.Highlight24, L.T("Evidenziatore")),
            (AnnotationTool.Pen, SymbolRegular.Pen24, L.T("Penna")),
            (AnnotationTool.Text, SymbolRegular.TextT24, L.T("Testo")),
            (AnnotationTool.Step, SymbolRegular.NumberCircle124, L.T("Numeri progressivi (1, 2, 3…)")),
            (AnnotationTool.Pixelate, SymbolRegular.Blur24, L.T("Sfoca/pixela (per nascondere dati sensibili)")),
            (AnnotationTool.Crop, SymbolRegular.Crop24, L.T("Ritaglia")),
        };
        foreach (var (tool, icon, label) in tools)
        {
            var rb = new RadioButton
            {
                Style = (Style)Application.Current.FindResource("ToolToggle"), Content = new SymbolIcon { Symbol = icon, FontSize = 18 },
                GroupName = "captool", ToolTip = label, IsChecked = tool == AnnotationTool.Arrow,
            };
            rb.Checked += (_, _) =>
            {
                _editor.CommitText();
                _editor.Tool = tool;
                if (tool == AnnotationTool.Highlight && _editor.Color == Palette[0].color) SetColor(Palette[1].color);
            };
            _editBar.Children.Add(rb);
        }
        _editBar.Children.Add(Ui.Separator());
        foreach (var (name, color) in Palette)
        {
            var sw = new Border
            {
                Width = 20, Height = 20, CornerRadius = new CornerRadius(10), Background = new SolidColorBrush(color), Margin = new Thickness(3, 0, 3, 0),
                BorderThickness = new Thickness(2), ToolTip = name, Cursor = System.Windows.Input.Cursors.Hand, Tag = color, VerticalAlignment = VerticalAlignment.Center,
            };
            sw.MouseLeftButtonUp += (_, _) => SetColor(color);
            _swatches.Add(sw);
            _editBar.Children.Add(sw);
        }
        _editBar.Children.Add(Ui.Separator());
        foreach (var (size, label) in new[] { (1, "S"), (2, "M"), (3, "L") })
        {
            var rb = new RadioButton
            {
                Style = (Style)Application.Current.FindResource("ToolToggle"), Content = new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Width = 14, TextAlignment = TextAlignment.Center },
                GroupName = "capsize", ToolTip = L.T("Spessore"), IsChecked = size == 2,
            };
            rb.Checked += (_, _) => _editor.Size = size;
            _editBar.Children.Add(rb);
        }
        _editBar.Children.Add(Ui.Separator());
        var undo = Ui.IconBtn(SymbolRegular.ArrowUndo24, L.T("Annulla (Ctrl+Z)"), (_, _) => _editor.Undo());
        undo.Tag = "undo";
        _editBar.Children.Add(undo);
        _editBar.Children.Add(Ui.Separator());
        _editBar.Children.Add(Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => CopyImage()));
        _editBar.Children.Add(Ui.Btn(L.T("Salva"), SymbolRegular.Save24, (_, _) => SaveImage(), primary: true));
        _editBar.Children.Add(Ui.IconBtn(SymbolRegular.ImageEdit24, L.T("Apri nell'editor foto (regolazioni e filtri)"), (_, _) => OpenIn("photo")));
        _editBar.Children.Add(Ui.IconBtn(SymbolRegular.Sparkle24, L.T("Chiedi a Gemini su questa schermata"), (_, _) => _ = AskGemini()));
        _editBar.Children.Add(_imageInfo);
        SetColor(Palette[0].color);
        KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Z && System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control && e.OriginalSource is not System.Windows.Controls.TextBox)
            {
                _editor.Undo();
                e.Handled = true;
            }
        };
    }

    private void SetColor(Color c)
    {
        _editor.Color = c;
        foreach (var sw in _swatches)
            sw.BorderBrush = (Color)sw.Tag == c ? Ui.Res("SwinAccentBrush") : new SolidColorBrush(Color.FromArgb(90, 128, 128, 128));
    }

    private void UpdateEditor()
    {
        var has = _editor.HasImage;
        _editorPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
        _placeholder.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        _undoBtn.IsEnabled = _editor.CanUndo;
        _imageInfo.Text = has ? L.T($"{_editor.PixelWidth} × {_editor.PixelHeight} px") : "";
    }

    // ------------------------------------------------------------------ screenshot
    private IntPtr MainHandle => new WindowInteropHelper(_main).Handle;

    /// <summary>Nasconde SwinKnife, aspetta il ritardo, cattura e lo rimostra.</summary>
    private async Task<(BitmapSource shot, Int32Rect virt)> GrabScreen(int delaySeconds)
    {
        var wasMax = _main.WindowState;
        _main.Hide();
        try
        {
            await Task.Delay(350 + delaySeconds * 1000); // tempo per l'animazione di chiusura
            var virt = ScreenCapture.VirtualScreen;
            return (ScreenCapture.Capture(virt), virt);
        }
        catch
        {
            ShowMain(wasMax);
            throw;
        }
    }

    private void ShowMain(WindowState state)
    {
        _main.Show();
        _main.WindowState = state == WindowState.Minimized ? WindowState.Normal : state;
        _main.Activate();
    }

    private async Task Screenshot(bool fullScreen)
    {
        var state = _main.WindowState;
        var delay = new[] { 0, 3, 5, 10 }[_delay.SelectedIndex];
        BitmapSource? result = null;
        try
        {
            var cursor = ScreenCapture.Cursor;
            var (shot, virt) = await GrabScreen(delay);
            Int32Rect? area;
            if (fullScreen)
            {
                var mon = ScreenCapture.MonitorAt(cursor);
                area = new Int32Rect(mon.X - virt.X, mon.Y - virt.Y, mon.Width, mon.Height);
            }
            else area = CaptureOverlay.Select(shot, virt, [MainHandle]);
            if (area is { } a) result = new CroppedBitmap(shot, a);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Cattura non riuscita:\n") + ex.Message);
        }
        finally
        {
            ShowMain(state);
        }
        if (result == null) return;
        result.Freeze();
        SetImage(result);
        if (_autoCopy.IsChecked == true)
        {
            CopyToClipboard(result);
            MainWindow.Notify(L.T("Screenshot copiato negli appunti: incollalo dove vuoi con Ctrl+V"));
        }
    }

    private void SetImage(BitmapSource img)
    {
        _editor.Load(img);
        _lastSaved = null;
        UpdateEditor();
    }

    public bool Accepts(string path) => Formats.KindOf(path) is FileKind.Image;

    public void OpenFile(string path)
    {
        try
        {
            using var img = ImageIO.Load(path);
            SetImage(ImageIO.ToBitmapSource(img));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile aprire l'immagine:\n") + ex.Message);
        }
    }

    private static void CopyToClipboard(BitmapSource img)
    {
        for (var i = 0; i < 5; i++)
        {
            try
            {
                Clipboard.SetImage(img);
                return;
            }
            catch (System.Runtime.InteropServices.COMException)
            {
                Thread.Sleep(100); // appunti occupati da un altro programma
            }
        }
    }

    private void CopyImage()
    {
        if (!_editor.HasImage) return;
        _editor.CommitText();
        CopyToClipboard(_editor.Render());
        MainWindow.Notify(L.T("Immagine copiata negli appunti"));
    }

    private static string ScreenshotsFolder()
    {
        var dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Screenshots");
        Directory.CreateDirectory(dir);
        return dir;
    }

    private static void WriteImage(BitmapSource img, string path)
    {
        BitmapEncoder enc = Path.GetExtension(path).ToLowerInvariant() is ".jpg" or ".jpeg"
            ? new JpegBitmapEncoder { QualityLevel = 92 }
            : new PngBitmapEncoder();
        // il JPEG non ha trasparenza: appiattisco su bianco
        BitmapSource frame = enc is JpegBitmapEncoder ? new FormatConvertedBitmap(img, PixelFormats.Bgr24, null, 0) : img;
        enc.Frames.Add(BitmapFrame.Create(frame));
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    private void SaveImage()
    {
        if (!_editor.HasImage) return;
        _editor.CommitText();
        // la prima volta propongo Immagini\Screenshots, poi l'ultima cartella usata
        var suggested = _lastSaved ?? Path.Combine(Settings.Get("dir.screenshot") ?? ScreenshotsFolder(), ScreenCapture.DefaultName("Screenshot", ".png"));
        var path = Dlg.SaveFile(L.T("Salva screenshot"), suggested, L.T("Immagine PNG|*.png|Immagine JPEG|*.jpg"), "screenshot");
        if (path == null) return;
        try
        {
            WriteImage(_editor.Render(), path);
            _lastSaved = path;
            MainWindow.Notify(L.T("Screenshot salvato: ") + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Salvataggio non riuscito:\n") + ex.Message);
        }
    }

    private string TempImage()
    {
        _editor.CommitText();
        Directory.CreateDirectory(AppInfo.TempDir);
        var path = Path.Combine(AppInfo.TempDir, ScreenCapture.DefaultName("Screenshot", ".png"));
        WriteImage(_editor.Render(), path);
        return path;
    }

    private void OpenIn(string page)
    {
        if (!_editor.HasImage) return;
        _main.OpenFile(TempImage(), page);
    }

    private async Task AskGemini()
    {
        if (!_editor.HasImage) return;
        var question = Dlg.Prompt(L.T("Chiedi a Gemini"), L.T("Cosa vuoi sapere su questa schermata?"), L.T("Spiegami cosa mostra questa schermata."), multiline: true);
        if (string.IsNullOrWhiteSpace(question)) return;
        var path = TempImage();
        await _main.Page<GeminiPage>("gemini").SendToGemini(question, path);
    }

    // ------------------------------------------------------------------ registrazione
    private void LoadAudioDevices()
    {
        if (_audioLoaded) return;
        _audioLoaded = true;
        var ff = Ffmpeg.Find();
        if (ff == null)
        {
            _recAudio.Items.Add(new ComboBoxItem { Content = L.T("(FFmpeg verrà scaricato al primo uso)"), IsEnabled = false });
            _audioLoaded = false;
            return;
        }
        try
        {
            foreach (var d in ScreenRecorder.AudioDevices(ff)) _recAudio.Items.Add(d);
        }
        catch { }
        if (_recAudio.Items.Count == 1) _recAudio.Items.Add(new ComboBoxItem { Content = L.T("(nessun microfono trovato)"), IsEnabled = false });
    }

    private static async Task<string?> EnsureFfmpeg()
    {
        var ff = Ffmpeg.Find();
        if (ff != null) return ff;
        if (!Dlg.Confirm(L.T("Per registrare lo schermo serve FFmpeg (circa 100 MB). Scaricarlo ora?"))) return null;
        try
        {
            return await Ffmpeg.DownloadAsync(new Progress<(double, string)>(p => MainWindow.Notify(p.Item2, 0)), CancellationToken.None);
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Download non riuscito:\n") + ex.Message);
            return null;
        }
    }

    private async Task Record()
    {
        if (_recorder != null) return;
        var ff = await EnsureFfmpeg();
        if (ff == null) return;
        var gif = _recFormat.SelectedIndex == 1;
        var audio = !gif && _recAudio.SelectedItem is string dev ? dev : null;
        var state = _main.WindowState;
        var cursor = ScreenCapture.Cursor;

        // area da registrare (pixel fisici assoluti)
        Int32Rect area;
        if (_recArea.SelectedIndex == 1)
        {
            var (shot, virt) = await GrabScreen(0);
            var sel = CaptureOverlay.Select(shot, virt, [MainHandle],
                L.T("Scegli cosa registrare: trascina per un'area, clic su una finestra, Invio per lo schermo intero · Esc: annulla"));
            if (sel is not { } s)
            {
                ShowMain(state);
                return;
            }
            area = new Int32Rect(s.X + virt.X, s.Y + virt.Y, s.Width, s.Height);
        }
        else
        {
            _main.Hide();
            area = ScreenCapture.MonitorAt(cursor);
        }
        if (area.Width < 16 || area.Height < 16)
        {
            ShowMain(state);
            return;
        }

        var videos = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyVideos), L.T("Registrazioni SwinKnife"));
        Directory.CreateDirectory(videos);
        Directory.CreateDirectory(AppInfo.TempDir);
        var final = Util.UniquePath(Path.Combine(videos, ScreenCapture.DefaultName("Registrazione", gif ? ".gif" : ".mp4")));
        var mp4 = gif ? Path.Combine(AppInfo.TempDir, Path.GetFileNameWithoutExtension(final) + ".mp4") : final;

        var frame = new RecordingFrame(area);
        frame.Show();
        await frame.CountdownAsync(3);
        var monitor = ScreenCapture.MonitorAt(new Point(area.X + area.Width / 2.0, area.Y + area.Height / 2.0));
        var recorder = new ScreenRecorder(ff, area, gif ? 15 : 30, _recCursor.IsChecked == true, audio, mp4);
        var bar = new RecorderBar(() => recorder.Elapsed, monitor);
        var finished = new TaskCompletionSource<bool>();
        bar.StopRequested += () => finished.TrySetResult(true);
        bar.CancelRequested += () => finished.TrySetResult(false);
        string? failure = null;
        recorder.Failed += msg =>
        {
            failure = msg;
            finished.TrySetResult(false);
        };
        _recorder = recorder;
        _recBtn.IsEnabled = false;
        var keep = false;
        try
        {
            recorder.Start();
            bar.Show();
            keep = await finished.Task;
            bar.Close();
            frame.Close();
            if (failure == null) await recorder.StopAsync();
        }
        catch (Exception ex)
        {
            failure ??= ex.Message;
        }
        finally
        {
            bar.Close();
            frame.Close();
            recorder.Dispose();
            _recorder = null;
            _recBtn.IsEnabled = true;
            ShowMain(state);
        }
        if (failure != null)
        {
            Dlg.Error(L.T("La registrazione si è interrotta:\n") + failure);
            TryDelete(mp4);
            return;
        }
        if (!keep)
        {
            TryDelete(mp4);
            MainWindow.Notify(L.T("Registrazione annullata"));
            return;
        }
        if (gif)
        {
            MainWindow.Notify(L.T("Creazione della GIF…"), 0);
            try
            {
                await Task.Run(() => ScreenRecorder.ToGif(ff, mp4, final, 12, 960, p => Dispatcher.InvokeAsync(() => MainWindow.Notify(L.T($"Creazione della GIF… {p:P0}"), 0)), CancellationToken.None));
            }
            catch (Exception ex)
            {
                Dlg.Error(L.T("Conversione in GIF non riuscita:\n") + ex.Message);
                return;
            }
            finally
            {
                TryDelete(mp4);
            }
        }
        ShowRecording(final);
        MainWindow.Notify(L.T($"Registrazione salvata: {Path.GetFileName(final)} ({Util.HumanSize(new FileInfo(final).Length)})"), 10);
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { }
    }

    private void ShowRecording(string path)
    {
        _recResult.Children.Clear();
        var name = Ui.Label(Path.GetFileName(path));
        name.Margin = new Thickness(0, 0, 10, 0);
        _recResult.Children.Add(name);
        _recResult.Children.Add(Ui.Btn(L.T("Guarda"), SymbolRegular.Play24, (_, _) => _main.OpenFile(path, "viewer")));
        _recResult.Children.Add(Ui.Btn(L.T("Mostra"), SymbolRegular.FolderOpen24, (_, _) => Util.Reveal(path), L.T("Mostra in Esplora risorse")));
        _recResult.Visibility = Visibility.Visible;
    }

    public bool CanClose() => _recorder == null || Dlg.Confirm(L.T("È in corso una registrazione dello schermo: interromperla?"));

    public void Shutdown() => _recorder?.Dispose();
}
