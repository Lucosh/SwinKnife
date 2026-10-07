using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Mostra le reti Wi-Fi salvate sul PC con le relative password, e permette di copiarle o esportarle.</summary>
public sealed class WifiPasswordsPage : UserControl, IToolPage
{
    private readonly StackPanel _list = new();
    private readonly TextBlock _count = Ui.Hint("");
    private List<SavedWifi> _nets = new();

    public WifiPasswordsPage(MainWindow main)
    {
        var refresh = Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => Load(), primary: true);
        var export = Ui.Btn(L.T("Esporta tutto…"), SymbolRegular.Save24, (_, _) => Export());
        var card = Ui.Card(L.T("Reti salvate"), null, Ui.Row(refresh, export, _count), _list);
        Content = Ui.ScrollPage(
            Ui.Header(L.T("Password Wi-Fi salvate"), L.T("Le reti Wi-Fi memorizzate su questo PC e le loro password.")),
            card);
        Loaded += (_, _) => { if (_nets.Count == 0) Load(); };
    }

    private async void Load()
    {
        _list.Children.Clear();
        _count.Text = L.T("Lettura…");
        try
        {
            _nets = await Task.Run(WifiPasswords.All);
            _count.Text = L.T($"{_nets.Count} reti");
            if (_nets.Count == 0)
            {
                _list.Children.Add(Ui.Hint(L.T("Nessuna rete Wi-Fi salvata trovata su questo PC.")));
                return;
            }
            var anyKey = _nets.Any(n => n.Password.Length > 0);
            foreach (var n in _nets) _list.Children.Add(Row(n));
            if (!anyKey)
                _list.Children.Insert(0, new InfoBar
                {
                    Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                    Title = L.T("Password non visibili"),
                    Message = L.T("Windows non ha mostrato le password. Di solito succede con le reti aziendali; per alcune può servire avviare SwinKnife come amministratore."),
                    Margin = new Thickness(0, 0, 0, 10),
                });
        }
        catch (Exception ex) { _count.Text = ""; Dlg.Error(ex.Message); }
    }

    private FrameworkElement Row(SavedWifi n)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 6) };
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var left = new StackPanel();
        left.Children.Add(new TextBlock { Text = n.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
        var pw = n.Password.Length > 0 ? n.Password : L.T("(nessuna password / rete aperta o protetta diversamente)");
        left.Children.Add(new System.Windows.Controls.TextBox
        {
            Text = pw, IsReadOnly = true, BorderThickness = new Thickness(0), Background = Brushes.Transparent,
            Padding = new Thickness(0), FontFamily = new FontFamily("Consolas, monospace"),
            Foreground = n.Password.Length > 0 ? Ui.Res("TextFillColorPrimaryBrush") : Ui.Res("TextFillColorTertiaryBrush"),
        });
        left.Children.Add(Ui.Hint(n.Authentication));

        var copy = Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia la password"), (_, _) =>
        {
            if (n.Password.Length == 0) return;
            try { Clipboard.SetText(n.Password); MainWindow.Notify(L.T($"Password di «{n.Name}» copiata.")); } catch { }
        });
        copy.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(copy, 1);
        grid.Children.Add(left);
        grid.Children.Add(copy);
        return new Border
        {
            Child = grid, Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6),
            Background = Ui.Res("CardBackgroundFillColorDefaultBrush"),
        };
    }

    private void Export()
    {
        if (_nets.Count == 0) { Dlg.Info(L.T("Non c'è niente da esportare.")); return; }
        var dst = Dlg.SaveFile(L.T("Esporta le reti Wi-Fi"), "reti-wifi.txt", L.T("File di testo|*.txt"), "wifi_save");
        if (dst == null) return;
        var sb = new StringBuilder();
        foreach (var n in _nets) sb.AppendLine($"{n.Name}\t{n.Authentication}\t{n.Password}");
        try { File.WriteAllText(dst, sb.ToString(), Encoding.UTF8); Util.Reveal(dst); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }
}
