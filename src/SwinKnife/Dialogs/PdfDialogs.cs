using System.Windows;
using System.Windows.Controls;
using System.Windows.Ink;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SwinKnife.Controls;
using SwinKnife.Pdf;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Dialogs;

public static class Palette
{
    public static readonly (string name, Color color)[] Colors =
    [
        (L.T("Nero"), System.Windows.Media.Colors.Black), (L.T("Grigio"), Color.FromRgb(0x80, 0x80, 0x80)),
        (L.T("Blu scuro"), Color.FromRgb(0x10, 0x20, 0x6A)), (L.T("Blu"), Color.FromRgb(0x1E, 0x6F, 0xE8)),
        (L.T("Rosso"), Color.FromRgb(0xE0, 0x24, 0x24)), (L.T("Verde"), Color.FromRgb(0x1C, 0x9A, 0x4A)),
        (L.T("Giallo"), Color.FromRgb(0xFF, 0xD6, 0x0A)), (L.T("Arancione"), Color.FromRgb(0xF2, 0x8C, 0x1C)),
        (L.T("Viola"), Color.FromRgb(0x8E, 0x44, 0xC8)), (L.T("Bianco"), System.Windows.Media.Colors.White),
    ];

    public static ComboBox Combo(Color selected)
    {
        var c = new ComboBox { MinWidth = 150 };
        foreach (var (name, color) in Colors)
        {
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new Border { Width = 16, Height = 16, CornerRadius = new CornerRadius(3), Background = new SolidColorBrush(color), BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) });
            row.Children.Add(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center });
            c.Items.Add(new ComboBoxItem { Content = row, Tag = color });
        }
        var idx = Array.FindIndex(Colors, x => x.color == selected);
        c.SelectedIndex = idx >= 0 ? idx : 0;
        return c;
    }

    public static Color Value(ComboBox c) => c.SelectedItem is ComboBoxItem { Tag: Color col } ? col : System.Windows.Media.Colors.Black;
}

/// <summary>Testo da inserire o modificare nel PDF, con font standard, dimensione, stile e colore.</summary>
public sealed class TextEditDialog
{
    internal static ComboBox SizeCombo(double size) => new()
    {
        IsEditable = true, Width = 90, ItemsSource = SizeInput.Common,
        Text = Math.Round(size, 1).ToString(System.Globalization.CultureInfo.CurrentCulture),
    };

    internal static float ParseSize(string text, float fallback) =>
        float.TryParse(text.Replace(',', '.'), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var v) && v is >= 2 and <= 500
            ? v : fallback;

    public string Text { get; private set; } = "";
    public float Size { get; private set; }
    public Color Color { get; private set; }
    public string FontName { get; private set; } = "Helvetica";

    public bool Show(string title, string text, float size, Color color, int family, bool bold, bool italic, string? note = null)
    {
        var panel = new StackPanel();
        if (note != null) panel.Children.Add(new TextBlock { Text = note, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10), Foreground = Ui.Res("TextFillColorSecondaryBrush") });
        var box = new TextBox { Text = text, AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 90, MaxHeight = 260, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        panel.Children.Add(box);
        var fam = new ComboBox { ItemsSource = PdfDoc.StandardFamilies, SelectedIndex = family, MinWidth = 120 };
        var sz = SizeCombo(size);
        var b = new CheckBox { Content = L.T("Grassetto"), IsChecked = bold };
        var i = new CheckBox { Content = L.T("Corsivo"), IsChecked = italic };
        var col = Palette.Combo(color);
        var row1 = Ui.Row(Ui.Label(L.T("Font  ")), fam, new Border { Width = 14 }, Ui.Label(L.T("Dimensione  ")), sz);
        row1.Margin = new Thickness(0, 12, 0, 8);
        var row2 = Ui.Row(b, new Border { Width = 10 }, i, new Border { Width = 18 }, Ui.Label(L.T("Colore  ")), col);
        panel.Children.Add(row1);
        panel.Children.Add(row2);
        var d = new FormDialog(title, panel, 560);
        d.AddButton("OK", 1, true);
        d.AddButton(L.T("Annulla"), 0);
        d.Loaded += (_, _) =>
        {
            box.Focus();
            box.SelectAll();
        };
        if (d.Run() != 1) return false;
        Text = box.Text;
        Size = ParseSize(sz.Text, size);
        Color = Palette.Value(col);
        FontName = PdfDoc.StandardFont(fam.SelectedIndex, b.IsChecked == true, i.IsChecked == true);
        return true;
    }
}

