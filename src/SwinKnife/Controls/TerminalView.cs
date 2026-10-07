using System.Globalization;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using SwinKnife.Core;

namespace SwinKnife.Controls;

/// <summary>Un terminale completo: la superficie di disegno (<see cref="TerminalSurface"/>) con la barra di scorrimento.</summary>
public sealed class TerminalView : DockPanel
{
    private readonly TerminalSurface _surface;
    private readonly ScrollBar _scroll = new() { Orientation = Orientation.Vertical, Width = 12, SmallChange = 1, LargeChange = 10 };

    public event Action<string>? TitleChanged;
    public event Action<int>? Exited;

    public TerminalView()
    {
        _surface = new TerminalSurface();
        _surface.TitleChanged += t => TitleChanged?.Invoke(t);
        _surface.Exited += c => Exited?.Invoke(c);
        _surface.ScrollInfoChanged += SyncScroll;
        _scroll.Scroll += (_, _) => _surface.ScrollToBottomLines((int)(_scroll.Maximum - _scroll.Value));
        DockPanel.SetDock(_scroll, Dock.Right);
        Children.Add(_scroll);
        Children.Add(_surface);
        Background = _surface.BackgroundBrush;
    }

    public void Start(string commandLine, string? workingDir = null, IDictionary<string, string>? env = null) =>
        _surface.Start(commandLine, workingDir, env);

    public bool HasSelection => _surface.HasSelection;
    public void Copy() => _surface.CopySelection();
    public void Paste() => _surface.Paste();
    public void SelectAllText() => _surface.SelectAll();
    public void Clear() => _surface.ClearScreen();
    public void FocusTerminal() => _surface.Focus();
    public void SendText(string text) => _surface.Send(text);
    public void Shutdown() => _surface.Shutdown();
    public bool IsAlive => _surface.IsAlive;

