using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using QRCoder;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using ListBox = System.Windows.Controls.ListBox;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Scambia file con il telefono sulla stessa rete Wi-Fi: inquadri il QR code e scarichi o invii file dal browser.</summary>
public sealed class PhonePage : UserControl, IToolPage
{
    private static LanShare? _share;
    private readonly Image _qr = new() { Width = 260, Height = 260, Stretch = Stretch.Uniform };
    private readonly TextBlock _url = new() { FontSize = 13, TextWrapping = TextWrapping.Wrap, TextAlignment = TextAlignment.Center, Margin = new Thickness(0, 10, 0, 0) };
    private readonly ToggleSwitch _on = new() { Content = L.T("Condivisione attiva") };
    private readonly ListBox _shared = new() { MinHeight = 140, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly ListBox _received = new() { MinHeight = 140, BorderThickness = new Thickness(0), Background = Brushes.Transparent };
    private readonly TextBlock _log = Ui.Hint();
    private readonly TextBlock _dir = Ui.Hint();

    public PhonePage(MainWindow main)
    {
        _share ??= new LanShare();
        _share.Activity += OnActivity;
        _share.Received += OnReceived;
        Unloaded += (_, _) =>
        {
            _share.Activity -= OnActivity;
            _share.Received -= OnReceived;
        };
        _on.Click += (_, _) => Toggle(_on.IsChecked == true);
        _log.TextWrapping = TextWrapping.Wrap;

        var qrPanel = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
        qrPanel.Children.Add(new Border { Background = Brushes.White, CornerRadius = new CornerRadius(10), Padding = new Thickness(10), Child = _qr, HorizontalAlignment = HorizontalAlignment.Center });
        qrPanel.Children.Add(_url);
        qrPanel.Children.Add(Ui.Row(_on));
        ((StackPanel)qrPanel.Children[^1]).HorizontalAlignment = HorizontalAlignment.Center;
        ((StackPanel)qrPanel.Children[^1]).Margin = new Thickness(0, 10, 0, 0);
        var steps = new TextBlock
        {
            Text = L.T("1. Collega telefono e PC alla stessa rete Wi-Fi.\n2. Inquadra il QR code con la fotocamera del telefono.\n3. Dal browser scarichi i file condivisi qui sotto o invii foto e file al PC."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 14, 0, 0), Foreground = Ui.Res("TextFillColorSecondaryBrush"), LineHeight = 22,
        };
        qrPanel.Children.Add(steps);
        var firewall = Ui.Btn(L.T("Il telefono non si collega?"), SymbolRegular.ShieldKeyhole24, (_, _) =>
        {
            if (!Dlg.Confirm(L.T("Se il telefono non riesce ad aprire la pagina, probabilmente il firewall di Windows blocca SwinKnife. Aggiungere una regola che consente le connessioni dalla rete privata? Serve l'autorizzazione di amministratore."))) return;
            if (LanShare.AllowInFirewall()) MainWindow.Notify(L.T("Regola del firewall aggiunta: riprova dal telefono."));
            else Dlg.Error(L.T("Impossibile modificare il firewall (autorizzazione negata o gestito dall'azienda)."));
        });
        firewall.Margin = new Thickness(0, 14, 0, 0);
        firewall.HorizontalAlignment = HorizontalAlignment.Center;
        qrPanel.Children.Add(firewall);

        // ---- file da inviare
        var addBtn = Ui.Btn(L.T("Aggiungi file…"), SymbolRegular.Add24, (_, _) =>
        {
            var files = Dlg.OpenFiles(L.T("File da mandare al telefono"), key: "phone");
            if (files.Length > 0) AddFiles(files);
        }, primary: true);
        var removeBtn = Ui.Btn(L.T("Togli"), SymbolRegular.Dismiss24, (_, _) =>
        {
            foreach (var p in _shared.SelectedItems.OfType<ListBoxItem>().Select(i => (string)i.Tag).ToList()) _share!.Remove(p);
            FillShared();
        });
        var sharedCard = Ui.Card(L.T("Dal PC al telefono"), L.T("Trascina qui i file (o usa \"Aggiungi\"): sul telefono compaiono pronti da scaricare."),
            Ui.Row(addBtn, removeBtn), Box(_shared));

        // ---- file ricevuti
        var openDir = Ui.Btn(L.T("Apri cartella"), SymbolRegular.FolderOpen24, (_, _) =>
        {
            Directory.CreateDirectory(LanShare.ReceiveDir);
            Util.OpenFolder(LanShare.ReceiveDir);
        });
        var changeDir = Ui.Btn(L.T("Cambia…"), SymbolRegular.Folder24, (_, _) =>
        {
            if (Dlg.PickFolder(L.T("Dove salvare i file ricevuti"), "phone.dir") is { } d)
            {
                LanShare.ReceiveDir = d;
                _dir.Text = d;
            }
        });
        _dir.Text = LanShare.ReceiveDir;
        _received.MouseDoubleClick += (_, _) =>
        {
            if (_received.SelectedItem is ListBoxItem { Tag: string p } && File.Exists(p)) Util.OpenExternal(p);
        };
        var receivedCard = Ui.Card(L.T("Dal telefono al PC"), L.T("Le foto e i file inviati dal telefono arrivano qui (doppio clic per aprirli)."),
            Ui.Row(openDir, changeDir, _dir), Box(_received));

        var right = new StackPanel();
        right.Children.Add(sharedCard);
        right.Children.Add(receivedCard);
        right.Children.Add(_log);
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(340) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(24) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new Border { Style = (Style)Application.Current.FindResource("Panel"), Child = qrPanel, VerticalAlignment = VerticalAlignment.Top };
        grid.Children.Add(left);
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);
        Content = Ui.ScrollPage(Ui.Header(L.T("Invia al telefono"), L.T("Scambia foto e file con il telefono sulla stessa rete Wi-Fi, senza cavi, app o cloud.")), grid);

        _on.IsChecked = _share.Running;
        UpdateQr();
        FillShared();
        Loaded += (_, _) =>
        {
            if (!_share.Running) Toggle(true); // si attiva aprendo la pagina
        };
    }