public static class SizeInput
{
    public static readonly double[] Common = [8, 9, 10, 11, 12, 14, 16, 18, 20, 24, 28, 32, 36, 48, 60, 72, 96, 120];
}

/// <summary>Firma disegnata a mano (InkCanvas) oppure immagine caricata da file.</summary>
public static class SignatureDialog
{
    public static BitmapSource? Show()
    {
        var ink = new InkCanvas { Background = Brushes.White, Height = 220, Cursor = System.Windows.Input.Cursors.Pen };
        ink.DefaultDrawingAttributes = new DrawingAttributes { Color = Color.FromRgb(0x10, 0x20, 0x6A), Width = 3, Height = 3, FitToCurve = true };
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T("Disegna la firma con il mouse o la penna, oppure carica un'immagine."), Margin = new Thickness(0, 0, 0, 10), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border { CornerRadius = new CornerRadius(6), ClipToBounds = true, Child = ink, BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1) });
        BitmapSource? loaded = null;
        FormDialog? dlg = null;
        var tools = Ui.Row(
            Ui.Btn(L.T("Cancella"), SymbolRegular.Eraser24, (_, _) => ink.Strokes.Clear()),
            Ui.Btn(L.T("Blu"), null, (_, _) => SetColor(ink, Color.FromRgb(0x10, 0x20, 0x6A))),
            Ui.Btn(L.T("Nero"), null, (_, _) => SetColor(ink, Colors.Black)),
            Ui.Btn(L.T("Carica immagine…"), SymbolRegular.Image24, (_, _) =>
            {
                var p = Dlg.OpenFile(L.T("Scegli un'immagine"), "Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.heic", "sign");
                if (p == null) return;
                try
                {
                    using var img = Core.ImageIO.Load(p);
                    loaded = Core.ImageIO.ToBitmapSource(img);
                    dlg!.Close();
                }
                catch (Exception ex)
                {
                    Dlg.Error(L.T("Immagine non valida: ") + ex.Message);
                }
            }));
        tools.Margin = new Thickness(0, 10, 0, 0);
        panel.Children.Add(tools);
        dlg = new FormDialog(L.T("Firma o immagine"), panel, 620);
        dlg.AddButton(L.T("Inserisci"), 1, true);
        dlg.AddButton(L.T("Annulla"), 0);
        var r = dlg.Run();
        if (loaded != null) return loaded;
        if (r != 1 || ink.Strokes.Count == 0) return null;
        var bounds = ink.Strokes.GetBounds();
        bounds.Inflate(6, 6);
        const double scale = 3;
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.PushTransform(new ScaleTransform(scale, scale));
            dc.PushTransform(new TranslateTransform(-bounds.X, -bounds.Y));
            ink.Strokes.Draw(dc);
        }
        var rtb = new RenderTargetBitmap((int)(bounds.Width * scale), (int)(bounds.Height * scale), 96, 96, PixelFormats.Pbgra32);
        rtb.Render(visual);
        // da premoltiplicato a BGRA normale (PDFium vuole alfa non premoltiplicato)
        var conv = new FormatConvertedBitmap(rtb, PixelFormats.Bgra32, null, 0);
        conv.Freeze();
        return conv;
    }

    private static void SetColor(InkCanvas ink, Color c)
    {
        ink.DefaultDrawingAttributes.Color = c;
        foreach (var s in ink.Strokes) s.DrawingAttributes.Color = c;
    }
}

