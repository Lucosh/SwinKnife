using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using SwinKnife.Controls;
using SwinKnife.Core;
using SwinKnife.Dialogs;
using Wpf.Ui.Controls;
using Button = Wpf.Ui.Controls.Button;
using TextBlock = System.Windows.Controls.TextBlock;

namespace SwinKnife.Pages;

/// <summary>
/// Recupero della password di un archivio di cui si è persa la password (ZIP, 7z, RAR):
/// prova a dizionario o a forza bruta, in memoria e su più core. Pensato per i propri archivi.
/// </summary>
public sealed class ArchivePasswordPage : UserControl, IToolPage
{
    private readonly MainWindow _main;

    // archivio
    private readonly TextBlock _archiveName = new() { FontSize = 15, FontWeight = FontWeights.SemiBold, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _archiveInfo = Ui.Hint(L.T("Nessun archivio scelto"));
    private string? _archive;

    // metodo
    private readonly RadioButton _modeDict = new() { Content = L.T("Da un elenco di parole (consigliato)"), IsChecked = true, Margin = new Thickness(0, 0, 0, 2) };
    private readonly RadioButton _modeBrute = new() { Content = L.T("Provando tutte le combinazioni (forza bruta)") };
    private readonly StackPanel _dictPanel = new();
    private readonly StackPanel _brutePanel = new() { Visibility = Visibility.Collapsed };

    private readonly CheckBox _useBuiltin = new() { Content = L.T("Usa l'elenco integrato delle password più comuni"), IsChecked = true, Margin = new Thickness(0, 0, 0, 6) };
    private readonly CheckBox _mutate = new() { Content = L.T("Prova anche le varianti (maiuscole, numeri e simboli in fondo)"), IsChecked = true };
    private readonly ListBox _wordlists = new() { MinHeight = 40, MaxHeight = 110, Margin = new Thickness(0, 6, 0, 6) };

    private readonly CheckBox _lower = new() { Content = L.T("minuscole (a–z)"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _upper = new() { Content = L.T("MAIUSCOLE (A–Z)"), Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _digits = new() { Content = L.T("numeri (0–9)"), IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly CheckBox _symbols = new() { Content = L.T("simboli (!@#…)"), Margin = new Thickness(0, 0, 16, 0) };
    private readonly System.Windows.Controls.TextBox _extraChars = new() { Width = 200, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumberBox _minLen = new() { Value = 1, Minimum = 1, Maximum = 16, Width = 90, ClearButtonEnabled = false };
    private readonly NumberBox _maxLen = new() { Value = 4, Minimum = 1, Maximum = 16, Width = 90, ClearButtonEnabled = false };
    private readonly TextBlock _bruteEstimate = Ui.Hint("");

    // avanzamento
    private readonly Button _startBtn, _stopBtn;
    private readonly Border _progressCard;
    private readonly ProgressBar _bar = new() { Height = 6, Margin = new Thickness(0, 4, 0, 10) };
    private readonly TextBlock _progressText = new() { TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock _currentText = Ui.Hint("");
    private readonly InfoBar _result = new() { IsOpen = false, IsClosable = false, Margin = new Thickness(0, 0, 0, 14) };

    private CancellationTokenSource? _cts;
    private string? _found;

    public ArchivePasswordPage(MainWindow main)
    {
        _main = main;

        var warn = new InfoBar
        {
            Severity = InfoBarSeverity.Warning, IsOpen = true, IsClosable = false,
            Title = L.T("Solo per i tuoi archivi"),
            Message = L.T("Usa questo strumento per riaprire un archivio tuo di cui hai dimenticato la password. Forzare la password di file altrui senza autorizzazione è illegale."),
            Margin = new Thickness(0, 0, 0, 14),
        };

        // --- archivio
        var pick = Ui.Btn(L.T("Scegli archivio…"), SymbolRegular.FolderOpen24, (_, _) =>
        {
            var f = Dlg.OpenFile(L.T("Archivio protetto"), L.T("Archivi|*.zip;*.7z;*.rar;*.cbz;*.cbr;*.jar;*.apk|Tutti i file|*.*"), "archivepw");
            if (f != null) SetArchive(f);
        }, primary: true);
        var archiveCard = Ui.Card(L.T("Archivio"), null,
            Ui.Row(pick),
            new StackPanel { Margin = new Thickness(0, 10, 0, 0), Children = { _archiveName, _archiveInfo } });

        // --- dizionario
        _wordlists.Visibility = Visibility.Collapsed;
        var addList = Ui.Btn(L.T("Aggiungi un elenco di parole…"), SymbolRegular.DocumentAdd24, (_, _) =>
        {
            foreach (var f in Dlg.OpenFiles(L.T("Elenchi di parole (file di testo)"), L.T("File di testo|*.txt;*.lst;*.dic|Tutti i file|*.*"), "wordlist"))
                if (!_wordlists.Items.Contains(f)) _wordlists.Items.Add(f);
            _wordlists.Visibility = _wordlists.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        var removeList = Ui.Btn(L.T("Rimuovi"), SymbolRegular.Dismiss24, (_, _) =>
        {
            if (_wordlists.SelectedItem is { } s) _wordlists.Items.Remove(s);
            _wordlists.Visibility = _wordlists.Items.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        });
        _dictPanel.Children.Add(_useBuiltin);
        _dictPanel.Children.Add(Ui.Row(addList, removeList));
        _dictPanel.Children.Add(_wordlists);
        _dictPanel.Children.Add(_mutate);

        // --- forza bruta
        foreach (var cb in new[] { _lower, _upper, _digits, _symbols })
            cb.Checked += (_, _) => UpdateEstimate();
        foreach (var cb in new[] { _lower, _upper, _digits, _symbols })
            cb.Unchecked += (_, _) => UpdateEstimate();
        _extraChars.TextChanged += (_, _) => UpdateEstimate();
        _minLen.ValueChanged += (_, _) => UpdateEstimate();
        _maxLen.ValueChanged += (_, _) => UpdateEstimate();
        var charsetRow = new WrapPanel { Margin = new Thickness(0, 0, 0, 8) };
        foreach (var cb in new UIElement[] { _lower, _upper, _digits, _symbols }) charsetRow.Children.Add(cb);
        _brutePanel.Children.Add(Ui.Label(L.T("Caratteri da provare"), bold: true));
        _brutePanel.Children.Add(charsetRow);
        _brutePanel.Children.Add(Ui.Row(Ui.Label(L.T("Altri caratteri:")), new Border { Width = 8 }, _extraChars));
        var lenRow = Ui.Row(Ui.Label(L.T("Lunghezza da")), new Border { Width = 8 }, _minLen,
            new Border { Width = 8 }, Ui.Label(L.T("a")), new Border { Width = 8 }, _maxLen,
            new Border { Width = 8 }, Ui.Label(L.T("caratteri")));
        lenRow.Margin = new Thickness(0, 10, 0, 6);
        _brutePanel.Children.Add(lenRow);
        _brutePanel.Children.Add(_bruteEstimate);

        _modeDict.Checked += (_, _) => { _dictPanel.Visibility = Visibility.Visible; _brutePanel.Visibility = Visibility.Collapsed; };
        _modeBrute.Checked += (_, _) => { _dictPanel.Visibility = Visibility.Collapsed; _brutePanel.Visibility = Visibility.Visible; UpdateEstimate(); };
        var methodCard = Ui.Card(L.T("Come cercare la password"), null,
            _modeDict, _dictPanel, new Border { Height = 8 }, _modeBrute, _brutePanel);

        // --- azioni + avanzamento
        _startBtn = Ui.Btn(L.T("Cerca la password"), SymbolRegular.Key24, async (_, _) => await Start(), primary: true);
        _stopBtn = Ui.Btn(L.T("Interrompi"), SymbolRegular.Stop24, (_, _) => _cts?.Cancel());
        _stopBtn.IsEnabled = false;
        _bar.Visibility = Visibility.Collapsed;
        _currentText.Visibility = Visibility.Collapsed;
        var progPanel = new StackPanel();
        progPanel.Children.Add(Ui.Row(_startBtn, _stopBtn));
        progPanel.Children.Add(new StackPanel { Margin = new Thickness(0, 12, 0, 0), Children = { _bar, _progressText, _currentText } });
        _progressCard = Ui.Card(L.T("Ricerca"), null, progPanel);

        Content = Ui.ScrollPage(
            Ui.Header(L.T("Recupero password archivi"),
                L.T("Hai dimenticato la password di un tuo archivio ZIP, 7z o RAR? Prova a ritrovarla.")),
            warn, _result, archiveCard, methodCard, _progressCard);

        UpdateEstimate();
    }

    // ------------------------------------------------------------------ archivio
    public bool Accepts(string path) => Archives.CanTestPassword(path);
    public void OpenFile(string path) => SetArchive(path);

    private void SetArchive(string? path)
    {
        _archive = path;
        _result.IsOpen = false;
        _found = null;
        if (path == null) { _archiveName.Text = ""; _archiveInfo.Text = L.T("Nessun archivio scelto"); return; }
        _archiveName.Text = Path.GetFileName(path);
        _archiveInfo.Text = L.T("Controllo…");
        _ = InspectAsync(path);
    }

    private async Task InspectAsync(string path)
    {
        if (!Archives.CanTestPassword(path))
        {
            _archiveInfo.Text = L.T("Su questo tipo di archivio non si può provare la password. Funziona con ZIP, 7z e RAR.");
            return;
        }
        try
        {
            var enc = await Task.Run(() =>
            {
                try { return Archives.Inspect(path, null).Encrypted; }
                catch (ArchivePasswordException) { return true; } // 7z con nomi cifrati
            });
            if (path != _archive) return;
            _archiveInfo.Text = enc
                ? L.T($"Archivio protetto da password · {Util.HumanSize(new FileInfo(path).Length)}")
                : L.T("Questo archivio non ha una password: puoi aprirlo direttamente da «Archivi».");
        }
        catch (Exception ex)
        {
            if (path == _archive) _archiveInfo.Text = L.T("Impossibile leggere l'archivio: ") + ex.Message;
        }
    }

    // ------------------------------------------------------------------ forza bruta: stima
    private string BuildCharset()
    {
        var chars = new SortedSet<char>();
        if (_lower.IsChecked == true) for (var c = 'a'; c <= 'z'; c++) chars.Add(c);
        if (_upper.IsChecked == true) for (var c = 'A'; c <= 'Z'; c++) chars.Add(c);
        if (_digits.IsChecked == true) for (var c = '0'; c <= '9'; c++) chars.Add(c);
        if (_symbols.IsChecked == true) foreach (var c in "!@#$%^&*()-_=+[]{};:,.?") chars.Add(c);
        foreach (var c in _extraChars.Text) chars.Add(c);
        return new string(chars.ToArray());
    }

    private (int min, int max) LenRange()
    {
        var min = (int)Math.Round(_minLen.Value ?? 1);
        var max = (int)Math.Round(_maxLen.Value ?? 1);
        if (max < min) (min, max) = (max, min);
        return (Math.Clamp(min, 1, 16), Math.Clamp(max, 1, 16));
    }

    private static double Combinations(int n, int min, int max)
    {
        if (n == 0) return 0;
        double total = 0;
        for (var len = min; len <= max; len++) total += Math.Pow(n, len);
        return total;
    }

    private void UpdateEstimate()
    {
        if (_modeBrute.IsChecked != true) return;
        var set = BuildCharset();
        var (min, max) = LenRange();
        if (set.Length == 0) { _bruteEstimate.Text = L.T("Scegli almeno un gruppo di caratteri."); return; }
        var combos = Combinations(set.Length, min, max);
        var warn = max - min >= 0 && set.Length >= 1 && combos > 5e9
            ? L.T("  ⚠ Sono tantissime: potrebbe non finire mai. Riduci la lunghezza o i caratteri.")
            : "";
        _bruteEstimate.Text = L.T($"{set.Length} caratteri · circa {Util.Number((long)Math.Min(combos, long.MaxValue))} combinazioni da provare.") + warn;
    }

    // ------------------------------------------------------------------ ricerca
    private async Task Start()
    {
        var path = _archive;
        if (path == null) { Dlg.Info(L.T("Scegli prima un archivio.")); return; }
        if (!Archives.CanTestPassword(path)) { Dlg.Info(L.T("Su questo archivio non si può provare la password.")); return; }

        IEnumerable<string> candidates;
        long? total = null;
        if (_modeDict.IsChecked == true)
        {
            List<string> words = new();
            if (_useBuiltin.IsChecked == true) words.AddRange(CommonPasswords.List);
            foreach (var wl in _wordlists.Items.Cast<string>())
            {
                try { words.AddRange(File.ReadLines(wl)); }
                catch (Exception ex) { Dlg.Error(L.T($"Non riesco a leggere {Path.GetFileName(wl)}:\n") + ex.Message); }
            }
            if (words.Count == 0) { Dlg.Info(L.T("Aggiungi un elenco di parole oppure tieni attivo l'elenco integrato.")); return; }
            candidates = Expand(words, _mutate.IsChecked == true);
        }
        else
        {
            var set = BuildCharset();
            var (min, max) = LenRange();
            if (set.Length == 0) { Dlg.Info(L.T("Scegli almeno un gruppo di caratteri.")); return; }
            var combos = Combinations(set.Length, min, max);
            if (combos > 5e9 && !Dlg.Confirm(L.T("Ci sono moltissime combinazioni da provare e la ricerca potrebbe durare giorni o non finire. Vuoi procedere lo stesso?")))
                return;
            if (combos <= long.MaxValue) total = (long)combos;
            candidates = BruteForce(set, min, max);
        }

        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        _found = null;
        _result.IsOpen = false;
        _startBtn.IsEnabled = false;
        _stopBtn.IsEnabled = true;
        _bar.Visibility = Visibility.Visible;
        _currentText.Visibility = Visibility.Visible;
        _bar.IsIndeterminate = total == null;
        _bar.Value = 0;
        _progressText.Text = L.T("Avvio…");

        var tried = new long[1];
        var sw = Stopwatch.StartNew();
        var currentLock = new object();
        var current = "";
        var ui = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        ui.Tick += (_, _) =>
        {
            var n = Interlocked.Read(ref tried[0]);
            var rate = n / Math.Max(0.001, sw.Elapsed.TotalSeconds);
            _progressText.Text = total is { } t
                ? L.T($"Provate {Util.Number(n)} di {Util.Number(t)} ({(t > 0 ? n * 100.0 / t : 0):0.0}%) · {Util.Number((long)rate)}/s")
                : L.T($"Provate {Util.Number(n)} password · {Util.Number((long)rate)}/s");
            if (total is { } tt && tt > 0) _bar.Value = Math.Min(100, n * 100.0 / tt);
            string snap;
            lock (currentLock) snap = current;
            _currentText.Text = snap.Length > 0 ? L.T("In prova: ") + snap : "";
        };
        ui.Start();

        string? found;
        try
        {
            found = await Task.Run(() => Search(path, candidates, tried, s => { lock (currentLock) current = s; }, ct), ct);
        }
        catch (OperationCanceledException) { found = null; }
        catch (Exception ex)
        {
            ui.Stop();
            Finish();
            Dlg.Error(L.T("La ricerca si è interrotta per un errore:\n") + ex.Message);
            return;
        }
        ui.Stop();
        sw.Stop();
        Finish();

        if (found != null)
        {
            _found = found;
            _result.Severity = InfoBarSeverity.Success;
            _result.Title = L.T("Password trovata!");
            _result.Message = L.T($"La password è:  {found}");
            _result.IsOpen = true;
            try { Clipboard.SetText(found); MainWindow.Notify(L.T("Password trovata e copiata negli appunti."), 10); } catch { }
            Dlg.Info(L.T($"La password dell'archivio è:\n\n{found}\n\nL'ho copiata negli appunti.") , L.T("Password trovata"));
        }
        else if (!ct.IsCancellationRequested)
        {
            _result.Severity = InfoBarSeverity.Warning;
            _result.Title = L.T("Password non trovata");
            _result.Message = _modeDict.IsChecked == true
                ? L.T("Nessuna delle parole provate è quella giusta. Prova ad aggiungere un elenco più grande o la forza bruta.")
                : L.T("Nessuna combinazione ha funzionato. Prova ad allungare la lunghezza o ad aggiungere altri caratteri.");
            _result.IsOpen = true;
        }
        else
            MainWindow.Notify(L.T("Ricerca interrotta."));
    }

    private void Finish()
    {
        _startBtn.IsEnabled = true;
        _stopBtn.IsEnabled = false;
        _bar.IsIndeterminate = false;
        _bar.Visibility = Visibility.Collapsed;
        _currentText.Visibility = Visibility.Collapsed;
        _progressText.Text = "";
        _cts?.Dispose();
        _cts = null;
    }

    /// <summary>Distribuisce le password candidate su più core finché una funziona o si annulla.</summary>
    private static string? Search(string path, IEnumerable<string> candidates, long[] tried, Action<string> onCurrent, CancellationToken ct)
    {
        using var it = candidates.GetEnumerator();
        var gate = new object();
        string? result = null;
        var workers = Math.Max(1, Environment.ProcessorCount - 1);
        var threads = new List<Thread>();

        string? Next()
        {
            lock (gate) return it.MoveNext() ? it.Current : null;
        }

        for (var i = 0; i < workers; i++)
        {
            var th = new Thread(() =>
            {
                while (!ct.IsCancellationRequested && Volatile.Read(ref result) == null)
                {
                    var pw = Next();
                    if (pw == null) break;
                    if (pw.Length == 0) continue;
                    var n = Interlocked.Increment(ref tried[0]);
                    if ((n & 7) == 0) onCurrent(pw);
                    if (Archives.TestPassword(path, pw))
                    {
                        lock (gate) result ??= pw;
                        return;
                    }
                }
            }) { IsBackground = true };
            threads.Add(th);
            th.Start();
        }
        foreach (var th in threads) th.Join();
        return result;
    }

    // ------------------------------------------------------------------ generatori di candidate
    private static readonly string[] Suffixes = { "1", "2", "12", "123", "1234", "12345", "!", "01", "00", "007", "2023", "2024", "2025", "2026" };

    private static IEnumerable<string> Expand(IEnumerable<string> words, bool mutate)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var raw in words)
        {
            var w = raw.Trim();
            if (w.Length == 0) continue;
            if (seen.Add(w)) yield return w;
            if (!mutate) continue;
            var variants = new List<string> { w.ToLowerInvariant(), w.ToUpperInvariant(), Capitalize(w) };
            foreach (var v in variants) if (seen.Add(v)) yield return v;
            foreach (var baseWord in new[] { w, Capitalize(w) })
                foreach (var suf in Suffixes)
                {
                    var c = baseWord + suf;
                    if (seen.Add(c)) yield return c;
                }
        }
    }

    private static string Capitalize(string w) => w.Length == 0 ? w : char.ToUpperInvariant(w[0]) + w[1..].ToLowerInvariant();

    private static IEnumerable<string> BruteForce(string charset, int min, int max)
    {
        var chars = charset.ToCharArray();
        for (var len = min; len <= max; len++)
        {
            var idx = new int[len];
            var buf = new char[len];
            for (var i = 0; i < len; i++) buf[i] = chars[0];
            while (true)
            {
                yield return new string(buf);
                var p = len - 1;
                while (p >= 0)
                {
                    if (++idx[p] < chars.Length) { buf[p] = chars[idx[p]]; break; }
                    idx[p] = 0; buf[p] = chars[0]; p--;
                }
                if (p < 0) break;
            }
        }
    }

    public bool CanClose() => _cts == null || Dlg.Confirm(L.T("C'è una ricerca della password in corso: interromperla?"));
    public void Shutdown() => _cts?.Cancel();
}
