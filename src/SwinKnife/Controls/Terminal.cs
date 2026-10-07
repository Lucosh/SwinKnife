using System.Text;

namespace SwinKnife.Controls;

/// <summary>Una cella della griglia del terminale: carattere, colori e stile.</summary>
public struct Cell
{
    public char Ch;
    public int Fg;   // -1 = colore predefinito; 0..255 = tavolozza; altrimenti 0x1_000000 | RGB
    public int Bg;
    public byte Attr; // 1 grassetto, 2 sottolineato, 4 invertito, 8 tenue, 16 corsivo

    public static readonly Cell Empty = new() { Ch = ' ', Fg = -1, Bg = -1, Attr = 0 };
}

/// <summary>
/// Emulatore di terminale (sottoinsieme di xterm/VT100 con colori a 256 e 24 bit): interpreta i byte
/// stampati dal programma e tiene aggiornata la griglia di caratteri, il cursore e lo scorrimento all'indietro.
/// Tutto gira sul thread dell'interfaccia: nessun lock necessario.
/// </summary>
public sealed class VtScreen
{
    public const int Bold = 1, Underline = 2, Inverse = 4, Dim = 8, Italic = 16;

    private Cell[][] _screen = [];
    private Cell[][]? _savedMain;              // schermo principale, mentre è attivo lo schermo alternativo
    private readonly List<Cell[]> _scrollback = []; // righe uscite dall'alto (solo schermo principale)
    private const int MaxScrollback = 5000;

    public int Rows { get; private set; }
    public int Cols { get; private set; }
    public int CursorX { get; private set; }
    public int CursorY { get; private set; }
    public bool CursorVisible { get; private set; } = true;
    public bool AltScreen { get; private set; }
    public bool BracketedPaste { get; private set; }

    public IReadOnlyList<Cell[]> Scrollback => _scrollback;
    public int Total => (AltScreen ? 0 : _scrollback.Count) + Rows;

    public Action<string>? Respond;

    private int _top, _bottom;                 // regione di scorrimento (incluse)
    private int _fg = -1, _bg = -1;
    private byte _attr;
    private bool _wrapPending;
    private bool _autoWrap = true;
    private readonly HashSet<int> _tabs = [];
    private (int x, int y, int fg, int bg, byte attr) _saved;

    public VtScreen(int cols, int rows) => Resize(cols, rows);

    public Cell[] LineAt(int index)
    {
        if (!AltScreen && index < _scrollback.Count) return _scrollback[index];
        var row = index - (AltScreen ? 0 : _scrollback.Count);
        return row >= 0 && row < Rows ? _screen[row] : NewLine();
    }

    private Cell[] NewLine()
    {
        var line = new Cell[Cols];
        for (var i = 0; i < Cols; i++) line[i] = Cell.Empty;
        return line;
    }

    public void Resize(int cols, int rows)
    {
        cols = Math.Max(1, cols);
        rows = Math.Max(1, rows);
        if (cols == Cols && rows == Rows) return;
        var old = _screen;
        Cols = cols;
        Rows = rows;
        _screen = new Cell[rows][];
        for (var y = 0; y < rows; y++)
        {
            _screen[y] = new Cell[cols];
            for (var x = 0; x < cols; x++)
                _screen[y][x] = y < old.Length && x < old[y].Length ? old[y][x] : Cell.Empty;
        }
        _top = 0;
        _bottom = rows - 1;
        CursorX = Math.Min(CursorX, cols - 1);
        CursorY = Math.Min(CursorY, rows - 1);
        _tabs.Clear();
        for (var i = 0; i < cols; i += 8) _tabs.Add(i);
        _wrapPending = false;
    }

    // ------------------------------------------------------------------ parser
    private enum S { Ground, Esc, Csi, Osc, OscEsc, Charset }
    private S _state = S.Ground;
    private readonly List<int> _params = [];
    private int _curParam = -1;
    private bool _priv;
    private readonly StringBuilder _osc = new();

    public void Feed(string text)
    {
        foreach (var c in text) Step(c);
    }

    private void Step(char c)
    {
        switch (_state)
        {
            case S.Ground: Ground(c); break;
            case S.Esc: Escape(c); break;
            case S.Csi: Csi(c); break;
            case S.Osc: if (c == '\x07') EndOsc(); else if (c == '\x1b') _state = S.OscEsc; else _osc.Append(c); break;
            case S.OscEsc: EndOsc(); if (c != '\\') Step(c); break;
            case S.Charset: _state = S.Ground; break; // ignora la tabella caratteri (es. ESC ( B)
        }
    }