public static class WatermarkDialog
{
    public static (string text, float size, byte alpha, bool diagonal, Color color)? Show()
    {
        var text = new TextBox { Text = "RISERVATO" };
        var size = TextEditDialog.SizeCombo(72);
        var opacity = new Slider { Minimum = 5, Maximum = 100, Value = 25, Width = 200 };
        var diag = new CheckBox { Content = L.T("In diagonale"), IsChecked = true };
        var col = Palette.Combo(Color.FromRgb(0x80, 0x80, 0x80));
        var panel = new StackPanel();
        panel.Children.Add(Ui.Label(L.T("Testo")));
        panel.Children.Add(text);
        var r = Ui.Row(Ui.Label(L.T("Dimensione  ")), size, new Border { Width = 18 }, Ui.Label(L.T("Colore  ")), col);
        r.Margin = new Thickness(0, 12, 0, 8);
        panel.Children.Add(r);
        panel.Children.Add(Ui.Row(Ui.Label(L.T("Opacità  ")), opacity, new Border { Width = 18 }, diag));
        var d = new FormDialog(L.T("Filigrana"), panel, 560);
        d.AddButton(L.T("Applica a tutte le pagine"), 1, true);
        d.AddButton(L.T("Annulla"), 0);
        if (d.Run() != 1 || string.IsNullOrWhiteSpace(text.Text)) return null;
        return (text.Text, TextEditDialog.ParseSize(size.Text, 72), (byte)(opacity.Value / 100 * 255), diag.IsChecked == true, Palette.Value(col));
    }
}

/// <summary>Compilazione dei campi di un modulo PDF.</summary>
public static class FormFillDialog
{
    public static bool Show(List<FormField> fields)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(220) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var apply = new List<Action>();
        var row = 0;
        foreach (var f in fields)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var label = new TextBlock { Text = f.Name, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 4, 12, 4) };
            FrameworkElement input;
            switch (f.Kind)
            {
                case FieldKind.Text:
                {
                    var tb = new TextBox { Text = f.Value, AcceptsReturn = f.Multiline, TextWrapping = f.Multiline ? TextWrapping.Wrap : TextWrapping.NoWrap, MinHeight = f.Multiline ? 70 : 0 };
                    apply.Add(() => f.Value = tb.Text);
                    input = tb;
                    break;
                }
                case FieldKind.CheckBox:
                {
                    var cb = new CheckBox { IsChecked = f.Checked };
                    apply.Add(() => f.Checked = cb.IsChecked == true);
                    input = cb;
                    break;
                }
                default:
                {
                    var combo = new ComboBox { ItemsSource = f.Options, SelectedIndex = f.SelectedIndex };
                    apply.Add(() => f.SelectedIndex = combo.SelectedIndex);
                    input = combo;
                    break;
                }
            }
            input.Margin = new Thickness(0, 4, 0, 4);
            input.IsEnabled = !f.ReadOnly;
            Grid.SetRow(label, row);
            Grid.SetRow(input, row);
            Grid.SetColumn(input, 1);
            grid.Children.Add(label);
            grid.Children.Add(input);
            row++;
        }
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = L.T($"{fields.Count} campi trovati. I campi in grigio sono di sola lettura."), Margin = new Thickness(0, 0, 0, 10), Opacity = 0.75 });
        panel.Children.Add(new ScrollViewer { Content = grid, MaxHeight = 520, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
        var d = new FormDialog(L.T("Compila modulo"), panel, 680);
        d.AddButton(L.T("Applica"), 1, true);
        d.AddButton(L.T("Annulla"), 0);
        if (d.Run() != 1) return false;
        foreach (var a in apply) a();
        return true;
    }
}
