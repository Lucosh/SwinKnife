using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TabControl = System.Windows.Controls.TabControl;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Un vero terminale (PowerShell, Prompt dei comandi, Ubuntu/WSL, Git Bash) con schede, colori e scorrimento.</summary>
public sealed class TerminalPage : UserControl, IToolPage
{
    private sealed record Profile(string Name, string CommandLine, string? WorkingDir, bool Admin);

    private readonly TabControl _tabs = new() { Margin = new Thickness(0) };
    private readonly List<Profile> _profiles = [];
    private readonly List<TerminalView> _views = [];

    public TerminalPage(MainWindow main)
    {
        BuildProfiles();

        var newTab = new Button
        {
            Content = L.T("Nuova scheda"), Icon = new SymbolIcon { Symbol = SymbolRegular.Add24 },
            Appearance = ControlAppearance.Primary, Margin = new Thickness(0, 0, 8, 0), Padding = new Thickness(12, 6, 12, 6),
        };
        newTab.Click += (_, _) => ShowProfileMenu(newTab);

        var copy = Ui.IconBtn(SymbolRegular.Copy24, L.T("Copia (Ctrl+Maiusc+C)"), (_, _) => Current?.Copy());
        var paste = Ui.IconBtn(SymbolRegular.ClipboardPaste24, L.T("Incolla (Ctrl+Maiusc+V)"), (_, _) => Current?.Paste());
        var clear = Ui.IconBtn(SymbolRegular.Eraser24, L.T("Pulisci"), (_, _) => Current?.Clear());

        var toolbar = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
        toolbar.Children.Add(newTab);
        toolbar.Children.Add(copy);
        toolbar.Children.Add(paste);
        toolbar.Children.Add(clear);

        var hint = new TextBlock
        {
            Text = L.T("Suggerimento: per i permessi da amministratore usa «sudo» dentro Ubuntu (WSL), oppure apri una scheda come amministratore (si apre in una finestra a parte, come richiede Windows)."),
            Style = (Style)Application.Current.FindResource("Hint"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8),
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(toolbar, 0);
        Grid.SetRow(hint, 1);
        Grid.SetRow(_tabs, 2);
        grid.Children.Add(toolbar);
        grid.Children.Add(hint);
        grid.Children.Add(_tabs);
        grid.Margin = new Thickness(16, 12, 16, 12);
        Content = grid;

        Loaded += (_, _) =>
        {
            if (_tabs.Items.Count == 0 && _profiles.Count > 0)
                OpenTab(_profiles.First(p => !p.Admin));
        };
    }

    private TerminalView? Current => (_tabs.SelectedItem as TabItem)?.Content as TerminalView;

    public void Activated() => Current?.FocusTerminal();

    private void BuildProfiles()
    {
        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var pwsh = Which("pwsh.exe") ?? Which("powershell.exe") ?? "powershell.exe";
        var psName = Path.GetFileName(pwsh).Equals("pwsh.exe", StringComparison.OrdinalIgnoreCase) ? "PowerShell 7" : "Windows PowerShell";
        _profiles.Add(new Profile(psName, Quote(pwsh), home, false));
        _profiles.Add(new Profile(L.T("Prompt dei comandi"), "cmd.exe", home, false));

        if (Which("wsl.exe") is { } wsl)
            _profiles.Add(new Profile("Ubuntu (WSL)", Quote(wsl) + " ~", null, false));

        foreach (var cand in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Git", "bin", "bash.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Git", "bin", "bash.exe"),
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Git", "bin", "bash.exe"),
                 })
            if (File.Exists(cand)) { _profiles.Add(new Profile("Git Bash", Quote(cand) + " --login -i", home, false)); break; }

        // varianti amministratore (si aprono in una finestra separata)
        _profiles.Add(new Profile(psName + " " + L.T("(amministratore)"), Quote(pwsh), home, true));
        _profiles.Add(new Profile(L.T("Prompt dei comandi") + " " + L.T("(amministratore)"), "cmd.exe", home, true));
    }