    private void Ground(char c)
    {
        switch (c)
        {
            case '\x1b': _state = S.Esc; break;
            case '\r': CursorX = 0; _wrapPending = false; break;
            case '\n': case '\x0b': case '\x0c': LineFeed(); break;
            case '\b': if (CursorX > 0) CursorX--; _wrapPending = false; break;
            case '\t': Tab(); break;
            case '\x07': break; // campanello
            default: if (c >= ' ') Print(c); break;
        }
    }

    private void Escape(char c)
    {
        switch (c)
        {
            case '[': _params.Clear(); _curParam = -1; _priv = false; _state = S.Csi; break;
            case ']': _osc.Clear(); _state = S.Osc; break;
            case '(': case ')': case '*': case '+': _state = S.Charset; break;
            case '7': _saved = (CursorX, CursorY, _fg, _bg, _attr); _state = S.Ground; break;
            case '8': (CursorX, CursorY, _fg, _bg, _attr) = _saved; CursorX = Math.Min(CursorX, Cols - 1); CursorY = Math.Min(CursorY, Rows - 1); _state = S.Ground; break;
            case 'M': ReverseIndex(); _state = S.Ground; break;
            case 'D': LineFeed(); _state = S.Ground; break;
            case 'E': CursorX = 0; LineFeed(); _state = S.Ground; break;
            case 'c': FullReset(); _state = S.Ground; break;
            default: _state = S.Ground; break;
        }
    }

    private void Csi(char c)
    {
        if (c == '?' || c == '>' || c == '<' || c == '=') { _priv = true; return; }
        if (c >= '0' && c <= '9') { _curParam = (_curParam < 0 ? 0 : _curParam) * 10 + (c - '0'); return; }
        if (c == ';') { _params.Add(_curParam); _curParam = -1; return; }
        if (c == ' ' || c == '!' || c == '"' || c == '\'' || c == '$') return; // byte intermedi: ignorati
        _params.Add(_curParam);
        Dispatch(c);
        _state = S.Ground;
    }

    private int P(int i, int def) => i < _params.Count && _params[i] >= 0 ? _params[i] : def;

    private void Dispatch(char c)
    {
        switch (c)
        {
            case 'A': CursorY = Math.Max(_top, CursorY - Math.Max(1, P(0, 1))); _wrapPending = false; break;
            case 'B': CursorY = Math.Min(_bottom, CursorY + Math.Max(1, P(0, 1))); _wrapPending = false; break;
            case 'C': CursorX = Math.Min(Cols - 1, CursorX + Math.Max(1, P(0, 1))); _wrapPending = false; break;
            case 'D': CursorX = Math.Max(0, CursorX - Math.Max(1, P(0, 1))); _wrapPending = false; break;
            case 'E': CursorX = 0; CursorY = Math.Min(_bottom, CursorY + Math.Max(1, P(0, 1))); break;
            case 'F': CursorX = 0; CursorY = Math.Max(_top, CursorY - Math.Max(1, P(0, 1))); break;
            case 'G': case '`': CursorX = Clamp(P(0, 1) - 1, Cols); _wrapPending = false; break;
            case 'd': CursorY = Clamp(P(0, 1) - 1, Rows); _wrapPending = false; break;
            case 'H': case 'f': CursorY = Clamp(P(0, 1) - 1, Rows); CursorX = Clamp(P(1, 1) - 1, Cols); _wrapPending = false; break;
            case 'J': EraseDisplay(P(0, 0)); break;
            case 'K': EraseLine(P(0, 0)); break;
            case 'L': InsertLines(Math.Max(1, P(0, 1))); break;
            case 'M': DeleteLines(Math.Max(1, P(0, 1))); break;
            case 'P': DeleteChars(Math.Max(1, P(0, 1))); break;
            case '@': InsertChars(Math.Max(1, P(0, 1))); break;
            case 'X': EraseChars(Math.Max(1, P(0, 1))); break;
            case 'S': ScrollUp(Math.Max(1, P(0, 1))); break;
            case 'T': ScrollDown(Math.Max(1, P(0, 1))); break;
            case 'm': Sgr(); break;
            case 'r': _top = Clamp(P(0, 1) - 1, Rows); _bottom = Clamp(P(1, Rows) - 1, Rows); if (_top >= _bottom) { _top = 0; _bottom = Rows - 1; } CursorX = 0; CursorY = _top; break;
            case 'h': Mode(true); break;
            case 'l': Mode(false); break;
            case 's': _saved = (CursorX, CursorY, _fg, _bg, _attr); break;
            case 'u': (CursorX, CursorY, _fg, _bg, _attr) = _saved; break;
            case 'n': if (P(0, 0) == 6) Respond?.Invoke($"\x1b[{CursorY + 1};{CursorX + 1}R"); else if (P(0, 0) == 5) Respond?.Invoke("\x1b[0n"); break;
            case 'c': Respond?.Invoke("\x1b[?1;2c"); break;
        }
    }

