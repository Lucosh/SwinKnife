using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;
using Ellipse = System.Windows.Shapes.Ellipse;

namespace SwinKnife.Pages;

/// <summary>
/// Analisi anti-malware: Windows Defender, VirusTotal (70+ motori) ed euristiche locali su processi,
/// file e voci di avvio. Termina, quarantena ed elimina solo su richiesta: niente viene fatto in automatico.
/// </summary>
public sealed class ScannerPage : UserControl, IToolPage
{
    private readonly TextBlock _vtStatus = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly StackPanel _procList = new(), _fileList = new(), _startupList = new(), _quarList = new();
    private readonly TextBlock _procInfo = Ui.Hint(""), _fileInfo = Ui.Hint(""), _startupInfo = Ui.Hint("");
    private readonly TextBlock _fileScanTarget = Ui.Hint("");
    private readonly ProgressBar _vtBar = new() { Height = 6, Margin = new Thickness(0, 8, 0, 0), Visibility = Visibility.Collapsed };
    private CancellationTokenSource? _cts;
    private string? _fileFolder;

    public ScannerPage(MainWindow main)
    {
        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
            Title = L.T("Come usarlo"),
            Message = L.T("Non sostituisce un antivirus sempre attivo, ma ti aiuta a controllare a mano ciò che ti insospettisce. Nulla viene terminato, messo in quarantena o eliminato senza che lo chieda tu. Attenzione: eliminare o quarantenare file di sistema può danneggiare Windows."),
            Margin = new Thickness(0, 0, 0, 12),
        };

        var vtRow = Ui.Card(L.T("VirusTotal"),
            L.T("Per il controllo su 70+ motori serve una chiave API gratuita (virustotal.com). Viene inviato solo l'hash del file, mai il file."),
            Ui.Row(_vtStatus),
            Ui.Row(Ui.Btn(L.T("Imposta chiave…"), SymbolRegular.Key24, (_, _) => SetKey()),
                   Ui.Btn(L.T("Verifica chiave"), SymbolRegular.CheckmarkCircle24, async (_, _) => await VerifyKey())));
        ((StackPanel)vtRow.Child).Children.OfType<StackPanel>().Last().Margin = new Thickness(0, 8, 0, 0);

