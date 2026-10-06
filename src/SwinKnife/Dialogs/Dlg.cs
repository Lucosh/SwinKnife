using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Microsoft.Win32;
using SwinKnife.Core;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using TextBox = Wpf.Ui.Controls.TextBox;

namespace SwinKnife.Dialogs;

/// <summary>Finestra di dialogo modale in stile Fluent con contenuto libero e pulsanti.</summary>
public class FormDialog : FluentWindow
{
    private readonly StackPanel _buttons;
    public int Result { get; private set; } = -1;

    public FormDialog(string title, UIElement content, double width = 460)
    {
        Title = title;
        Language = L.Xml;
        Width = width;
        SizeToContent = SizeToContent.Height;
        ResizeMode = ResizeMode.NoResize;
        ExtendsContentIntoTitleBar = true;
        WindowBackdropType = WindowBackdropType.Mica;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        ShowInTaskbar = false;
        var owner = Application.Current?.MainWindow;
        if (owner != null && owner.IsLoaded && !ReferenceEquals(owner, this)) Owner = owner;
        else WindowStartupLocation = WindowStartupLocation.CenterScreen;

        var root = new Grid();
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        var bar = new TitleBar { Title = title, ShowMaximize = false, ShowMinimize = false, CanMaximize = false };
        root.Children.Add(bar);
        var body = new Border { Padding = new Thickness(24, 8, 24, 20), Child = content };
        Grid.SetRow(body, 1);
        root.Children.Add(body);
        var footer = new Border
        {
            Padding = new Thickness(24, 16, 24, 20),
            Background = (System.Windows.Media.Brush)FindResource("SolidBackgroundFillColorBaseBrush"),
        };
        _buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        footer.Child = _buttons;
        Grid.SetRow(footer, 2);
        root.Children.Add(footer);
        Content = root;
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == Key.Escape) { Result = -1; Close(); }
        };
    }

    public Button AddButton(string text, int result, bool primary = false, bool isDefault = false)
    {
        var b = new Button
        {
            Content = text,
            MinWidth = 110,
            Margin = new Thickness(8, 0, 0, 0),
            Appearance = primary ? ControlAppearance.Primary : ControlAppearance.Secondary,
            IsDefault = isDefault,
        };
        b.Click += (_, _) =>
        {
            Result = result;
            Close();
        };
        _buttons.Children.Add(b);
        return b;
    }

    public int Run()
    {
        ShowDialog();
        return Result;
    }
}

/// <summary>Scorciatoie per messaggi, conferme, richieste di input e scelta di file/cartelle.</summary>
public static class Dlg
{
    private static UIElement Message(string text, SymbolRegular? icon = null)
    {
        var panel = new DockPanel();
        if (icon is { } sym)
        {
            var ic = new SymbolIcon { Symbol = sym, FontSize = 28, Margin = new Thickness(0, 2, 16, 0), VerticalAlignment = VerticalAlignment.Top };
            DockPanel.SetDock(ic, Dock.Left);
            panel.Children.Add(ic);
        }
        panel.Children.Add(new TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        return panel;
    }

    private static void OnUi(Action a)
    {
        var d = Application.Current?.Dispatcher;
        if (d == null || d.CheckAccess()) a();
        else d.Invoke(a);
    }

    public static void Info(string text, string title = AppInfo.Name) => OnUi(() =>
    {
        var d = new FormDialog(title, Message(text, SymbolRegular.Info24));
        d.AddButton("OK", 0, true, true);
        d.Run();
    });

    public static void Error(string text, string? title = null) => OnUi(() =>
    {
        var d = new FormDialog(title ?? L.T("Si è verificato un problema"), Message(text, SymbolRegular.ErrorCircle24), 520);
        d.AddButton("OK", 0, true, true);
        d.Run();
    });

    public static bool Confirm(string text, string title = AppInfo.Name, string? yes = null, string? no = null)
    {
        var r = false;
        OnUi(() =>
        {
            var d = new FormDialog(title, Message(text, SymbolRegular.QuestionCircle24), 500);
            d.AddButton(yes ?? L.T("Sì"), 1, true, true);
            d.AddButton(no ?? L.T("No"), 0);
            r = d.Run() == 1;
        });
        return r;
    }

    public static string? Prompt(string title, string label, string initial = "", bool password = false, bool multiline = false)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        Control input;
        if (password)
        {
            input = new Wpf.Ui.Controls.PasswordBox { Password = initial };
        }
        else
        {
            var tb = new TextBox { Text = initial, AcceptsReturn = multiline, TextWrapping = multiline ? TextWrapping.Wrap : TextWrapping.NoWrap };
            if (multiline) tb.MinHeight = 110;
            input = tb;
        }
        panel.Children.Add(input);
        var d = new FormDialog(title, panel);
        d.AddButton("OK", 1, true, !multiline);
        d.AddButton(L.T("Annulla"), 0);
        d.Loaded += (_, _) =>
        {
            input.Focus();
            if (input is TextBox t) t.SelectAll();
        };
        if (d.Run() != 1) return null;
        return input is Wpf.Ui.Controls.PasswordBox pb ? pb.Password : ((TextBox)input).Text;
    }

