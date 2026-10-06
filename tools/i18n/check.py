"""Controlla le traduzioni di SwinKnife.

    py tools/i18n/check.py            elenca, per ogni lingua, i testi da tradurre e quelli non più usati
    py tools/i18n/check.py --update   aggiunge i testi mancanti (vuoti) in fondo a ogni Lang/xx.json

I testi nel codice sono in italiano e fanno da chiave (L.T("...") nel C#, {l:T '...'} nello XAML).
Un valore vuoto in Lang/xx.json significa "non ancora tradotto": l'app mostra il testo italiano.
"""
import json, pathlib, re, sys

HERE = pathlib.Path(__file__).resolve().parent
SRC = HERE.parent.parent / 'src' / 'SwinKnife'
LANG = SRC / 'Lang'
sys.path.insert(0, str(HERE))
from extract import cs_keys, xaml_keys  # noqa: E402

# testi usati in modo dinamico (L.T(variabile)), da tradurre comunque
EXTRA = ["Testo o indirizzo web", "https://www.esempio.it", "Nome della rete (SSID)", "Password della rete", "Nome", "Cognome",
         "Telefono", "+39 …", "Email", "Azienda", "Sito web", "Indirizzo", "Destinatario", "nome@esempio.it", "Oggetto",
         "Messaggio", "Numero di telefono"]

PH = re.compile(r'(?<!\{)\{(\d+)(,[^}:]*)?(:[^}]*)?\}(?!\})')


def source_keys():
    keys = []
    for f in sorted(SRC.rglob('*.cs')) + sorted(SRC.rglob('*.xaml')):
        if 'obj' in f.parts or 'bin' in f.parts:
            continue
        text = f.read_text(encoding='utf-8-sig')
        keys += cs_keys(text) if f.suffix == '.cs' else xaml_keys(text)
    keys += EXTRA
    seen, out = set(), []
    for k in keys:
        if k not in seen:
            seen.add(k)
            out.append(k)
    return out


def main():
    update = '--update' in sys.argv
    keys = source_keys()
    used = set(keys)
    problems = 0
    for path in sorted(LANG.glob('*.json')):
        data = json.loads(path.read_text(encoding='utf-8'))
        texts = {k: v for k, v in data.items() if not k.startswith('//')}
        missing = [k for k in keys if k not in texts]
        empty = [k for k, v in texts.items() if v == '' and k in used]
        unused = [k for k in texts if k not in used]
        bad = [k for k, v in texts.items() if v and sorted(PH.findall(k)) != sorted(PH.findall(v))]
        print(f'{path.stem}: {len(texts) - len(empty)} tradotti, {len(missing)} mancanti, {len(empty)} vuoti, '
              f'{len(unused)} non più usati, {len(bad)} con segnaposto {{0}} sbagliati')
        for k in bad:
            print(f'   segnaposto diversi: {k!r} -> {texts[k]!r}')
        problems += len(bad)
        if update and missing:
            data['// da tradurre'] = ''
            for k in missing:
                data[k] = ''
            path.write_text(json.dumps(data, ensure_ascii=False, indent=2) + '\n', encoding='utf-8')
            print(f'   aggiunti {len(missing)} testi da tradurre in {path.name}')
    sys.exit(1 if problems else 0)


if __name__ == '__main__':
    main()
