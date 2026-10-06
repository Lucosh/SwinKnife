using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using System.Windows.Threading;
using SwinKnife.Core;

namespace SwinKnife.Controls;

/// <summary>Barra flottante durante la registrazione (non compare nel video).</summary>
public sealed class RecorderBar : Window
{
    private readonly TextBlock _time = new() { Foreground = Brushes.White, FontSize = 15, FontFamily = new FontFamily("Cascadia Mono, Consolas"), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 14, 0) };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    public event Action? StopRequested;
    public event Action? CancelRequested;

    public RecorderBar(Func<TimeSpan> elapsed, Int32Rect monitor)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ResizeMode = ResizeMode.NoResize;
        SizeToContent = SizeToContent.WidthAndHeight;
        Title = L.T("SwinKnife – registrazione in corso");
        Icon = Application.Current.MainWindow?.Icon;

        var dot = new Ellipse { Width = 12, Height = 12, Fill = new SolidColorBrush(Color.FromRgb(0xE8, 0x2C, 0x2C)), VerticalAlignment = VerticalAlignment.Center };
        dot.BeginAnimation(OpacityProperty, new DoubleAnimation(1, 0.2, TimeSpan.FromSeconds(0.8)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever });
        var stop = new Wpf.Ui.Controls.Button
        {
            Content = L.T("Ferma"), Appearance = Wpf.Ui.Controls.ControlAppearance.Primary, Padding = new Thickness(12, 4, 12, 4),
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Stop24 }, ToolTip = L.T("Ferma e salva la registrazione"),
        };
        stop.Click += (_, _) => StopRequested?.Invoke();
        var cancel = new Wpf.Ui.Controls.Button
        {
            Icon = new Wpf.Ui.Controls.SymbolIcon { Symbol = Wpf.Ui.Controls.SymbolRegular.Delete24 }, Padding = new Thickness(8, 4, 8, 4),
            Margin = new Thickness(6, 0, 0, 0), ToolTip = L.T("Annulla: la registrazione viene scartata"),
        };
        cancel.Click += (_, _) => CancelRequested?.Invoke();
        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(dot);
        row.Children.Add(_time);
        row.Children.Add(stop);
        row.Children.Add(cancel);
        Content = new Border
        {
            Background = new SolidColorBrush(Color.FromArgb(235, 32, 32, 32)), CornerRadius = new CornerRadius(10), Padding = new Thickness(14, 8, 8, 8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(80, 255, 255, 255)), BorderThickness = new Thickness(1), Child = row,
            Cursor = Cursors.SizeAll, ToolTip = L.T("Trascina per spostare"),
        };
        MouseLeftButtonDown += (_, e) =>
        {
            if (e.OriginalSource is not DependencyObject d || FindButton(d) == null) DragMove();
        };
        _timer.Tick += (_, _) =>
        {
            var t = elapsed();
            _time.Text = $"{(int)t.TotalMinutes:00}:{t.Seconds:00}";
        };
        _time.Text = "00:00";
        SourceInitialized += (_, _) => ScreenCapture.ExcludeFromCapture(new WindowInteropHelper(this).Handle);
        Loaded += (_, _) =>
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            var w = (int)(ActualWidth * dpi.DpiScaleX);
            ScreenCapture.PlaceWindow(this, new Int32Rect(monitor.X + (monitor.Width - w) / 2, monitor.Y + 10, 0, 0));
            _timer.Start();
        };
        Closed += (_, _) => _timer.Stop();
    }

    private static ButtonBase? FindButton(DependencyObject d)
    {
        for (var x = d; x != null; x = VisualTreeHelper.GetParent(x))
            if (x is ButtonBase b) return b;
        return null;
    }
}

/// <summary>Cornice rossa attorno all'area registrata, trasparente ai clic e invisibile nel video. Mostra anche il conto alla rovescia.</summary>
public sealed class RecordingFrame : Window
{
    private readonly TextBlock _count = new()
    {
        Foreground = Brushes.White, FontSize = 96, FontWeight = FontWeights.Bold, HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center, Effect = new System.Windows.Media.Effects.DropShadowEffect { BlurRadius = 16, ShadowDepth = 0 },
    };

    public RecordingFrame(Int32Rect area)
    {
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        Topmost = true;
        ShowInTaskbar = false;
        ShowActivated = false;
        ResizeMode = ResizeMode.NoResize;
        var grid = new Grid();
        grid.Children.Add(new Border
        {
            BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0x2C, 0x2C)), BorderThickness = new Thickness(3),
        });
        grid.Children.Add(_count);
        Content = grid;
        var frame = new Int32Rect(area.X - 3, area.Y - 3, area.Width + 6, area.Height + 6);
        SourceInitialized += (_, _) =>
        {
            var h = new WindowInteropHelper(this).Handle;
            ScreenCapture.ExcludeFromCapture(h);
            ScreenCapture.ClickThrough(h);
            ScreenCapture.PlaceWindow(this, frame);
        };
    }

    public async Task CountdownAsync(int seconds)
    {
        for (var i = seconds; i > 0; i--)
        {
            _count.Text = i.ToString();
            await Task.Delay(1000);
        }
        _count.Text = "";
    }
}
