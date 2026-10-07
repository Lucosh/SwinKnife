using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SwinKnife.Core;
using Clipboard = System.Windows.Clipboard;

namespace SwinKnife.Controls;

/// <summary>Strumenti rapidi richiamabili con una scorciatoia da qualsiasi programma.</summary>
public static class QuickTools
{
    private static bool _busy;

    /// <summary>Notifica breve: dall'area di notifica se la finestra è nascosta, altrimenti nella barra di stato.</summary>
    public static void Toast(string title, string text)
    {
        if (MainWindow.Instance is { IsVisible: true, WindowState: not WindowState.Minimized } && MainWindow.Instance.IsActive)
            MainWindow.Notify($"{title}: {text}");
        else if (!Resident.Balloon(title, text)) MainWindow.Notify($"{title}: {text}");
    }

    private static (BitmapSource shot, Int32Rect virt) Grab()
    {
        var virt = ScreenCapture.VirtualScreen;
        return (ScreenCapture.Capture(virt), virt);
    }

    // ------------------------------------------------------------------ testo dallo schermo
    public static async Task CopyTextFromScreen()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (shot, virt) = Grab();
            var area = CaptureOverlay.Select(shot, virt, [],
                L.T("Trascina sull'area con il testo da copiare · clic su una finestra per tutta la finestra · Esc: annulla"));
            if (area is not { Width: > 4, Height: > 4 } a) return;
            var crop = new FormatConvertedBitmap(new CroppedBitmap(shot, a), PixelFormats.Bgra32, null, 0);
            // il testo piccolo dello schermo si riconosce meglio ingrandito
            BitmapSource src = crop;
            if (a.Height < 400) src = new TransformedBitmap(crop, new ScaleTransform(2, 2));
            var w = src.PixelWidth;
            var h = src.PixelHeight;
            var px = new byte[w * h * 4];
            src.CopyPixels(px, w * 4, 0);
            var (_, text) = await Ocr.Recognize(px, w, h);
            text = text.Trim();
            if (text.Length == 0)
            {
                Toast(L.T("Testo dallo schermo"), L.T("Nessun testo riconosciuto in quell'area."));
                return;
            }
            Clipboard.SetText(text);
            Toast(L.T("Testo copiato negli appunti"), text.Length > 120 ? text[..117] + "…" : text);
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "Testo dallo schermo");
            Toast(L.T("Testo dallo schermo"), ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    // ------------------------------------------------------------------ colori
    public static event Action? ColorsChanged;

    public static List<string> RecentColors() =>
        (Settings.Get("colors") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries).ToList();

    public static void AddColor(string hex)
    {
        var list = RecentColors();
        list.Remove(hex);
        list.Insert(0, hex);
        Settings.Set("colors", string.Join(';', list.Take(16)));
        ColorsChanged?.Invoke();
    }

    public static void PickColor()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (shot, virt) = Grab();
            var w = new ColorPickerOverlay(shot, virt);
            w.ShowDialog();
            if (w.Result is not { } c) return;
            var hex = $"#{c.R:X2}{c.G:X2}{c.B:X2}";
            Clipboard.SetText(hex);
            AddColor(hex);
            Toast(L.T("Colore copiato"), $"{hex}  ·  rgb({c.R}, {c.G}, {c.B})");
        }
        finally
        {
            _busy = false;
        }
    }

    // ------------------------------------------------------------------ righello
    public static void Ruler()
    {
        if (_busy) return;
        _busy = true;
        try
        {
            var (shot, virt) = Grab();
            new RulerOverlay(shot, virt).ShowDialog();
        }
        finally
        {
            _busy = false;
        }
    }

    // ------------------------------------------------------------------ sempre in primo piano
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern int GetWindowLong(IntPtr hwnd, int index);
    [DllImport("user32.dll")] private static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int max);

    /// <summary>Mette o toglie la finestra attiva "sempre in primo piano".</summary>
    public static void ToggleTopmost()
    {
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;
        var topmost = (GetWindowLong(hwnd, -20) & 0x8) != 0; // WS_EX_TOPMOST
        SetWindowPos(hwnd, new IntPtr(topmost ? -2 : -1), 0, 0, 0, 0, 0x1 | 0x2 | 0x10); // NOTOPMOST/TOPMOST, NOSIZE|NOMOVE|NOACTIVATE
        var title = new StringBuilder(256);
        GetWindowText(hwnd, title, title.Capacity);
        Toast(topmost ? L.T("Non più in primo piano") : L.T("Sempre in primo piano"), title.Length > 0 ? title.ToString() : L.T("Finestra attiva"));
    }
}

