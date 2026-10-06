using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using Path = System.Windows.Shapes.Path;

namespace SwinKnife.Controls;

public enum AnnotationTool { Arrow, Rectangle, Ellipse, Highlight, Pen, Text, Step, Pixelate, Crop }

/// <summary>Disegno di annotazioni sopra uno screenshot; il risultato si esporta alla risoluzione originale.</summary>
public sealed class AnnotationEditor : UserControl
{
    private readonly Canvas _canvas = new() { ClipToBounds = true };
    private readonly Image _image = new();
    private readonly Viewbox _viewbox;
    private readonly Stack<Action> _undo = new();
    private BitmapSource? _base;
    private Point _start;
    private FrameworkElement? _drawing;
    private int _stepCounter = 1;

    public AnnotationTool Tool { get; set; } = AnnotationTool.Arrow;
    public Color Color { get; set; } = Color.FromRgb(0xE8, 0x2C, 0x2C);
    public int Size { get; set; } = 2;

    public event EventHandler? Changed;

    public bool HasImage => _base != null;
    public bool CanUndo => _undo.Count > 0;
    public int PixelWidth => _base?.PixelWidth ?? 0;
    public int PixelHeight => _base?.PixelHeight ?? 0;

    public AnnotationEditor()
    {
        _canvas.Children.Add(_image);
        _viewbox = new Viewbox { Child = _canvas, Stretch = Stretch.Uniform, StretchDirection = StretchDirection.DownOnly };
        Content = new Border { Child = _viewbox, Background = Brushes.Transparent, ClipToBounds = true };
        _canvas.Cursor = Cursors.Cross;
        _canvas.MouseLeftButtonDown += OnDown;
        _canvas.MouseMove += OnMove;
        _canvas.MouseLeftButtonUp += OnUp;
        Focusable = true;
    }

