"""Generates docs/agents/commands.md, the reference of the SimuLean MCP commands, from the code.

Reads every [CliCommand("simulean_…", "description", …)] in Assets/SimuLean.Unity/Editor/Pipeline/*.cs and the
[CliArg("name", "description")] parameters of its method (type and default from the C# signature), so the reference
never drifts from what the agent's tool list says.

    python Tools/Docs/command_reference.py           # writes docs/agents/commands.md
    python Tools/Docs/command_reference.py --check   # exit 1 when the file is not current
"""
import io
import os
import re
import sys

ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), '..', '..'))
SOURCES = os.path.join(ROOT, 'Assets', 'SimuLean.Unity', 'Editor', 'Pipeline')
OUT = os.path.join(ROOT, 'docs', 'agents', 'commands.md')

# the order of the reference: by area, then as listed
AREAS = [
    ('Describing the model', ['simulean_types', 'simulean_params', 'simulean_model', 'simulean_validate']),
    ('Building', ['simulean_add_element', 'simulean_set', 'simulean_connect', 'simulean_disconnect', 'simulean_add_team',
                  'simulean_load_table', 'simulean_place_model', 'simulean_remove', 'simulean_rename']),
    ('Building in bulk', ['simulean_apply', 'simulean_repeat', 'simulean_invoke']),
    ('Running and studying', ['simulean_run_headless', 'simulean_run_start', 'simulean_run_status', 'simulean_diagnose']),
    ('Optimising', ['simulean_optimize_start', 'simulean_optimize_status']),
    ('Results and data', ['simulean_results', 'simulean_query', 'simulean_fit']),
    ('Seeing and recording', ['simulean_capture', 'simulean_record_shot', 'simulean_record_start', 'simulean_record_status']),
    ('Tests', ['simulean_test', 'simulean_test_status']),
]


def strings(expr):
    """The value of a C# string expression made of literals joined by + (regular or verbatim)."""
    out, i = [], 0
    while i < len(expr):
        c = expr[i]
        if c == '@' and i + 1 < len(expr) and expr[i + 1] == '"':
            j = i + 2
            buf = []
            while j < len(expr):
                if expr[j] == '"':
                    if j + 1 < len(expr) and expr[j + 1] == '"':
                        buf.append('"'); j += 2; continue
                    break
                buf.append(expr[j]); j += 1
            out.append(''.join(buf)); i = j + 1
        elif c == '"':
            j = i + 1
            buf = []
            while j < len(expr) and expr[j] != '"':
                if expr[j] == '\\' and j + 1 < len(expr):
                    nxt = expr[j + 1]
                    buf.append({'n': '\n', 't': '\t', '"': '"', '\\': '\\'}.get(nxt, nxt)); j += 2; continue
                buf.append(expr[j]); j += 1
            out.append(''.join(buf)); i = j + 1
        else:
            i += 1
    return ''.join(out)


def split_top(text, sep=','):
    """Splits at separators outside strings, parentheses, brackets and braces."""
    parts, depth, buf, i = [], 0, [], 0
    while i < len(text):
        c = text[i]
        if c == '@' and i + 1 < len(text) and text[i + 1] == '"':
            j = i + 2
            while j < len(text):
                if text[j] == '"' and (j + 1 >= len(text) or text[j + 1] != '"'): break
                j += 2 if text[j] == '"' else 1
            buf.append(text[i:j + 1]); i = j + 1; continue
        if c == '"':
            j = i + 1
            while j < len(text) and text[j] != '"':
                j += 2 if text[j] == '\\' else 1
            buf.append(text[i:j + 1]); i = j + 1; continue
        if c in '([{': depth += 1
        elif c in ')]}': depth -= 1
        if c == sep and depth == 0:
            parts.append(''.join(buf)); buf = []
        else:
            buf.append(c)
        i += 1
    if ''.join(buf).strip(): parts.append(''.join(buf))
    return parts


def balanced(text, start):
    """Index after the bracket that closes the one at text[start] (strings skipped)."""
    open_c = text[start]
    close_c = {'(': ')', '[': ']'}[open_c]
    depth, i = 0, start
    while i < len(text):
        c = text[i]
        if c == '@' and i + 1 < len(text) and text[i + 1] == '"':
            j = i + 2
            while j < len(text):
                if text[j] == '"' and (j + 1 >= len(text) or text[j + 1] != '"'): break
                j += 2 if text[j] == '"' else 1
            i = j + 1; continue
        if c == '"':
            j = i + 1
            while j < len(text) and text[j] != '"':
                j += 2 if text[j] == '\\' else 1
            i = j + 1; continue
        if c == open_c: depth += 1
        elif c == close_c:
            depth -= 1
            if depth == 0: return i + 1
        i += 1
    raise ValueError('unbalanced')


