"""Avvolge i testi italiani dei sorgenti C# in L.T(...).

Uso: py wrap_cs.py <file.cs> [...]   (modifica i file sul posto, stampa le stringhe avvolte)
"""
import re, sys

STYLE_KEYS = set()  # riempito da main con le chiavi di Styles.xaml

# contesti (testo che precede sulla stessa riga) in cui la stringa NON va tradotta
CTX_IMMEDIATE = re.compile(
    r'(FindResource|TryFindResource|Res|Settings\.Get|Settings\.Set|GetValue|SetValue|OpenSubKey|CreateSubKey|DeleteSubKeyTree|'
    r'SetDefine|nameof|Binding|PropertyGroupDescription|GetManifestResourceStream|GetEnvironmentVariable|ExecuteScriptAsync|'
    r'Log|Contains|StartsWith|EndsWith|Equals|IndexOf|Replace|Split|TrimEnd|TrimStart|Trim|GetField|GetMethod|GetProperty|'
    r'ArgumentList\.Add|FontFamily|GetCultureInfo|Language|ParseAdd|Navigate|AddWebResourceRequestedFilter|Uri|Regex|IsMatch|'
    r'Matches|Match|ConvertFromString|PostWebMessageAsString|AddScriptToExecuteOnDocumentCreatedAsync|CallDevToolsProtocolMethodAsync)\(\s*$')
CTX_LINE = re.compile(
    r'(\bField\(|ArgumentList|DllImport|GeneratedRegex|ManagementObjectSearcher|\bWmi\(|\bS\(\w+, |\bL\(\w+, |'
    r'ProcessStartInfo\(|\bconst\b|\bcase\b|\bis\s+$|==\s*$|!=\s*$|\bis\s+(not\s+)?\(?$|Registry\.|ExecuteScriptAsync|'
    r'Path\.Combine\(|Existing\(|GetFolderPath|ChromiumCaches\(|Debug\.|Console\.|AppInfo\.Log|\.Log\(|Interlocked|UserAgent|DefaultRequestHeaders)')

ITALIAN_HINT = re.compile(r'[àèéìòùÀÈÉÌÒÙ]|\b(il|lo|la|le|gli|di|da|del|della|dei|delle|un|una|uno|per|con|non|che|in|al|alla|ai|su|sul|nella|nel|è|sono|tutti|tutto|file|cartella|immagine|pagina|errore|salva|apri)\b', re.I)


def italian(text: str) -> bool:
    """Heuristics: is this literal (text parts only) user-visible Italian text?"""
    t = text
    if not re.search(r'[A-Za-zÀ-ÿ]{2}', t):
        return False
    s = t.strip()
    if s in STYLE_KEYS:
        return False
    low = s.lower()
    if low.startswith(('http', 'ms-settings', 'pack:', 'select ', 'root\\', '--', 'mailto:', 'smsto:', 'begin:vcard', 'file:')):
        return False
    if '\\\\' in t or re.search(r'\b[A-Z]:\\\\', t):
        return False
    if re.search(r'document\.|window\.|querySelector|function\s*\(|=>|\(\)\s*;|\bvar\s|\bconst\s|return\s', t):
        return False
    if re.search(r'<\w+[ >]|\w+-\w+:\s*[^;]+;|\{\{\s*\w+:', t):  # html / css
        return False
    if re.fullmatch(r'[dMyHhmsftz/.:\-_ ,]+', s):  # formati di data
        return False
    if ' ' not in s:
        # una sola parola
        if re.fullmatch(r'[a-z0-9_.\-/:*?;|+#%=]+', s):  # identificatori, estensioni, chiavi
            return False
        if re.fullmatch(r'[A-Z0-9_.\-]+', s):  # sigle
            return False
        if re.search(r'[a-z][A-Z]', s) and not ITALIAN_HINT.search(s):  # CamelCase
            return False
        if re.search(r'[_]|\.\w', s):
            return False
        if s.startswith(('/', '.', '-', '@', '#', '{', '(')):
            return False
        return bool(re.fullmatch(r"[A-ZÀ-Ý][a-zà-ÿ'’]+[.:…!?]?|[a-zà-ÿ]+[…:]|[A-ZÀ-Ý][a-zà-ÿ]+-[A-Za-zà-ÿ]+|[A-Za-zÀ-ÿ'’]+…", s)) or bool(re.search(r'[àèéìòù]', s))
    # più parole
    if re.fullmatch(r'[\w.\-]+( [\w.\-]+)*', s) and not re.search(r'[A-ZÀ-Ý]', s[:1]) and not ITALIAN_HINT.search(s):
        return False  # es. "x y z" tecnici tutti minuscoli senza parole italiane
    return True


