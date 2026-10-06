using System.Windows;
using System.Windows.Controls;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using PasswordBox = Wpf.Ui.Controls.PasswordBox;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>Crea archivi ZIP/7z/TAR.GZ (anche con password AES-256) ed estrae qualsiasi archivio.</summary>
public sealed class ArchivePage : UserControl, IToolPage
{
    private readonly MainWindow _main;

    // ---- creazione
    private readonly ListBox _items = new() { MinHeight = 60, MaxHeight = 200 };
    private readonly ComboBox _format = new() { Width = 400 };
    private readonly ComboBox _level = new() { Width = 300 };
    private readonly PasswordBox _password = new() { PlaceholderText = L.T("Password (facoltativa)"), Width = 300 };
    private readonly PasswordBox _password2 = new() { PlaceholderText = L.T("Ripeti la password"), Width = 300 };
    private readonly TextBlock _pwHint = new() { Margin = new Thickness(0, 6, 0, 0) };
    private readonly TextBlock _createInfo = new();
    private readonly Wpf.Ui.Controls.Button _createBtn;

    // ---- estrazione
    private readonly TextBlock _archiveName = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _archiveInfo = new();
    private readonly System.Windows.Controls.TextBox _dest = new() { Width = 520 };
    private readonly PasswordBox _extractPw = new() { PlaceholderText = L.T("Password dell'archivio"), Width = 300, Visibility = Visibility.Collapsed };
    private readonly Wpf.Ui.Controls.Button _extractBtn, _browseBtn;
    private readonly CheckBox _openAfter = new() { Content = L.T("Apri la cartella al termine"), IsChecked = true };
    private string? _archive;
    private ArchiveSummary? _summary;

    // ---- avanzamento
    private readonly ProgressBar _progress = new() { Maximum = 1000, Height = 6 };
    private readonly TextBlock _progressText = new() { TextTrimming = TextTrimming.CharacterEllipsis, Margin = new Thickness(0, 6, 0, 0) };
    private readonly Wpf.Ui.Controls.Button _stop;
    private readonly Border _progressCard;
    private CancellationTokenSource? _cts;