    private void SyncScroll(int total, int rows, int offsetFromBottom)
    {
        var max = Math.Max(0, total - rows);
        _scroll.Maximum = max;
        _scroll.ViewportSize = rows;
        _scroll.Value = max - offsetFromBottom;
        _scroll.Visibility = max > 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

/// <summary>Disegna la griglia del terminale, gestisce tastiera, mouse e la comunicazione con la pseudo-console.</summary>
public sealed class TerminalSurface : FrameworkElement
{
    private readonly VtScreen _screen = new(80, 25);
    private ConPty? _pty;
    private readonly GlyphTypeface _glyph;
    private readonly double _fontSize = 14;
    private readonly double _cellW, _cellH, _baseline;
    private double _dpi = 1;
    private int _scrollOffset;       // righe sopra il fondo (0 = in diretta)
    private bool _focused;
    private readonly Dictionary<int, Brush> _brushCache = new();

    // selezione col mouse (coordinate in righe/colonne del buffer completo)
    private bool _selecting;
    private (int line, int col) _selA, _selB;
    private bool _hasSel;

    public event Action<string>? TitleChanged;
    public event Action<int>? Exited;
    public event Action<int, int, int>? ScrollInfoChanged;

    public Brush BackgroundBrush { get; } = Frozen(Color.FromRgb(0x0C, 0x0C, 0x0C));
    private readonly Brush _defaultFg = Frozen(Color.FromRgb(0xCC, 0xCC, 0xCC));
    private readonly Brush _selectionBg = Frozen(Color.FromArgb(0x80, 0x3A, 0x6E, 0xA5));
    private readonly Brush _cursorBrush = Frozen(Color.FromRgb(0xCC, 0xCC, 0xCC));

    public TerminalSurface()
    {
        Focusable = true;
        FocusVisualStyle = null;
        ClipToBounds = true;
        var typeface = PickFont();
        if (!typeface.TryGetGlyphTypeface(out _glyph!))
            new Typeface("Consolas").TryGetGlyphTypeface(out _glyph!);
        _cellW = _glyph.AdvanceWidths[_glyph.CharacterToGlyphMap[' ']] * _fontSize;
        _cellH = Math.Ceiling(_glyph.Height * _fontSize);
        _baseline = _glyph.Baseline * _fontSize;
        Cursor = Cursors.IBeam;
    }

    private static Typeface PickFont()
    {
        foreach (var name in new[] { "Cascadia Mono", "Cascadia Code", "Consolas", "Lucida Console" })
        {
            var tf = new Typeface(new FontFamily(name), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);
            if (tf.TryGetGlyphTypeface(out _)) return tf;
        }
        return new Typeface("Consolas");
    }

    private static Brush Frozen(Color c)
    {
        var b = new SolidColorBrush(c);
        b.Freeze();
        return b;
    }

    public bool IsAlive => _pty != null;
    public bool HasSelection => _hasSel;

    private (string cmd, string? cwd, IDictionary<string, string>? env)? _pending;

    public void Start(string commandLine, string? workingDir, IDictionary<string, string>? env)
    {
        _pending = (commandLine, workingDir, env);
        TryStart();
    }

    private void TryStart()
    {
        if (_pending is not { } p || _pty != null) return;
        if (ActualWidth < _cellW || ActualHeight < _cellH) return; // non ancora disposto: aspetto la dimensione reale
        _pending = null;
        var (cols, rows) = Grid();
        try
        {
            _pty = new ConPty(p.cmd, (short)cols, (short)rows, p.cwd, p.env);
        }
        catch (Exception ex)
        {
            _screen.Feed("\x1b[31m" + L.T("Impossibile avviare il terminale: ") + ex.Message + "\x1b[0m\r\n");
            InvalidateVisual();
            return;
        }
        _screen.Respond = s => _pty?.Write(s);
        _pty.Exited += code => Dispatcher.BeginInvoke(() => { Exited?.Invoke(code); _pty = null; });
        _ = ReadLoopAsync(_pty);
        Focus();
    }

    private async Task ReadLoopAsync(ConPty pty)
    {
        var stream = pty.Output;
        var buffer = new byte[8192];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[8192];
        try
        {
            while (true)
            {
                var n = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (n <= 0) break;
                var count = decoder.GetChars(buffer, 0, n, chars, 0);
                if (count == 0) continue;
                var text = new string(chars, 0, count);
                await Dispatcher.InvokeAsync(() =>
                {
                    _screen.Title = TitleChanged;
                    _screen.Feed(text);
                    if (_scrollOffset == 0) { } else ClampOffset();
                    InvalidateVisual();
                    RaiseScroll();
                });
            }
        }
        catch { /* chiusura della pipe */ }
    }

    public void Shutdown()
    {
        _pty?.Dispose();
        _pty = null;
    }

    // ------------------------------------------------------------------ dimensione / griglia
    private (int cols, int rows) Grid()
    {
        var cols = Math.Max(1, (int)(ActualWidth / _cellW));
        var rows = Math.Max(1, (int)(ActualHeight / _cellH));
        return (cols, rows);
    }

    protected override void OnRenderSizeChanged(SizeChangedInfo info)
    {
        base.OnRenderSizeChanged(info);
        var (cols, rows) = Grid();
        _screen.Resize(cols, rows);
        _pty?.Resize((short)cols, (short)rows);
        ClampOffset();
        TryStart();
        InvalidateVisual();
        RaiseScroll();
    }

    private void ClampOffset() => _scrollOffset = Math.Clamp(_scrollOffset, 0, Math.Max(0, _screen.Total - _screen.Rows));

    public void ScrollToBottomLines(int offsetFromBottom)
    {
        _scrollOffset = Math.Clamp(offsetFromBottom, 0, Math.Max(0, _screen.Total - _screen.Rows));
        InvalidateVisual();
        RaiseScroll();
    }

    private void RaiseScroll() => ScrollInfoChanged?.Invoke(_screen.Total, _screen.Rows, _screen.AltScreen ? 0 : _scrollOffset);

    protected override void OnMouseWheel(MouseWheelEventArgs e)
    {
        if (_screen.AltScreen) { Send(e.Delta > 0 ? "\x1b[A\x1b[A\x1b[A" : "\x1b[B\x1b[B\x1b[B"); e.Handled = true; return; }
        _scrollOffset = Math.Clamp(_scrollOffset + (e.Delta > 0 ? 3 : -3), 0, Math.Max(0, _screen.Total - _screen.Rows));
        InvalidateVisual();
        RaiseScroll();
        e.Handled = true;
    }

    // ------------------------------------------------------------------ disegno
    protected override void OnRender(DrawingContext dc)
    {
        _dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        dc.DrawRectangle(BackgroundBrush, null, new Rect(0, 0, ActualWidth, ActualHeight));
        if (_screen.Total == 0) return;

        var first = _screen.Total - _screen.Rows - (_screen.AltScreen ? 0 : _scrollOffset);
        for (var r = 0; r < _screen.Rows; r++)
        {
            var lineIndex = first + r;
            if (lineIndex < 0 || lineIndex >= _screen.Total) continue;
            DrawRow(dc, _screen.LineAt(lineIndex), r, lineIndex);
        }
        DrawCursor(dc, first);
    }

    private void DrawRow(DrawingContext dc, Cell[] line, int r, int lineIndex)
    {
        var y = r * _cellH;
        // sfondi
        var x = 0;
        while (x < line.Length)
        {
            var bg = ResolveBg(line[x], out _);
            if (bg == null) { x++; continue; }
            var start = x;
            while (x < line.Length && ResolveBg(line[x], out _) == bg) x++;
            dc.DrawRectangle(bg, null, new Rect(start * _cellW, y, (x - start) * _cellW, _cellH));
        }
        // selezione
        if (_hasSel) DrawSelection(dc, line, r, lineIndex, y);
        // testo, raggruppato per colore
        x = 0;
        while (x < line.Length)
        {
            if (line[x].Ch is '\0' or ' ' && (line[x].Attr & VtScreen.Underline) == 0) { x++; continue; }
            var fg = ResolveFg(line[x]);
            var underline = (line[x].Attr & VtScreen.Underline) != 0;
            var start = x;
            var sb = new StringBuilder();
            while (x < line.Length && ResolveFg(line[x]) == fg && ((line[x].Attr & VtScreen.Underline) != 0) == underline)
            {
                var ch = line[x].Ch;
                sb.Append(ch == '\0' ? ' ' : ch);
                x++;
            }
            DrawText(dc, sb.ToString(), start, y, fg);
            if (underline)
                dc.DrawRectangle(fg, null, new Rect(start * _cellW, y + _cellH - 2, (x - start) * _cellW, 1));
        }
    }

    private void DrawText(DrawingContext dc, string s, int col, double y, Brush brush)
    {
        if (s.Trim().Length == 0) return;
        var indices = new ushort[s.Length];
        var advances = new double[s.Length];
        for (var i = 0; i < s.Length; i++)
        {
            indices[i] = _glyph.CharacterToGlyphMap.TryGetValue(s[i], out var gi) ? gi : (ushort)0;
            advances[i] = _cellW;
        }
        var origin = new Point(col * _cellW, y + _baseline);
        try
        {
            var run = new GlyphRun(_glyph, 0, false, _fontSize, (float)_dpi, indices, origin, advances,
                null, null, null, null, null, null);
            dc.DrawGlyphRun(brush, run);
        }
        catch { /* carattere non disegnabile */ }
    }

    private void DrawSelection(DrawingContext dc, Cell[] line, int r, int lineIndex, double y)
    {
        var (a, b) = OrderedSel();
        if (lineIndex < a.line || lineIndex > b.line) return;
        var from = lineIndex == a.line ? a.col : 0;
        var to = lineIndex == b.line ? b.col : line.Length;
        from = Math.Clamp(from, 0, line.Length);
        to = Math.Clamp(to, 0, line.Length);
        if (to > from) dc.DrawRectangle(_selectionBg, null, new Rect(from * _cellW, y, (to - from) * _cellW, _cellH));
    }

    private void DrawCursor(DrawingContext dc, int first)
    {
        if (!_screen.CursorVisible || _scrollOffset != 0) return;
        var r = _screen.CursorY;
        var rect = new Rect(_screen.CursorX * _cellW, r * _cellH, _cellW, _cellH);
        if (_focused) dc.DrawRectangle(_cursorBrush, null, rect);
        else dc.DrawRectangle(null, new Pen(_cursorBrush, 1), rect);
        // ridisegna il carattere sotto il cursore con i colori invertiti
        if (_focused)
        {
            var line = _screen.LineAt(first + r);
            if (_screen.CursorX < line.Length)
            {
                var ch = line[_screen.CursorX].Ch;
                if (ch is not ('\0' or ' ')) DrawText(dc, ch.ToString(), _screen.CursorX, r * _cellH, BackgroundBrush);
            }
        }
    }

    // ------------------------------------------------------------------ colori
    private Brush ResolveFg(Cell c)
    {
        var v = c.Fg;
        if ((c.Attr & VtScreen.Inverse) != 0) v = c.Bg;
        if ((c.Attr & VtScreen.Bold) != 0 && v is >= 0 and < 8) v += 8; // grassetto = colore acceso
        if (v < 0) return (c.Attr & VtScreen.Inverse) != 0 ? BackgroundBrush : _defaultFg;
        return BrushFor(v);
    }

    private Brush? ResolveBg(Cell c, out bool isDefault)
    {
        var v = c.Bg;
        if ((c.Attr & VtScreen.Inverse) != 0) v = c.Fg;
        isDefault = v < 0;
        if (v < 0) return (c.Attr & VtScreen.Inverse) != 0 ? _defaultFg : null;
        return BrushFor(v);
    }

    private Brush BrushFor(int v)
    {
        if (_brushCache.TryGetValue(v, out var b)) return b;
        b = Frozen(ColorFor(v));
        _brushCache[v] = b;
        return b;
    }

    private static Color ColorFor(int v)
    {
        if ((v & 0x1_000000) != 0) return Color.FromRgb((byte)(v >> 16), (byte)(v >> 8), (byte)v);
        if (v < 16) return Palette16[v];
        if (v < 232) { v -= 16; return Color.FromRgb(Cube(v / 36), Cube(v / 6 % 6), Cube(v % 6)); }
        var g = (byte)(8 + (v - 232) * 10);
        return Color.FromRgb(g, g, g);
    }

    private static byte Cube(int n) => (byte)(n == 0 ? 0 : 55 + n * 40);

    private static readonly Color[] Palette16 =
    [
        Color.FromRgb(0x0C,0x0C,0x0C), Color.FromRgb(0xC5,0x0F,0x1F), Color.FromRgb(0x13,0xA1,0x0E), Color.FromRgb(0xC1,0x9C,0x00),
        Color.FromRgb(0x00,0x37,0xDA), Color.FromRgb(0x88,0x17,0x98), Color.FromRgb(0x3A,0x96,0xDD), Color.FromRgb(0xCC,0xCC,0xCC),
        Color.FromRgb(0x76,0x76,0x76), Color.FromRgb(0xE7,0x48,0x56), Color.FromRgb(0x16,0xC6,0x0C), Color.FromRgb(0xF9,0xF1,0xA5),
        Color.FromRgb(0x3B,0x78,0xFF), Color.FromRgb(0xB4,0x00,0x9E), Color.FromRgb(0x61,0xD6,0xD6), Color.FromRgb(0xF2,0xF2,0xF2),
    ];

    // ------------------------------------------------------------------ fuoco e mouse
    protected override void OnGotKeyboardFocus(KeyboardFocusChangedEventArgs e) { _focused = true; InvalidateVisual(); base.OnGotKeyboardFocus(e); }
    protected override void OnLostKeyboardFocus(KeyboardFocusChangedEventArgs e) { _focused = false; InvalidateVisual(); base.OnLostKeyboardFocus(e); }

    protected override void OnMouseDown(MouseButtonEventArgs e)
    {
        Focus();
        if (e.ChangedButton == MouseButton.Left)
        {
            _selecting = true;
            _selA = _selB = CellAt(e.GetPosition(this));
            _hasSel = false;
            CaptureMouse();
            InvalidateVisual();
        }
        else if (e.ChangedButton == MouseButton.Middle)
        {
            Paste();
        }
        base.OnMouseDown(e);
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        if (_selecting && e.LeftButton == MouseButtonState.Pressed)
        {
            _selB = CellAt(e.GetPosition(this));
            _hasSel = _selA != _selB;
            InvalidateVisual();
        }
        base.OnMouseMove(e);
    }

    protected override void OnMouseUp(MouseButtonEventArgs e)
    {
        if (_selecting && e.ChangedButton == MouseButton.Left)
        {
            _selecting = false;
            ReleaseMouseCapture();
        }
        base.OnMouseUp(e);
    }

    private (int line, int col) CellAt(Point p)
    {
        var first = _screen.Total - _screen.Rows - (_screen.AltScreen ? 0 : _scrollOffset);
        var r = Math.Clamp((int)(p.Y / _cellH), 0, _screen.Rows - 1);
        var col = Math.Clamp((int)(p.X / _cellW), 0, _screen.Cols);
        return (first + r, col);
    }

    private ((int line, int col) a, (int line, int col) b) OrderedSel() =>
        _selA.line < _selB.line || (_selA.line == _selB.line && _selA.col <= _selB.col) ? (_selA, _selB) : (_selB, _selA);

    public void SelectAll()
    {
        _selA = (0, 0);
        _selB = (Math.Max(0, _screen.Total - 1), _screen.Cols);
        _hasSel = _screen.Total > 0;
        InvalidateVisual();
    }

    public void CopySelection()
    {
        if (!_hasSel) return;
        var (a, b) = OrderedSel();
        var text = _screen.TextRange(a.line, a.col, b.line, b.col);
        if (text.Length > 0) try { Clipboard.SetText(text); } catch { }
    }

    public void Paste()
    {
        string text;
        try { text = Clipboard.ContainsText() ? Clipboard.GetText() : ""; } catch { return; }
        if (string.IsNullOrEmpty(text)) return;
        text = text.Replace("\r\n", "\r").Replace('\n', '\r');
        if (_screen.BracketedPaste) Send("\x1b[200~" + text + "\x1b[201~");
        else Send(text);
    }

    public void ClearScreen()
    {
        Send("\x1b[H\x1b[2J\x1b[3J");
    }

    public void Send(string text) => _pty?.Write(text);

    // ------------------------------------------------------------------ tastiera
    protected override void OnTextInput(TextCompositionEventArgs e)
    {
        if (!string.IsNullOrEmpty(e.Text) && _pty != null)
        {
            Send(e.Text);
            GoLive();
            e.Handled = true;
        }
        base.OnTextInput(e);
    }

    protected override void OnPreviewKeyDown(KeyEventArgs e)
    {
        if (_pty == null) { base.OnPreviewKeyDown(e); return; }
        var ctrl = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);

        // scorciatoie di copia/incolla
        if (ctrl && shift && e.Key == Key.C) { CopySelection(); e.Handled = true; return; }
        if (ctrl && shift && e.Key == Key.V) { Paste(); e.Handled = true; return; }
        if (ctrl && e.Key == Key.C && _hasSel) { CopySelection(); _hasSel = false; InvalidateVisual(); e.Handled = true; return; }
        if (shift && e.Key is Key.PageUp or Key.PageDown)
        {
            _scrollOffset = Math.Clamp(_scrollOffset + (e.Key == Key.PageUp ? _screen.Rows - 1 : -(_screen.Rows - 1)), 0, Math.Max(0, _screen.Total - _screen.Rows));
            InvalidateVisual(); RaiseScroll(); e.Handled = true; return;
        }

        var seq = KeyToSequence(e.Key, ctrl, shift);
        if (seq != null)
        {
            Send(seq);
            GoLive();
            e.Handled = true;
        }
    }

    private void GoLive()
    {
        if (_scrollOffset == 0) return;
        _scrollOffset = 0;
        InvalidateVisual();
        RaiseScroll();
    }

    private static string? KeyToSequence(Key key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case Key.Enter: return "\r";
            case Key.Back: return "\x7f";
            case Key.Tab: return "\t";
            case Key.Escape: return "\x1b";
            case Key.Up: return "\x1b[A";
            case Key.Down: return "\x1b[B";
            case Key.Right: return "\x1b[C";
            case Key.Left: return "\x1b[D";
            case Key.Home: return "\x1b[H";
            case Key.End: return "\x1b[F";
            case Key.Insert: return "\x1b[2~";
            case Key.Delete: return "\x1b[3~";
            case Key.PageUp: return "\x1b[5~";
            case Key.PageDown: return "\x1b[6~";
            case Key.F1: return "\x1bOP";
            case Key.F2: return "\x1bOQ";
            case Key.F3: return "\x1bOR";
            case Key.F4: return "\x1bOS";
            case Key.F5: return "\x1b[15~";
            case Key.F6: return "\x1b[17~";
            case Key.F7: return "\x1b[18~";
            case Key.F8: return "\x1b[19~";
            case Key.F9: return "\x1b[20~";
            case Key.F10: return "\x1b[21~";
            case Key.F11: return "\x1b[23~";
            case Key.F12: return "\x1b[24~";
        }
        if (ctrl && !shift)
        {
            // Ctrl+lettera -> codice di controllo (Ctrl+C = 0x03, ecc.)
            if (key is >= Key.A and <= Key.Z) return ((char)(key - Key.A + 1)).ToString();
            switch (key)
            {
                case Key.Space: return "\0";
                case Key.OemOpenBrackets: return "\x1b";
                case Key.OemBackslash or Key.Oem5: return "\x1c";
                case Key.OemCloseBrackets: return "\x1d";
            }
        }
        return null;
    }
}
