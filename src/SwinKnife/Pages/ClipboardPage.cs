using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using ListView = Wpf.Ui.Controls.ListView;
using TextBox = Wpf.Ui.Controls.TextBox;
using ListViewItem = Wpf.Ui.Controls.ListViewItem;

namespace SwinKnife.Pages;

/// <summary>Cronologia degli appunti: cerca, ricopia, fissa o elimina quello che hai copiato.</summary>
public sealed class ClipboardPage : UserControl, IToolPage
{
    private readonly ListView _list = new() { BorderThickness = new Thickness(0) };
    private readonly TextBox _search = new() { PlaceholderText = L.T("Cerca negli appunti…"), Width = 320 };
    private readonly ComboBox _kind = new() { Width = 150, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ToggleSwitch _enabled = new() { Content = L.T("Registra quello che copio"), Margin = new Thickness(16, 0, 0, 0) };
    private readonly System.Windows.Controls.TextBlock _count = Ui.Hint();

    public ClipboardPage(MainWindow main)
    {
        foreach (var k in new[] { L.T("Tutto"), L.T("Testo"), L.T("Immagini"), L.T("File") }) _kind.Items.Add(k);
        _kind.SelectedIndex = 0;
        _enabled.IsChecked = ClipboardHistory.Enabled;
        _enabled.Click += (_, _) => ClipboardHistory.Enabled = _enabled.IsChecked == true;
        _search.TextChanged += (_, _) => Fill();
        _kind.SelectionChanged += (_, _) => Fill();
        _list.MouseDoubleClick += (_, _) => CopySelected();
        _list.KeyDown += (_, e) =>
        {
            if (e.Key == Key.Enter) CopySelected();
            else if (e.Key == Key.Delete && Selected is { } s) ClipboardHistory.Remove(s);
        };
        var popupKey = Resident.Hotkeys.FirstOrDefault(h => h.Id == "clipboard")?.Current;
        var top = new DockPanel { Margin = new Thickness(0, 0, 0, 10) };
        var right = Ui.Row(
            Ui.Btn(L.T("Copia"), SymbolRegular.Copy24, (_, _) => CopySelected(), primary: true),
            Ui.Btn(L.T("Fissa / sblocca"), SymbolRegular.Pin24, (_, _) => { if (Selected is { } s) ClipboardHistory.TogglePin(s); }),
            Ui.Btn(L.T("Elimina"), SymbolRegular.Delete24, (_, _) => { if (Selected is { } s) ClipboardHistory.Remove(s); }),
            Ui.Btn(L.T("Svuota"), SymbolRegular.DeleteDismiss24, (_, _) =>
            {
                if (Dlg.Confirm(L.T("Eliminare tutta la cronologia degli appunti? Le voci fissate restano.")))
                    ClipboardHistory.ClearUnpinned();
            }));
        DockPanel.SetDock(right, Dock.Right);
        top.Children.Add(right);
        top.Children.Add(Ui.Row(_search, _kind, _enabled));
        var hint = Ui.Hint(string.IsNullOrEmpty(popupKey)
            ? L.T("Doppio clic o Invio: copia di nuovo · Canc: elimina")
            : L.T($"Doppio clic o Invio: copia di nuovo · Canc: elimina · {popupKey}: finestrella per incollare da qualsiasi programma"));
        hint.Margin = new Thickness(0, 0, 0, 8);
        var header = Ui.Header(L.T("Appunti"), L.T("Tutto quello che copi (testi, immagini, file) resta qui: cercalo e copialo di nuovo. I contenuti privati dei password manager non vengono salvati."));
        var grid = new DockPanel { Margin = new Thickness(24, 18, 24, 14) };
        DockPanel.SetDock(header, Dock.Top);
        DockPanel.SetDock(top, Dock.Top);
        DockPanel.SetDock(hint, Dock.Top);
        DockPanel.SetDock(_count, Dock.Bottom);
        grid.Children.Add(header);
        grid.Children.Add(top);
        grid.Children.Add(hint);
        grid.Children.Add(_count);
        grid.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Panel"), Padding = new Thickness(6), Child = _list });
        Content = grid;
        ClipboardHistory.Changed += OnChanged;
        Unloaded += (_, _) => ClipboardHistory.Changed -= OnChanged;
        Loaded += (_, _) =>
        {
            ClipboardHistory.Changed -= OnChanged;
            ClipboardHistory.Changed += OnChanged;
            Fill();
        };
        Fill();
    }

    private void OnChanged() => Dispatcher.InvokeAsync(Fill);

    private ClipEntry? Selected => (_list.SelectedItem as ListViewItem)?.Tag as ClipEntry;

    private void Fill()
    {
        var keep = Selected?.Id;
        var q = _search.Text.Trim();
        var kind = _kind.SelectedIndex switch { 1 => ClipKind.Text, 2 => ClipKind.Image, 3 => ClipKind.Files, _ => (ClipKind?)null };
        _list.Items.Clear();
        var items = ClipboardHistory.Items.OrderByDescending(x => x.Pinned).ThenByDescending(x => x.Time)
            .Where(x => (kind == null || x.Kind == kind) && ClipViews.Matches(x, q)).Take(300).ToList();
        foreach (var e in items)
        {
            var it = new ListViewItem { Content = ClipViews.Item(e), Tag = e };
            _list.Items.Add(it);
            if (e.Id == keep) _list.SelectedItem = it;
        }
        _count.Text = ClipboardHistory.Items.Count == 0
            ? (ClipboardHistory.Enabled ? L.T("Ancora niente: quello che copi apparirà qui.") : L.T("La cronologia è disattivata."))
            : L.T($"{items.Count} di {ClipboardHistory.Items.Count} voci");
    }

    private void CopySelected()
    {
        if (Selected is not { } e) return;
        try
        {
            ClipboardHistory.CopyBack(e);
            MainWindow.Notify(L.T("Copiato negli appunti"));
        }
        catch (Exception ex)
        {
            Dlg.Error(ex.Message);
        }
    }
}
