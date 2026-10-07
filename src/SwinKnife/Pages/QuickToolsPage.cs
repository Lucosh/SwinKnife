using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;
using TextBox = System.Windows.Controls.TextBox;

namespace SwinKnife.Pages;

/// <summary>Strumenti da usare in qualsiasi programma con una scorciatoia: testo dallo schermo, contagocce, righello, primo piano, appunti.</summary>
public sealed class QuickToolsPage : UserControl, IToolPage
{
    private readonly WrapPanel _colors = new();
    private readonly List<(HotkeyDef def, TextBlock status)> _rows = new();

    public QuickToolsPage(MainWindow main)
    {
        var tools = new StackPanel();
        foreach (var h in Resident.Hotkeys)
        {
            var (desc, icon) = h.Id switch
            {
                "text" => (L.T("Seleziona un'area dello schermo (un video, un'immagine, un programma che non lascia copiare): il testo riconosciuto finisce negli appunti."), SymbolRegular.TextT24),
                "color" => (L.T("Contagocce con lente d'ingrandimento: il colore sotto il mouse viene copiato in formato #RRGGBB."), SymbolRegular.Color24),
                "ruler" => (L.T("Misura in pixel distanze e riquadri sullo schermo."), SymbolRegular.Ruler24),
                "topmost" => (L.T("La finestra attiva resta sopra a tutte le altre; ripeti la scorciatoia per toglierla."), SymbolRegular.Pin24),
                "clipboard" => (L.T("Gli ultimi testi, immagini e file copiati: scegli e viene incollato dove stavi scrivendo."), SymbolRegular.ClipboardTextLtr24),
                _ => ("", SymbolRegular.Keyboard24),
            };
            var status = new TextBlock { FontSize = 12, Margin = new Thickness(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
            _rows.Add((h, status));
            var box = HotkeyBox(h);
            var run = Ui.Btn(L.T("Avvia ora"), SymbolRegular.Play24, (_, _) => RunTool(h));
            var head = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };
            var ic = new SymbolIcon { Symbol = icon, FontSize = 22, Margin = new Thickness(0, 0, 12, 0), Foreground = Ui.Res("AccentTextFillColorPrimaryBrush") };
            head.Children.Add(ic);
            head.Children.Add(new TextBlock { Text = h.Title, FontSize = 15, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Foreground = Ui.Res("TextFillColorPrimaryBrush") });
            var body = new StackPanel { Margin = new Thickness(34, 0, 0, 0) };
            body.Children.Add(new TextBlock { Text = desc, Style = (Style)Application.Current.FindResource("Hint"), Margin = new Thickness(0, 0, 0, 8) });
            body.Children.Add(Ui.Row(run, Ui.Label(L.T("Scorciatoia:")), box, status));
            var panel = new StackPanel();
            panel.Children.Add(head);
            panel.Children.Add(body);
            tools.Children.Add(new Border { Style = (Style)Application.Current.FindResource("Panel"), Child = panel, Margin = new Thickness(0, 0, 0, 10) });
        }
        UpdateStatus();

        var note = Ui.Hint(L.T("Le scorciatoie funzionano finché SwinKnife è aperto. Per averle sempre, nelle Impostazioni attiva \"Resta nell'area di notifica\" e \"Avvia con Windows\". Clic sulla casella e premi la nuova combinazione; Canc la disattiva."));
        note.TextWrapping = TextWrapping.Wrap;
        note.Margin = new Thickness(0, 0, 0, 14);

        QuickTools.ColorsChanged += FillColors;
        Unloaded += (_, _) => QuickTools.ColorsChanged -= FillColors;
        FillColors();

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Strumenti rapidi"), L.T("Piccoli strumenti da usare in qualsiasi programma con una scorciatoia da tastiera.")),
            note, tools,
            Ui.Card(L.T("Colori recenti"), L.T("Clic su un colore per copiarlo di nuovo."), _colors));
    }

