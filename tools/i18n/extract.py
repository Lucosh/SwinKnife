"""Estrae le chiavi di traduzione (L.T(...) nel C#, {l:T '...'} nello XAML) come stringhe a runtime.

Uso: py extract.py <cartella src/SwinKnife> <out keys.json>
Il risultato è una lista ordinata [ [file, chiave], ... ] senza doppioni.
"""
import html, json, pathlib, re, sys
from wrap_cs import Parser


def unescape_regular(t: str) -> str:
    out = []
    i = 0
    while i < len(t):
        c = t[i]
        if c == '\\' and i + 1 < len(t):
            n = t[i + 1]
            simple = {'n': '\n', 't': '\t', 'r': '\r', '"': '"', "'": "'", '\\': '\\', '0': '\0'}
            if n in simple:
                out.append(simple[n]); i += 2; continue
            if n == 'u':
                out.append(chr(int(t[i + 2:i + 6], 16))); i += 6; continue
            if n == 'x':
                m = re.match(r'[0-9a-fA-F]{1,4}', t[i + 2:])
                out.append(chr(int(m.group(0), 16))); i += 2 + len(m.group(0)); continue
        out.append(c); i += 1
    return ''.join(out)


def key_of(info) -> str:
    parts = []
    n = 0
    for p in info['parts']:
        if p[0] == 'lit':
            t = p[1]
            t = t.replace('""', '"') if info['verbatim'] else unescape_regular(t)
            parts.append(t)  # nelle interpolate {{ e }} restano raddoppiate: è il formato della chiave
        else:
            _, expr, align, fmt = p
            h = '{' + str(n)
            if align is not None:
                h += ',' + align.strip()
            if fmt is not None:
                h += ':' + fmt
            parts.append(h + '}')
            n += 1
    key = ''.join(parts)
    if not info['interp']:
        pass
    return key


def cs_keys(src: str):
    keys = []
    p = Parser(src)
    for m in re.finditer(r'\bL\.T\(', src):
        r = p.read_string(m.end())
        if r is None:
            continue
        end, info = r
        # concatenazioni "a" + "b" dentro L.T(...)
        chain = [info]
        while True:
            mm = re.match(r'\s*\+\s*', src[end:])
            if not mm:
                break
            r2 = p.read_string(end + mm.end())
            if not r2:
                break
            chain.append(r2[1]); end = r2[0]
        merged = p.merge(chain) if len(chain) > 1 else info
        if merged is None:
            continue
        k = key_of(merged)
        if not merged['interp']:
            # stringa normale: a runtime le graffe non sono raddoppiate
            pass
        keys.append(k)
    return keys


def xaml_keys(src: str):
    keys = []
    for m in re.finditer(r"\{l:T '((?:\\.|[^'\\])*)'\}", src):
        v = re.sub(r'\\(.)', r'\1', m.group(1))
        keys.append(html.unescape(v))
    return keys


def main():
    root = pathlib.Path(sys.argv[1])
    seen = set()
    result = []
    files = sorted(root.rglob('*.cs')) + sorted(root.rglob('*.xaml'))
    for f in files:
        if 'obj' in f.parts or 'bin' in f.parts:
            continue
        src = f.read_text(encoding='utf-8-sig')
        ks = cs_keys(src) if f.suffix == '.cs' else xaml_keys(src)
        rel = f.relative_to(root).as_posix()
        for k in ks:
            if k not in seen:
                seen.add(k)
                result.append([rel, k])
    pathlib.Path(sys.argv[2]).write_text(json.dumps(result, ensure_ascii=False, indent=0), encoding='utf-8')
    print(len(result), 'chiavi')


if __name__ == '__main__':
    main()
