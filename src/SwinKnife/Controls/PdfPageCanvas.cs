using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SwinKnife.Controls;

public enum PdfTool { Select, Text, EditText, Highlight, Whiteout, Redact, Sign, Ink, Note, EraseAnnot }

/// <summary>Mostra una pagina PDF e trasforma i gesti del mouse in coordinate della pagina (punti, y in basso).</summary>
public sealed class PdfPageCanvas : FrameworkElement
{
    private BitmapSource? _bitmap;
    private double _zoom = 1;
    private Point? _start;
    private Rect? _dragRect;
    private readonly List<Point> _ink = new();

    public PdfTool Tool { get; set; } = PdfTool.Select;
    public List<Rect> Hits { get; } = new();
    public List<Rect> EditableLines { get; } = new();
    public Rect? Hover { get; private set; }
    public Color InkColor { get; set; } = Colors.Red;

    public event Action<Point>? Clicked;
    public event Action<Rect>? Dragged;
    public event Action<List<Point>>? Stroke;

    public PdfPageCanvas()
    {
        Focusable = true;
        ClipToBounds = true;
    }

    public void SetPage(BitmapSource bmp, Size pageSize, double zoom)
    {
        _bitmap = bmp;
        _zoom = zoom;
        Width = pageSize.Width * zoom;
        Height = pageSize.Height * zoom;
        Hover = null;
        InvalidateVisual();
    }

    private Point ToPage(Point p) => new(p.X / _zoom, p.Y / _zoom);

    private Rect ToView(Rect r) => new(r.X * _zoom, r.Y * _zoom, r.Width * _zoom, r.Height * _zoom);

    protected override void OnRender(DrawingContext dc)
    {
        var full = new Rect(0, 0, ActualWidth, ActualHeight);
        dc.DrawRectangle(Brushes.White, null, full);
        if (_bitmap != null) dc.DrawImage(_bitmap, full);
        var hitBrush = new SolidColorBrush(Color.FromArgb(90, 255, 200, 0));
        foreach (var h in Hits) dc.DrawRectangle(hitBrush, null, ToView(h));
        if (Tool == PdfTool.EditText)
        {
            var faint = new Pen(new SolidColorBrush(Color.FromArgb(70, 0xE5, 0x48, 0x4D)), 1) { DashStyle = DashStyles.Dash };
            foreach (var l in EditableLines) dc.DrawRectangle(null, faint, Inflate(ToView(l), 2, 1));
            if (Hover is { } hv)
                dc.DrawRectangle(new SolidColorBrush(Color.FromArgb(35, 0xE5, 0x48, 0x4D)), new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), 1.5), Inflate(ToView(hv), 3, 2));
        }
        if (_dragRect is { } r)
        {
            var fill = Tool switch
            {
                PdfTool.Highlight => Color.FromArgb(90, 255, 230, 0),
                PdfTool.Whiteout => Color.FromArgb(220, 255, 255, 255),
                PdfTool.Redact => Color.FromArgb(170, 0, 0, 0),
                _ => Color.FromArgb(50, 80, 140, 255),
            };
            dc.DrawRectangle(new SolidColorBrush(fill), new Pen(new SolidColorBrush(Color.FromRgb(0xE5, 0x48, 0x4D)), 1) { DashStyle = DashStyles.Dash }, r);
        }
        if (_ink.Count > 1)
        {
            var geo = new StreamGeometry();
            using (var ctx = geo.Open())
            {
                ctx.BeginFigure(_ink[0], false, false);
                ctx.PolyLineTo(_ink.Skip(1).ToList(), true, true);
            }
            dc.DrawGeometry(null, new Pen(new SolidColorBrush(InkColor), 2) { LineJoin = PenLineJoin.Round, StartLineCap = PenLineCap.Round, EndLineCap = PenLineCap.Round }, geo);
        }
    }

    private static Rect Inflate(Rect r, double x, double y)
    {
        r.Inflate(x, y);
        return r;
    }

    private bool IsAreaTool => Tool is PdfTool.Highlight or PdfTool.Whiteout or PdfTool.Redact or PdfTool.Sign;

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        Focus();
        _start = e.GetPosition(this);
        if (Tool == PdfTool.Ink)
        {
            _ink.Clear();
            _ink.Add(_start.Value);
        }
        CaptureMouse();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        var p = e.GetPosition(this);
        if (_start is { } s && e.LeftButton == MouseButtonState.Pressed)
        {
            if (Tool == PdfTool.Ink) _ink.Add(p);
            else if (IsAreaTool) _dragRect = new Rect(s, p);
            InvalidateVisual();
        }
        else if (Tool == PdfTool.EditText)
        {
            var pt = ToPage(p);
            Rect? hov = null;
            foreach (var l in EditableLines)
            {
                var r = Inflate(l, 2, 2);
                if (r.Contains(pt))
                {
                    hov = l;
                    break;
                }
            }
            if (hov != Hover)
            {
                Hover = hov;
                InvalidateVisual();
            }
        }
        Cursor = Tool switch
        {
            PdfTool.Select => Cursors.Arrow,
            PdfTool.Text => Cursors.IBeam,
            PdfTool.EditText => Hover != null ? Cursors.Hand : Cursors.Arrow,
            _ => Cursors.Cross,
        };
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        ReleaseMouseCapture();
        if (_start is not { } s) return;
        _start = null;
        var end = e.GetPosition(this);
        if (Tool == PdfTool.Ink)
        {
            var pts = _ink.Select(ToPage).ToList();
            _ink.Clear();
            InvalidateVisual();
            if (pts.Count > 1) Stroke?.Invoke(pts);
            return;
        }
        if (IsAreaTool)
        {
            var r = new Rect(s, end);
            _dragRect = null;
            InvalidateVisual();
            if (r.Width > 4 && r.Height > 4)
                Dragged?.Invoke(new Rect(ToPage(r.TopLeft), ToPage(r.BottomRight)));
            else if (Tool == PdfTool.Sign)
            {
                var c = ToPage(end);
                Dragged?.Invoke(new Rect(c.X - 90, c.Y - 35, 180, 70));
            }
            return;
        }
        if ((end - s).Length < 5) Clicked?.Invoke(ToPage(end));
    }

    protected override void OnMouseLeave(MouseEventArgs e)
    {
        if (Hover != null)
        {
            Hover = null;
            InvalidateVisual();
        }
    }
}
