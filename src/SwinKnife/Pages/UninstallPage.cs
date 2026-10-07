using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using DataGrid = System.Windows.Controls.DataGrid;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Disinstalla i programmi e poi trova cartelle e chiavi di registro che si sono lasciati dietro.</summary>
public sealed class UninstallPage : UserControl, IToolPage
{
    public sealed class Row(InstalledProgram p)
    {
        public InstalledProgram Program { get; } = p;
        public string Name => Program.Name;
        public string Publisher => Program.Publisher;
        public string Version => Program.Version;
        public string Size => Program.SizeBytes > 0 ? Util.HumanSize(Program.SizeBytes) : "";
        public long SizeBytes => Program.SizeBytes;
        public string Date => Program.Installed is { } d ? d.ToString("d", L.Culture) : "";
        public DateTime? DateValue => Program.Installed;
        public ImageSource? Icon => Program.Icon.Length > 0 && File.Exists(Program.Icon) ? ShellIcons.For(Program.Icon) : null;
    }

    private readonly DataGrid _grid;
    private readonly TextBox _search = new() { PlaceholderText = L.T("Cerca un programma…"), Width = 320 };
    private readonly TextBlock _count = Ui.Hint();
    private readonly TextBlock _status = Ui.Hint();
    private readonly ProgressBar _busy = new() { IsIndeterminate = true, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 6, 0, 0) };
    private List<Row> _all = new();

    public UninstallPage(MainWindow main)
    {
        _grid = new DataGrid
        {
            AutoGenerateColumns = false, IsReadOnly = true, HeadersVisibility = DataGridHeadersVisibility.Column, GridLinesVisibility = DataGridGridLinesVisibility.None,
            SelectionMode = DataGridSelectionMode.Single, CanUserSortColumns = true,
        };
        var name = new DataGridTemplateColumn { Header = L.T("Programma"), Width = new DataGridLength(1, DataGridLengthUnitType.Star), SortMemberPath = nameof(Row.Name) };
        var panel = new FrameworkElementFactory(typeof(StackPanel));
        panel.SetValue(StackPanel.OrientationProperty, Orientation.Horizontal);
        var img = new FrameworkElementFactory(typeof(System.Windows.Controls.Image));
        img.SetBinding(System.Windows.Controls.Image.SourceProperty, new Binding(nameof(Row.Icon)) { IsAsync = true });
        img.SetValue(WidthProperty, 16.0);
        img.SetValue(HeightProperty, 16.0);
        img.SetValue(MarginProperty, new Thickness(0, 0, 8, 0));
        var text = new FrameworkElementFactory(typeof(TextBlock));
        text.SetBinding(TextBlock.TextProperty, new Binding(nameof(Row.Name)));
        panel.AppendChild(img);
        panel.AppendChild(text);
        name.CellTemplate = new DataTemplate { VisualTree = panel };
        _grid.Columns.Add(name);
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Produttore"), Binding = new Binding(nameof(Row.Publisher)), Width = new DataGridLength(220) });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Versione"), Binding = new Binding(nameof(Row.Version)), Width = new DataGridLength(120) });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Dimensione"), Binding = new Binding(nameof(Row.Size)), Width = new DataGridLength(100), SortMemberPath = nameof(Row.SizeBytes) });
        _grid.Columns.Add(new DataGridTextColumn { Header = L.T("Installato il"), Binding = new Binding(nameof(Row.Date)), Width = new DataGridLength(110), SortMemberPath = nameof(Row.DateValue) });
        _grid.MouseDoubleClick += async (_, _) => await Uninstall();
        _search.TextChanged += (_, _) => Filter();

        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var actions = Ui.Row(
            Ui.Btn(L.T("Disinstalla"), SymbolRegular.Delete24, async (_, _) => await Uninstall(), primary: true),
            Ui.Btn(L.T("Cerca solo i residui"), SymbolRegular.Broom24, async (_, _) =>
            {
                if (_grid.SelectedItem is Row r) await Leftovers(r.Program, afterUninstall: false);
            }, L.T("Per i programmi già disinstallati che risultano ancora nell'elenco o che hanno lasciato file")),
            Ui.Btn(L.T("Aggiorna"), SymbolRegular.ArrowSync24, async (_, _) => await Load()));
        DockPanel.SetDock(actions, Dock.Right);
        top.Children.Add(actions);
        top.Children.Add(Ui.Row(_search, new Border { Width = 12 }, _count));

        var dock = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        var header = Ui.Header(L.T("Disinstalla programmi"), L.T("Rimuovi i programmi che non usi più: dopo la disinstallazione SwinKnife cerca le cartelle e le voci di registro rimaste (con una copia di sicurezza del registro)."));
        foreach (var e in new UIElement[] { header, top }) { DockPanel.SetDock(e, Dock.Top); dock.Children.Add(e); }
        var bottom = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
        bottom.Children.Add(_status);
        bottom.Children.Add(_busy);
        DockPanel.SetDock(bottom, Dock.Bottom);
        dock.Children.Add(bottom);
        dock.Children.Add(_grid);
        Content = dock;
        Loaded += async (_, _) =>
        {
            if (_all.Count == 0) await Load();
        };
    }

    private async Task Load()
    {
        _busy.Visibility = Visibility.Visible;
        _all = (await Task.Run(Programs.List)).Select(p => new Row(p)).ToList();
        _busy.Visibility = Visibility.Collapsed;
        Filter();
    }

    private void Filter()
    {
        var q = _search.Text.Trim();
        var rows = _all.Where(r => q.Length == 0 || r.Name.Contains(q, StringComparison.CurrentCultureIgnoreCase) || r.Publisher.Contains(q, StringComparison.CurrentCultureIgnoreCase)).ToList();
        _grid.ItemsSource = rows;
        _count.Text = L.T($"{rows.Count} programmi · {Util.HumanSize(rows.Sum(r => r.SizeBytes))}");
    }

    private async Task Uninstall()
    {
        if (_grid.SelectedItem is not Row r) return;
        var p = r.Program;
        if (!Dlg.Confirm(L.T($"Disinstallare «{p.Name}»?\n\nSi apre il programma di disinstallazione del produttore; quando ha finito SwinKnife cerca i residui."), AppInfo.Name, L.T("Disinstalla"), L.T("Annulla"))) return;
        _busy.Visibility = Visibility.Visible;
        _status.Text = L.T($"Disinstallazione di {p.Name} in corso… completa i passaggi nella finestra del programma.");
        try
        {
            await Programs.UninstallAsync(p);
        }
        catch (Exception ex)
        {
            _busy.Visibility = Visibility.Collapsed;
            _status.Text = "";
            if (ex is System.ComponentModel.Win32Exception { NativeErrorCode: 1223 }) return; // richiesta UAC rifiutata
            Dlg.Error(L.T("Impossibile avviare la disinstallazione:\n") + ex.Message);
            return;
        }
        _busy.Visibility = Visibility.Collapsed;
        await Leftovers(p, afterUninstall: true);
        await Load();
    }

    private async Task Leftovers(InstalledProgram p, bool afterUninstall)
    {
        _busy.Visibility = Visibility.Visible;
        _status.Text = L.T("Cerco i residui…");
        var still = Programs.StillInstalled(p);
        var found = await Task.Run(() => Programs.FindLeftovers(p));
        _busy.Visibility = Visibility.Collapsed;
        _status.Text = "";
        if (afterUninstall && still && !Dlg.Confirm(L.T($"«{p.Name}» risulta ancora installato (forse la disinstallazione è stata annullata o non è finita). Cercare comunque i residui?")))
            return;
        if (found.Count == 0)
        {
            Dlg.Info(afterUninstall ? L.T($"«{p.Name}» è stato rimosso e non ha lasciato residui.") : L.T("Nessun residuo trovato."));
            return;
        }
        if (LeftoverDialog(p, found) is not { Count: > 0 } chosen) return;
        if (chosen.Any(c => c.NeedsAdmin))
        {
            Dlg.Info(L.T("Alcune voci richiedono i privilegi di amministratore e verranno saltate. Per eliminarle avvia SwinKnife come amministratore e usa \"Cerca solo i residui\"."));
            chosen = chosen.Where(c => !c.NeedsAdmin).ToList();
        }
        var (ok, failed, backup) = Programs.Remove(chosen);
        var msg = L.T($"{ok} residui eliminati (i file sono nel Cestino).");
        if (failed > 0) msg += " " + L.T($"{failed} non eliminati.");
        if (backup != null) msg += "\n\n" + L.T($"Copia di sicurezza del registro: {backup}");
        Dlg.Info(msg);
    }

    /// <summary>Elenco dei residui con le caselle per scegliere cosa eliminare.</summary>
    private static List<Leftover>? LeftoverDialog(InstalledProgram p, List<Leftover> found)
    {
        var list = new StackPanel();
        foreach (var f in found)
        {
            var label = f.Path + (f.IsRegistry ? "" : $"  ({Util.HumanSize(f.Size)})") + (f.NeedsAdmin ? L.T("  · serve amministratore") : "");
            var cb = new CheckBox { Content = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap }, IsChecked = f.Selected && !f.NeedsAdmin, Tag = f, Margin = new Thickness(0, 2, 0, 2) };
            list.Children.Add(cb);
        }
        var scroll = new ScrollViewer { Content = list, MaxHeight = 360, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var intro = new TextBlock
        {
            Text = L.T($"Queste cartelle e chiavi di registro sembrano appartenere a «{p.Name}». Controlla l'elenco e togli la spunta a ciò che vuoi tenere."),
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10),
        };
        var content = new StackPanel();
        content.Children.Add(intro);
        content.Children.Add(scroll);
        var dlg = new FormDialog(L.T("Residui trovati"), content, 720);
        dlg.AddButton(L.T("Annulla"), 0);
        dlg.AddButton(L.T("Elimina selezionati"), 1, primary: true, isDefault: true);
        if (dlg.Run() != 1) return null;
        return list.Children.OfType<CheckBox>().Where(c => c.IsChecked == true).Select(c => (Leftover)c.Tag).ToList();
    }
}
