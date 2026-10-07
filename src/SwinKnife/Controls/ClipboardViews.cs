using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Controls;

/// <summary>Aspetto comune delle voci della cronologia appunti.</summary>
public static class ClipViews
{
    public static string When(DateTime t)
    {
        var d = DateTime.Now - t;
        if (d.TotalMinutes < 1) return L.T("adesso");
        if (d.TotalHours < 1) return L.T($"{(int)d.TotalMinutes} min fa");
        if (t.Date == DateTime.Today) return t.ToString("t", L.Culture);
        if (t.Date == DateTime.Today.AddDays(-1)) return L.T("ieri") + " " + t.ToString("t", L.Culture);
        return t.ToString("g", L.Culture);
    }

    public static FrameworkElement Item(ClipEntry e, double width = 0)
    {
        var grid = new Grid { Margin = new Thickness(2, 4, 2, 4) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        FrameworkElement icon;
        if (e.Kind == ClipKind.Image && ClipboardHistory.LoadImage(e, 160) is { } img)
            icon = new Border
            {
                Width = 72, Height = 48, CornerRadius = new CornerRadius(4), ClipToBounds = true, Margin = new Thickness(0, 0, 12, 0),
                Background = Ui.Res("ControlFillColorDefaultBrush"),
                Child = new System.Windows.Controls.Image { Source = img, Stretch = Stretch.Uniform },
            };
        else
            icon = new SymbolIcon
            {
                Symbol = e.Kind == ClipKind.Files ? SymbolRegular.DocumentCopy24 : SymbolRegular.TextT24, FontSize = 20,
                Margin = new Thickness(4, 0, 14, 0), VerticalAlignment = VerticalAlignment.Top,
                Foreground = Ui.Res("TextFillColorSecondaryBrush"),
            };
        grid.Children.Add(icon);
        var text = e.Kind switch
        {
            ClipKind.Files => string.Join(", ", e.Text.Split('\n').Select(Path.GetFileName)),
            ClipKind.Image => L.T($"Immagine {e.Width} × {e.Height}"),
            _ => e.Text.Trim(),
        };
        var col = new StackPanel();
        col.Children.Add(new TextBlock
        {
            Text = text.Length > 400 ? text[..400] : text, TextWrapping = TextWrapping.Wrap, MaxHeight = 40, TextTrimming = TextTrimming.CharacterEllipsis,
            Foreground = Ui.Res("TextFillColorPrimaryBrush"),
        });
        var meta = (e.Pinned ? "📌 " : "") + When(e.Time) + (e.Source != null ? "  ·  " + e.Source : "");
        col.Children.Add(new TextBlock { Text = meta, FontSize = 11, Foreground = Ui.Res("TextFillColorTertiaryBrush"), Margin = new Thickness(0, 2, 0, 0) });
        Grid.SetColumn(col, 1);
        grid.Children.Add(col);
        grid.Tag = e;
        return grid;
    }

    public static bool Matches(ClipEntry e, string q) =>
        q.Length == 0 || e.Text.Contains(q, StringComparison.CurrentCultureIgnoreCase) || (e.Source?.Contains(q, StringComparison.CurrentCultureIgnoreCase) ?? false);
}

/// <summary>Finestrella vicino al mouse con gli ultimi appunti: scegli una voce e viene incollata dove stavi scrivendo.</summary>
public sealed class ClipboardPopup : Window
{
    private static ClipboardPopup? _open;
    private readonly IntPtr _target;
    private readonly ListBox _list = new() { BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly Wpf.Ui.Controls.TextBox _search = new() { PlaceholderText = L.T("Cerca negli appunti…"), Margin = new Thickness(0, 0, 0, 8) };

    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern uint SendInput(uint n, INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT { public uint type; public KEYBDINPUT ki; public long pad; }
    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public IntPtr extra; }

    public static void Toggle()
    {
        if (_open != null)
        {
            _open.Close();
            return;
        }
        _open = new ClipboardPopup();
        _open.Show();
        _open.Activate();
    }

    private ClipboardPopup()
    {
        _target = GetForegroundWindow();
        Title = L.T("Appunti");
        Width = 420;
        Height = 520;
        WindowStyle = WindowStyle.None;
        ResizeMode = ResizeMode.NoResize;
        ShowInTaskbar = false;
        Topmost = true;
        Background = Ui.Res("SolidBackgroundFillColorBaseBrush");
        BorderBrush = Ui.Res("CardStrokeColorDefaultBrush");
        BorderThickness = new Thickness(1);
        var p = ScreenCapture.Cursor;
        var mon = ScreenCapture.MonitorAt(p);
        WindowStartupLocation = WindowStartupLocation.Manual;
        SourceInitialized += (_, _) =>
        {
            var s = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformToDevice.M11 ?? 1;
            int w = (int)(Width * s), h = (int)(Height * s);
            var x = (int)Math.Clamp(p.X + 8, mon.X, mon.X + mon.Width - w);
            var y = (int)Math.Clamp(p.Y + 8, mon.Y, mon.Y + mon.Height - h);
            ScreenCapture.PlaceWindow(this, new Int32Rect(x, y, 0, 0));
        };
        var title = new TextBlock { Text = L.T("Appunti recenti"), FontWeight = FontWeights.SemiBold, FontSize = 14, Margin = new Thickness(0, 0, 0, 8), Foreground = Ui.Res("TextFillColorPrimaryBrush") };
        var hint = new TextBlock { Text = L.T("Invio o clic: incolla · Esc: chiudi"), FontSize = 11, Margin = new Thickness(0, 8, 0, 0), Foreground = Ui.Res("TextFillColorTertiaryBrush") };
        var dock = new DockPanel { Margin = new Thickness(12) };
        DockPanel.SetDock(title, Dock.Top);
        DockPanel.SetDock(_search, Dock.Top);
        DockPanel.SetDock(hint, Dock.Bottom);
        dock.Children.Add(title);
        dock.Children.Add(_search);
        dock.Children.Add(hint);
        dock.Children.Add(_list);
        Content = dock;
        Fill();
        _search.TextChanged += (_, _) => Fill();
        _list.MouseLeftButtonUp += (_, _) => Paste();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) Close();
            else if (e.Key == Key.Enter) Paste();
            else if (e.Key is Key.Down or Key.Up && _search.IsKeyboardFocusWithin && _list.Items.Count > 0)
            {
                _list.SelectedIndex = Math.Clamp(_list.SelectedIndex + (e.Key == Key.Down ? 1 : -1), 0, _list.Items.Count - 1);
                _list.ScrollIntoView(_list.SelectedItem);
                e.Handled = true;
            }
        };
        Deactivated += (_, _) => { if (IsVisible) Close(); };
        Closed += (_, _) => _open = null;
        Loaded += (_, _) => _search.Focus();
    }

