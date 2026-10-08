using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using Image = System.Windows.Controls.Image;

namespace SwinKnife.Pages;

/// <summary>Disegna e scrive sopra un'istantanea dello schermo (per spiegazioni, presentazioni, appunti).</summary>
public sealed class AnnotatePage : UserControl, IToolPage
{
    public AnnotatePage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Come funziona"),
            Message = L.T("Scatta un'istantanea dello schermo e la apre a tutto schermo con una penna: puoi disegnare frecce e cerchi, evidenziare e scrivere. Poi copi o salvi l'immagine. Premi ESC per uscire."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        var start = Ui.Btn(L.T("Avvia annotazioni sullo schermo"), SymbolRegular.InkingTool24, (_, _) => Start(), primary: true);
        start.Padding = new Thickness(18, 12, 18, 12);
        start.FontSize = 15;

        var card = Ui.Card(L.T("Lavagna sullo schermo"),
            L.T("Verrà catturato lo schermo dove si trova il mouse."),
            new StackPanel { Children = { start } });

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Annotazioni schermo"), L.T("Disegna e scrivi sopra lo schermo, poi salva o copia.")),
            info, card);
    }

    private void Start()
    {
        var owner = Application.Current?.MainWindow;
        try
        {
            if (owner != null) owner.WindowState = WindowState.Minimized;
            // piccola attesa visiva lasciata al sistema: cattura al prossimo ciclo UI
            Dispatcher.BeginInvoke(new Action(() =>
            {
                var rect = ScreenCapture.MonitorAt(ScreenCapture.Cursor);
                var shot = ScreenCapture.Capture(rect);
                var overlay = new AnnotateOverlay(shot, rect);
                overlay.Closed += (_, _) => { if (owner != null) owner.WindowState = WindowState.Normal; };
                overlay.Show();
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }
        catch (Exception ex)
        {
            if (owner != null) owner.WindowState = WindowState.Normal;
            Dlg.Error(ex.Message);
        }
    }
}

/// <summary>Finestra a tutto schermo con lo screenshot sotto e un livello di disegno (InkCanvas) sopra.</summary>
internal sealed class AnnotateOverlay : Window
{
    private readonly InkCanvas _ink = new() { Background = Brushes.Transparent };
    private readonly Grid _root = new();
    private readonly Int32Rect _rect;
    private double _penSize = 4;
    private Color _penColor = Colors.Red;

    public AnnotateOverlay(BitmapSource shot, Int32Rect rect)
    {
        _rect = rect;
        Title = "SwinKnife";
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        WindowStartupLocation = WindowStartupLocation.Manual;
        AllowsTransparency = false;
        Background = Brushes.Black;

        _root.Children.Add(new Image { Source = shot, Stretch = Stretch.Fill });
        SetPen(false);
        _root.Children.Add(_ink);
        _root.Children.Add(Toolbar());
        Content = _root;

        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control) Undo();
        };
        SourceInitialized += (_, _) => ScreenCapture.PlaceWindow(this, _rect);
        Loaded += (_, _) => { ScreenCapture.PlaceWindow(this, _rect); Activate(); _ink.Focus(); };
    }

    private void SetPen(bool eraser)
    {
        if (eraser) { _ink.EditingMode = InkCanvasEditingMode.EraseByStroke; return; }
        _ink.EditingMode = InkCanvasEditingMode.Ink;
        _ink.DefaultDrawingAttributes = new DrawingAttributes
        {
            Color = _penColor, Width = _penSize, Height = _penSize, FitToCurve = true,
            StylusTip = StylusTip.Ellipse,
        };
    }

    private UIElement Toolbar()
    {
        var bar = new StackPanel
        {
            Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 16, 0, 0),
        };

        void AddColor(Color c)
        {
            var b = new Button
            {
                Width = 28, Height = 28, Margin = new Thickness(3, 0, 3, 0), Padding = new Thickness(0),
                Background = new SolidColorBrush(c), Content = "", ToolTip = L.T("Colore"),
            };
            b.Click += (_, _) => { _penColor = c; SetPen(false); };
            bar.Children.Add(b);
        }

        foreach (var c in new[] { Colors.Red, Colors.Yellow, (Color)ColorConverter.ConvertFromString("#4CAF50"), Colors.DodgerBlue, Colors.Black, Colors.White })
            AddColor(c);

        bar.Children.Add(Sep());
        bar.Children.Add(SizeBtn(L.T("Sottile"), 3));
        bar.Children.Add(SizeBtn(L.T("Media"), 6));
        bar.Children.Add(SizeBtn(L.T("Spessa"), 12));
        bar.Children.Add(Sep());
        bar.Children.Add(Ui.Btn(L.T("Gomma"), SymbolRegular.Eraser24, (_, _) => SetPen(true)));
        bar.Children.Add(Ui.Btn(L.T("Annulla"), SymbolRegular.ArrowUndo24, (_, _) => Undo()));
        bar.Children.Add(Ui.Btn(L.T("Pulisci"), SymbolRegular.Delete24, (_, _) => _ink.Strokes.Clear()));
        bar.Children.Add(Sep());
        bar.Children.Add(Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => Copy()));
        bar.Children.Add(Ui.Btn(L.T("Salva"), SymbolRegular.Save24, (_, _) => Save(), primary: true));
        bar.Children.Add(Ui.Btn(L.T("Chiudi"), SymbolRegular.Dismiss24, (_, _) => Close()));

        return new Border
        {
            Child = bar, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10, 8, 10, 8), CornerRadius = new CornerRadius(10),
            Background = new SolidColorBrush(Color.FromArgb(0xE6, 0x20, 0x20, 0x20)),
        };
    }

    private Button SizeBtn(string label, double size)
    {
        var b = Ui.Btn(label, null, (_, _) => { _penSize = size; SetPen(false); });
        return b;
    }

    private static Border Sep() => new() { Width = 1, Margin = new Thickness(6, 2, 6, 2), Background = new SolidColorBrush(Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF)) };

    private void Undo()
    {
        if (_ink.Strokes.Count > 0) _ink.Strokes.RemoveAt(_ink.Strokes.Count - 1);
    }

    private RenderTargetBitmap Flatten()
    {
        _root.UpdateLayout();
        var dpi = VisualTreeHelper.GetDpi(_root);
        var w = Math.Max(1, (int)Math.Round(_root.ActualWidth * dpi.DpiScaleX));
        var h = Math.Max(1, (int)Math.Round(_root.ActualHeight * dpi.DpiScaleY));
        var rtb = new RenderTargetBitmap(w, h, dpi.PixelsPerInchX, dpi.PixelsPerInchY, PixelFormats.Pbgra32);
        rtb.Render(_root);
        return rtb;
    }

    private void Copy()
    {
        try { Clipboard.SetImage(Flatten()); MainWindow.Notify(L.T("Immagine copiata negli appunti.")); Close(); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void Save()
    {
        var dst = Dlg.SaveFile(L.T("Salva l'immagine annotata"),
            ScreenCapture.DefaultName(L.T("Annotazione"), ".png"), L.T("Immagine PNG|*.png"), "annotate");
        if (dst == null) return;
        try
        {
            var enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(Flatten()));
            using (var fs = File.Create(dst)) enc.Save(fs);
            MainWindow.Notify(L.T("Immagine salvata."), 8);
            Close();
            Util.Reveal(dst);
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }
}