    private static int Clamp(int v, int size) => Math.Max(0, Math.Min(size - 1, v));

    private void Mode(bool on)
    {
        var n = P(0, 0);
        if (!_priv) return;
        switch (n)
        {
            case 7: _autoWrap = on; break;
            case 25: CursorVisible = on; break;
            case 2004: BracketedPaste = on; break;
            case 47: case 1047: case 1049:
                if (on && !AltScreen) EnterAlt(n == 1049);
                else if (!on && AltScreen) LeaveAlt(n == 1049);
                break;
            case 1048: if (on) _saved = (CursorX, CursorY, _fg, _bg, _attr); else (CursorX, CursorY, _fg, _bg, _attr) = _saved; break;
        }
    }

    private void EnterAlt(bool saveCursor)
    {
        if (saveCursor) _saved = (CursorX, CursorY, _fg, _bg, _attr);
        _savedMain = _screen;
        _screen = new Cell[Rows][];
        for (var y = 0; y < Rows; y++) _screen[y] = NewLine();
        AltScreen = true;
        CursorX = CursorY = 0;
        _top = 0; _bottom = Rows - 1;
    }

    private void LeaveAlt(bool restoreCursor)
    {
        if (_savedMain != null && _savedMain.Length == Rows && _savedMain[0].Length == Cols) _screen = _savedMain;
        else { _screen = new Cell[Rows][]; for (var y = 0; y < Rows; y++) _screen[y] = NewLine(); }
        _savedMain = null;
        AltScreen = false;
        _top = 0; _bottom = Rows - 1;
        if (restoreCursor) (CursorX, CursorY, _fg, _bg, _attr) = _saved;
    }

    private void Sgr()
    {
        if (_params.Count == 0 || (_params.Count == 1 && _params[0] < 0)) { _fg = _bg = -1; _attr = 0; return; }
        for (var i = 0; i < _params.Count; i++)
        {
            var n = _params[i] < 0 ? 0 : _params[i];
            switch (n)
            {
                case 0: _fg = _bg = -1; _attr = 0; break;
                case 1: _attr |= Bold; break;
                case 2: _attr |= Dim; break;
                case 3: _attr |= Italic; break;
                case 4: _attr |= Underline; break;
                case 7: _attr |= Inverse; break;
                case 22: _attr &= unchecked((byte)~(Bold | Dim)); break;
                case 23: _attr &= unchecked((byte)~Italic); break;
                case 24: _attr &= unchecked((byte)~Underline); break;
                case 27: _attr &= unchecked((byte)~Inverse); break;
                case >= 30 and <= 37: _fg = n - 30; break;
                case 38: i = ExtColor(i, ref _fg); break;
                case 39: _fg = -1; break;
                case >= 40 and <= 47: _bg = n - 40; break;
                case 48: i = ExtColor(i, ref _bg); break;
                case 49: _bg = -1; break;
                case >= 90 and <= 97: _fg = n - 90 + 8; break;
                case >= 100 and <= 107: _bg = n - 100 + 8; break;
            }
        }
    }

    private int ExtColor(int i, ref int slot)
    {
        var mode = P(i + 1, 0);
        if (mode == 5) { slot = Math.Clamp(P(i + 2, 0), 0, 255); return i + 2; }
        if (mode == 2) { slot = 0x1_000000 | (Math.Clamp(P(i + 2, 0), 0, 255) << 16) | (Math.Clamp(P(i + 3, 0), 0, 255) << 8) | Math.Clamp(P(i + 4, 0), 0, 255); return i + 4; }
        return i;
    }

    // ------------------------------------------------------------------ operazioni sulla griglia
    private void Print(char c)
    {
        if (_wrapPending && _autoWrap) { CursorX = 0; LineFeed(); _wrapPending = false; }
        _screen[CursorY][CursorX] = new Cell { Ch = c, Fg = _fg, Bg = _bg, Attr = _attr };
        if (CursorX == Cols - 1) _wrapPending = true;
        else CursorX++;
    }

    private void Tab()
    {
        _wrapPending = false;
        for (var x = CursorX + 1; x < Cols; x++)
            if (_tabs.Contains(x)) { CursorX = x; return; }
        CursorX = Cols - 1;
    }

    private void LineFeed()
    {
        _wrapPending = false;
        if (CursorY == _bottom) ScrollUp(1);
        else if (CursorY < Rows - 1) CursorY++;
    }

