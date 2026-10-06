using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwinKnife.Controls;

/// <summary>Mostra la foto adattata alla finestra; in modalità ritaglio disegna riquadro, griglia e maniglie.</summary>
public sealed class PhotoCanvas : FrameworkElement
{
    private const double MinCrop = 24;
    private BitmapSource? _image;
    private (string mode, Point start, Rect startRect)? _drag;

    public bool CropMode { get; set; }
    public Rect Crop { get; set; } = new(0, 0, 1, 1);
    public double? Aspect { get; set; }

    public event Action? CropCommitted;

    public PhotoCanvas()
    {
        ClipToBounds = true;
        Focusable = true;
    }

    public BitmapSource? Image
    {
        get => _image;
        set
        {
            _image = value;
            InvalidateVisual();
        }
    }

    public Rect ImageRect()
    {
        if (_image == null) return Rect.Empty;
        var m = CropMode ? 44.0 : 16.0;
        var aw = ActualWidth - 2 * m;
        var ah = ActualHeight - 2 * m;
        var s = Math.Min(aw / _image.PixelWidth, ah / _image.PixelHeight);
        if (s <= 0) return Rect.Empty;
        var w = _image.PixelWidth * s;
        var h = _image.PixelHeight * s;
        return new Rect((ActualWidth - w) / 2, (ActualHeight - h) / 2, w, h);
    }

    private Rect CropRect()
    {
        var ir = ImageRect();
        return new Rect(ir.X + Crop.X * ir.Width, ir.Y + Crop.Y * ir.Height, Crop.Width * ir.Width, Crop.Height * ir.Height);
    }