def commands():
    found = {}
    for name in sorted(os.listdir(SOURCES)):
        if not name.endswith('.cs'): continue
        text = io.open(os.path.join(SOURCES, name), encoding='utf-8-sig').read()
        for m in re.finditer(r'\[CliCommand\(', text):
            attr_end = balanced(text, m.start() + len('[CliCommand'))
            args = split_top(text[m.start() + len('[CliCommand('):attr_end - 1])
            cmd = strings(args[0])
            if not cmd.startswith('simulean_'): continue
            desc = strings(args[1])
            sig = re.search(r'public\s+static\s+[\w<>\.\s]+?\s+(\w+)\s*\(', text[attr_end:])
            p_start = attr_end + sig.end() - 1
            p_end = balanced(text, p_start)
            params = []
            for p in split_top(text[p_start + 1:p_end - 1]):
                p = p.strip()
                if not p: continue
                arg = re.match(r'\[CliArg\(', p)
                a_name, a_desc, required = None, '', False
                rest = p
                if arg:
                    a_end = balanced(p, len('[CliArg'))
                    inner = split_top(p[len('[CliArg('):a_end - 1])
                    a_name = strings(inner[0])
                    a_desc = strings(inner[1]) if len(inner) > 1 else ''
                    required = any(re.match(r'\s*Required\s*=\s*true', x) for x in inner[2:])
                    rest = p[a_end + 1:].strip()
                decl = re.match(r'([\w<>\.\[\]]+)\s+@?(\w+)(?:\s*=\s*(.+))?$', rest, re.S)
                if not decl: continue
                ctype, cname, default = decl.group(1), decl.group(2), decl.group(3)
                if default is not None:
                    default = default.strip()
                    if default.startswith('"'): default = '"' + strings(default) + '"'
                params.append({'name': a_name or cname, 'type': ctype.split('.')[-1], 'default': default,
                               'required': required or default is None, 'desc': a_desc})
            found[cmd] = {'desc': desc, 'params': params, 'file': name}
    return found


def cell(text):
    return ' '.join(text.replace('|', '\\|').split())


def render(found):
    lines = [
        '# SimuLean MCP commands',
        '',
        '> Generated from the code by `Tools/Docs/command_reference.py` — do not edit by hand (run it after changing a',
        '> command; `--check` fails when this file is not current). How to use them together: [workflow.md](workflow.md);',
        '> what their errors mean: [errors.md](errors.md).',
        '',
        f'{len(found)} commands. Every answer is a JSON object with the command\'s fields plus `errors` and `warnings` (what was',
        'logged while it ran). Objects are named by their GameObject name (unique) or their hierarchy path. String',
        'arguments that take a list accept the shorthand shown or JSON.',
        '',
    ]
    listed = set()
    for area, names in AREAS:
        present = [n for n in names if n in found]
        if not present: continue
        lines += [f'## {area}', '']
        for n in present:
            lines += entry(n, found[n]); listed.add(n)
    rest = sorted(set(found) - listed)
    if rest:
        lines += ['## Other', '']
        for n in rest: lines += entry(n, found[n])
    return '\n'.join(lines).rstrip() + '\n'


def entry(name, c):
    out = [f'### `{name}`', '', cell(c['desc']), '']
    if c['params']:
        out += ['| Argument | Type | Default | |', '|---|---|---|---|']
        for p in c['params']:
            default = 'required' if p['required'] else (f"`{p['default']}`" if p['default'] not in (None, '""') else '—')
            out.append(f"| `{p['name']}` | {p['type']} | {default} | {cell(p['desc'])} |")
        out.append('')
    else:
        out += ['No arguments.', '']
    return out


def main():
    text = render(commands())
    if '--check' in sys.argv:
        current = io.open(OUT, encoding='utf-8').read() if os.path.exists(OUT) else ''
        if current != text:
            print('docs/agents/commands.md is not current: run python Tools/Docs/command_reference.py')
            sys.exit(1)
        print('docs/agents/commands.md is current')
        return
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    io.open(OUT, 'w', encoding='utf-8', newline='\n').write(text)
    print(f'wrote {OUT} ({text.count("### ")} commands)')


if __name__ == '__main__':
    main()