    public void Load(BitmapSource image)
    {
        _undo.Clear();
        SetBase(image);
        Clear();
        _stepCounter = 1;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SetBase(BitmapSource image)
    {
        _base = image;
        _image.Source = image;
        _image.Width = _canvas.Width = image.PixelWidth;
        _image.Height = _canvas.Height = image.PixelHeight;
    }

    private void Clear()
    {
        for (var i = _canvas.Children.Count - 1; i >= 1; i--) _canvas.Children.RemoveAt(i);
    }

    private double Stroke => Math.Max(2, Math.Min(PixelWidth, PixelHeight) / 260.0) * (Size switch { 1 => 0.6, 3 => 1.8, _ => 1 });

    private Brush Fill(byte alpha = 255) => new SolidColorBrush(Color.FromArgb(alpha, Color.R, Color.G, Color.B));

    private Point Clamp(Point p) => new(Math.Clamp(p.X, 0, PixelWidth), Math.Clamp(p.Y, 0, PixelHeight));

    private void Add(UIElement e)
    {
        _canvas.Children.Add(e);
        _undo.Push(() => _canvas.Children.Remove(e));
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ mouse
    private void OnDown(object sender, MouseButtonEventArgs e)
    {
        if (_base == null) return;
        if (PointerDown(e.GetPosition(_canvas))) e.Handled = true;
        if (_drawing != null) _canvas.CaptureMouse();
    }

    private void OnMove(object sender, MouseEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) PointerMove(e.GetPosition(_canvas));
    }

    private void OnUp(object sender, MouseButtonEventArgs e)
    {
        _canvas.ReleaseMouseCapture();
        PointerUp(e.GetPosition(_canvas));
    }

    /// <summary>Inizio di un tratto (coordinate in pixel dell'immagine). True se l'evento è stato consumato.</summary>
    public bool PointerDown(Point p)
    {
        if (_base == null) return false;
        CommitText();
        _start = Clamp(p);
        switch (Tool)
        {
            case AnnotationTool.Text:
                StartText(_start);
                return true;
            case AnnotationTool.Step:
                AddStep(_start);
                return true;
            case AnnotationTool.Pen:
                _drawing = new Polyline
                {
                    Stroke = Fill(), StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round,
                    StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, Points = { _start },
                };
                break;
            case AnnotationTool.Arrow:
                _drawing = new Path { Stroke = Fill(), Fill = Fill(), StrokeThickness = Stroke, StrokeLineJoin = PenLineJoin.Round, StrokeEndLineCap = PenLineCap.Round, StrokeStartLineCap = PenLineCap.Round };
                break;
            case AnnotationTool.Rectangle:
                _drawing = new Rectangle { Stroke = Fill(), StrokeThickness = Stroke, RadiusX = Stroke, RadiusY = Stroke };
                break;
            case AnnotationTool.Ellipse:
                _drawing = new Ellipse { Stroke = Fill(), StrokeThickness = Stroke };
                break;
            case AnnotationTool.Highlight:
                _drawing = new Rectangle { Fill = Fill(90) };
                break;
            case AnnotationTool.Pixelate:
            case AnnotationTool.Crop:
                _drawing = new Rectangle
                {
                    Stroke = Brushes.White, StrokeThickness = Math.Max(1, Stroke / 2), StrokeDashArray = [4, 3],
                    Fill = new SolidColorBrush(Color.FromArgb(40, 255, 255, 255)),
                };
                break;
        }
        if (_drawing == null) return false;
        _drawing.IsHitTestVisible = false;
        _canvas.Children.Add(_drawing);
        Update(_start);
        return true;
    }

    public void PointerMove(Point p)
    {
        if (_drawing != null) Update(Clamp(p));
    }

    private void Update(Point p)
    {
        var r = new Rect(_start, p);
        switch (_drawing)
        {
            case Polyline line:
                line.Points.Add(p);
                break;
            case Path arrow:
                arrow.Data = ArrowGeometry(_start, p, Stroke);
                break;
            case Shape shape:
                Canvas.SetLeft(shape, r.X);
                Canvas.SetTop(shape, r.Y);
                shape.Width = r.Width;
                shape.Height = r.Height;
                break;
        }
    }

    public void PointerUp(Point p)
    {
        if (_drawing == null) return;
        var shape = _drawing;
        _drawing = null;
        _canvas.Children.Remove(shape);
        var end = Clamp(p);
        var r = new Rect(_start, end);
        var tiny = r.Width < 4 && r.Height < 4;
        switch (Tool)
        {
            case AnnotationTool.Pixelate:
                if (!tiny) Pixelate(r);
                break;
            case AnnotationTool.Crop:
                if (!tiny) Crop(r);
                break;
            case AnnotationTool.Pen:
                Add(shape);
                break;
            default:
                if (!tiny) Add(shape);
                break;
        }
    }

    private static Geometry ArrowGeometry(Point a, Point b, double t)
    {
        var v = b - a;
        var len = v.Length;
        if (len < 1) return Geometry.Empty;
        v /= len;
        var head = Math.Min(len * 0.6, t * 4 + 10);
        var width = head * 0.55;
        var normal = new Vector(-v.Y, v.X);
        var baseCenter = b - v * head;
        var g = new GeometryGroup();
        g.Children.Add(new LineGeometry(a, b - v * (head * 0.8)));
        var tri = new PathFigure { StartPoint = b, IsClosed = true, IsFilled = true };
        tri.Segments.Add(new LineSegment(baseCenter + normal * width, true));
        tri.Segments.Add(new LineSegment(baseCenter - normal * width, true));
        g.Children.Add(new PathGeometry([tri]));
        return g;
    }

    // ------------------------------------------------------------------ testo e numeri
    private TextBox? _editing;

    private void StartText(Point p)
    {
        var size = Math.Max(16, Math.Min(PixelWidth, PixelHeight) / 26.0) * (Size switch { 1 => 0.7, 3 => 1.6, _ => 1 });
        var tb = new TextBox
        {
            FontSize = size, Foreground = Fill(), Background = new SolidColorBrush(Color.FromArgb(60, 0, 0, 0)), BorderThickness = new Thickness(1),
            BorderBrush = Brushes.White, MinWidth = size * 4, AcceptsReturn = true, FontWeight = FontWeights.SemiBold, Padding = new Thickness(2),
            CaretBrush = Fill(),
        };
        Canvas.SetLeft(tb, p.X);
        Canvas.SetTop(tb, p.Y - size * 0.7);
        tb.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape)
            {
                tb.Text = "";
                CommitText();
            }
            else if (e.Key == Key.Enter && (Keyboard.Modifiers & ModifierKeys.Shift) == 0)
            {
                e.Handled = true;
                CommitText();
            }
        };
        tb.LostKeyboardFocus += (_, _) => CommitText();
        _canvas.Children.Add(tb);
        _editing = tb;
        Dispatcher.BeginInvoke(() => tb.Focus(), System.Windows.Threading.DispatcherPriority.Input);
    }

    /// <summary>Trasforma la casella di testo in corso in un'annotazione definitiva.</summary>
    public void CommitText()
    {
        if (_editing is not { } tb) return;
        _editing = null;
        _canvas.Children.Remove(tb);
        if (string.IsNullOrWhiteSpace(tb.Text)) return;
        var text = new TextBlock
        {
            Text = tb.Text, FontSize = tb.FontSize, Foreground = tb.Foreground, FontWeight = FontWeights.SemiBold, IsHitTestVisible = false,
            Effect = new DropShadowEffect { BlurRadius = tb.FontSize / 5, ShadowDepth = 0, Opacity = 0.9, Color = Color.R + Color.G + Color.B > 600 ? Colors.Black : Colors.White },
        };
        Canvas.SetLeft(text, Canvas.GetLeft(tb) + 3);
        Canvas.SetTop(text, Canvas.GetTop(tb) + 3);
        Add(text);
    }

    private void AddStep(Point p)
    {
        var d = Math.Max(26, Math.Min(PixelWidth, PixelHeight) / 18.0) * (Size switch { 1 => 0.7, 3 => 1.5, _ => 1 });
        var n = _stepCounter++;
        var g = new Grid { Width = d, Height = d, IsHitTestVisible = false };
        g.Children.Add(new Ellipse { Fill = Fill(), Stroke = Brushes.White, StrokeThickness = Math.Max(1.5, d / 14) });
        g.Children.Add(new TextBlock
        {
            Text = n.ToString(), Foreground = Color.R + Color.G + Color.B > 600 ? Brushes.Black : Brushes.White, FontWeight = FontWeights.Bold, FontSize = d * 0.55,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
        });
        Canvas.SetLeft(g, p.X - d / 2);
        Canvas.SetTop(g, p.Y - d / 2);
        _canvas.Children.Add(g);
        _undo.Push(() =>
        {
            _canvas.Children.Remove(g);
            _stepCounter--;
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------------ pixelatura e ritaglio
    private static Int32Rect PixelRect(Rect r, int w, int h)
    {
        var x = (int)Math.Floor(r.X);
        var y = (int)Math.Floor(r.Y);
        return new Int32Rect(x, y, Math.Min(w - x, (int)Math.Ceiling(r.Width)), Math.Min(h - y, (int)Math.Ceiling(r.Height)));
    }

    private void Pixelate(Rect r)
    {
        var flat = Render();
        var pr = PixelRect(r, flat.PixelWidth, flat.PixelHeight);
        if (pr.Width < 2 || pr.Height < 2) return;
        var block = Math.Max(6, Math.Min(flat.PixelWidth, flat.PixelHeight) / 70.0);
        var small = new TransformedBitmap(new CroppedBitmap(flat, pr), new ScaleTransform(1 / block, 1 / block));
        var img = new Image { Source = small, Width = pr.Width, Height = pr.Height, Stretch = Stretch.Fill, IsHitTestVisible = false };
        RenderOptions.SetBitmapScalingMode(img, BitmapScalingMode.NearestNeighbor);
        Canvas.SetLeft(img, pr.X);
        Canvas.SetTop(img, pr.Y);
        Add(img);
    }

    private void Crop(Rect r)
    {
        var flat = Render();
        var pr = PixelRect(r, flat.PixelWidth, flat.PixelHeight);
        if (pr.Width < 4 || pr.Height < 4) return;
        var oldBase = _base!;
        var oldChildren = _canvas.Children.Cast<UIElement>().Skip(1).ToList();
        var cropped = new CroppedBitmap(flat, pr);
        cropped.Freeze();
        Clear();
        SetBase(cropped);
        _undo.Push(() =>
        {
            SetBase(oldBase);
            Clear();
            foreach (var c in oldChildren) _canvas.Children.Add(c);
        });
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public void Undo()
    {
        CommitText();
        if (_undo.Count == 0) return;
        _undo.Pop()();
        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Immagine finale con le annotazioni, alla risoluzione originale.</summary>
    public BitmapSource Render()
    {
        if (_base == null) throw new InvalidOperationException();
        _canvas.UpdateLayout();
        var rtb = new RenderTargetBitmap(PixelWidth, PixelHeight, 96, 96, PixelFormats.Pbgra32);
        var dv = new DrawingVisual();
        using (var dc = dv.RenderOpen())
            dc.DrawRectangle(new VisualBrush(_canvas) { Stretch = Stretch.None, AlignmentX = AlignmentX.Left, AlignmentY = AlignmentY.Top },
                null, new Rect(0, 0, PixelWidth, PixelHeight));
        rtb.Render(dv);
        rtb.Freeze();
        return rtb;
    }
}
