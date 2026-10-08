using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using ToggleSwitch = Wpf.Ui.Controls.ToggleSwitch;

namespace SwinKnife.Pages;

/// <summary>Interruttori di privacy e ottimizzazione di Windows, tutti reversibili (stile ShutUp10).</summary>
public sealed class TweaksPage : UserControl, IToolPage
{
    private readonly StackPanel _list = new();
    private readonly bool _admin = RawSource.IsAdmin();

    public TweaksPage(MainWindow main)
    {
        var info = new InfoBar
        {
            Severity = InfoBarSeverity.Informational, IsOpen = true, IsClosable = false,
            Title = L.T("Tutto reversibile"),
            Message = L.T("Ogni interruttore attiva o disattiva una singola impostazione di Windows. Puoi tornare indietro quando vuoi. Alcune voci valgono per tutto il PC e richiedono l'amministratore."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        if (!_admin)
            _list.Children.Add(new InfoBar
            {
                Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
                Title = L.T("Alcune voci richiedono l'amministratore"),
                Message = L.T("Le impostazioni valide per tutti gli utenti sono disattivate. Riavvia SwinKnife come amministratore per cambiarle."),
                Margin = new Thickness(0, 0, 0, 10),
            });

        foreach (var t in Tweaks.All()) _list.Children.Add(Row(t));

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Privacy e ottimizzazioni"), L.T("Meno pubblicità, meno telemetria, più controllo: con un interruttore.")),
            info, _list);
    }

    private FrameworkElement Row(TweakDef t)
    {
        var sw = new ToggleSwitch { VerticalAlignment = VerticalAlignment.Center };
        try { sw.IsChecked = t.IsApplied(); } catch { }
        sw.IsEnabled = !t.NeedsAdmin || _admin;

        sw.Checked += (_, _) => Toggle(t, true, sw);
        sw.Unchecked += (_, _) => Toggle(t, false, sw);

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var titleRow = new TextBlock { FontWeight = FontWeights.SemiBold, Foreground = Ui.Res("TextFillColorPrimaryBrush"), Text = t.Title + (t.NeedsAdmin ? L.T("  (amministratore)") : "") };
        text.Children.Add(titleRow);
        text.Children.Add(Ui.Hint(t.Desc));

        var dock = new DockPanel { Margin = new Thickness(0, 8, 0, 8) };
        DockPanel.SetDock(sw, Dock.Right);
        dock.Children.Add(sw);
        dock.Children.Add(text);
        return new Border { Child = dock, Padding = new Thickness(12, 2, 8, 2), Margin = new Thickness(0, 0, 0, 6), CornerRadius = new CornerRadius(6), Background = Ui.Res("CardBackgroundFillColorDefaultBrush") };
    }

    private bool _busy;
    private void Toggle(TweakDef t, bool on, ToggleSwitch sw)
    {
        if (_busy) return;
        try { t.Apply(on); MainWindow.Notify(on ? L.T($"«{t.Title}» attivato.") : L.T($"«{t.Title}» disattivato.")); }
        catch (Exception ex)
        {
            _busy = true;
            sw.IsChecked = !on; // ripristino lo stato dell'interruttore
            _busy = false;
            Dlg.Error(_admin ? ex.Message : L.T("Per cambiare questa voce serve avviare SwinKnife come amministratore."));
        }
    }
}
