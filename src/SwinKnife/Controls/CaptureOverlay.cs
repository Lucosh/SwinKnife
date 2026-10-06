using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using SwinKnife.Core;
using Path = System.Windows.Shapes.Path;

namespace SwinKnife.Controls;

/// <summary>
/// Schermo "congelato" a tutto schermo su cui scegliere cosa catturare:
/// trascinando si seleziona un'area, con un clic la finestra sotto il mouse, con Invio lo schermo intero.
/// </summary>
public sealed class CaptureOverlay : Window
{
    private readonly Int32Rect _virt;
    private readonly List<ScreenCapture.WindowInfo> _windows;
    private readonly Canvas _canvas = new();
    private readonly Path _mask = new() { Fill = new SolidColorBrush(Color.FromArgb(110, 0, 0, 0)), IsHitTestVisible = false };
    private readonly Rectangle _sel = new() { Stroke = new SolidColorBrush(Color.FromRgb(0xE8, 0x3A, 0x3A)), StrokeThickness = 2, IsHitTestVisible = false };
    private readonly Border _sizeLabel;
    private readonly TextBlock _sizeText = new() { Foreground = Brushes.White, FontSize = 12 };
    private Point? _start;
    private bool _dragging;
    private Rect _current;               // selezione in DIP
    private ScreenCapture.WindowInfo? _hover;

    /// <summary>Rettangolo scelto, in pixel rispetto all'angolo dello schermo virtuale.</summary>
    public Int32Rect? Result { get; private set; }

    private CaptureOverlay(BitmapSource shot, Int32Rect virt, List<ScreenCapture.WindowInfo> windows, string hint)
    {
        _virt = virt;
        _windows = windows;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Brushes.Black;
        Cursor = Cursors.Cross;
        Left = virt.X;
        Top = virt.Y;
        Width = virt.Width;
        Height = virt.Height;

        var img = new Image { Source = shot, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        _sizeLabel = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(200, 20, 20, 20)), CornerRadius = new CornerRadius(4), Padding = new Thickness(6, 2, 6, 2),
            Child = _sizeText, IsHitTestVisible = false, Visibility = Visibility.Collapsed,
        };
        var help = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(220, 32, 32, 32)), CornerRadius = new CornerRadius(8), Padding = new Thickness(16, 8, 16, 8),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 24, 0, 0),
            IsHitTestVisible = false,
            Child = new TextBlock { Text = hint, Foreground = Brushes.White, FontSize = 14 },
        };
        _canvas.Children.Add(_mask);
        _canvas.Children.Add(_sel);
        _canvas.Children.Add(_sizeLabel);
        var root = new Grid();
        root.Children.Add(img);
        root.Children.Add(_canvas);
        root.Children.Add(help);
        Content = root;

        SourceInitialized += (_, _) => ScreenCapture.PlaceWindow(this, virt);
        Loaded += (_, _) =>
        {
            Activate();
            Focus();
            UpdateHover(Mouse.GetPosition(this));
        };
        MouseMove += (_, e) => OnMove(e.GetPosition(this), e.LeftButton == MouseButtonState.Pressed);
        MouseLeftButtonDown += (_, e) =>
        {
            _start = e.GetPosition(this);
            CaptureMouse();
        };
        MouseLeftButtonUp += (_, e) =>
        {
            ReleaseMouseCapture();
            if (_dragging) Finish(Physical(_current));
            else if (_hover != null) Finish(_hover.Bounds);
            _start = null;
            _dragging = false;
        };
        MouseRightButtonUp += (_, _) => Close();
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key is Key.Enter or Key.Space)
                Finish(ScreenCapture.MonitorAt(ScreenCapture.Cursor));
        };
    }

    /// <summary>Mostra la selezione e restituisce il rettangolo scelto (null se annullato).</summary>
    public static Int32Rect? Select(BitmapSource shot, Int32Rect virt, IEnumerable<IntPtr> exclude,
        string? hint = null)
    {
        hint ??= L.T("Trascina per scegliere un'area · clic su una finestra per catturarla · Invio: schermo intero · Esc: annulla");
        var overlay = new CaptureOverlay(shot, virt, ScreenCapture.Windows(exclude), hint);
        overlay.ShowDialog();
        return overlay.Result;
    }

    private Point ToDip(double x, double y) => PointFromScreen(new Point(x, y));

    private Int32Rect Physical(Rect dip)
    {
        var a = PointToScreen(dip.TopLeft);
        var b = PointToScreen(dip.BottomRight);
        return new Int32Rect((int)Math.Round(a.X), (int)Math.Round(a.Y), (int)Math.Round(b.X - a.X), (int)Math.Round(b.Y - a.Y));
    }

    private Rect Dip(Int32Rect r) => new(ToDip(r.X, r.Y), ToDip(r.X + r.Width, r.Y + r.Height));

    private void OnMove(Point p, bool pressed)
    {
        if (pressed && _start is { } s)
        {
            if (!_dragging && (p - s).Length < 5) return;
            _dragging = true;
            _hover = null;
            Show(new Rect(s, p));
        }
        else UpdateHover(p);
    }

    private void UpdateHover(Point dip)
    {
        var phys = PointToScreen(dip);
        _hover = _windows.FirstOrDefault(w => phys.X >= w.Bounds.X && phys.X < w.Bounds.X + w.Bounds.Width &&
                                              phys.Y >= w.Bounds.Y && phys.Y < w.Bounds.Y + w.Bounds.Height);
        if (_hover != null) Show(Dip(_hover.Bounds));
        else Show(Rect.Empty);
    }

    private void Show(Rect r)
    {
        _current = r;
        var full = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        if (r.IsEmpty || r.Width < 1 || r.Height < 1)
        {
            _mask.Data = full;
            _sel.Visibility = _sizeLabel.Visibility = Visibility.Collapsed;
            return;
        }
        _mask.Data = new CombinedGeometry(GeometryCombineMode.Exclude, full, new RectangleGeometry(r));
        _sel.Visibility = _sizeLabel.Visibility = Visibility.Visible;
        Canvas.SetLeft(_sel, r.X);
        Canvas.SetTop(_sel, r.Y);
        _sel.Width = r.Width;
        _sel.Height = r.Height;
        var phys = Physical(r);
        _sizeText.Text = $"{phys.Width} × {phys.Height}";
        Canvas.SetLeft(_sizeLabel, r.X);
        Canvas.SetTop(_sizeLabel, r.Y > 26 ? r.Y - 24 : r.Bottom + 4);
    }

    private void Finish(Int32Rect phys)
    {
        // limito allo schermo virtuale e porto le coordinate rispetto al suo angolo
        var x1 = Math.Clamp(phys.X, _virt.X, _virt.X + _virt.Width);
        var y1 = Math.Clamp(phys.Y, _virt.Y, _virt.Y + _virt.Height);
        var x2 = Math.Clamp(phys.X + phys.Width, _virt.X, _virt.X + _virt.Width);
        var y2 = Math.Clamp(phys.Y + phys.Height, _virt.Y, _virt.Y + _virt.Height);
        if (x2 - x1 < 4 || y2 - y1 < 4) return;
        Result = new Int32Rect(x1 - _virt.X, y1 - _virt.Y, x2 - x1, y2 - y1);
        Close();
    }
}
