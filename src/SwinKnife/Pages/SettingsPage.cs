using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Impostazioni: integrazione con Esplora risorse, componenti aggiuntivi, dati dell'app.</summary>
public sealed class SettingsPage : UserControl, IToolPage
{
    private readonly ToggleSwitch _menu = new() { Content = L.T("Mostra SwinKnife nel menu del tasto destro") };
    private readonly StackPanel _components = new();

    public SettingsPage(MainWindow main)
    {
        _menu.IsChecked = ShellIntegration.IsInstalled();
        _menu.Click += (_, _) =>
        {
            try
            {
                if (_menu.IsChecked == true) ShellIntegration.Install();
                else ShellIntegration.Uninstall();
                MainWindow.Notify(_menu.IsChecked == true ? L.T("Menu del tasto destro attivato") : L.T("Menu del tasto destro rimosso"));
            }
            catch (Exception ex)
            {
                Dlg.Error(L.T("Impossibile modificare il menu:\n") + ex.Message);
                _menu.IsChecked = ShellIntegration.IsInstalled();
            }
        };
        // ---- lingua
        var language = new ComboBox { Width = 220, HorizontalAlignment = HorizontalAlignment.Left };
        foreach (var (code, name) in L.Languages) language.Items.Add(new ComboBoxItem { Content = name, Tag = code });
        language.SelectedIndex = Array.FindIndex(L.Languages, l => l.Code == L.Code);
        language.SelectionChanged += (_, _) =>
        {
            var code = (string)((ComboBoxItem)language.SelectedItem).Tag;
            if (code == L.Code) return;
            Settings.Set("language", code);
            var name = L.Languages.First(l => l.Code == code).Name;
            if (Dlg.Confirm(L.T("Per cambiare lingua SwinKnife deve riavviarsi. Riavviare adesso?") + $"\n\n({name})", AppInfo.Name, L.T("Riavvia"), L.T("Più tardi")))
                Restart();
        };
        var languageCard = Ui.Card(L.T("Lingua"), L.T("La lingua dell'interfaccia. Le traduzioni si possono migliorare su GitHub."), language);

        // ---- aggiornamenti
        var auto = new ToggleSwitch { Content = L.T("Controlla automaticamente all'avvio (una volta al giorno)"), IsChecked = Updater.AutoCheck, Margin = new Thickness(0, 0, 20, 0) };
        auto.Click += (_, _) => Updater.AutoCheck = auto.IsChecked == true;
        var updateCard = Ui.Card(L.T("Aggiornamenti"),
            L.T($"Versione installata: {AppInfo.Version}. Le nuove versioni arrivano da GitHub: SwinKnife le scarica, ne verifica l'integrità e si aggiorna da solo."),
            Ui.Row(auto,
                Ui.Btn(L.T("Controlla ora"), SymbolRegular.ArrowSync24, async (_, _) => await UpdateUi.CheckAsync(manual: true)),
                Ui.Btn(L.T("Novità"), SymbolRegular.Open24, (_, _) => Util.OpenExternal($"https://github.com/{Updater.Repo}/releases"))));

        var menuCard = Ui.Card(L.T("Menu del tasto destro in Esplora risorse"),
            L.T("Su file e cartelle compare la voce SwinKnife: apri, converti, comprimi, OCR, modifica PDF e foto, analizza spazio, cerca duplicati, rinomina. In Windows 11 la trovi in “Mostra altre opzioni” (o premendo Maiusc+F10)."), _menu);

        var dataCard = Ui.Card(L.T("Dati dell'app"), AppInfo.DataDir,
            Ui.Row(
                Ui.Btn(L.T("Apri cartella"), SymbolRegular.FolderOpen24, (_, _) => Util.OpenFolder(AppInfo.DataDir)),
                Ui.Btn(L.T("Apri registro errori"), SymbolRegular.DocumentText24, (_, _) =>
                {
                    if (File.Exists(AppInfo.LogFile)) Util.OpenExternal(AppInfo.LogFile);
                    else Dlg.Info(L.T("Nessun errore registrato."));
                }),
                Ui.Btn(L.T("Svuota file temporanei"), SymbolRegular.Delete24, (_, _) =>
                {
                    AppInfo.CleanTemp();
                    MainWindow.Notify(L.T("File temporanei di SwinKnife eliminati"));
                })));

        var about = Ui.Card($"SwinKnife {AppInfo.Version}", null, Ui.KeyValues(
        [
            (L.T("Piattaforma"), L.T($".NET {Environment.Version} · {(Environment.Is64BitProcess ? "64 bit" : "32 bit")}")),
            (L.T("Librerie"), "WPF-UI, PDFium, PDFsharp, Magick.NET, ClosedXML, Markdig, SharpCompress, SharpZipLib, AvalonEdit, QRCoder, WebView2, FFmpeg"),
            (L.T("Eseguibile"), Environment.ProcessPath ?? ""),
        ]));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Impostazioni"), L.T("Integrazione con Windows e componenti aggiuntivi.")),
            languageCard,
            updateCard,
            menuCard,
            Ui.Card(L.T("Componenti"), L.T("Strumenti esterni usati da alcune funzioni."), _components),
            dataCard, about);
        Loaded += async (_, _) => await RefreshComponents();
    }

    private static void Restart()
    {
        if (MainWindow.Instance is { } w && !w.CanCloseAllPages()) return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(Environment.ProcessPath!, "--restart --page settings") { UseShellExecute = false });
            Application.Current.Shutdown();
        }
        catch (Exception ex)
        {
            Dlg.Error(ex.Message);
        }
    }

    private async Task RefreshComponents()
    {
        _components.Children.Clear();
        string webview;
        try { webview = CoreWebView2Environment.GetAvailableBrowserVersionString(); }
        catch { webview = L.T("non installato"); }
        var ffmpeg = Ffmpeg.Find();
        var seven = SevenZip.Find();
        var office = new[] { ("Word", OfficeSession.WordAvailable), ("Excel", OfficeSession.ExcelAvailable), ("PowerPoint", OfficeSession.PowerPointAvailable) }
            .Where(x => x.Item2).Select(x => x.Item1).ToList();
        string ocr;
        try { ocr = Ocr.Available ? string.Join(", ", Windows.Media.Ocr.OcrEngine.AvailableRecognizerLanguages.Select(l => l.DisplayName)) : L.T("nessuna lingua installata"); }
        catch { ocr = L.T("non disponibile"); }
        _components.Children.Add(Ui.KeyValues(
        [
            (L.T("FFmpeg (audio/video)"), ffmpeg ?? L.T("non installato: verrà scaricato al primo uso")),
            (L.T("7-Zip (archivi .7z)"), seven ?? L.T("non installato: verrà scaricato al primo uso (600 KB)")),
            (L.T("Microsoft Office"), office.Count > 0 ? string.Join(", ", office) : OfficeSession.SofficePath() != null ? "LibreOffice" : L.T("non installato")),
            (L.T("OCR di Windows"), ocr),
            (L.T("Edge WebView2"), webview),
        ], 200));
        var row = Ui.Row();
        row.Margin = new Thickness(0, 10, 0, 0);
        if (ffmpeg == null)
            row.Children.Add(Ui.Btn(L.T("Scarica FFmpeg"), SymbolRegular.ArrowDownload24, async (_, _) =>
            {
                MainWindow.Notify(L.T("Download di FFmpeg in corso…"), 0);
                try
                {
                    await Ffmpeg.DownloadAsync(new Progress<(double, string)>(p => MainWindow.Notify(p.Item2, 0)), CancellationToken.None);
                    MainWindow.Notify(L.T("FFmpeg installato"));
                }
                catch (Exception ex) { Dlg.Error(L.T("Download non riuscito:\n") + ex.Message); }
                await RefreshComponents();
            }));
        if (seven == null)
            row.Children.Add(Ui.Btn(L.T("Scarica 7-Zip"), SymbolRegular.ArrowDownload24, async (_, _) =>
            {
                try
                {
                    await SevenZip.DownloadAsync(CancellationToken.None);
                    MainWindow.Notify("7-Zip installato");
                }
                catch (Exception ex) { Dlg.Error(L.T("Download non riuscito:\n") + ex.Message); }
                await RefreshComponents();
            }));
        if (row.Children.Count > 0) _components.Children.Add(row);
    }
}
