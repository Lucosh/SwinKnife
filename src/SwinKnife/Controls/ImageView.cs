using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Controls;

/// <summary>Visualizzatore di immagini con zoom (rotellina), trascinamento, adatta/100% e animazioni GIF.</summary>
public sealed class ImageView : DockPanel
{
    private readonly ScrollViewer _scroll;
    private readonly Image _image = new() { Stretch = Stretch.None, SnapsToDevicePixels = true };
    private readonly ScaleTransform _scale = new(1, 1);
    private readonly RotateTransform _rotate = new(0);
    private readonly TextBlock _zoomLabel = Ui.Hint();
    private readonly DispatcherTimer _anim = new();
    private List<(BitmapSource frame, int delay)>? _frames;
    private int _frameIndex;
    private bool _fit = true;
    private Point? _dragStart;
    private Point _dragOffset;

    public ImageView()
    {
        var bar = Ui.Row(
            Ui.IconBtn(SymbolRegular.ZoomOut24, L.T("Riduci"), (_, _) => Zoom(1 / 1.25)),
            Ui.IconBtn(SymbolRegular.ZoomIn24, L.T("Ingrandisci"), (_, _) => Zoom(1.25)),
            Ui.IconBtn(SymbolRegular.ZoomFit24, L.T("Adatta alla finestra"), (_, _) => Fit()),
            Ui.IconBtn(SymbolRegular.ArrowMaximize24, L.T("Dimensione reale (100%)"), (_, _) => Actual()),
            Ui.IconBtn(SymbolRegular.ArrowRotateCounterclockwise24, L.T("Ruota a sinistra (solo vista)"), (_, _) => Rotate(-90)),
            Ui.IconBtn(SymbolRegular.ArrowRotateClockwise24, L.T("Ruota a destra (solo vista)"), (_, _) => Rotate(90)),
            _zoomLabel);
        bar.Margin = new Thickness(0, 0, 0, 6);
        SetDock(bar, Dock.Top);
        Children.Add(bar);

        var tg = new TransformGroup();
        tg.Children.Add(_scale);
        tg.Children.Add(_rotate);
        _image.LayoutTransform = tg;
        RenderOptions.SetBitmapScalingMode(_image, BitmapScalingMode.HighQuality);
        var holder = new Grid { Background = Brushes.Transparent };
        holder.Children.Add(_image);
        _image.HorizontalAlignment = HorizontalAlignment.Center;
        _image.VerticalAlignment = VerticalAlignment.Center;
        _scroll = new ScrollViewer
        {
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Content = holder, Background = (Brush)Application.Current.FindResource("CanvasBrush"), Cursor = Cursors.Hand,
        };
        Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _scroll });
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            Zoom(e.Delta > 0 ? 1.15 : 1 / 1.15);
            e.Handled = true;
        };
        _scroll.PreviewMouseLeftButtonDown += (_, e) =>
        {
            _dragStart = e.GetPosition(_scroll);
            _dragOffset = new Point(_scroll.HorizontalOffset, _scroll.VerticalOffset);
            _scroll.CaptureMouse();
        };
        _scroll.PreviewMouseMove += (_, e) =>
        {
            if (_dragStart is not { } s) return;
            var p = e.GetPosition(_scroll);
            _scroll.ScrollToHorizontalOffset(_dragOffset.X - (p.X - s.X));
            _scroll.ScrollToVerticalOffset(_dragOffset.Y - (p.Y - s.Y));
        };
        _scroll.PreviewMouseLeftButtonUp += (_, _) =>
        {
            _dragStart = null;
            _scroll.ReleaseMouseCapture();
        };
        _scroll.SizeChanged += (_, _) =>
        {
            if (_fit) Fit();
        };
        _anim.Tick += (_, _) => NextFrame();
    }

    public void SetImage(BitmapSource bmp)
    {
        Stop();
        _rotate.Angle = 0;
        _image.Source = bmp;
        Dispatcher.BeginInvoke(Fit, DispatcherPriority.Loaded);
    }

    public void SetAnimation(List<(BitmapSource frame, int delay)> frames)
    {
        SetImage(frames[0].frame);
        _frames = frames;
        _frameIndex = 0;
        _anim.Interval = TimeSpan.FromMilliseconds(frames[0].delay);
        _anim.Start();
    }

    public void Stop()
    {
        _anim.Stop();
        _frames = null;
    }

    public void Clear()
    {
        Stop();
        _image.Source = null;
    }

    private void NextFrame()
    {
        if (_frames == null) return;
        _frameIndex = (_frameIndex + 1) % _frames.Count;
        _image.Source = _frames[_frameIndex].frame;
        _anim.Interval = TimeSpan.FromMilliseconds(_frames[_frameIndex].delay);
    }

    private void Zoom(double f)
    {
        _fit = false;
        var z = Math.Clamp(_scale.ScaleX * f, 0.02, 40);
        _scale.ScaleX = _scale.ScaleY = z;
        UpdateLabel();
    }

    private double DpiScale => VisualTreeHelper.GetDpi(this).DpiScaleX;

    private void Actual()
    {
        _fit = false;
        _scale.ScaleX = _scale.ScaleY = 1 / DpiScale;
        UpdateLabel();
    }

    private void Rotate(double deg)
    {
        _rotate.Angle = (_rotate.Angle + deg) % 360;
        Fit();
    }

    private void Fit()
    {
        _fit = true;
        if (_image.Source is not BitmapSource b || _scroll.ActualWidth < 10) return;
        var sideways = Math.Abs(_rotate.Angle) % 180 == 90;
        double w = sideways ? b.PixelHeight : b.PixelWidth, h = sideways ? b.PixelWidth : b.PixelHeight;
        var s = Math.Min((_scroll.ActualWidth - 8) / w, (_scroll.ActualHeight - 8) / h);
        s = Math.Min(s, 1 / DpiScale); // non ingrandire oltre la dimensione reale
        _scale.ScaleX = _scale.ScaleY = Math.Max(0.01, s);
        UpdateLabel();
    }

    private void UpdateLabel() => _zoomLabel.Text = $"  {_scale.ScaleX * DpiScale * 100:0}%";
}