/// <summary>Base per gli strumenti a schermo intero sopra un'immagine "congelata" dello schermo.</summary>
public abstract class FrozenScreen : Window
{
    protected readonly BitmapSource Shot;
    protected readonly Int32Rect Virt;
    protected readonly Canvas Layer = new();
    private readonly byte[] _pixels;

    protected FrozenScreen(BitmapSource shot, Int32Rect virt)
    {
        Shot = shot;
        Virt = virt;
        var bgra = new FormatConvertedBitmap(shot, PixelFormats.Bgra32, null, 0);
        _pixels = new byte[bgra.PixelWidth * bgra.PixelHeight * 4];
        bgra.CopyPixels(_pixels, bgra.PixelWidth * 4, 0);
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        var img = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        var root = new Grid();
        root.Children.Add(img);
        root.Children.Add(Layer);
        Content = root;
        SourceInitialized += (_, _) => ScreenCapture.PlaceWindow(this, virt);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
        };
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
        };
        MouseRightButtonUp += (_, _) => Close();
    }

    /// <summary>Pixel dello screenshot sotto un punto della finestra.</summary>
    protected (int x, int y) PixelAt(Point dip)
    {
        var p = PointToScreen(dip);
        return ((int)Math.Clamp(p.X - Virt.X, 0, Shot.PixelWidth - 1), (int)Math.Clamp(p.Y - Virt.Y, 0, Shot.PixelHeight - 1));
    }

    protected Color ColorAt(int x, int y)
    {
        var i = (y * Shot.PixelWidth + x) * 4;
        return Color.FromRgb(_pixels[i + 2], _pixels[i + 1], _pixels[i]);
    }

    protected double DipPerPixel => 1 / (PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1);

    protected static Border Label(TextBlock text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(230, 24, 24, 24)), CornerRadius = new CornerRadius(6),
        Padding = new Thickness(8, 4, 8, 4), Child = text, IsHitTestVisible = false,
    };

    protected static Border Help(string text) => new()
    {
        Background = new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 8, 16, 8),
        IsHitTestVisible = false, Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 14 },
    };
}

/// <summary>Contagocce: lente d'ingrandimento sotto il mouse, clic per prendere il colore.</summary>
public sealed class ColorPickerOverlay : FrozenScreen
{
    private const int Cells = 11, Zoom = 12;
    private readonly Image _lens = new() { Width = Cells * Zoom, Height = Cells * Zoom, Stretch = Stretch.Fill };
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
    private readonly Border _swatch = new() { Width = 18, Height = 18, CornerRadius = new CornerRadius(3), BorderBrush = Brushes.White, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
    private readonly Border _box;
    private (int x, int y) _px;

    public Color? Result { get; private set; }

    public ColorPickerOverlay(BitmapSource shot, Int32Rect virt) : base(shot, virt)
    {
        RenderOptions.SetBitmapScalingMode(_lens, BitmapScalingMode.NearestNeighbor);
        var grid = new Grid();
        grid.Children.Add(_lens);
        // mirino sul pixel centrale
        grid.Children.Add(new Rectangle { Width = Zoom + 2, Height = Zoom + 2, Stroke = Brushes.White, StrokeThickness = 2, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center });
        var info = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
        info.Children.Add(_swatch);
        info.Children.Add(_text);
        var panel = new StackPanel();
        panel.Children.Add(new Border { BorderBrush = Brushes.White, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(4), Child = grid, ClipToBounds = true });
        panel.Children.Add(info);
        _box = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 24, 24, 24)), CornerRadius = new CornerRadius(8), Padding = new Thickness(8),
            Child = panel, IsHitTestVisible = false,
        };
        Layer.Children.Add(_box);
        var help = Help(L.T("Clic: copia il colore · frecce: sposta di un pixel · Esc: annulla"));
        Canvas.SetTop(help, 24);
        Layer.Children.Add(help);
        Loaded += (_, _) => Canvas.SetLeft(help, (ActualWidth - help.ActualWidth) / 2);
        MouseMove += (_, e) => Update(PixelAt(e.GetPosition(this)));
        MouseLeftButtonUp += (_, _) => Pick();
        KeyDown += (_, e) =>
        {
            var (dx, dy) = e.Key switch { Key.Left => (-1, 0), Key.Right => (1, 0), Key.Up => (0, -1), Key.Down => (0, 1), _ => (0, 0) };
            if (dx != 0 || dy != 0) Update((Math.Clamp(_px.x + dx, 0, Shot.PixelWidth - 1), Math.Clamp(_px.y + dy, 0, Shot.PixelHeight - 1)));
            else if (e.Key is Key.Enter or Key.Space) Pick();
        };
        Loaded += (_, _) => Update(PixelAt(Mouse.GetPosition(this)));
    }

    private void Pick()
    {
        Result = ColorAt(_px.x, _px.y);
        Close();
    }

    private void Update((int x, int y) px)
    {
        _px = px;
        var half = Cells / 2;
        var x0 = Math.Clamp(px.x - half, 0, Shot.PixelWidth - Cells);
        var y0 = Math.Clamp(px.y - half, 0, Shot.PixelHeight - Cells);
        _lens.Source = new CroppedBitmap(Shot, new Int32Rect(x0, y0, Cells, Cells));
        var c = ColorAt(px.x, px.y);
        _swatch.Background = new SolidColorBrush(c);
        _text.Text = $"#{c.R:X2}{c.G:X2}{c.B:X2}   rgb({c.R}, {c.G}, {c.B})";
        // la lente segue il mouse, spostandosi dall'altra parte vicino ai bordi
        var dip = PointFromScreen(new Point(px.x + Virt.X, px.y + Virt.Y));
        double mx = dip.X, my = dip.Y;
        _box.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var bw = _box.DesiredSize.Width;
        var bh = _box.DesiredSize.Height;
        var left = mx + 24 + bw > ActualWidth ? mx - 24 - bw : mx + 24;
        var top = my + 24 + bh > ActualHeight ? my - 24 - bh : my + 24;
        Canvas.SetLeft(_box, left);
        Canvas.SetTop(_box, top);
    }
}

