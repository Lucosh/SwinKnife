using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwinKnife.Pdf;
using Wpf.Ui.Controls;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Controls;

/// <summary>Vista continua delle pagine di un PDF con rendering delle sole pagine visibili e ricerca.</summary>
public sealed class PdfView : DockPanel
{
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _pages = new() { Margin = new Thickness(20) };
    private readonly List<(Grid host, Image image, Canvas overlay)> _items = new();
    private readonly HashSet<int> _rendered = new();
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(25) };
    private readonly TextBlock _pageLabel = Ui.Hint();
    private readonly TextBox _find = new() { PlaceholderText = L.T("Cerca nel documento…"), Width = 240 };
    private PdfDoc? _doc;
    private double _zoom = 1.3;
    private (int page, int hit) _findPos = (0, -1);

    public PdfView()
    {
        var bar = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };
        var left = Ui.Row(
            Ui.IconBtn(SymbolRegular.ZoomOut24, L.T("Riduci"), (_, _) => SetZoom(_zoom / 1.2)),
            Ui.IconBtn(SymbolRegular.ZoomIn24, L.T("Ingrandisci"), (_, _) => SetZoom(_zoom * 1.2)),
            Ui.IconBtn(SymbolRegular.ZoomFit24, L.T("Adatta alla larghezza"), (_, _) => FitWidth()),
            _pageLabel);
        _find.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) FindNext();
        };
        SetDock(_find, Dock.Right);
        bar.Children.Add(_find);
        bar.Children.Add(left);
        SetDock(bar, Dock.Top);
        Children.Add(bar);
        _scroll = new ScrollViewer
        {
            Content = _pages, HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            Background = (Brush)Application.Current.FindResource("CanvasBrush"),
        };
        Children.Add(new Border { CornerRadius = new CornerRadius(8), ClipToBounds = true, Child = _scroll });
        _scroll.ScrollChanged += (_, _) => _timer.Start();
        _timer.Tick += (_, _) => RenderVisible();
        _scroll.PreviewMouseWheel += (_, e) =>
        {
            if (Keyboard.Modifiers != ModifierKeys.Control) return;
            SetZoom(_zoom * (e.Delta > 0 ? 1.1 : 1 / 1.1));
            e.Handled = true;
        };
    }

    public PdfDoc? Document => _doc;

    public void SetDocument(PdfDoc doc)
    {
        Clear();
        _doc = doc;
        _findPos = (0, -1);
        for (var i = 0; i < doc.PageCount; i++)
        {
            var img = new Image { Stretch = Stretch.Fill };
            var overlay = new Canvas { IsHitTestVisible = false };
            var host = new Grid { Background = Brushes.White, Margin = new Thickness(0, 0, 0, 14), HorizontalAlignment = HorizontalAlignment.Center };
            host.Children.Add(img);
            host.Children.Add(overlay);
            _pages.Children.Add(host);
            _items.Add((host, img, overlay));
        }
        ApplySizes();
        Dispatcher.BeginInvoke(FitWidth, DispatcherPriority.Loaded);
    }

    public void Clear()
    {
        _timer.Stop();
        _pages.Children.Clear();
        _items.Clear();
        _rendered.Clear();
        _doc?.Dispose();
        _doc = null;
    }

    private void ApplySizes()
    {
        if (_doc == null) return;
        for (var i = 0; i < _items.Count; i++)
        {
            var size = _doc.PageSize(i);
            _items[i].host.Width = size.Width * _zoom;
            _items[i].host.Height = size.Height * _zoom;
            _items[i].image.Source = null;
            _items[i].overlay.Children.Clear();
        }
        _rendered.Clear();
        _timer.Start();
    }

    public void SetZoom(double z)
    {
        if (_doc == null) return;
        var rel = _scroll.ScrollableHeight > 0 ? _scroll.VerticalOffset / _scroll.ScrollableHeight : 0;
        _zoom = Math.Clamp(z, 0.2, 6);
        ApplySizes();
        _scroll.UpdateLayout();
        _scroll.ScrollToVerticalOffset(rel * _scroll.ScrollableHeight);
    }

    public void FitWidth()
    {
        if (_doc == null || _doc.PageCount == 0) return;
        var maxW = Enumerable.Range(0, Math.Min(_doc.PageCount, 5)).Max(i => _doc.PageSize(i).Width);
        SetZoom((_scroll.ActualWidth - 60) / maxW);
    }

    private void RenderVisible()
    {
        _timer.Stop();
        if (_doc == null) return;
        var dpi = VisualTreeHelper.GetDpi(this).DpiScaleX;
        var top = _scroll.VerticalOffset - 400;
        var bottom = _scroll.VerticalOffset + _scroll.ViewportHeight + 400;
        var y = 20.0;
        var done = 0;
        int? current = null;
        for (var i = 0; i < _items.Count; i++)
        {
            var h = _items[i].host.Height + 14;
            var visible = y + h >= top && y <= bottom;
            if (current == null && y + h >= _scroll.VerticalOffset) current = i;
            if (visible && !_rendered.Contains(i))
            {
                if (done >= 2)
                {
                    _timer.Start();
                    break;
                }
                _items[i].image.Source = _doc.Render(i, _zoom * dpi);
                _rendered.Add(i);
                done++;
            }
            else if (!visible && _rendered.Contains(i) && Math.Abs(y - _scroll.VerticalOffset) > 8000)
            {
                _items[i].image.Source = null;
                _rendered.Remove(i);
            }
            y += h;
        }
        if (current is { } c) _pageLabel.Text = L.T($"  Pagina {c + 1} di {_items.Count}");
    }

    private void FindNext()
    {
        var text = _find.Text.Trim();
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
                foreach (var it in _items) it.overlay.Children.Clear();
                foreach (var r in hits)
                {
                    var rect = new Rectangle { Width = r.Width * _zoom, Height = r.Height * _zoom, Fill = new SolidColorBrush(Color.FromArgb(90, 255, 200, 0)) };
                    Canvas.SetLeft(rect, r.X * _zoom);
                    Canvas.SetTop(rect, r.Y * _zoom);
                    _items[p].overlay.Children.Add(rect);
                }
                var y = 20.0;
                for (var i = 0; i < p; i++) y += _items[i].host.Height + 14;
                _scroll.ScrollToVerticalOffset(y + hits[first].Y * _zoom - 120);
                MainWindow.Notify(L.T($"Trovato a pagina {p + 1}"));
                return;
            }
        }
        MainWindow.Notify(L.T($"“{text}” non trovato"));
    }
}
