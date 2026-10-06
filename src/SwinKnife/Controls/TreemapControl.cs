using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using SwinKnife.Core;

namespace SwinKnife.Controls;

/// <summary>Mostra la treemap, gestisce passaggio del mouse, clic, doppio clic ed evidenziazione per estensione.</summary>
public sealed class TreemapControl : FrameworkElement
{
    private DiskScan.Treemap? _map;
    private BitmapSource? _bitmap;
    private BitmapSource? _overlay;
    private double _dpi = 1;
    private readonly DispatcherTimer _resize = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public DiskNode? Selected { get; set; }
    public event Action<DiskNode?>? Hovered;
    public event Action<DiskNode>? NodeClicked;
    public event Action<DiskNode>? NodeDoubleClicked;
    public event Action? NeedsRender;

    public TreemapControl()
    {
        ClipToBounds = true;
        _resize.Tick += (_, _) =>
        {
            _resize.Stop();
            NeedsRender?.Invoke();
        };
        SizeChanged += (_, _) => _resize.Start();
    }

    public (int w, int h, double dpi) PixelSize()
    {
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        return (Math.Max(10, (int)(ActualWidth * dpi)), Math.Max(10, (int)(ActualHeight * dpi)), dpi);
    }

    public void SetMap(DiskScan.Treemap? map, double dpi)
    {
        _map = map;
        _dpi = dpi;
        _overlay = null;
        if (map != null)
        {
            var b = BitmapSource.Create(map.Width, map.Height, 96 * dpi, 96 * dpi, PixelFormats.Bgra32, null, map.Pixels, map.Width * 4);
            b.Freeze();
            _bitmap = b;
        }
        else _bitmap = null;
        InvalidateVisual();
    }

    /// <summary>Scurisce tutto tranne i file dell'estensione con l'indice colore dato (null = nessuna evidenziazione).</summary>
    public void HighlightExtension(int? index)
    {
        _overlay = null;
        if (_map != null && index != null)
        {
            var px = new byte[_map.Width * _map.Height * 4];
            for (var i = 0; i < _map.ExtIds.Length; i++)
                if (_map.ExtIds[i] != index) px[i * 4 + 3] = 175;
            var b = BitmapSource.Create(_map.Width, _map.Height, 96 * _dpi, 96 * _dpi, PixelFormats.Bgra32, null, px, _map.Width * 4);
            b.Freeze();
            _overlay = b;
        }
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle((Brush)Application.Current.FindResource("CanvasBrush"), null, full);
        if (_bitmap == null || _map == null)
        {
            var ft = new FormattedText(L.T("La mappa apparirà al termine della scansione"), CultureInfo.CurrentCulture, FlowDirection.LeftToRight,
                new Typeface("Segoe UI"), 14, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
            return;
        }
        dc.DrawImage(_bitmap, new Rect(0, 0, _map.Width / _dpi, _map.Height / _dpi));
        if (_overlay != null) dc.DrawImage(_overlay, new Rect(0, 0, _map.Width / _dpi, _map.Height / _dpi));
        var n = Selected;
        while (n != null && !_map.Rects.ContainsKey(n)) n = n.Parent;
        if (n != null)
        {
            var (x, y, w, h) = _map.Rects[n];
            dc.DrawRectangle(null, new Pen(Brushes.White, 2), new Rect(x / _dpi + 1, y / _dpi + 1, Math.Max(1, w / _dpi - 2), Math.Max(1, h / _dpi - 2)));
        }
    }

    private DiskNode? NodeAt(Point p)
    {
        if (_map == null) return null;
        var x = (int)(p.X * _dpi);
        var y = (int)(p.Y * _dpi);
        if (x < 0 || y < 0 || x >= _map.Width || y >= _map.Height) return null;
        var i = _map.Ids[y * _map.Width + x];
        return i >= 0 ? _map.Nodes[i] : null;
    }

    protected override void OnMouseMove(MouseEventArgs e) => Hovered?.Invoke(NodeAt(e.GetPosition(this)));

    protected override void OnMouseLeave(MouseEventArgs e) => Hovered?.Invoke(null);

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        var node = NodeAt(e.GetPosition(this));
        if (node == null) return;
        if (e.ClickCount == 2) NodeDoubleClicked?.Invoke(node);
        else NodeClicked?.Invoke(node);
    }
}