        var tabs = new TabControl { Margin = new Thickness(0, 4, 0, 0), Background = Brushes.Transparent, BorderThickness = new Thickness(0) };
        tabs.Items.Add(Tab(L.T("Processi"), SymbolRegular.Apps24, ProcTab()));
        tabs.Items.Add(Tab(L.T("File e cartelle"), SymbolRegular.FolderSearch24, FileTab()));
        tabs.Items.Add(Tab(L.T("Avvio"), SymbolRegular.Flash24, StartupTab()));
        tabs.Items.Add(Tab(L.T("Quarantena"), SymbolRegular.ShieldKeyhole24, QuarantineTab()));
        tabs.SelectionChanged += (_, e) => { if (e.AddedItems.Count > 0 && ReferenceEquals(e.AddedItems[0], tabs.Items[3])) RenderQuarantine(); };

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Antivirus"), L.T("Controlla processi, file e avvii con Windows Defender, VirusTotal ed euristiche locali.")),
            warn, vtRow, tabs);

        UpdateVtStatus();
    }

    private static TabItem Tab(string title, SymbolRegular icon, UIElement content)
    {
        var head = new StackPanel { Orientation = Orientation.Horizontal };
        head.Children.Add(new SymbolIcon { Symbol = icon, FontSize = 16, Margin = new Thickness(0, 0, 8, 0) });
        head.Children.Add(new TextBlock { Text = title });
        return new TabItem { Header = head, Content = content, Padding = new Thickness(4, 8, 4, 8) };
    }

    // ------------------------------------------------------------------ VirusTotal chiave
    private void UpdateVtStatus()
    {
        if (VirusTotal.HasKey) { _vtStatus.Text = L.T("✓ Chiave impostata."); _vtStatus.Foreground = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50)); }
        else { _vtStatus.Text = L.T("Nessuna chiave: il controllo VirusTotal è disattivato (Defender ed euristiche funzionano comunque)."); _vtStatus.Foreground = Ui.Res("TextFillColorSecondaryBrush"); }
    }

    private void SetKey()
    {
        var k = Dlg.Prompt(L.T("Chiave VirusTotal"), L.T("Incolla la tua chiave API (gratuita su virustotal.com):"), VirusTotal.ApiKey ?? "", password: true);
        if (k == null) return;
        VirusTotal.ApiKey = k;
        UpdateVtStatus();
    }

    private async Task VerifyKey()
    {
        if (!VirusTotal.HasKey) { Dlg.Info(L.T("Imposta prima una chiave.")); return; }
        MainWindow.Notify(L.T("Verifica della chiave…"), 0);
        var r = await VirusTotal.TestKeyAsync(CancellationToken.None);
        MainWindow.Notify("");
        Dlg.Info(r.Status switch
        {
            VtStatus.BadKey => L.T("La chiave non è valida."),
            VtStatus.RateLimited => L.T("Chiave valida, ma hai raggiunto il limite di richieste al minuto."),
            VtStatus.Error => L.T("Impossibile contattare VirusTotal: ") + r.Message,
            _ => L.T("La chiave funziona."),
        });
    }

    // ------------------------------------------------------------------ scheda Processi
    private UIElement ProcTab()
    {
        var bar = Ui.Row(
            Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => LoadProcesses(), primary: true),
            Ui.Btn(L.T("Controlla su VirusTotal i sospetti"), SymbolRegular.CloudArrowUp24, async (_, _) => await VtSuspects(_procItems, _procList, ItemKind.Process)),
            _procInfo);
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(bar);
        panel.Children.Add(_vtBar);
        panel.Children.Add(_procList);
        return panel;
    }

    private List<ScanItem> _procItems = new();

    private async void LoadProcesses()
    {
        _procInfo.Text = L.T("Lettura dei processi…");
        _procList.Children.Clear();
        _procItems = await Task.Run(ThreatScan.Processes);
        _procInfo.Text = L.T($"{_procItems.Count} processi · {_procItems.Count(i => i.Flags.Count > 0)} da controllare");
        Render(_procItems, _procList, ItemKind.Process);
    }

    // ------------------------------------------------------------------ scheda File
    private UIElement FileTab()
    {
        var onlyExe = new CheckBox { Content = L.T("Solo file eseguibili (più veloce)"), IsChecked = true, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
        var bar = Ui.Row(
            Ui.Btn(L.T("Scegli cartella…"), SymbolRegular.FolderOpen24, async (_, _) =>
            {
                var d = Dlg.PickFolder(L.T("Cartella da analizzare"), "scan");
                if (d != null) { _fileFolder = d; await ScanFolder(d, onlyExe.IsChecked == true); }
            }, primary: true),
            Ui.Btn(L.T("Scegli file…"), SymbolRegular.Document24, async (_, _) =>
            {
                var f = Dlg.OpenFile(L.T("File da analizzare"), null, "scan");
                if (f != null) await ScanSingle(f);
            }),
            onlyExe);
        var defenderRow = Ui.Row(
            Ui.Btn(L.T("Scansione rapida (Defender)"), SymbolRegular.ShieldTask24, async (_, _) => await RunDefender(DefenderScan.Quick, null)),
            Ui.Btn(L.T("Scansione completa (Defender)"), SymbolRegular.Shield24, async (_, _) => await RunDefender(DefenderScan.Full, null)),
            Ui.Btn(L.T("Scansiona la cartella con Defender"), SymbolRegular.FolderArrowRight24, async (_, _) =>
            {
                if (_fileFolder == null) { Dlg.Info(L.T("Scegli prima una cartella.")); return; }
                await RunDefender(DefenderScan.Custom, _fileFolder);
            }));
        defenderRow.Margin = new Thickness(0, 8, 0, 0);

        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(bar);
        panel.Children.Add(defenderRow);
        panel.Children.Add(Ui.Row(_fileScanTarget));
        panel.Children.Add(_fileInfo);
        panel.Children.Add(_fileList);
        _fileInfo.Margin = new Thickness(0, 8, 0, 4);
        return panel;
    }

    private List<ScanItem> _fileItems = new();

    private async Task ScanFolder(string folder, bool onlyExe)
    {
        _fileScanTarget.Text = folder;
        _fileInfo.Text = L.T("Analisi in corso…");
        _fileList.Children.Clear();
        _cts = new CancellationTokenSource();
        try
        {
            var ct = _cts.Token;
            _fileItems = await Task.Run(() => ThreatScan.Files(folder, onlyExe, ct), ct);
            _fileInfo.Text = L.T($"{_fileItems.Count} file · {_fileItems.Count(i => i.Flags.Count > 0)} da controllare");
            Render(_fileItems, _fileList, ItemKind.File);
        }
        catch (OperationCanceledException) { _fileInfo.Text = L.T("Analisi interrotta."); }
        catch (Exception ex) { _fileInfo.Text = ex.Message; }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private async Task ScanSingle(string file)
    {
        _fileScanTarget.Text = file;
        var item = new ScanItem { Kind = ItemKind.File, Name = Path.GetFileName(file), Path = file };
        ThreatScan.Inspect(item);
        _fileItems = new List<ScanItem> { item };
        _fileInfo.Text = "";
        Render(_fileItems, _fileList, ItemKind.File);
        if (VirusTotal.HasKey) await VtOne(item, _fileItems, _fileList, ItemKind.File);
    }

    private async Task RunDefender(DefenderScan type, string? path)
    {
        MainWindow.Notify(L.T("Scansione di Windows Defender in corso… (può richiedere diversi minuti)"), 0);
        _cts = new CancellationTokenSource();
        try
        {
            var r = await Defender.ScanAsync(type, path, _cts.Token);
            MainWindow.Notify("");
            if (!r.Ok) { Dlg.Error(r.Message); return; }
            if (r.Threats.Count == 0) Dlg.Info(r.Message, L.T("Windows Defender"));
            else Dlg.Info(r.Message + "\n\n" + string.Join("\n", r.Threats.Select(t => $"• {t.Name}\n   {t.Resource}")), L.T("Windows Defender"));
        }
        catch (OperationCanceledException) { MainWindow.Notify(L.T("Scansione interrotta.")); }
        catch (Exception ex) { MainWindow.Notify(""); Dlg.Error(ex.Message); }
        finally { _cts?.Dispose(); _cts = null; }
    }

    // ------------------------------------------------------------------ scheda Avvio
    private UIElement StartupTab()
    {
        var bar = Ui.Row(
            Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => LoadStartup(), primary: true),
            Ui.Btn(L.T("Controlla su VirusTotal i sospetti"), SymbolRegular.CloudArrowUp24, async (_, _) => await VtSuspects(_startupItems, _startupList, ItemKind.Startup)),
            _startupInfo);
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(bar);
        panel.Children.Add(_startupList);
        return panel;
    }

    private List<ScanItem> _startupItems = new();

    private async void LoadStartup()
    {
        _startupInfo.Text = L.T("Lettura…");
        _startupList.Children.Clear();
        _startupItems = await Task.Run(ThreatScan.Startup);
        _startupInfo.Text = L.T($"{_startupItems.Count} voci di avvio");
        Render(_startupItems, _startupList, ItemKind.Startup);
    }

    // ------------------------------------------------------------------ scheda Quarantena
    private UIElement QuarantineTab()
    {
        var panel = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
        panel.Children.Add(Ui.Row(Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => RenderQuarantine())));
        panel.Children.Add(_quarList);
        return panel;
    }

    private void RenderQuarantine()
    {
        _quarList.Children.Clear();
        var items = Quarantine.Load();
        if (items.Count == 0) { _quarList.Children.Add(Ui.Hint(L.T("La quarantena è vuota."))); return; }
        foreach (var q in items.OrderByDescending(i => i.QuarantinedAt))
        {
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock { Text = q.Name, FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            text.Children.Add(Ui.Hint(q.OriginalPath));
            text.Children.Add(Ui.Hint(L.T($"In quarantena dal {q.QuarantinedAt:dd/MM/yyyy HH:mm} · {Util.HumanSize(q.Size)} · {q.Reason}")));
            var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
            actions.Children.Add(Ui.Btn(L.T("Ripristina"), SymbolRegular.ArrowUndo24, (_, _) =>
            {
                if (!Dlg.Confirm(L.T($"Ripristinare «{q.Name}» in {q.OriginalPath}?"))) return;
                try { Quarantine.Restore(q); MainWindow.Notify(L.T("File ripristinato.")); RenderQuarantine(); }
                catch (Exception ex) { Dlg.Error(ex.Message); }
            }));
            actions.Children.Add(Ui.Btn(L.T("Elimina"), SymbolRegular.Delete24, (_, _) =>
            {
                if (!Dlg.Confirm(L.T($"Eliminare definitivamente «{q.Name}»? Non sarà più recuperabile."))) return;
                Quarantine.Delete(q); MainWindow.Notify(L.T("File eliminato.")); RenderQuarantine();
            }));
            _quarList.Children.Add(RowBorder(text, actions));
        }
    }

    // ------------------------------------------------------------------ rendering righe
    private void Render(List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        host.Children.Clear();
        if (items.Count == 0) { host.Children.Add(Ui.Hint(L.T("Niente da mostrare."))); return; }
        foreach (var it in items) host.Children.Add(RowView(it, items, host, kind));
    }

    private FrameworkElement RowView(ScanItem it, List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        var dot = new Ellipse { Width = 10, Height = 10, Fill = RiskBrush(it.Risk), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 10, 0) };
        var title = new StackPanel { Orientation = Orientation.Horizontal };
        title.Children.Add(dot);
        title.Children.Add(new TextBlock { Text = it.Name + (kind == ItemKind.Process ? $"  (PID {it.Pid})" : ""), FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush"), VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(title);
        if (it.Path.Length > 0) text.Children.Add(Ui.Hint(it.Path));
        else text.Children.Add(Ui.Hint(L.T("percorso non accessibile (processo protetto)")));
        text.Children.Add(Ui.Hint(SignText(it) + VtText(it)));
        if (it.Flags.Count > 0)
            text.Children.Add(new TextBlock { Text = "⚠ " + string.Join(" · ", it.Flags), TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x3E)), FontSize = 12 });

        var actions = new StackPanel { Orientation = Orientation.Horizontal, VerticalAlignment = VerticalAlignment.Center };
        if (it.Path.Length > 0)
            actions.Children.Add(Ui.Btn("VirusTotal", SymbolRegular.CloudArrowUp24, async (_, _) => await VtOne(it, items, host, kind)));
        if (kind == ItemKind.Process)
        {
            actions.Children.Add(Ui.Btn(L.T("Termina"), SymbolRegular.Stop24, (_, _) => KillProcess(it)));
            if (it.Path.Length > 0) actions.Children.Add(Ui.Btn(L.T("Quarantena"), SymbolRegular.ShieldKeyhole24, (_, _) => QuarantineProcess(it)));
        }
        else if (kind == ItemKind.File)
        {
            actions.Children.Add(Ui.Btn(L.T("Quarantena"), SymbolRegular.ShieldKeyhole24, (_, _) => QuarantineFile(it, items, host, kind)));
            actions.Children.Add(Ui.Btn(L.T("Elimina"), SymbolRegular.Delete24, (_, _) => DeleteFile(it, items, host, kind)));
        }
        else if (kind == ItemKind.Startup)
        {
            if (it.Startup is { Enabled: true }) actions.Children.Add(Ui.Btn(L.T("Disattiva"), SymbolRegular.PlugDisconnected24, (_, _) => DisableStartup(it)));
            if (it.Path.Length > 0) actions.Children.Add(Ui.Btn(L.T("Quarantena"), SymbolRegular.ShieldKeyhole24, (_, _) => QuarantineFile(it, items, host, kind)));
        }
        return RowBorder(text, actions);
    }

    private static Border RowBorder(UIElement text, UIElement actions)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 6, 0, 6) };
        DockPanel.SetDock(actions, Dock.Right);
        dock.Children.Add(actions);
        dock.Children.Add(text);
        return new Border { Child = dock, Padding = new Thickness(12, 4, 8, 4), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") };
    }

    private static Brush RiskBrush(Risk r) => new SolidColorBrush(r switch
    {
        Risk.Malicious => Color.FromRgb(0xE5, 0x4B, 0x4B),
        Risk.Suspicious => Color.FromRgb(0xF0, 0x9A, 0x3E),
        Risk.Clean => Color.FromRgb(0x4C, 0xAF, 0x50),
        _ => Color.FromRgb(0x9A, 0xA5, 0xB3),
    });

    private static string SignText(ScanItem it) => it.Sign switch
    {
        SignStatus.Valid => it.Signer.Length > 0 ? L.T($"Firmato: {it.Signer}") : L.T("Firmato"),
        SignStatus.Invalid => L.T("Firma non valida"),
        _ => L.T("Non firmato"),
    };

    private static string VtText(ScanItem it) => it.Vt switch
    {
        null => "",
        { Status: VtStatus.Malicious } v => L.T($"   ·   VirusTotal: {v.Malicious}/{v.Total} motori la segnalano"),
        { Status: VtStatus.Suspicious } v => L.T($"   ·   VirusTotal: {v.Suspicious}/{v.Total} sospetta"),
        { Status: VtStatus.Clean } v => L.T($"   ·   VirusTotal: pulito (0/{v.Total})"),
        { Status: VtStatus.Unknown } => L.T("   ·   VirusTotal: file sconosciuto"),
        { Status: VtStatus.RateLimited } => L.T("   ·   VirusTotal: limite raggiunto"),
        { Status: VtStatus.BadKey } => L.T("   ·   VirusTotal: chiave non valida"),
        { Status: VtStatus.NoKey } => "",
        var v => L.T("   ·   VirusTotal: errore (") + v!.Message + ")",
    };

    // ------------------------------------------------------------------ azioni VirusTotal
    private async Task VtOne(ScanItem it, List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        if (!VirusTotal.HasKey) { Dlg.Info(L.T("Imposta prima una chiave VirusTotal.")); return; }
        if (it.Path.Length == 0 || !File.Exists(it.Path)) { Dlg.Info(L.T("File non accessibile.")); return; }
        MainWindow.Notify(L.T($"Controllo di {it.Name} su VirusTotal…"), 0);
        it.Vt = await VirusTotal.LookupFileAsync(it.Path, CancellationToken.None);
        MainWindow.Notify("");
        Render(items, host, kind);
    }

    private async Task VtSuspects(List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        if (!VirusTotal.HasKey) { Dlg.Info(L.T("Imposta prima una chiave VirusTotal.")); return; }
        var todo = items.Where(i => i.Flags.Count > 0 && i.Path.Length > 0 && i.Vt == null && File.Exists(i.Path)).ToList();
        if (todo.Count == 0) { Dlg.Info(L.T("Non ci sono voci sospette da controllare.")); return; }
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _vtBar.Visibility = Visibility.Visible;
        _vtBar.Maximum = todo.Count;
        _vtBar.Value = 0;
        try
        {
            foreach (var it in todo)
            {
                if (ct.IsCancellationRequested) break;
                MainWindow.Notify(L.T($"VirusTotal: {it.Name}…"), 0);
                it.Vt = await VirusTotal.LookupFileAsync(it.Path, ct);
                _vtBar.Value++;
                if (it.Vt.Status == VtStatus.RateLimited)
                {
                    Dlg.Info(L.T("Hai raggiunto il limite di richieste di VirusTotal (4 al minuto nel piano gratuito). Riprova tra un minuto."));
                    break;
                }
            }
        }
        catch (OperationCanceledException) { }
        finally
        {
            _vtBar.Visibility = Visibility.Collapsed;
            MainWindow.Notify("");
            _cts?.Dispose(); _cts = null;
            Render(items, host, kind);
        }
    }

    // ------------------------------------------------------------------ azioni su processi/file
    private void KillProcess(ScanItem it)
    {
        if (!Dlg.Confirm(L.T($"Terminare il processo «{it.Name}» (PID {it.Pid})? I dati non salvati andranno persi."))) return;
        try { using var p = Process.GetProcessById(it.Pid); p.Kill(true); MainWindow.Notify(L.T($"Processo «{it.Name}» terminato.")); LoadProcesses(); }
        catch (Exception ex) { Dlg.Error(L.T("Impossibile terminare il processo (potrebbe servire l'amministratore):\n") + ex.Message); }
    }

    private void QuarantineProcess(ScanItem it)
    {
        if (!Dlg.Confirm(L.T($"Terminare «{it.Name}» e mettere in quarantena il file?\n{it.Path}"))) return;
        try
        {
            try { using var p = Process.GetProcessById(it.Pid); p.Kill(true); p.WaitForExit(3000); } catch { }
            var q = Quarantine.Add(it.Path, L.T("processo messo in quarantena manualmente"));
            MainWindow.Notify(L.T($"«{q.Name}» messo in quarantena."));
            LoadProcesses();
        }
        catch (Exception ex) { Dlg.Error(L.T("Quarantena non riuscita (potrebbe servire l'amministratore):\n") + ex.Message); }
    }

    private void QuarantineFile(ScanItem it, List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        if (!Dlg.Confirm(L.T($"Mettere in quarantena questo file?\n{it.Path}\n\nPotrai ripristinarlo dalla scheda Quarantena."))) return;
        try { Quarantine.Add(it.Path, it.Flags.Count > 0 ? string.Join("; ", it.Flags) : L.T("scelto manualmente")); items.Remove(it); Render(items, host, kind); MainWindow.Notify(L.T("File messo in quarantena.")); }
        catch (Exception ex) { Dlg.Error(L.T("Quarantena non riuscita (file in uso o permessi mancanti):\n") + ex.Message); }
    }

    private void DeleteFile(ScanItem it, List<ScanItem> items, StackPanel host, ItemKind kind)
    {
        if (!Dlg.Confirm(L.T($"Eliminare definitivamente questo file?\n{it.Path}\n\nNon sarà recuperabile. Meglio la quarantena se non sei sicuro."))) return;
        try { File.Delete(it.Path); items.Remove(it); Render(items, host, kind); MainWindow.Notify(L.T("File eliminato.")); }
        catch (Exception ex) { Dlg.Error(L.T("Eliminazione non riuscita (file in uso o permessi mancanti):\n") + ex.Message); }
    }

    private void DisableStartup(ScanItem it)
    {
        if (it.Startup == null) return;
        try { SysInfo.SetEnabled(it.Startup, false); MainWindow.Notify(L.T($"«{it.Name}» disattivato all'avvio.")); LoadStartup(); }
        catch (Exception ex) { Dlg.Error(L.T("Per cambiare questa voce potrebbe servire l'amministratore:\n") + ex.Message); }
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un'analisi in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