    public static int? Choose(string title, string label, IList<string> options, int selected = 0)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var combo = new ComboBox { ItemsSource = options, SelectedIndex = selected };
        panel.Children.Add(combo);
        var d = new FormDialog(title, panel);
        d.AddButton("OK", 1, true, true);
        d.AddButton(L.T("Annulla"), 0);
        return d.Run() == 1 ? combo.SelectedIndex : null;
    }

    public static int? PromptInt(string title, string label, int value, int min, int max)
    {
        var panel = new StackPanel();
        panel.Children.Add(new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var nb = new TextBox { Text = value.ToString() };
        panel.Children.Add(nb);
        panel.Children.Add(new TextBlock { Text = L.T($"Valore da {min} a {max}"), Margin = new Thickness(0, 6, 0, 0), Opacity = 0.7 });
        var d = new FormDialog(title, panel);
        d.AddButton("OK", 1, true, true);
        d.AddButton(L.T("Annulla"), 0);
        d.Loaded += (_, _) =>
        {
            nb.Focus();
            nb.SelectAll();
        };
        return d.Run() == 1 && int.TryParse(nb.Text.Trim(), out var v) ? Math.Clamp(v, min, max) : null;
    }

    // ------------------------------------------------------------------ file e cartelle
    private static string LastDir(string key)
    {
        var d = Settings.Get("dir." + key);
        return d != null && Directory.Exists(d) ? d : Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
    }

    private static void Remember(string key, string path)
    {
        var dir = Directory.Exists(path) ? path : Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Settings.Set("dir." + key, dir);
    }

    public static string AllFiles => L.T("Tutti i file|*.*");

    public static string? OpenFile(string title, string? filter = null, string key = "open")
    {
        filter ??= AllFiles;
        var dlg = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = LastDir(key) };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return null;
        Remember(key, dlg.FileName);
        return dlg.FileName;
    }

    public static string[] OpenFiles(string title, string? filter = null, string key = "open")
    {
        filter ??= AllFiles;
        var dlg = new OpenFileDialog { Title = title, Filter = filter, InitialDirectory = LastDir(key), Multiselect = true };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return [];
        Remember(key, dlg.FileNames[0]);
        return dlg.FileNames;
    }

    public static string? SaveFile(string title, string suggested, string? filter = null, string key = "save")
    {
        filter ??= AllFiles;
        var dlg = new SaveFileDialog
        {
            Title = title,
            Filter = filter,
            FileName = Path.GetFileName(suggested),
            InitialDirectory = Path.IsPathRooted(suggested) ? Path.GetDirectoryName(suggested) : LastDir(key),
            OverwritePrompt = true,
        };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return null;
        Remember(key, dlg.FileName);
        return dlg.FileName;
    }

    public static string? PickFolder(string title, string key = "dir")
    {
        var dlg = new OpenFolderDialog { Title = title, InitialDirectory = LastDir(key) };
        if (dlg.ShowDialog(Application.Current?.MainWindow) != true) return null;
        Remember(key, dlg.FolderName);
        return dlg.FolderName;
    }
}
