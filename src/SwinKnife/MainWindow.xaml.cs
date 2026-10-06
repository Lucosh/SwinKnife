using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using SwinKnife.Pages;
using Wpf.Ui.Controls;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife;

public partial class MainWindow : FluentWindow
{
    private readonly Dictionary<string, FrameworkElement> _pages = new();
    private readonly DispatcherTimer _statusTimer = new();
    private bool _navigating;

    public MainWindow()
    {
        InitializeComponent();
        Language = L.Xml;
        VersionText.Text = "v" + AppInfo.Version;
        var group = "";
        foreach (var tool in Tools.All)
        {
            if (tool.Group != group && tool.Group.Length > 0)
            {
                group = tool.Group;
                Nav.Items.Add(new ListBoxItem
                {
                    Content = new TextBlock { Text = group.ToUpperInvariant(), FontSize = 11, FontWeight = FontWeights.SemiBold, Opacity = 0.55, Margin = new Thickness(0, 10, 0, 2) },
                    IsEnabled = false, Focusable = false, IsHitTestVisible = false, Height = 30,
                    Template = HeaderTemplate(),
                });
            }
            var row = new StackPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new SymbolIcon { Symbol = tool.Icon, FontSize = 18, Margin = new Thickness(0, 0, 14, 0) });
            row.Children.Add(new TextBlock { Text = tool.Title, FontSize = 14, VerticalAlignment = VerticalAlignment.Center });
            Nav.Items.Add(new ListBoxItem { Content = row, Tag = tool.Key, ToolTip = tool.Description.Length > 0 ? tool.Description : null });
        }
        _statusTimer.Tick += (_, _) =>
        {
            _statusTimer.Stop();
            StatusText.Text = L.T("Pronto");
        };
        Drop += OnDrop;
        DragOver += (_, e) =>
        {
            e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
            e.Handled = true;
        };
        ShowPage("home");
    }

    public static MainWindow? Instance => Application.Current?.MainWindow as MainWindow;

    /// <summary>Messaggio nella barra di stato (0 secondi = permanente).</summary>
    public void Status(string text, double seconds = 6)
    {
        StatusText.Text = string.IsNullOrEmpty(text) ? L.T("Pronto") : text;
        _statusTimer.Stop();
        if (seconds > 0)
        {
            _statusTimer.Interval = TimeSpan.FromSeconds(seconds);
            _statusTimer.Start();
        }
    }

    public static void Notify(string text, double seconds = 6) => Instance?.Status(text, seconds);

    private void Nav_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_navigating || Nav.SelectedItem is not ListBoxItem { Tag: string key }) return;
        ShowPage(key);
    }

    public FrameworkElement CurrentPage => (FrameworkElement)PageHost.Content;

    public FrameworkElement ShowPage(string key)
    {
        var tool = Tools.All.FirstOrDefault(t => t.Key == key) ?? Tools.All[0];
        if (!_pages.TryGetValue(tool.Key, out var page))
        {
            try
            {
                Mouse.OverrideCursor = Cursors.Wait;
                page = tool.Create(this);
                _pages[tool.Key] = page;
            }
            catch (Exception ex)
            {
                AppInfo.Log(ex, $"Creazione pagina {key}");
                Dlg.Error(L.T($"Impossibile aprire questo strumento:\n{ex.Message}"));
                return CurrentPage;
            }
            finally
            {
                Mouse.OverrideCursor = null;
            }
        }
        PageHost.Content = page;
        _navigating = true;
        Nav.SelectedItem = Nav.Items.OfType<ListBoxItem>().FirstOrDefault(i => i.Tag as string == tool.Key);
        _navigating = false;
        (page as IToolPage)?.Activated();
        return page;
    }

    public T Page<T>(string key) where T : FrameworkElement => (T)ShowPage(key);

    private static ControlTemplate HeaderTemplate()
    {
        var t = new ControlTemplate(typeof(ListBoxItem));
        var cp = new FrameworkElementFactory(typeof(ContentPresenter));
        cp.SetValue(MarginProperty, new Thickness(18, 0, 0, 0));
        t.VisualTree = cp;
        return t;
    }

    /// <summary>Gestisce gli argomenti della riga di comando (anche inviati da un'altra istanza o dal menu di Esplora risorse).</summary>
    public void HandleArgs(IReadOnlyList<string> args)
    {
        var list = args.ToList();
        string? Take(string name)
        {
            var i = list.IndexOf(name);
            if (i < 0 || i + 1 >= list.Count) return null;
            var v = list[i + 1];
            list.RemoveRange(i, 2);
            return v;
        }
        var page = Take("--page");
        var action = Take("--action");
        list.Remove("--elevated");
        list.Remove("--restart");
        var files = list.Where(a => File.Exists(a) || Directory.Exists(a)).Select(Path.GetFullPath).ToList();
        if (action != null)
        {
            QuickActions.Run(this, action, files);
            return;
        }
        if (files.Count == 0)
        {
            if (page != null) ShowPage(page);
            return;
        }
        if (page != null) (ShowPage(page) as IToolPage)?.AddFiles(files);
        else OpenFile(files[0]);
    }

    public void BringToFront()
    {
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Show();
        Activate();
        Topmost = true;
        Topmost = false;
        Focus();
    }

    /// <summary>Apre un file nello strumento indicato oppure in quello corrente se lo accetta, altrimenti nel visualizzatore.</summary>
    public void OpenFile(string path, string? key = null)
    {
        if (key == null)
        {
            if (CurrentPage is IToolPage cur && cur.Accepts(path))
            {
                cur.OpenFile(path);
                return;
            }
            key = Directory.Exists(path) ? "disk" : "viewer";
        }
        (ShowPage(key) as IToolPage)?.OpenFile(path);
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] files || files.Length == 0) return;
        e.Handled = true;
        if (CurrentPage is IToolPage page && files.Any(page.Accepts))
            page.AddFiles(files);
        else
            OpenFile(files[0]);
    }

    private bool _closeConfirmed;

    /// <summary>Chiede alle pagine se si può chiudere (modifiche non salvate); se sì non lo richiede alla chiusura.</summary>
    public bool CanCloseAllPages()
    {
        // copia: CanClose può aprire un dialogo e nel frattempo si possono creare altre pagine
        if (_pages.Values.OfType<IToolPage>().ToList().Any(p => !p.CanClose())) return false;
        _closeConfirmed = true;
        return true;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_closeConfirmed && !CanCloseAllPages())
        {
            e.Cancel = true;
            return;
        }
        SavePlacement();
        foreach (var p in _pages.Values.OfType<IToolPage>().ToList())
        {
            try { p.Shutdown(); }
            catch (Exception ex) { AppInfo.Log(ex, "Chiusura pagina"); }
        }
        base.OnClosing(e);
    }
}