    private void ReverseIndex()
    {
        if (CursorY == _top) ScrollDown(1);
        else if (CursorY > 0) CursorY--;
    }

    private void ScrollUp(int n)
    {
        for (var k = 0; k < n; k++)
        {
            var leaving = _screen[_top];
            if (_top == 0 && _bottom == Rows - 1 && !AltScreen)
            {
                _scrollback.Add(leaving);
                if (_scrollback.Count > MaxScrollback) _scrollback.RemoveAt(0);
            }
            for (var y = _top; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewLine();
        }
    }

    private void ScrollDown(int n)
    {
        for (var k = 0; k < n; k++)
        {
            for (var y = _bottom; y > _top; y--) _screen[y] = _screen[y - 1];
            _screen[_top] = NewLine();
        }
    }

    private void InsertLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        for (var k = 0; k < n; k++)
        {
            for (var y = _bottom; y > CursorY; y--) _screen[y] = _screen[y - 1];
            _screen[CursorY] = NewLine();
        }
    }

    private void DeleteLines(int n)
    {
        if (CursorY < _top || CursorY > _bottom) return;
        for (var k = 0; k < n; k++)
        {
            for (var y = CursorY; y < _bottom; y++) _screen[y] = _screen[y + 1];
            _screen[_bottom] = NewLine();
        }
    }

    private void InsertChars(int n)
    {
        var line = _screen[CursorY];
        for (var x = Cols - 1; x >= CursorX + n; x--) line[x] = line[x - n];
        for (var x = CursorX; x < CursorX + n && x < Cols; x++) line[x] = Blank();
    }

    private void DeleteChars(int n)
    {
        var line = _screen[CursorY];
        for (var x = CursorX; x < Cols; x++) line[x] = x + n < Cols ? line[x + n] : Blank();
    }

    private void EraseChars(int n)
    {
        var line = _screen[CursorY];
        for (var x = CursorX; x < CursorX + n && x < Cols; x++) line[x] = Blank();
    }

    private Cell Blank() => new() { Ch = ' ', Fg = -1, Bg = _bg, Attr = 0 };

    private void EraseLine(int mode)
    {
        var line = _screen[CursorY];
        var from = mode == 0 ? CursorX : 0;
        var to = mode == 1 ? CursorX + 1 : Cols;
        for (var x = from; x < to && x < Cols; x++) line[x] = Blank();
    }

    private void EraseDisplay(int mode)
    {
        if (mode == 0) { EraseLine(0); for (var y = CursorY + 1; y < Rows; y++) ClearRow(y); }
        else if (mode == 1) { EraseLine(1); for (var y = 0; y < CursorY; y++) ClearRow(y); }
        else { for (var y = 0; y < Rows; y++) ClearRow(y); if (mode == 3 && !AltScreen) _scrollback.Clear(); }
    }

    private void ClearRow(int y)
    {
        var line = _screen[y];
        for (var x = 0; x < Cols; x++) line[x] = Blank();
    }

    private void FullReset()
    {
        _fg = _bg = -1; _attr = 0; _top = 0; _bottom = Rows - 1;
        CursorX = CursorY = 0; CursorVisible = true; _autoWrap = true; _wrapPending = false;
        if (AltScreen) LeaveAlt(false);
        for (var y = 0; y < Rows; y++) ClearRow(y);
    }

    private void EndOsc()
    {
        _state = S.Ground;
        // OSC 0/2 = titolo finestra: catturato ma non usato qui
        var s = _osc.ToString();
        if ((s.StartsWith("0;") || s.StartsWith("2;")) && Title != null) Title(s[2..]);
    }

    public Action<string>? Title;

    /// <summary>Testo di un intervallo di celle (per copiare la selezione), togliendo gli spazi finali di ogni riga.</summary>
    public string TextRange(int startLine, int startCol, int endLine, int endCol)
    {
        if (startLine > endLine || (startLine == endLine && startCol > endCol))
            (startLine, startCol, endLine, endCol) = (endLine, endCol, startLine, startCol);
        var sb = new StringBuilder();
        for (var li = startLine; li <= endLine && li < Total; li++)
        {
            var line = LineAt(li);
            var from = li == startLine ? startCol : 0;
            var to = li == endLine ? endCol : line.Length - 1;
            var row = new StringBuilder();
            for (var x = from; x <= to && x < line.Length; x++) row.Append(line[x].Ch == '\0' ? ' ' : line[x].Ch);
            sb.Append(row.ToString().TrimEnd());
            if (li != endLine) sb.Append('\n');
        }
        return sb.ToString();
    }
}