    private static Border Box(UIElement content) => new()
    {
        BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(6),
        Margin = new Thickness(0, 10, 0, 0), Padding = new Thickness(4), Child = content,
    };

    public bool Accepts(string path) => true;
    public void OpenFile(string path) => AddFiles([path]);
    public void AddFiles(IReadOnlyList<string> paths) => AddFiles(paths.ToArray());

    private void AddFiles(string[] paths)
    {
        _share!.Add(paths);
        FillShared();
        if (!_share.Running) Toggle(true);
    }

    private void Toggle(bool on)
    {
        try
        {
            if (on) _share!.Start();
            else _share!.Stop();
        }
        catch (Exception ex)
        {
            Dlg.Error(ex.Message);
        }
        _on.IsChecked = _share!.Running;
        UpdateQr();
    }

    private void UpdateQr()
    {
        if (!_share!.Running)
        {
            _qr.Source = null;
            _url.Text = L.T("Condivisione spenta");
            return;
        }
        using var gen = new QRCodeGenerator();
        using var data = gen.CreateQrCode(_share.Url, QRCodeGenerator.ECCLevel.M);
        var bmp = new BitmapImage();
        bmp.BeginInit();
        bmp.CacheOption = BitmapCacheOption.OnLoad;
        bmp.StreamSource = new MemoryStream(new PngByteQRCode(data).GetGraphic(10));
        bmp.EndInit();
        bmp.Freeze();
        _qr.Source = bmp;
        _url.Text = _share.Url;
    }

    private void FillShared()
    {
        _shared.Items.Clear();
        foreach (var p in _share!.Shared)
            _shared.Items.Add(new ListBoxItem { Content = Entry(p), Tag = p });
        if (_shared.Items.Count == 0) _shared.Items.Add(new ListBoxItem { Content = Ui.Hint(L.T("Nessun file: trascinali qui.")), IsEnabled = false });
    }

    private static UIElement Entry(string p)
    {
        var size = File.Exists(p) ? Util.HumanSize(new FileInfo(p).Length) : "";
        var row = Ui.Row(new Image { Source = ShellIcons.For(p), Width = 16, Height = 16, Margin = new Thickness(0, 0, 8, 0) }, Ui.Label(Path.GetFileName(p)), Ui.Hint("  " + size));
        return row;
    }

    private void OnActivity(string msg) => Dispatcher.InvokeAsync(() => _log.Text = DateTime.Now.ToString("T", L.Culture) + "  " + msg);

    private void OnReceived(string path) => Dispatcher.InvokeAsync(() =>
    {
        _received.Items.Insert(0, new ListBoxItem { Content = Entry(path), Tag = path });
        Controls.QuickTools.Toast(L.T("File ricevuto dal telefono"), Path.GetFileName(path));
    });
}