/// <summary>Righello: trascina per misurare in pixel; le linee guida mostrano la posizione del mouse.</summary>
public sealed class RulerOverlay : FrozenScreen
{
    private readonly Line _h = Guide(), _v = Guide();
    private readonly Rectangle _rect = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), StrokeThickness = 1.5, Fill = new SolidColorBrush(Color.FromArgb(40, 0xE5, 0x48, 0x4D)), IsHitTestVisible = false };
    private readonly Line _diag = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xFF, 0xD0, 0x60)), StrokeThickness = 1, StrokeDashArray = [4, 3], IsHitTestVisible = false };
    private readonly TextBlock _text = new() { Foreground = Brushes.White, FontFamily = new FontFamily("Consolas"), FontSize = 13 };
    private readonly Border _label;
    private Point? _start;
    private (int x, int y) _a, _b;

    public RulerOverlay(BitmapSource shot, Int32Rect virt) : base(shot, virt)
    {
        _label = Label(_text);
        Layer.Children.Add(_h);
        Layer.Children.Add(_v);
        Layer.Children.Add(_rect);
        Layer.Children.Add(_diag);
        Layer.Children.Add(_label);
        var help = Help(L.T("Trascina per misurare · Ctrl+C: copia le misure · Esc: chiudi"));
        Canvas.SetTop(help, 24);
        Layer.Children.Add(help);
        Loaded += (_, _) => Canvas.SetLeft(help, (ActualWidth - help.ActualWidth) / 2);
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(this);
            _a = PixelAt(_start.Value);
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, _) =>
        {
            _start = null;
            ReleaseMouseCapture();
        };
        MouseMove += (_, e) => Move(e.GetPosition(this));
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.C && Keyboard.Modifiers.HasFlag(ModifierKeys.Control))
            {
                Clipboard.SetText(Measure());
                QuickTools.Toast(L.T("Misure copiate"), Measure());
            }
        };
    }

    private static Line Guide() => new() { Stroke = new SolidColorBrush(Color.FromArgb(160, 0, 200, 255)), StrokeThickness = 1, IsHitTestVisible = false };

    private string Measure()
    {
        int w = Math.Abs(_b.x - _a.x) + 1, h = Math.Abs(_b.y - _a.y) + 1;
        return $"{w} × {h} px  ·  {Math.Sqrt((w - 1) * (w - 1) + (h - 1) * (h - 1)):0} px";
    }

    private void Move(Point p)
    {
        _h.X1 = 0; _h.X2 = ActualWidth; _h.Y1 = _h.Y2 = p.Y;
        _v.Y1 = 0; _v.Y2 = ActualHeight; _v.X1 = _v.X2 = p.X;
        var px = PixelAt(p);
        if (_start is { } s)
        {
            _b = px;
            var r = new Rect(s, p);
            Canvas.SetLeft(_rect, r.X);
            Canvas.SetTop(_rect, r.Y);
            _rect.Width = r.Width;
            _rect.Height = r.Height;
            _diag.X1 = s.X; _diag.Y1 = s.Y; _diag.X2 = p.X; _diag.Y2 = p.Y;
            _text.Text = Measure();
        }
        else if (_rect.Width == 0) _text.Text = $"x {px.x + Virt.X}  y {px.y + Virt.Y}";
        Canvas.SetLeft(_label, p.X + 16);
        Canvas.SetTop(_label, p.Y + 16);
    }
}