class Parser:
    def __init__(self, src):
        self.s = src
        self.out = []
        self.wrapped = []

    # -------------------------------------------------- lettura di una stringa
    def read_string(self, i):
        """Legge la stringa che inizia in i. Restituisce (fine, dict) oppure None."""
        s = self.s
        m = re.match(r'(\$@|@\$|\$|@)?"', s[i:])
        if not m:
            return None
        prefix = m.group(1) or ''
        if s.startswith('"""', i + len(prefix)):
            return None  # raw string: lasciate stare
        interp = '$' in prefix
        verbatim = '@' in prefix
        j = i + len(m.group(0))
        parts = []  # ("lit", text) | ("hole", expr, align, fmt)
        buf = []
        while j < len(s):
            c = s[j]
            if verbatim and c == '"':
                if s.startswith('""', j):
                    buf.append('""'); j += 2; continue
                break
            if not verbatim and c == '\\':
                buf.append(s[j:j + 2]); j += 2; continue
            if not verbatim and c == '"':
                break
            if interp and c == '{':
                if s.startswith('{{', j):
                    buf.append('{{'); j += 2; continue
                if buf:
                    parts.append(('lit', ''.join(buf))); buf = []
                j, hole = self.read_hole(j + 1, verbatim)
                parts.append(hole)
                continue
            if interp and c == '}':
                if s.startswith('}}', j):
                    buf.append('}}'); j += 2; continue
            if c == '\n' and not verbatim:
                return None
            buf.append(c); j += 1
        if buf:
            parts.append(('lit', ''.join(buf)))
        return j + 1, {'prefix': prefix, 'interp': interp, 'verbatim': verbatim, 'parts': parts, 'start': i}

    def read_hole(self, j, verbatim):
        """Legge un'espressione {expr,align:fmt}; restituisce (indice dopo '}', ('hole', expr, align, fmt))."""
        s = self.s
        depth = 0
        start = j
        colon = comma = None
        while j < len(s):
            c = s[j]
            if c in '([{':
                depth += 1
            elif c in ')]':
                depth -= 1
            elif c == '}':
                if depth == 0:
                    break
                depth -= 1
            elif c == '"' or (c in '$@' and j + 1 < len(s) and s[j + 1] in '"$@'):
                r = self.read_string(j)
                if r:
                    j = r[0]; continue
            elif c == "'":
                k = j + 1
                while k < len(s) and s[k] != "'":
                    k += 2 if s[k] == '\\' else 1
                j = k + 1; continue
            elif c == ':' and depth == 0 and colon is None:
                colon = j
            elif c == ',' and depth == 0 and colon is None and comma is None:
                comma = j
            elif c == '?' and depth == 0:
                pass
            j += 1
        end_expr = comma if comma is not None else (colon if colon is not None else j)
        expr = s[start:end_expr]
        align = s[comma + 1:(colon if colon is not None else j)].strip() if comma is not None else None
        fmt = s[colon + 1:j] if colon is not None else None
        return j + 1, ('hole', expr, align, fmt)

    # -------------------------------------------------- trasformazione
    def literal_text(self, info):
        return ''.join(p[1] for p in info['parts'] if p[0] == 'lit')

    def render(self, info):
        """Ricostruisce il sorgente della stringa trasformando anche le espressioni nei buchi."""
        out = [info['prefix'], '"']
        for p in info['parts']:
            if p[0] == 'lit':
                out.append(p[1])
            else:
                _, expr, align, fmt = p
                inner = Parser(expr)
                inner.run()
                self.wrapped.extend(inner.wrapped)
                out.append('{' + ''.join(inner.out) + (',' + align if align is not None else '') + (':' + fmt if fmt is not None else '') + '}')
        out.append('"')
        return ''.join(out)

    def merge(self, infos):
        """Unisce "a" + "b" + $"c" in un'unica stringa (interpolata se serve)."""
        if len(infos) == 1:
            return infos[0]
        interp = any(i['interp'] for i in infos)
        verbatim = any(i['verbatim'] for i in infos)
        parts = []
        for info in infos:
            for p in info['parts']:
                if p[0] == 'lit':
                    t = p[1]
                    if verbatim and not info['verbatim']:
                        return None  # combinazione scomoda: non unisco
                    if not verbatim and info['verbatim']:
                        return None
                    if interp and not info['interp']:
                        t = t.replace('{', '{{').replace('}', '}}')
                    parts.append(('lit', t))
                else:
                    parts.append(p)
        prefix = ('$' if interp else '') + ('@' if verbatim else '')
        return {'prefix': prefix, 'interp': interp, 'verbatim': verbatim, 'parts': parts, 'start': infos[0]['start']}

    def line_before(self, i):
        k = self.s.rfind('\n', 0, i)
        return self.s[k + 1:i]

    def run(self):
        s = self.s
        i = 0
        n = len(s)
        while i < n:
            c = s[i]
            if s.startswith('//', i):
                k = s.find('\n', i)
                k = n if k < 0 else k
                self.out.append(s[i:k]); i = k; continue
            if s.startswith('/*', i):
                k = s.find('*/', i + 2)
                k = n if k < 0 else k + 2
                self.out.append(s[i:k]); i = k; continue
            if c == "'":
                k = i + 1
                while k < n and s[k] != "'":
                    k += 2 if s[k] == '\\' else 1
                self.out.append(s[i:k + 1]); i = k + 1; continue
            if c in '"$@':
                prev = s[i - 1] if i > 0 else ' '
                if c != '"' and (prev.isalnum() or prev == '_'):
                    self.out.append(c); i += 1; continue
                r = self.read_string(i)
                if r is None:
                    if s.startswith('"""', i) or s.startswith('$"""', i):
                        k = s.find('"""', s.find('"""', i) + 3)
                        self.out.append(s[i:k + 3]); i = k + 3; continue
                    self.out.append(c); i += 1; continue
                end, info = r
                chain = [info]
                # catena di concatenazioni tra stringhe
                while True:
                    m = re.match(r'\s*\+\s*', s[end:])
                    if not m:
                        break
                    r2 = self.read_string(end + m.end())
                    if not r2:
                        break
                    after = s[r2[0]:r2[0] + 1]
                    if after in '.[':
                        break
                    chain.append(r2[1]); end = r2[0]
                merged = self.merge(chain)
                if merged is None:
                    merged, end = info, r[0]
                    chain = [info]
                before = self.line_before(i)
                after = s[end:end + 40]
                text = self.literal_text(merged)
                skip = (CTX_IMMEDIATE.search(before) or CTX_LINE.search(before) or re.match(r'\s*(=>|:\s*$)', after)
                        or re.match(r'^\s*\[\w+\(', before) or (before.rstrip().endswith('[') and ' ' not in text)
                        or re.search(r'\bL\.T\(\s*$|\bL\.F\(\s*$|\bT\(\s*$', before)
                        or (merged['verbatim'] and '\\' in text))
                if not skip and italian(text):
                    self.out.append('L.T(' + self.render(merged) + ')')
                    self.wrapped.append(text)
                else:
                    if len(chain) == 1:
                        self.out.append(self.render(info))
                    else:
                        self.out.append(s[i:end])
                i = end
                continue
            self.out.append(c)
            i += 1


def main():
    import pathlib
    styles = pathlib.Path(sys.argv[1]).read_text(encoding='utf-8')
    STYLE_KEYS.update(re.findall(r'x:Key="([^"]+)"', styles))
    total = 0
    for f in sys.argv[2:]:
        src = pathlib.Path(f).read_text(encoding='utf-8-sig')
        p = Parser(src)
        p.run()
        out = ''.join(p.out)
        if out != src:
            pathlib.Path(f).write_text(out, encoding='utf-8')
        total += len(p.wrapped)
        print(f'### {f}: {len(p.wrapped)}')
        for w in p.wrapped:
            print('   ', w)
    print('TOTAL', total)


if __name__ == '__main__':
    main()
