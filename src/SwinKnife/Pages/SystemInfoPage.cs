using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Clipboard = System.Windows.Clipboard;
using Image = System.Windows.Controls.Image;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Informazioni di sistema: hardware, dischi, batteria, rete, programmi all'avvio.</summary>
public sealed class SystemInfoPage : UserControl, IToolPage
{
    private readonly StackPanel _computer = new(), _cpu = new(), _gpu = new(), _disks = new(), _battery = new(), _network = new(), _startup = new();
    private readonly Border _batteryCard;
    private readonly TextBlock _usage = new() { FontSize = 13 };
    private readonly ProgressBar _cpuBar = new() { Maximum = 100, Width = 160, Height = 6 }, _ramBar = new() { Maximum = 100, Width = 160, Height = 6 };
    private readonly TextBlock _cpuText = new() { Width = 60 }, _ramText = new() { MinWidth = 200 };
    private readonly DispatcherTimer _timer = new() { Interval = TimeSpan.FromSeconds(1) };
    private readonly Dictionary<string, List<InfoRow>> _summary = new();
    private long _idle, _kernel, _user;
    private bool _loading;

    public SystemInfoPage(MainWindow main)
    {
        var tools = new WrapPanel();
        foreach (var (label, cmd, icon) in new[]
                 {
                     (L.T("Gestione dispositivi"), "devmgmt.msc", SymbolRegular.DeveloperBoard24),
                     (L.T("Gestione disco"), "diskmgmt.msc", SymbolRegular.Storage24),
                     (L.T("Monitoraggio risorse"), "resmon.exe", SymbolRegular.DataUsage24),
                     (L.T("Gestione attività"), "taskmgr.exe", SymbolRegular.Apps24),
                     (L.T("Informazioni di sistema"), "msinfo32.exe", SymbolRegular.Info24),
                     (L.T("Visualizzatore eventi"), "eventvwr.msc", SymbolRegular.TextBulletListSquare24),
                 })
        {
            var b = Ui.Btn(label, icon, (_, _) => Launch(cmd));
            b.Margin = new Thickness(0, 0, 6, 6);
            tools.Children.Add(b);
        }

        var usageRow = Ui.Row(Ui.Label(L.T("Processore")), Spacer(10), _cpuBar, Spacer(8), _cpuText, Spacer(20), Ui.Label(L.T("Memoria")), Spacer(10), _ramBar, Spacer(8), _ramText);

        _batteryCard = Ui.Card(L.T("Batteria"), null, _battery);
        _batteryCard.Visibility = Visibility.Collapsed;

        var toolbar = Ui.Row(
            Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowClockwise24, (_, _) => _ = Load()),
            Ui.Btn(L.T("Copia riepilogo"), SymbolRegular.Copy24, (_, _) => CopySummary(), L.T("Copia tutte le informazioni come testo (utile per chiedere assistenza)")));
        toolbar.Margin = new Thickness(0, 0, 0, 14);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Informazioni di sistema"), L.T("Cosa c'è dentro il tuo PC, in che stato sono dischi e batteria e cosa parte all'accensione.")),
            toolbar,
            Ui.Card(L.T("Utilizzo in questo momento"), null, usageRow),
            Ui.Card(L.T("Computer"), null, _computer),
            Ui.Card(L.T("Processore e memoria"), null, _cpu),
            Ui.Card(L.T("Grafica e schermi"), null, _gpu),
            Ui.Card(L.T("Dischi"), L.T("Lo stato di salute viene letto dai dischi stessi (S.M.A.R.T.)."), _disks),
            _batteryCard,
            Ui.Card(L.T("Rete"), null, _network),
            Ui.Card(L.T("Programmi all'avvio"), L.T("Disattiva quelli che non ti servono subito: il PC si accenderà più in fretta. Non vengono disinstallati."), _startup),
            Ui.Card(L.T("Strumenti di Windows"), null, tools));

        _timer.Tick += (_, _) => UpdateUsage();
        IsVisibleChanged += (_, _) =>
        {
            if (IsVisible)
            {
                UpdateUsage();
                _timer.Start();
            }
            else _timer.Stop();
        };
        Loaded += async (_, _) =>
        {
            if (_summary.Count == 0) await Load();
        };
    }

    private static FrameworkElement Spacer(double w) => new Border { Width = w };

    private static void Launch(string cmd)
    {
        try { Process.Start(new ProcessStartInfo(cmd) { UseShellExecute = true }); }
        catch (Exception ex) { Dlg.Error(ex.Message); }
    }

    private static TextBlock Loading() => Ui.Hint(L.T("Lettura in corso…"));

    private async Task Load()
    {
        if (_loading) return;
        _loading = true;
        _summary.Clear();
        foreach (var p in new[] { _computer, _cpu, _gpu, _disks, _network, _startup })
        {
            p.Children.Clear();
            p.Children.Add(Loading());
        }
        try
        {
            // le interrogazioni WMI sono lente: le eseguo in parallelo
            var computer = Task.Run(SysInfo.Computer);
            var cpu = Task.Run(SysInfo.Processor);
            var gpu = Task.Run(SysInfo.Graphics);
            var disks = Task.Run(SysInfo.Disks);
            var battery = Task.Run(SysInfo.Battery);
            var network = Task.Run(SysInfo.Network);
            var startup = Task.Run(SysInfo.StartupItems);

            Fill(_computer, L.T("Computer"), await computer);
            Fill(_cpu, L.T("Processore e memoria"), await cpu);
            Fill(_gpu, L.T("Grafica"), await gpu);
            ShowDisks(await disks);
            ShowBattery(await battery);
            ShowNetwork(await network);
            ShowStartup(await startup);
        }
        catch (Exception ex)
        {
            AppInfo.Log(ex, "sysinfo");
            Dlg.Error(L.T("Lettura delle informazioni non riuscita:\n") + ex.Message);
        }
        finally
        {
            _loading = false;
        }
    }

    private void Fill(StackPanel panel, string section, List<InfoRow> rows)
    {
        panel.Children.Clear();
        _summary[section] = rows;
        panel.Children.Add(rows.Count == 0 ? Ui.Hint(L.T("Informazioni non disponibili.")) : Ui.KeyValues(rows.Select(r => (r.Key, r.Value)), 200));
    }

    // ------------------------------------------------------------------ dischi
    private static Border Badge(string text, int level) => new()
    {
        CornerRadius = new CornerRadius(10), Padding = new Thickness(10, 2, 10, 3), Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center,
        Background = new SolidColorBrush(level switch
        {
            0 => Color.FromRgb(0x10, 0x8A, 0x3B),
            1 => Color.FromRgb(0xC7, 0x7C, 0x00),
            2 => Color.FromRgb(0xC4, 0x2B, 0x1C),
            _ => Color.FromRgb(0x70, 0x70, 0x70),
        }),
        Child = new TextBlock { Text = text, Foreground = Brushes.White, FontSize = 12, FontWeight = FontWeights.SemiBold },
    };

    private void ShowDisks(List<DiskInfo> disks)
    {
        _disks.Children.Clear();
        if (disks.Count == 0)
        {
            _disks.Children.Add(Ui.Hint(L.T("Nessun disco trovato.")));
            return;
        }
        var first = true;
        foreach (var d in disks)
        {
            var box = new StackPanel { Margin = new Thickness(0, first ? 0 : 18, 0, 0) };
            first = false;
            var title = Ui.Row(new SymbolIcon { Symbol = d.Kind.Contains("USB") ? SymbolRegular.UsbStick24 : SymbolRegular.Storage24, FontSize = 22, Margin = new Thickness(0, 0, 10, 0) },
                Ui.Label($"{d.Name} · {Util.HumanSize(d.Size)}", 14, true), Badge(d.Health, d.HealthLevel));
            title.Margin = new Thickness(0, 0, 0, 6);
            box.Children.Add(title);
            box.Children.Add(Ui.KeyValues(d.Details.Select(r => (r.Key, r.Value)), 200));
            foreach (var v in d.Volumes)
            {
                var used = v.Total - v.Free;
                var pct = 100.0 * used / Math.Max(1, v.Total);
                var bar = new ProgressBar { Maximum = 100, Value = pct, Height = 8, Width = 260, VerticalAlignment = VerticalAlignment.Center };
                if (pct > 90) bar.Foreground = new SolidColorBrush(Color.FromRgb(0xC4, 0x2B, 0x1C));
                var row = Ui.Row(Ui.Label($"{v.Letter} {(v.Label.Length > 0 ? v.Label : L.T("Disco locale"))}"), Spacer(12), bar, Spacer(12),
                    Ui.Hint(L.T($"{Util.HumanSize(v.Free)} liberi su {Util.HumanSize(v.Total)} · {v.FileSystem}")));
                ((TextBlock)row.Children[0]).Width = 190;
                row.Margin = new Thickness(0, 6, 0, 0);
                box.Children.Add(row);
            }
            _disks.Children.Add(box);
        }
        _summary[L.T("Dischi")] = disks.SelectMany(d => new[] { new InfoRow(d.Name, $"{Util.HumanSize(d.Size)} {d.Kind} · {d.Health}") }
            .Concat(d.Details.Select(r => new InfoRow("   " + r.Key, r.Value)))
            .Concat(d.Volumes.Select(v => new InfoRow("   " + v.Letter, L.T($"{Util.HumanSize(v.Free)} liberi su {Util.HumanSize(v.Total)} ({v.FileSystem})"))))).ToList();
        if (disks.Any(d => d.HealthLevel >= 1))
            MainWindow.Notify(L.T("Attenzione: uno dei dischi segnala problemi di salute. Fai subito una copia dei dati importanti."), 15);
    }

    // ------------------------------------------------------------------ batteria
    private void ShowBattery(BatteryInfo? bat)
    {
        _battery.Children.Clear();
        if (bat == null)
        {
            _batteryCard.Visibility = Visibility.Collapsed;
            return;
        }
        _batteryCard.Visibility = Visibility.Visible;
        var icon = new SymbolIcon
        {
            Symbol = bat.Charge switch
            {
                >= 95 => SymbolRegular.Battery1024, >= 75 => SymbolRegular.Battery824, >= 55 => SymbolRegular.Battery624,
                >= 35 => SymbolRegular.Battery424, >= 15 => SymbolRegular.Battery224, _ => SymbolRegular.Battery024,
            },
            FontSize = 34, Margin = new Thickness(0, 0, 12, 0),
        };
        var head = Ui.Row(icon, Ui.Label($"{bat.Charge}%", 26, true), Spacer(14), Ui.Hint(bat.Status));
        head.Margin = new Thickness(0, 0, 0, 8);
        _battery.Children.Add(head);
        _battery.Children.Add(Ui.KeyValues(bat.Details.Select(r => (r.Key, r.Value)), 200));
        var report = Ui.Btn(L.T("Rapporto dettagliato"), SymbolRegular.DocumentText24, async (s, _) =>
        {
            var b = (FrameworkElement)s;
            b.IsEnabled = false;
            try
            {
                var path = await Task.Run(SysInfo.BatteryReport);
                MainWindow.Instance?.OpenFile(path);
            }
            catch (Exception ex) { Dlg.Error(L.T("Impossibile creare il rapporto:\n") + ex.Message); }
            finally { b.IsEnabled = true; }
        }, L.T("Storico di carica e autonomia generato da Windows"));
        report.Margin = new Thickness(0, 10, 0, 0);
        report.HorizontalAlignment = HorizontalAlignment.Left;
        _battery.Children.Add(report);
        _summary[L.T("Batteria")] = bat.Details.Prepend(new InfoRow(L.T("Carica"), $"{bat.Charge}% {bat.Status}")).ToList();
    }

    // ------------------------------------------------------------------ rete
    private void ShowNetwork(List<(string title, List<InfoRow> rows)> nets)
    {
        _network.Children.Clear();
        if (nets.Count == 0) _network.Children.Add(Ui.Hint(L.T("Nessuna connessione di rete attiva.")));
        var first = true;
        foreach (var (title, rows) in nets)
        {
            var label = Ui.Label(title, 14, true);
            label.Margin = new Thickness(0, first ? 0 : 16, 0, 6);
            first = false;
            _network.Children.Add(label);
            _network.Children.Add(Ui.KeyValues(rows.Select(r => (r.Key, r.Value)), 200));
            _summary[L.T("Rete: ") + title] = rows;
        }
        var publicIp = Ui.Hint();
        var btn = Ui.Btn(L.T("Mostra IP pubblico"), SymbolRegular.Globe24, null, L.T("Chiede a api.ipify.org l'indirizzo con cui il PC è visto su Internet"));
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            try
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
                publicIp.Text = L.T("IP pubblico: ") + (await http.GetStringAsync("https://api.ipify.org")).Trim();
            }
            catch (Exception ex) { publicIp.Text = L.T("Impossibile leggere l'IP pubblico: ") + ex.Message; }
            finally { btn.IsEnabled = true; }
        };
        var row = Ui.Row(btn, publicIp);
        row.Margin = new Thickness(0, 12, 0, 0);
        _network.Children.Add(row);
    }

    // ------------------------------------------------------------------ avvio
    private void ShowStartup(List<StartupItem> items)
    {
        _startup.Children.Clear();
        _summary[L.T("Programmi all'avvio")] = items.Select(i => new InfoRow(i.Name, (i.Enabled ? L.T("attivo") : L.T("disattivato")) + " · " + i.Command)).ToList();
        if (items.Count == 0) _startup.Children.Add(Ui.Hint(L.T("Nessun programma configurato per partire all'avvio.")));
        foreach (var item in items)
        {
            var g = new Grid { Margin = new Thickness(0, 4, 0, 4) };
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(34) });
            g.ColumnDefinitions.Add(new ColumnDefinition());
            g.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var exe = item.Executable;
            if (exe != null && File.Exists(exe))
                g.Children.Add(new Image { Source = ShellIcons.For(exe), Width = 20, Height = 20, VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Left });
            else
                g.Children.Add(new SymbolIcon { Symbol = SymbolRegular.Apps24, FontSize = 20, VerticalAlignment = VerticalAlignment.Center });
            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(Ui.Label(item.Name, 13, true));
            var sub = Ui.Hint(string.Join(" · ", new[] { item.Publisher, item.Location }.Where(s => s.Length > 0)));
            sub.ToolTip = item.Command;
            sub.TextTrimming = TextTrimming.CharacterEllipsis;
            text.Children.Add(sub);
            Grid.SetColumn(text, 1);
            g.Children.Add(text);
            var toggle = new ToggleSwitch { IsChecked = item.Enabled, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            toggle.Click += (_, _) =>
            {
                try
                {
                    SysInfo.SetEnabled(item, toggle.IsChecked == true);
                    MainWindow.Notify(L.T($"{item.Name}: {(item.Enabled ? L.T("partirà") : L.T("non partirà più"))} all'avvio di Windows"));
                }
                catch (UnauthorizedAccessException)
                {
                    toggle.IsChecked = item.Enabled;
                    Dlg.Info(L.T("Questo programma è configurato per tutti gli utenti: per modificarlo avvia SwinKnife come amministratore."));
                }
                catch (Exception ex)
                {
                    toggle.IsChecked = item.Enabled;
                    Dlg.Error(ex.Message);
                }
            };
            Grid.SetColumn(toggle, 2);
            g.Children.Add(toggle);
            if (exe != null && File.Exists(exe))
            {
                var menu = new ContextMenu();
                var open = new System.Windows.Controls.MenuItem { Header = L.T("Mostra il file in Esplora risorse") };
                open.Click += (_, _) => Util.Reveal(exe);
                menu.Items.Add(open);
                g.ContextMenu = menu;
            }
            g.Background = Brushes.Transparent;
            _startup.Children.Add(g);
        }
        var store = Ui.Btn(L.T("App dello Store all'avvio…"), SymbolRegular.Open24, (_, _) => Launch("ms-settings:startupapps"),
            L.T("Le app installate dal Microsoft Store si gestiscono dalle Impostazioni di Windows"));
        store.Margin = new Thickness(0, 10, 0, 0);
        store.HorizontalAlignment = HorizontalAlignment.Left;
        _startup.Children.Add(store);
    }

    // ------------------------------------------------------------------ utilizzo
    [StructLayout(LayoutKind.Sequential)]
    private struct MEMORYSTATUSEX
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);

    [DllImport("kernel32.dll")]
    private static extern bool GetSystemTimes(out long idle, out long kernel, out long user);

    private void UpdateUsage()
    {
        if (GetSystemTimes(out var idle, out var kernel, out var user))
        {
            var total = kernel - _kernel + (user - _user);
            if (_kernel != 0 && total > 0)
            {
                var cpu = 100.0 * (total - (idle - _idle)) / total;
                _cpuBar.Value = cpu;
                _cpuText.Text = $"{cpu:0}%";
            }
            (_idle, _kernel, _user) = (idle, kernel, user);
        }
        var m = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
        if (GlobalMemoryStatusEx(ref m))
        {
            _ramBar.Value = m.dwMemoryLoad;
            _ramText.Text = L.T($"{Util.HumanSize(m.ullTotalPhys - m.ullAvailPhys)} di {Util.HumanSize(m.ullTotalPhys)} ({m.dwMemoryLoad}%)");
        }
    }

    private void CopySummary()
    {
        if (_summary.Count == 0) return;
        var sb = new StringBuilder(L.T($"Riepilogo di sistema – {Environment.MachineName} – {Util.Date(DateTime.Now)}\r\n"));
        foreach (var (section, rows) in _summary)
        {
            sb.Append("\r\n").Append(section.ToUpperInvariant()).Append("\r\n");
            foreach (var r in rows) sb.Append(r.Key).Append(": ").Append(r.Value).Append("\r\n");
        }
        Clipboard.SetText(sb.ToString());
        MainWindow.Notify(L.T("Riepilogo copiato negli appunti"));
    }

    public void Shutdown() => _timer.Stop();
}