    private void Fill()
    {
        var q = _search.Text.Trim();
        _list.Items.Clear();
        foreach (var e in ClipboardHistory.Items.OrderByDescending(x => x.Pinned).Where(x => ClipViews.Matches(x, q)).Take(60))
            _list.Items.Add(new ListBoxItem { Content = ClipViews.Item(e), Tag = e });
        if (_list.Items.Count > 0) _list.SelectedIndex = 0;
        if (_list.Items.Count == 0)
            _list.Items.Add(new ListBoxItem { Content = Ui.Hint(ClipboardHistory.Enabled ? L.T("Ancora niente: quello che copi apparirà qui.") : L.T("La cronologia degli appunti è disattivata (Strumenti rapidi).")), IsEnabled = false });
    }

    private void Paste()
    {
        if (_list.SelectedItem is not ListBoxItem { Tag: ClipEntry e }) return;
        try
        {
            ClipboardHistory.CopyBack(e);
        }
        catch
        {
            return;
        }
        Close();
        if (_target == IntPtr.Zero) return;
        SetForegroundWindow(_target);
        // Ctrl+V nella finestra in cui si stava scrivendo
        Task.Delay(120).ContinueWith(_ =>
        {
            const ushort VK_CONTROL = 0x11, VK_V = 0x56;
            const uint KEYUP = 2;
            var inputs = new[]
            {
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_CONTROL } },
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_V } },
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_V, dwFlags = KEYUP } },
                new INPUT { type = 1, ki = new KEYBDINPUT { wVk = VK_CONTROL, dwFlags = KEYUP } },
            };
            SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
        });
    }
}
