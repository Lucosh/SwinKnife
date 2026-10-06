using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Controls;

/// <summary>Scorciatoie per costruire l'interfaccia da codice con lo stile dell'app.</summary>
public static class Ui
{
    public static Brush Res(string key) => (Brush)Application.Current.FindResource(key);

    public static Button Btn(string? text, SymbolRegular? icon = null, RoutedEventHandler? click = null, string? tip = null,
        bool primary = false)
    {
        var b = new Button
        {
            Content = text,
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
            Margin = new Thickness(0, 0, 6, 0),
            Padding = new Thickness(12, 6, 12, 6),
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (icon is { } sym) b.Icon = new SymbolIcon { Symbol = sym };
        if (tip != null) b.ToolTip = tip;
        if (click != null) b.Click += click;
        return b;
    }

    public static Button IconBtn(SymbolRegular icon, string tip, RoutedEventHandler? click = null, string? text = null)
    {
        var b = new Button
        {
            Style = (Style)Application.Current.FindResource("IconButton"),
            Icon = new SymbolIcon { Symbol = icon, FontSize = 18 },
            Content = text,
            ToolTip = tip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        if (click != null) b.Click += click;
        return b;
    }

    public static TextBlock Hint(string text = "") => new()
    {
        Text = text, Style = (Style)Application.Current.FindResource("Hint"), VerticalAlignment = VerticalAlignment.Center,
    };

    public static TextBlock Label(string text, double size = 13, bool bold = false) => new()
    {
        Text = text, FontSize = size, FontWeight = bold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center, Foreground = Res("TextFillColorPrimaryBrush"),
        TextWrapping = TextWrapping.Wrap,
    };

    public static StackPanel Row(params UIElement[] children)
    {
        var p = new StackPanel { Orientation = Orientation.Horizontal };
        foreach (var c in children) p.Children.Add(c);
        return p;
    }

    public static Border Separator() => new()
    {
        Width = 1, Margin = new Thickness(6, 4, 8, 4), Background = Res("DividerStrokeColorDefaultBrush"),
    };

    /// <summary>Pannello vuoto/di stato con icona e testo centrati.</summary>
    public static FrameworkElement Placeholder(SymbolRegular icon, string text, out TextBlock label)
    {
        label = new TextBlock
        {
            Text = text, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, FontSize = 14,
            Foreground = Res("TextFillColorSecondaryBrush"), MaxWidth = 620, Margin = new Thickness(0, 14, 0, 0),
        };
        var panel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
        panel.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 46, Foreground = Res("TextFillColorTertiaryBrush") });
        panel.Children.Add(label);
        return new Border { Style = (Style)Application.Current.FindResource("DropZone"), Child = panel };
    }

    /// <summary>Riquadro con titolo, descrizione facoltativa e contenuto.</summary>
    public static Border Card(string title, string? description, params UIElement[] content)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("SectionTitle") });
        if (description != null)
            panel.Children.Add(new TextBlock { Text = description, Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(0, -4, 0, 10) });
        foreach (var c in content) panel.Children.Add(c);
        return new Border { Style = (Style)Application.Current.FindResource("Panel"), Child = panel, Margin = new Thickness(0, 0, 0, 14) };
    }

    /// <summary>Intestazione standard delle pagine (titolo + sottotitolo).</summary>
    public static StackPanel Header(string title, string subtitle)
    {
        var p = new StackPanel { Margin = new Thickness(0, 0, 0, 14) };
        p.Children.Add(new TextBlock { Text = title, Style = (Style)Application.Current.FindResource("PageTitle") });
        p.Children.Add(new TextBlock { Text = subtitle, Style = (Style)Application.Current.FindResource("PageSubtitle") });
        return p;
    }

    /// <summary>Griglia etichetta/valore a due colonne.</summary>
    public static Grid KeyValues(IEnumerable<(string key, string value)> rows, double keyWidth = 170)
    {
        var g = new Grid();
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(keyWidth) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        var r = 0;
        foreach (var (k, v) in rows)
        {
            g.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var kt = new TextBlock { Text = k, Foreground = Res("TextFillColorTertiaryBrush"), Margin = new Thickness(0, 3, 12, 3), TextWrapping = TextWrapping.Wrap };
            var vt = new System.Windows.Controls.TextBox
            {
                Text = v, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent, Padding = new Thickness(0),
                Margin = new Thickness(0, 3, 0, 3), TextWrapping = TextWrapping.Wrap, Foreground = Res("TextFillColorPrimaryBrush"),
            };
            Grid.SetRow(kt, r);
            Grid.SetRow(vt, r);
            Grid.SetColumn(vt, 1);
            g.Children.Add(kt);
            g.Children.Add(vt);
            r++;
        }
        return g;
    }

    /// <summary>Pagina scorrevole con margini standard.</summary>
    public static ScrollViewer ScrollPage(params UIElement[] children)
    {
        var p = new StackPanel { Margin = new Thickness(24, 18, 24, 18) };
        foreach (var c in children) p.Children.Add(c);
        return new ScrollViewer { Content = p, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    public static Color Lighten(Color c, double amount) => Color.FromRgb(
        (byte)(c.R + (255 - c.R) * amount), (byte)(c.G + (255 - c.G) * amount), (byte)(c.B + (255 - c.B) * amount));
}