    private void SetFromRect(Rect r)
    {
        var ir = ImageRect();
        if (ir.Width <= 0) return;
        var x0 = Math.Max(0, (r.Left - ir.X) / ir.Width);
        var y0 = Math.Max(0, (r.Top - ir.Y) / ir.Height);
        var x1 = Math.Min(1, (r.Right - ir.X) / ir.Width);
        var y1 = Math.Min(1, (r.Bottom - ir.Y) / ir.Height);
        Crop = new Rect(x0, y0, Math.Max(0.001, x1 - x0), Math.Max(0.001, y1 - y0));
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x10)), null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_image == null)
        {
            var ft = new FormattedText(L.T("Apri una foto (o trascinala qui) per modificarla"), CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, new Typeface("Segoe UI"), 15, Brushes.Gray, VisualTreeHelper.GetDpi(this).PixelsPerDip);
            dc.DrawText(ft, new Point((ActualWidth - ft.Width) / 2, (ActualHeight - ft.Height) / 2));
            return;
        }
        var ir = ImageRect();
        RenderOptions.SetBitmapScalingMode(this, BitmapScalingMode.HighQuality);
        dc.DrawImage(_image, ir);
        if (!CropMode) return;
        var cr = CropRect();
        var dim = new SolidColorBrush(Color.FromArgb(165, 0, 0, 0));
        dc.DrawRectangle(dim, null, new Rect(ir.Left, ir.Top, ir.Width, Math.Max(0, cr.Top - ir.Top)));
        dc.DrawRectangle(dim, null, new Rect(ir.Left, cr.Bottom, ir.Width, Math.Max(0, ir.Bottom - cr.Bottom)));
        dc.DrawRectangle(dim, null, new Rect(ir.Left, cr.Top, Math.Max(0, cr.Left - ir.Left), cr.Height));
        dc.DrawRectangle(dim, null, new Rect(cr.Right, cr.Top, Math.Max(0, ir.Right - cr.Right), cr.Height));
        var grid = new Pen(new SolidColorBrush(Color.FromArgb(110, 255, 255, 255)), 1);
        for (var k = 1; k <= 2; k++)
        {
            var x = cr.Left + cr.Width * k / 3;
            var y = cr.Top + cr.Height * k / 3;
            dc.DrawLine(grid, new Point(x, cr.Top), new Point(x, cr.Bottom));
            dc.DrawLine(grid, new Point(cr.Left, y), new Point(cr.Right, y));
        }
        dc.DrawRectangle(null, new Pen(Brushes.White, 1.2), cr);
        var handle = new Pen(Brushes.White, 4) { StartLineCap = PenLineCap.Square, EndLineCap = PenLineCap.Square };
        const double arm = 18;
        foreach (var (cx, cy, sx, sy) in new[] { (cr.Left, cr.Top, 1, 1), (cr.Right, cr.Top, -1, 1), (cr.Left, cr.Bottom, 1, -1), (cr.Right, cr.Bottom, -1, -1) })
        {
            dc.DrawLine(handle, new Point(cx, cy), new Point(cx + arm * sx, cy));
            dc.DrawLine(handle, new Point(cx, cy), new Point(cx, cy + arm * sy));
        }
    }

    private string? Hit(Point p)
    {
        var cr = CropRect();
        static bool Near(double a, double b) => Math.Abs(a - b) <= 16;
        bool l = Near(p.X, cr.Left), r = Near(p.X, cr.Right), t = Near(p.Y, cr.Top), b = Near(p.Y, cr.Bottom);
        var inX = p.X >= cr.Left - 16 && p.X <= cr.Right + 16;
        var inY = p.Y >= cr.Top - 16 && p.Y <= cr.Bottom + 16;
        if ((t || b) && (l || r)) return (t ? "t" : "b") + (l ? "l" : "r");
        if ((l || r) && inY) return l ? "l" : "r";
        if ((t || b) && inX) return t ? "t" : "b";
        return cr.Contains(p) ? "move" : null;
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        if (!CropMode || _image == null) return;
        var p = e.GetPosition(this);
        var mode = Hit(p);
        if (mode == null) return;
        _drag = (mode, p, CropRect());
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (!CropMode || _image == null) return;
        var p = e.GetPosition(this);
        if (_drag is not { } drag)
        {
            Cursor = Hit(p) switch
            {
                "move" => Cursors.SizeAll,
                "l" or "r" => Cursors.SizeWE,
                "t" or "b" => Cursors.SizeNS,
                "tl" or "br" => Cursors.SizeNWSE,
                "tr" or "bl" => Cursors.SizeNESW,
                _ => Cursors.Arrow,
            };
            return;
        }
        var ir = ImageRect();
        var d = p - drag.start;
        var r0 = drag.startRect;
        var mode = drag.mode;
        Rect r;
        if (mode == "move")
        {
            var x = Math.Clamp(r0.X + d.X, ir.Left, ir.Right - r0.Width);
            var y = Math.Clamp(r0.Y + d.Y, ir.Top, ir.Bottom - r0.Height);
            r = new Rect(x, y, r0.Width, r0.Height);
        }
        else
        {
            if (Aspect != null && mode.Length == 1) mode = mode switch { "l" => "bl", "r" => "br", "t" => "tr", _ => "br" };
            double left = r0.Left, top = r0.Top, right = r0.Right, bottom = r0.Bottom;
            if (mode.Contains('l')) left = Math.Min(Math.Max(r0.Left + d.X, ir.Left), right - MinCrop);
            if (mode.Contains('r')) right = Math.Max(Math.Min(r0.Right + d.X, ir.Right), left + MinCrop);
            if (mode.Contains('t')) top = Math.Min(Math.Max(r0.Top + d.Y, ir.Top), bottom - MinCrop);
            if (mode.Contains('b')) bottom = Math.Max(Math.Min(r0.Bottom + d.Y, ir.Bottom), top + MinCrop);
            if (Aspect is { } a)
            {
                // l'aspetto è nelle coordinate dei pixel dell'immagine: la scala di visualizzazione è uniforme
                var w = right - left;
                var h = w / a;
                var limit = mode.Contains('t') ? bottom - ir.Top : ir.Bottom - top;
                if (h > limit)
                {
                    h = limit;
                    w = h * a;
                    if (mode.Contains('l')) left = right - w;
                    else right = left + w;
                }
                if (mode.Contains('t')) top = bottom - h;
                else bottom = top + h;
            }
            r = new Rect(new Point(left, top), new Point(right, bottom));
        }
        SetFromRect(r);
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        if (_drag == null) return;
        _drag = null;
        ReleaseMouseCapture();
        CropCommitted?.Invoke();
    }
}