    private void ShowProfileMenu(UIElement anchor)
    {
        var menu = new ContextMenu { PlacementTarget = anchor, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        foreach (var p in _profiles)
        {
            var item = new System.Windows.Controls.MenuItem { Header = p.Name };
            if (p.Admin) item.Icon = new SymbolIcon { Symbol = SymbolRegular.ShieldKeyhole24 };
            var profile = p;
            item.Click += (_, _) => OpenTab(profile);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    private void OpenTab(Profile p)
    {
        if (p.Admin) { OpenElevated(p); return; }

        var view = new TerminalView();
        _views.Add(view);

        var title = new TextBlock { Text = p.Name, VerticalAlignment = VerticalAlignment.Center };
        var close = new Button
        {
            Icon = new SymbolIcon { Symbol = SymbolRegular.Dismiss16, FontSize = 12 },
            Appearance = ControlAppearance.Transparent, Padding = new Thickness(4, 0, 4, 0), Margin = new Thickness(8, 0, -6, 0),
            Background = Brushes.Transparent, BorderBrush = Brushes.Transparent,
        };
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        header.Children.Add(title);
        header.Children.Add(close);

        var tab = new TabItem { Header = header, Content = view };
        close.Click += (_, _) => CloseTab(tab, view);
        view.TitleChanged += t => Dispatcher.BeginInvoke(() => { if (!string.IsNullOrWhiteSpace(t)) title.Text = $"{p.Name} — {t}"; });
        view.Exited += _ => Dispatcher.BeginInvoke(() => title.Text = p.Name + " " + L.T("(terminato)"));

        _tabs.Items.Add(tab);
        _tabs.SelectedItem = tab;
        view.Start(p.CommandLine, p.WorkingDir, new Dictionary<string, string> { ["SWINKNIFE"] = AppInfo.Version });
    }

    private void OpenElevated(Profile p)
    {
        try
        {
            var (exe, args) = SplitCommand(p.CommandLine);
            Process.Start(new ProcessStartInfo(exe, args)
            {
                UseShellExecute = true, Verb = "runas", WorkingDirectory = p.WorkingDir ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception)
        {
            // l'utente ha annullato il controllo UAC: nessun messaggio
        }
        catch (Exception ex)
        {
            Dlg.Error(L.T("Impossibile aprire il terminale come amministratore: ") + ex.Message);
        }
    }

    private void CloseTab(TabItem tab, TerminalView view)
    {
        view.Shutdown();
        _views.Remove(view);
        _tabs.Items.Remove(tab);
        if (_tabs.Items.Count == 0) Dispatcher.BeginInvoke(() => { if (_tabs.Items.Count == 0) OpenTab(_profiles.First(p => !p.Admin)); });
    }

    public bool CanClose()
    {
        var alive = _views.Count(v => v.IsAlive);
        if (alive == 0) return true;
        return Dlg.Confirm(L.T($"Ci sono {alive} terminali aperti. Chiuderli?"), AppInfo.Name, L.T("Chiudi"), L.T("Annulla"));
    }

    public void Shutdown()
    {
        foreach (var v in _views) v.Shutdown();
        _views.Clear();
    }

    // ------------------------------------------------------------------ utilità
    private static string Quote(string path) => path.Contains(' ') ? $"\"{path}\"" : path;

    private static (string exe, string args) SplitCommand(string commandLine)
    {
        commandLine = commandLine.Trim();
        if (commandLine.StartsWith('"'))
        {
            var end = commandLine.IndexOf('"', 1);
            return (commandLine[1..end], commandLine[(end + 1)..].Trim());
        }
        var sp = commandLine.IndexOf(' ');
        return sp < 0 ? (commandLine, "") : (commandLine[..sp], commandLine[(sp + 1)..].Trim());
    }

    private static string? Which(string exe)
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
        foreach (var dir in paths)
        {
            try
            {
                var full = Path.Combine(dir.Trim('"'), exe);
                if (File.Exists(full)) return full;
            }
            catch { }
        }
        return null;
    }
}
