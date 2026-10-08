using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;
using ImageMagick;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Legge il contenuto di un QR code da un'immagine, dagli appunti o dallo schermo.</summary>
public sealed class QrReadPage : UserControl, IToolPage
{
    private readonly System.Windows.Controls.TextBox _result = new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.Wrap, MinHeight = 80, Padding = new Thickness(8),
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
    };
    private readonly Wpf.Ui.Controls.Button _openLink;
    private string _text = "";

    public QrReadPage(MainWindow main)
    {
        _openLink = Ui.Btn(L.T("Apri il link"), SymbolRegular.Open24, (_, _) => { if (IsUrl(_text)) Util.OpenUrl(_text); });
        _openLink.Visibility = Visibility.Collapsed;

        var card = Ui.Card(L.T("Leggi un QR code"), null,
            Ui.Row(
                Ui.Btn(L.T("Apri immagine…"), SymbolRegular.Image24, (_, _) => FromFile(), primary: true),
                Ui.Btn(L.T("Incolla dagli appunti"), SymbolRegular.ClipboardPaste24, (_, _) => FromClipboard()),
                Ui.Btn(L.T("Scansiona lo schermo"), SymbolRegular.Screenshot24, (_, _) => FromScreen())),
            _result,
            Ui.Row(Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => { if (_text.Length > 0) { try { Clipboard.SetText(_text); MainWindow.Notify(L.T("Copiato.")); } catch { } } }), _openLink));
        var kids = ((StackPanel)card.Child).Children.OfType<StackPanel>().ToList();
        _result.Margin = new Thickness(0, 10, 0, 10);
        kids[1].Margin = new Thickness(0, 0, 0, 0);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Lettore QR"), L.T("Scopri cosa contiene un QR code: link, testo, Wi-Fi, contatti.")),
            card);
    }

    public bool Accepts(string path) => Archives.IsArchive(path) == false && IsImage(path);
    public void OpenFile(string path) { if (IsImage(path)) Show(Safe(() => QrDecode.FromFile(path))); }

    private static bool IsImage(string p) => Path.GetExtension(p).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".bmp" or ".gif" or ".webp" or ".tif" or ".tiff";

    private void FromFile()
    {
        var f = Dlg.OpenFile(L.T("Immagine con il QR code"), L.T("Immagini|*.png;*.jpg;*.jpeg;*.bmp;*.gif;*.webp;*.tif;*.tiff|Tutti i file|*.*"), "qr");
        if (f != null) Show(Safe(() => QrDecode.FromFile(f)));
    }

    private void FromClipboard()
    {
        try
        {
            if (Clipboard.ContainsImage()) Show(DecodeBitmap(Clipboard.GetImage()));
            else if (Clipboard.ContainsText()) Show(Clipboard.GetText());
            else Dlg.Info(L.T("Negli appunti non c'è un'immagine."));
        }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private void FromScreen()
    {
        var w = MainWindow.Instance;
        if (w == null) return;
        w.WindowState = WindowState.Minimized;
        w.Dispatcher.InvokeAsync(async () =>
        {
            await Task.Delay(350);
            string? text = null;
            try { text = DecodeBitmap(ScreenCapture.Capture(ScreenCapture.VirtualScreen)); } catch { }
            w.WindowState = WindowState.Normal;
            if (text == null) Dlg.Info(L.T("Nessun QR code trovato sullo schermo."));
            else Show(text);
        });
    }

    private static string? DecodeBitmap(BitmapSource? bs)
    {
        if (bs == null) return null;
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(bs));
        using var ms = new MemoryStream();
        enc.Save(ms);
        ms.Position = 0;
        using var img = new MagickImage(ms);
        return QrDecode.FromMagick(img);
    }

    private static string? Safe(Func<string?> f) { try { return f(); } catch { return null; } }

    private void Show(string? text)
    {
        if (string.IsNullOrEmpty(text)) { Dlg.Info(L.T("Nessun QR code riconosciuto nell'immagine.")); return; }
        _text = text;
        _result.Text = text;
        _openLink.Visibility = IsUrl(text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private static bool IsUrl(string t) => t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
}