    public ArchivePage(MainWindow main)
    {
        _main = main;
        foreach (var s in new[] { L.T("ZIP – si apre ovunque (Windows, Mac, telefoni)"), L.T("7z – il più compresso (serve 7-Zip per aprirlo)"), L.T("TAR.GZ – per Linux e macOS (senza password)") })
            _format.Items.Add(s);
        foreach (var s in new[] { L.T("Solo archivio (nessuna compressione)"), L.T("Veloce"), L.T("Normale"), L.T("Massima (più lenta)") }) _level.Items.Add(s);
        _format.SelectedIndex = 0;
        _level.SelectedIndex = 2;
        _format.SelectionChanged += (_, _) => UpdateCreate();
        _password.PasswordChanged += (_, _) => UpdateCreate();
        _password2.PasswordChanged += (_, _) => UpdateCreate();
        _pwHint.Style = (Style)Application.Current.FindResource("Hint");
        _createInfo.Style = (Style)Application.Current.FindResource("Hint");
        _archiveInfo.Style = (Style)Application.Current.FindResource("Hint");
        _items.SelectionMode = SelectionMode.Extended;

        // ---- crea
        var itemsBar = Ui.Row(
            Ui.Btn(L.T("Aggiungi file…"), SymbolRegular.DocumentAdd24, (_, _) => AddToCreate(Dlg.OpenFiles(L.T("File da comprimere"), Dlg.AllFiles, "archive"))),
            Ui.Btn(L.T("Aggiungi cartella…"), SymbolRegular.FolderAdd24, (_, _) =>
            {
                var d = Dlg.PickFolder(L.T("Cartella da comprimere"), "archive");
                if (d != null) AddToCreate([d]);
            }),
            Ui.Btn(L.T("Rimuovi"), SymbolRegular.Dismiss24, (_, _) =>
            {
                foreach (var s in _items.SelectedItems.Cast<object>().ToList()) _items.Items.Remove(s);
                UpdateCreate();
            }),
            _createInfo);
        itemsBar.Margin = new Thickness(0, 8, 0, 0);
        _createBtn = Ui.Btn(L.T("Crea archivio…"), SymbolRegular.FolderZip24, (_, _) => _ = Create(), primary: true);
        var create = new StackPanel();
        create.Children.Add(_items);
        create.Children.Add(itemsBar);
        create.Children.Add(Form(L.T("Formato"), _format));
        create.Children.Add(Form(L.T("Compressione"), _level));
        create.Children.Add(Form(L.T("Protezione"), _password));
        create.Children.Add(Form("", _password2));
        create.Children.Add(Form("", _pwHint));
        var cb = Ui.Row(_createBtn);
        cb.Margin = new Thickness(0, 14, 0, 0);
        create.Children.Add(cb);

        // ---- estrai
        _browseBtn = Ui.Btn(L.T("Scegli archivio…"), SymbolRegular.FolderOpen24, (_, _) =>
        {
            var p = Dlg.OpenFile(L.T("Archivio da estrarre"), L.T("Archivi|*.zip;*.7z;*.rar;*.tar;*.gz;*.tgz;*.bz2;*.xz;*.jar;*.cbz;*.cbr|Tutti i file|*.*"), "archive_open");
            if (p != null) SetArchive(p);
        });
        _extractBtn = Ui.Btn(L.T("Estrai"), SymbolRegular.ArrowExportUp24, (_, _) => _ = Extract(), primary: true);
        var extract = new StackPanel();
        var head = new DockPanel();
        DockPanel.SetDock(_browseBtn, Dock.Right);
        head.Children.Add(_browseBtn);
        var names = new StackPanel();
        names.Children.Add(_archiveName);
        names.Children.Add(_archiveInfo);
        head.Children.Add(names);
        extract.Children.Add(head);
        var destRow = Ui.Row(_dest, new Border { Width = 8 }, Ui.Btn(L.T("Cambia…"), null, (_, _) =>
        {
            var d = Dlg.PickFolder(L.T("Dove estrarre i file"), "extract");
            if (d != null) _dest.Text = Path.Combine(d, _archive != null ? Archives.Stem(_archive) : "");
        }));
        extract.Children.Add(Form(L.T("Estrai in"), destRow));
        extract.Children.Add(Form("", _extractPw));
        extract.Children.Add(Form("", _openAfter));
        var eb = Ui.Row(_extractBtn, Ui.Btn(L.T("Sfoglia il contenuto"), SymbolRegular.Open24, (_, _) =>
        {
            if (_archive != null) _main.OpenFile(_archive, "viewer");
        }));
        eb.Margin = new Thickness(0, 14, 0, 0);
        extract.Children.Add(eb);

        // ---- avanzamento
        _stop = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        var prog = new DockPanel();
        DockPanel.SetDock(_stop, Dock.Right);
        prog.Children.Add(_stop);
        var pp = new StackPanel { VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 12, 0) };
        pp.Children.Add(_progress);
        pp.Children.Add(_progressText);
        prog.Children.Add(pp);
        _progressCard = Ui.Card(L.T("Operazione in corso"), null, prog);
        _progressCard.Visibility = Visibility.Collapsed;

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Archivi"), L.T("Comprimi file e cartelle in ZIP o 7z, anche protetti da password, ed estrai ZIP, 7z, RAR, TAR e altri.")),
            _progressCard,
            Ui.Card(L.T("Estrai un archivio"), L.T("Trascina qui un archivio o sceglilo dal disco."), extract),
            Ui.Card(L.T("Crea un archivio"), L.T("Trascina qui file e cartelle oppure aggiungili con i pulsanti."), create));

        SetArchive(null);
        UpdateCreate();
    }

    private static FrameworkElement Form(string label, FrameworkElement content)
    {
        var g = new Grid { Margin = new Thickness(0, 10, 0, 0) };
        g.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(130) });
        g.ColumnDefinitions.Add(new ColumnDefinition());
        g.Children.Add(Ui.Label(label));
        Grid.SetColumn(content, 1);
        content.HorizontalAlignment = HorizontalAlignment.Left;
        g.Children.Add(content);
        return g;
    }

    // ------------------------------------------------------------------ file in ingresso
    public bool Accepts(string path) => true;

    public void OpenFile(string path) => AddFiles([path]);

    public void AddFiles(IReadOnlyList<string> paths)
    {
        // un solo archivio: lo si vuole estrarre; altrimenti si vuole comprimere
        if (paths.Count == 1 && File.Exists(paths[0]) && Archives.IsArchive(paths[0])) SetArchive(paths[0]);
        else AddToCreate(paths);
    }

    private void AddToCreate(IEnumerable<string> paths)
    {
        foreach (var p in paths)
            if ((File.Exists(p) || Directory.Exists(p)) && !_items.Items.Cast<string>().Contains(p, StringComparer.OrdinalIgnoreCase))
                _items.Items.Add(p);
        UpdateCreate();
    }

    // ------------------------------------------------------------------ creazione
    private ArchiveFormat Format => (ArchiveFormat)_format.SelectedIndex;

    private void UpdateCreate()
    {
        var tar = Format == ArchiveFormat.TarGz;
        _password.IsEnabled = _password2.IsEnabled = !tar;
        var pw = _password.Password;
        var mismatch = !tar && pw.Length > 0 && pw != _password2.Password;
        _pwHint.Text = tar ? L.T("Il formato TAR.GZ non supporta le password.") :
            pw.Length == 0 ? L.T("Con una password il contenuto viene cifrato con AES-256.") :
            mismatch ? L.T("Le due password non coincidono.") :
            Format == ArchiveFormat.SevenZip ? L.T("Verranno cifrati contenuto e nomi dei file (AES-256).") :
            L.T("Contenuto cifrato con AES-256. Si apre con Esplora risorse di Windows 11 24H2+, 7-Zip, WinRAR o SwinKnife.");
        _pwHint.Foreground = mismatch ? Ui.Res("SystemFillColorCriticalBrush") : Ui.Res("TextFillColorSecondaryBrush");
        var n = _items.Items.Count;
        _createInfo.Text = n == 0 ? L.T("  Nessun elemento") : (n == 1 ? L.T("  1 elemento") : L.T($"  {n} elementi"));
        _createBtn.IsEnabled = _cts == null && n > 0 && !mismatch;
    }

    private async Task Create()
    {
        var items = _items.Items.Cast<string>().ToList();
        if (items.Count == 0) return;
        var format = Format;
        var ext = format switch { ArchiveFormat.SevenZip => ".7z", ArchiveFormat.TarGz => ".tar.gz", _ => ".zip" };
        var baseName = items.Count == 1 ? Path.GetFileNameWithoutExtension(items[0].TrimEnd('\\')) : L.T("Archivio");
        if (items.Count == 1 && Directory.Exists(items[0])) baseName = Path.GetFileName(items[0].TrimEnd('\\'));
        var dir = Path.GetDirectoryName(items[0].TrimEnd('\\'));
        var dst = Dlg.SaveFile(L.T("Salva archivio"), baseName + ext, L.T($"Archivio {ext}|*{ext}"), "archive_save");
        if (dst == null) return;
        if (format == ArchiveFormat.TarGz && !dst.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)) dst = Path.ChangeExtension(dst, null) + ".tar.gz";
        var level = new[] { 0, 1, 5, 9 }[_level.SelectedIndex];
        var password = format == ArchiveFormat.TarGz || _password.Password.Length == 0 ? null : _password.Password;
        if (format == ArchiveFormat.SevenZip && SevenZip.Find() == null)
        {
            if (!Dlg.Confirm(L.T("Per creare archivi 7z serve 7-Zip. Scaricare ora il componente ufficiale da 7-zip.org (circa 600 KB)?"))) return;
            try
            {
                MainWindow.Notify(L.T("Download di 7-Zip…"), 0);
                await SevenZip.DownloadAsync(CancellationToken.None);
            }
            catch (Exception ex)
            {
                Dlg.Error(L.T("Download non riuscito:\n") + ex.Message);
                return;
            }
        }
        await Run(L.T($"Creazione di {Path.GetFileName(dst)}"), ct =>
        {
            Archives.Create(dst, items, format, level, password, Report, ct);
            return 0;
        }, _ =>
        {
            MainWindow.Notify(L.T($"Archivio creato: {Path.GetFileName(dst)} ({Util.HumanSize(new FileInfo(dst).Length)})"), 10);
            Util.Reveal(dst);
        });
    }

    // ------------------------------------------------------------------ estrazione
    private void SetArchive(string? path)
    {
        _archive = path;
        _summary = null;
        _extractPw.Password = "";
        _extractPw.Visibility = Visibility.Collapsed;
        if (path == null)
        {
            _archiveName.Text = L.T("Nessun archivio scelto");
            _archiveInfo.Text = "";
            _dest.Text = "";
            _extractBtn.IsEnabled = false;
            return;
        }
        _archiveName.Text = Path.GetFileName(path);
        _dest.Text = Path.Combine(Path.GetDirectoryName(path) ?? "", Archives.Stem(path));
        _extractBtn.IsEnabled = _cts == null;
        _ = Inspect(null);
    }

    private async Task Inspect(string? password)
    {
        var path = _archive;
        if (path == null) return;
        _archiveInfo.Text = L.T("Lettura…");
        try
        {
            var s = await Task.Run(() => Archives.Inspect(path, password));
            if (path != _archive) return;
            _summary = s;
            _archiveInfo.Text = $"{s.Type} · {Util.HumanSize(new FileInfo(path).Length)}" +
                                (s.Files >= 0 ? L.T($" · {Util.Number(s.Files)} file, {Util.HumanSize(s.Size)} una volta estratti") : "") +
                                (s.Encrypted ? L.T(" · protetto da password") : "");
            if (s.Encrypted) _extractPw.Visibility = Visibility.Visible;
            // se tutto è già dentro un'unica cartella non ne creo un'altra
            if (s.SingleRoot != null) _dest.Text = Path.Combine(Path.GetDirectoryName(path) ?? "", s.SingleRoot);
        }
        catch (ArchivePasswordException)
        {
            _archiveInfo.Text = L.T("Archivio protetto da password (anche i nomi dei file sono cifrati).");
            _extractPw.Visibility = Visibility.Visible;
        }
        catch (Exception ex)
        {
            _archiveInfo.Text = L.T("Impossibile leggere l'archivio: ") + ex.Message;
        }
    }

    private async Task Extract()
    {
        var path = _archive;
        if (path == null) return;
        var dest = _dest.Text.Trim();
        if (dest.Length == 0) return;
        // con una sola cartella radice si estrae nella cartella che la contiene
        var target = _summary?.SingleRoot != null && Path.GetFileName(dest).Equals(_summary.SingleRoot, StringComparison.OrdinalIgnoreCase)
            ? Path.GetDirectoryName(dest)! : dest;
        var password = _extractPw.Password.Length > 0 ? _extractPw.Password : null;
        var ok = await Run(L.T($"Estrazione di {Path.GetFileName(path)}"), ct => Archives.Extract(path, target, password, Report, ct), count =>
        {
            MainWindow.Notify(L.T($"Estratti {count} file in {dest}"), 10);
            if (_openAfter.IsChecked == true) Util.OpenFolder(Directory.Exists(dest) ? dest : target);
        });
        if (!ok && _lastError is ArchivePasswordException pe)
        {
            _extractPw.Visibility = Visibility.Visible;
            _extractPw.Focus();
            Dlg.Info(pe.Message + (password == null ? L.T(" Scrivila nel campo «Password dell'archivio» e premi di nuovo Estrai.") : ""));
        }
    }

    // ------------------------------------------------------------------ esecuzione
    private Exception? _lastError;
    private DateTime _lastReport;

    private void Report(double fraction, string name)
    {
        if ((DateTime.Now - _lastReport).TotalMilliseconds < 100 && fraction < 1) return;
        _lastReport = DateTime.Now;
        Dispatcher.InvokeAsync(() =>
        {
            _progress.Value = fraction * 1000;
            if (name.Length > 0) _progressText.Text = name;
        });
    }

    private async Task<bool> Run<T>(string title, Func<CancellationToken, T> work, Action<T> done)
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _lastError = null;
        ((TextBlock)((StackPanel)_progressCard.Child).Children[0]).Text = title;
        _progressCard.Visibility = Visibility.Visible;
        _progress.Value = 0;
        _progressText.Text = "";
        _createBtn.IsEnabled = _extractBtn.IsEnabled = false;
        try
        {
            var result = await Task.Run(() => work(ct), ct);
            done(result);
            return true;
        }
        catch (OperationCanceledException)
        {
            MainWindow.Notify(L.T("Operazione interrotta"));
            return false;
        }
        catch (ArchivePasswordException ex)
        {
            _lastError = ex;
            return false;
        }
        catch (Exception ex)
        {
            _lastError = ex;
            Dlg.Error(L.T("Operazione non riuscita:\n") + ex.Message);
            return false;
        }
        finally
        {
            _cts.Dispose();
            _cts = null;
            _progressCard.Visibility = Visibility.Collapsed;
            _extractBtn.IsEnabled = _archive != null;
            UpdateCreate();
        }
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è un'operazione sugli archivi in corso: interromperla?"));

    public void Shutdown() => _cts?.Cancel();
}