    private static void RunTool(HotkeyDef h)
    {
        // lascio sparire SwinKnife prima di "fotografare" lo schermo
        if (h.Id is "text" or "color" or "ruler" && MainWindow.Instance is { } w)
        {
            w.WindowState = WindowState.Minimized;
            w.Dispatcher.InvokeAsync(async () =>
            {
                await Task.Delay(350);
                h.Run();
                w.WindowState = WindowState.Normal;
            });
        }
        else h.Run();
    }

    private void UpdateStatus()
    {
        foreach (var (def, status) in _rows)
        {
            if (string.IsNullOrEmpty(def.Current))
            {
                status.Text = L.T("disattivata");
                status.Foreground = Ui.Res("TextFillColorTertiaryBrush");
            }
            else if (Resident.HotkeyStatus.TryGetValue(def.Id, out var ok) && !ok)
            {
                status.Text = L.T("già usata da un altro programma: scegline un'altra");
                status.Foreground = new SolidColorBrush(Color.FromRgb(0xF0, 0x9A, 0x3E));
            }
            else
            {
                status.Text = L.T("attiva");
                status.Foreground = new SolidColorBrush(Color.FromRgb(0x6C, 0xCB, 0x5F));
            }
        }
    }

    private TextBox HotkeyBox(HotkeyDef h)
    {
        var box = new TextBox { Text = h.Current, IsReadOnly = true, Width = 190, Margin = new Thickness(8, 0, 0, 0), Cursor = Cursors.Hand, IsReadOnlyCaretVisible = false };
        box.PreviewKeyDown += (_, e) =>
        {
            e.Handled = true;
            var key = e.Key == Key.System ? e.SystemKey : e.Key;
            if (key is Key.Delete or Key.Back)
            {
                Set(h, box, "");
                return;
            }
            if (key == Key.Escape || key == Key.Tab)
            {
                Keyboard.ClearFocus();
                return;
            }
            var win = Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin);
            if (Resident.Format(Keyboard.Modifiers, win, key) is { } combo) Set(h, box, combo);
        };
        box.GotKeyboardFocus += (_, _) => box.Text = L.T("Premi la combinazione…");
        box.LostKeyboardFocus += (_, _) => box.Text = string.IsNullOrEmpty(h.Current) ? "—" : h.Current;
        if (string.IsNullOrEmpty(h.Current)) box.Text = "—";
        return box;
    }

    private void Set(HotkeyDef h, TextBox box, string combo)
    {
        Settings.Set("hotkey." + h.Id, combo);
        Resident.RegisterHotkeys();
        box.Text = combo.Length == 0 ? "—" : combo;
        UpdateStatus();
        Keyboard.ClearFocus();
    }

    private void FillColors()
    {
        _colors.Children.Clear();
        var list = QuickTools.RecentColors();
        if (list.Count == 0)
        {
            _colors.Children.Add(Ui.Hint(L.T("Nessun colore ancora: usa il contagocce.")));
            return;
        }
        foreach (var hex in list)
        {
            Color c;
            try { c = (Color)ColorConverter.ConvertFromString(hex); }
            catch { continue; }
            var swatch = new Border { Width = 26, Height = 26, CornerRadius = new CornerRadius(5), Background = new SolidColorBrush(c), BorderBrush = Ui.Res("CardStrokeColorDefaultBrush"), BorderThickness = new Thickness(1), Margin = new Thickness(0, 0, 8, 0) };
            var b = new Wpf.Ui.Controls.Button
            {
                Content = Ui.Row(swatch, new TextBlock { Text = hex, FontFamily = new FontFamily("Consolas"), VerticalAlignment = VerticalAlignment.Center }),
                Margin = new Thickness(0, 0, 8, 8), Padding = new Thickness(6, 4, 10, 4), ToolTip = $"rgb({c.R}, {c.G}, {c.B})",
            };
            b.Click += (_, _) =>
            {
                System.Windows.Clipboard.SetText(hex);
                MainWindow.Notify(L.T($"{hex} copiato"));
            };
            _colors.Children.Add(b);
        }
    }
}
