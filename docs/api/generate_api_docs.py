#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
SimuLean core API-reference generator.

Parses the C# sources of the simulation core (``Assets/SimuLean.Net/``) and emits a
single self-contained HTML page (``docs/api/index.html``) documenting every public /
protected type and member, using the XML documentation comments found in the code.

Usage (from the repository root):

    python docs/api/generate_api_docs.py

No third-party dependencies; standard library only.
"""

from __future__ import annotations

import html
import io
import os
import re
import sys
from collections import OrderedDict
from datetime import date

# ---------------------------------------------------------------------------
# Configuration
# ---------------------------------------------------------------------------

REPO_ROOT = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", ".."))
SOURCE_ROOT = os.path.join(REPO_ROOT, "Assets", "SimuLean.Net")
OUTPUT_FILE = os.path.join(REPO_ROOT, "docs", "api", "index.html")

# Folder -> (display group, one-line description). Order defines sidebar order.
GROUPS = OrderedDict([
    ("SimClock",      ("Simulation kernel",   "Clock, event heap, per-model context and cancellation handles — the next-event time-advance engine.")),
    ("SimElements",   ("Model elements",      "The Element graph: sources, servers, queues, sinks, resources, items and the visual-adapter contract.")),
    ("SimLink",       ("Links & routing",     "The connection layer between elements. Every statistic is a side effect of an item crossing a link.")),
    ("SimValues",     ("Stochastic values",   "Random engines, distribution catalog and process-style delay strategies.")),
    ("Statistics",    ("Statistics",          "Per-element collectors and the three statistic-variable families (level, observation, time-weighted).")),
    ("Transport",     ("Transport subsystem", "Network, vehicles and transfer points — material transport decoupled from material flow.")),
    ("Storage",       ("Storage subsystem",   "Parametric lanes, put-away / relocation policies and the storage system element.")),
    ("Collections",   ("Collections",         "Model-level data structures: pull/push lists with back-orders and the queryable stored-entity list.")),
    ("Serialization", ("Serialization",       "Config DTOs, the per-type element-handler registry and the headless model factory.")),
    ("Headless",      ("Headless execution",  "No-op visual adapters that let the same element classes run without Unity.")),
    ("Data",          ("Project data",        "SQLite-backed project tables and the pick-list sources that read them.")),
])

GROUP_FALLBACK = ("Other", "Types that do not belong to one of the named subsystems.")

# Directories to skip entirely.
SKIP_DIR_PARTS = {"Examples"}

# ---------------------------------------------------------------------------
# Lightweight C# parser
# ---------------------------------------------------------------------------

ACCESS = r"(?:public|internal|protected internal|private protected|protected|private)"
TYPE_KINDS = r"(?:class|interface|struct|enum|record)"

RE_NAMESPACE = re.compile(r"^\s*namespace\s+([\w.]+)")
RE_TYPE = re.compile(
    r"^\s*(?P<mods>(?:(?:public|internal|protected|private|static|abstract|sealed|partial|readonly|unsafe|new)\s+)*)"
    r"(?P<kind>" + TYPE_KINDS + r")\s+"
    r"(?P<name>[A-Za-z_]\w*)\s*"
    r"(?P<generic><[^>{]*>)?\s*"
    r"(?P<bases>:\s*[^{]+?)?\s*(?:where[^{]*)?(?:\{|$)"
)
RE_ATTRIBUTE = re.compile(r"^\s*\[[^\]]*\]\s*$")

MOD_WORDS = ("public", "internal", "protected", "private", "static", "abstract",
             "sealed", "partial", "readonly", "unsafe", "new", "virtual",
             "override", "extern", "async", "const", "volatile", "event",
             "implicit", "explicit", "ref")


def strip_literals(line: str) -> str:
    """Blank out string/char literals and trailing line comments (for brace counting)."""
    out = []
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if c == '"':
            if line.startswith('@"', max(i - 1, 0)):
                pass
            i += 1
            while i < n:
                if line[i] == "\\":
                    i += 2
                    continue
                if line[i] == '"':
                    i += 1
                    break
                i += 1
            out.append('""')
            continue
        if c == "'":
            i += 1
            while i < n:
                if line[i] == "\\":
                    i += 2
                    continue
                if line[i] == "'":
                    i += 1
                    break
                i += 1
            out.append("''")
            continue
        if c == "/" and i + 1 < n and line[i + 1] == "/":
            break
        out.append(c)
        i += 1
    return "".join(out)


def trailing_comment(line: str) -> str:
    """Return the `// …` comment at the end of a declaration line, if any."""
    i, n = 0, len(line)
    while i < n:
        c = line[i]
        if c == '"':
            i += 1
            while i < n and line[i] != '"':
                i += 2 if line[i] == "\\" else 1
            i += 1
            continue
        if c == "'":
            i += 1
            while i < n and line[i] != "'":
                i += 2 if line[i] == "\\" else 1
            i += 1
            continue
        if c == "/" and i + 1 < n and line[i + 1] == "/":
            return line[i + 2:].strip()
        i += 1
    return ""


SEPARATOR_RE = re.compile(r"^[\s\-=─━#*_/]+$")


def is_separator_comment(text: str) -> bool:
    body = text.strip()
    if not body:
        return True
    if SEPARATOR_RE.match(body):
        return True
    # "── Section title ──────" decorations
    return bool(re.match(r"^[─━=\-]{2,}.*[─━=\-]{2,}$", body))


def clean_xml_text(text: str) -> str:
    """Turn an XML doc fragment into readable plain text."""
    text = re.sub(r'<see\s+cref\s*=\s*"[A-Za-z]:?([^"]+)"\s*/?>', r"\1", text)
    text = re.sub(r'<see\s+langword\s*=\s*"([^"]+)"\s*/?>', r"\1", text)
    text = re.sub(r'<paramref\s+name\s*=\s*"([^"]+)"\s*/?>', r"\1", text)
    text = re.sub(r'<typeparamref\s+name\s*=\s*"([^"]+)"\s*/?>', r"\1", text)
    text = re.sub(r"</?(c|code|para|b|i|list|item|description|term)[^>]*>", " ", text)
    text = re.sub(r"<[^>]+>", " ", text)
    text = text.replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")
    text = re.sub(r"[ \t]+", " ", text)
    return text.strip()


def parse_doc(lines: list) -> dict:
    """Parse a block of `///` comment lines into {summary, params, returns, remarks}."""
    raw = "\n".join(re.sub(r"^\s*///\s?", "", ln) for ln in lines)
    doc = {"summary": "", "params": [], "returns": "", "remarks": ""}

    m = re.search(r"<summary>(.*?)</summary>", raw, re.S)
    if m:
        doc["summary"] = clean_xml_text(m.group(1))
    else:
        # Some members use a bare `/// text` form.
        bare = clean_xml_text(raw)
        if bare and "<" not in raw:
            doc["summary"] = bare

    for pm in re.finditer(r'<param\s+name\s*=\s*"([^"]+)"\s*>(.*?)</param>', raw, re.S):
        body = clean_xml_text(pm.group(2))
        if body:
            doc["params"].append((pm.group(1), body))

    m = re.search(r"<returns>(.*?)</returns>", raw, re.S)
    if m:
        doc["returns"] = clean_xml_text(m.group(1))

    m = re.search(r"<remarks>(.*?)</remarks>", raw, re.S)
    if m:
        doc["remarks"] = clean_xml_text(m.group(1))

    return doc


def classify_member(sig: str, type_name: str, type_kind: str):
    """Return (kind, name, display_signature) or None if the line is not a member."""
    s = sig.strip().rstrip("{").strip()
    if not s:
        return None
    if s.startswith(("if ", "for ", "foreach ", "while ", "switch ", "return", "else",
                     "try", "catch", "lock", "using ", "#", "case ", "do ")):
        return None

    # Split off modifiers.
    words = s.split()
    idx = 0
    while idx < len(words) and words[idx] in MOD_WORDS:
        idx += 1
    rest = " ".join(words[idx:])
    if not rest:
        return None

    # Constructor / destructor: Name( ... ) with no return type.
    ctor = re.match(r"^~?" + re.escape(type_name) + r"\s*\(", rest)
    if ctor:
        name = type_name
        display = re.sub(r"\)\s*(:\s*(this|base)\s*\([^)]*\))?\s*;?$", ")", rest).strip()
        return ("constructor", name, display)

    # Truncate an expression body ("=> expr;") after the closing parenthesis of the
    # parameter list, so `T Foo() => new T(x)` does not swallow the body.
    open_at = rest.find("(")
    if open_at != -1:
        d, k = 0, open_at
        while k < len(rest):
            if rest[k] == "(":
                d += 1
            elif rest[k] == ")":
                d -= 1
                if d == 0:
                    break
            k += 1
        if d == 0 and k < len(rest):
            tail = rest[k + 1:]
            if "=>" in tail:
                rest = rest[:k + 1]

    # Method: <type> Name<T>(args)
    meth = re.match(
        r"^(?P<ret>[\w\.\<\>\[\],\?\s]+?)\s+"
        r"(?P<name>[A-Za-z_]\w*(?:\.[A-Za-z_]\w*)*)\s*"
        r"(?P<gen><[^(]*>)?\s*\((?P<args>.*)\)\s*(?:where[^;{=]*)?[;{]?\s*(?:=>.*)?$",
        rest, re.S)
    if meth:
        ret = " ".join(meth.group("ret").split())
        name = meth.group("name").split(".")[-1]
        args = " ".join(meth.group("args").split())
        gen = meth.group("gen") or ""
        return ("method", name, "%s %s%s(%s)" % (ret, meth.group("name"), gen, args))

    # Property / indexer: <type> Name { ... }  or  <type> Name => expr;
    prop = re.match(r"^(?P<t>[\w\.\<\>\[\],\?\s]+?)\s+(?P<name>[A-Za-z_]\w*)\s*(?:\{|=>)", rest)
    if prop and "(" not in rest.split("=>")[0].split("{")[0]:
        t = " ".join(prop.group("t").split())
        return ("property", prop.group("name"), "%s %s" % (t, prop.group("name")))

    # Event
    if s.split() and "event" in words[:idx]:
        ev = re.match(r"^(?P<t>[\w\.\<\>\[\],\?]+)\s+(?P<name>[A-Za-z_]\w*)", rest)
        if ev:
            return ("event", ev.group("name"),
                    "%s %s" % (ev.group("t"), ev.group("name")))

    # Field / constant: <type> Name[ = value];
    fld = re.match(r"^(?P<t>[\w\.\<\>\[\],\?]+)\s+(?P<name>[A-Za-z_]\w*)\s*(=[^;]*)?;\s*$", rest)
    if fld:
        return ("field", fld.group("name"), "%s %s" % (fld.group("t"), fld.group("name")))

    return None


def visibility_of(sig: str, type_kind: str) -> str:
    words = sig.strip().split()
    for w in words[:4]:
        if w in ("public", "internal", "private", "protected"):
            if w == "protected":
                return "protected"
            return w
    # No explicit access modifier.
    if type_kind in ("interface", "enum"):
        return "public"
    return "private"


def merge_doc(doc, slash_lines, trailing):
    """Fall back to plain `//` comments when a member has no XML <summary>."""
    if not doc["summary"]:
        text = " ".join(l for l in slash_lines if l).strip()
        if not text and trailing and not is_separator_comment(trailing):
            text = trailing.strip()
        if text and len(text) > 2:
            doc["summary"] = clean_xml_text(text)
            doc["from_comment"] = True
    return doc


class TypeInfo(object):
    def __init__(self, name, kind, namespace, bases, doc, file_rel, line, modifiers, group):
        self.name = name
        self.kind = kind
        self.namespace = namespace
        self.bases = bases
        self.doc = doc
        self.file = file_rel
        self.line = line
        self.modifiers = modifiers
        self.group = group
        self.members = []          # list of dicts

    @property
    def anchor(self):
        return "t-" + re.sub(r"\W+", "-", self.namespace + "-" + self.name)


def parse_file(path: str, rel: str, group: str):
    with io.open(path, "r", encoding="utf-8-sig", errors="replace") as fh:
        lines = fh.read().split("\n")

    types = []
    namespace = ""
    depth = 0
    in_block_comment = False
    pending_doc = []
    pending_slash = []
    stack = []               # list of (depth_at_open, TypeInfo|None)
    current_type = None
    ns_depth = None

    i = 0
    n = len(lines)
    while i < n:
        raw = lines[i]
        line = raw.rstrip()
        stripped = line.strip()

        if in_block_comment:
            if "*/" in stripped:
                in_block_comment = False
            i += 1
            continue
        if stripped.startswith("/*"):
            if "*/" not in stripped:
                in_block_comment = True
            i += 1
            continue

        if stripped.startswith("///"):
            pending_doc.append(stripped)
            i += 1
            continue
        if stripped.startswith("//"):
            body = stripped.lstrip("/").strip()
            if is_separator_comment(body):
                pending_slash = []
            else:
                pending_slash.append(body)
            i += 1
            continue
        if not stripped:
            i += 1
            continue
        if RE_ATTRIBUTE.match(stripped):
            i += 1
            continue

        clean = strip_literals(line)

        # namespace
        m = RE_NAMESPACE.match(stripped)
        if m:
            namespace = m.group(1)
            ns_depth = depth
            depth += clean.count("{") - clean.count("}")
            pending_doc = []
            pending_slash = []
            i += 1
            continue

        # ---- type declaration -------------------------------------------
        expected_type_depth = (ns_depth + 1) if ns_depth is not None else 0
        m = RE_TYPE.match(stripped)
        if m and depth in (expected_type_depth, expected_type_depth + 1):
            vis = visibility_of(m.group("mods") or "", "class")
            bases = (m.group("bases") or "").lstrip(":").strip()
            bases = " ".join(bases.split())
            ti = TypeInfo(
                name=m.group("name") + (m.group("generic") or ""),
                kind=m.group("kind"),
                namespace=namespace,
                bases=bases,
                doc=merge_doc(parse_doc(pending_doc), pending_slash, ""),
                file_rel=rel,
                line=i + 1,
                modifiers=" ".join((m.group("mods") or "").split()),
                group=group,
            )
            ti.visibility = vis
            if vis in ("public", "internal"):
                types.append(ti)
            pending_doc = []
            pending_slash = []
            # find the opening brace (may be on the next line)
            opened = clean.count("{")
            j = i
            while opened == 0 and j + 1 < n:
                j += 1
                nxt = strip_literals(lines[j])
                opened = nxt.count("{")
                if opened:
                    clean = nxt
                    break
            stack.append((depth, ti))
            current_type = ti
            depth += clean.count("{") - clean.count("}")
            i = j + 1
            continue

        # ---- member declaration -----------------------------------------
        member_depth = None
        if stack:
            member_depth = stack[-1][0] + 1

        if current_type is not None and depth == member_depth:
            # enum values
            if current_type.kind == "enum":
                for token in stripped.split(","):
                    tk = token.strip()
                    if not tk or tk in ("{", "}"):
                        continue
                    nm = tk.split("=")[0].strip()
                    if re.match(r"^[A-Za-z_]\w*$", nm):
                        current_type.members.append({
                            "kind": "enum value", "name": nm, "signature": tk,
                            "doc": merge_doc(parse_doc(pending_doc), pending_slash,
                                             trailing_comment(line)),
                            "visibility": "public",
                            "static": False, "line": i + 1,
                        })
                pending_doc = []
                pending_slash = []
                depth += clean.count("{") - clean.count("}")
                i += 1
                continue

            # join continuation lines until parentheses balance
            joined = stripped
            jclean = clean
            j = i
            guard = 0
            while (jclean.count("(") > jclean.count(")")) and j + 1 < n and guard < 25:
                j += 1
                guard += 1
                joined += " " + lines[j].strip()
                jclean += " " + strip_literals(lines[j])

            info = classify_member(joined, current_type.name.split("<")[0], current_type.kind)
            if info:
                kind, name, display = info
                vis = visibility_of(joined, current_type.kind)
                if vis in ("public", "protected"):
                    current_type.members.append({
                        "kind": kind,
                        "name": name,
                        "signature": " ".join(display.split()),
                        "doc": merge_doc(parse_doc(pending_doc), pending_slash,
                                         trailing_comment(lines[j])),
                        "visibility": vis,
                        "static": " static " in " " + joined + " ",
                        "line": i + 1,
                    })
                pending_doc = []
                pending_slash = []
                # advance past the whole joined declaration
                for k in range(i, j + 1):
                    c = strip_literals(lines[k])
                    depth += c.count("{") - c.count("}")
                i = j + 1
                continue

        pending_doc = []
        pending_slash = []
        depth += clean.count("{") - clean.count("}")

        # pop finished types
        while stack and depth <= stack[-1][0]:
            stack.pop()
            current_type = stack[-1][1] if stack else None

        i += 1

    return types


# ---------------------------------------------------------------------------
# Collection
# ---------------------------------------------------------------------------

def group_for(rel_path: str) -> str:
    parts = rel_path.replace("\\", "/").split("/")
    # rel is relative to Assets/SimuLean.Net
    top = parts[0] if parts else ""
    return top


def collect() -> list:
    all_types = []
    for dirpath, dirnames, filenames in os.walk(SOURCE_ROOT):
        dirnames[:] = [d for d in dirnames if d not in SKIP_DIR_PARTS]
        for fn in sorted(filenames):
            if not fn.endswith(".cs"):
                continue
            full = os.path.join(dirpath, fn)
            rel_src = os.path.relpath(full, SOURCE_ROOT).replace("\\", "/")
            rel_repo = os.path.relpath(full, REPO_ROOT).replace("\\", "/")
            top = group_for(rel_src)
            try:
                all_types.extend(parse_file(full, rel_repo, top))
            except Exception as exc:      # pragma: no cover - defensive
                sys.stderr.write("WARN: failed to parse %s (%s)\n" % (rel_repo, exc))
    return all_types


# ---------------------------------------------------------------------------
# HTML rendering
# ---------------------------------------------------------------------------

KIND_ORDER = ["constructor", "property", "method", "event", "field", "enum value"]
KIND_LABEL = {
    "constructor": "Constructors",
    "property": "Properties",
    "method": "Methods",
    "event": "Events",
    "field": "Fields &amp; constants",
    "enum value": "Values",
}

CSS = """
:root{
  --bg:#ffffff; --bg-alt:#f6f7f9; --bg-code:#f0f2f5; --fg:#1c2024; --fg-dim:#5b646e;
  --border:#dfe3e8; --accent:#0b6ec9; --accent-soft:#e6f1fb;
  --k-class:#2f6f4f; --k-class-bg:#e4f3ea;
  --k-interface:#8a5a00; --k-interface-bg:#fbf0da;
  --k-enum:#7a3ea1; --k-enum-bg:#f2e8fa;
  --k-struct:#0b6ec9; --k-struct-bg:#e6f1fb;
  --k-static:#8a5a00;
  --k-protected:#a04040; --k-protected-bg:#fbe9e9;
  --mono:ui-monospace,SFMono-Regular,"SF Mono",Menlo,Consolas,"Liberation Mono",monospace;
}
@media (prefers-color-scheme: dark){
  :root{
    --bg:#14171a; --bg-alt:#1b1f23; --bg-code:#22272c; --fg:#e6e9ec; --fg-dim:#9aa4ae;
    --border:#2c3239; --accent:#5aa9f0; --accent-soft:#1d2b38;
    --k-class:#7fd0a4; --k-class-bg:#1c2f26;
    --k-interface:#e0b25c; --k-interface-bg:#332a17;
    --k-enum:#c39ce8; --k-enum-bg:#2b2136;
    --k-struct:#7fb8ee; --k-struct-bg:#1a2836;
    --k-protected:#eb9c9c; --k-protected-bg:#341f1f;
  }
}
*{box-sizing:border-box}
html{scroll-behavior:smooth}
body{margin:0;background:var(--bg);color:var(--fg);
  font:15px/1.6 -apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,Helvetica,Arial,sans-serif;}
a{color:var(--accent);text-decoration:none}
a:hover{text-decoration:underline}
code,pre{font-family:var(--mono)}

/* ---------- layout ---------- */
.layout{display:flex;min-height:100vh}
#sidebar{width:310px;flex:0 0 310px;border-right:1px solid var(--border);
  background:var(--bg-alt);height:100vh;position:sticky;top:0;display:flex;flex-direction:column}
.side-head{padding:14px 16px 10px;border-bottom:1px solid var(--border)}
.side-head h1{margin:0;font-size:16px;letter-spacing:.02em}
.side-head p{margin:2px 0 10px;font-size:12px;color:var(--fg-dim)}
#q{width:100%;padding:7px 9px;border:1px solid var(--border);border-radius:6px;
  background:var(--bg);color:var(--fg);font-size:13px}
#q:focus{outline:2px solid var(--accent-soft);border-color:var(--accent)}
.side-nav{overflow-y:auto;padding:10px 8px 40px;flex:1}
.side-group{margin-bottom:6px}
.side-group>summary{cursor:pointer;list-style:none;padding:5px 8px;border-radius:5px;
  font-size:12px;font-weight:600;text-transform:uppercase;letter-spacing:.05em;color:var(--fg-dim)}
.side-group>summary::-webkit-details-marker{display:none}
.side-group>summary:hover{background:var(--bg-code);color:var(--fg)}
.side-group ul{list-style:none;margin:2px 0 8px;padding:0 0 0 8px}
.side-group li a{display:block;padding:3px 8px;border-radius:5px;font-size:13px;color:var(--fg)}
.side-group li a:hover{background:var(--bg-code);text-decoration:none}
.side-group li a.active{background:var(--accent-soft);color:var(--accent);font-weight:600}
.side-group li a .sk{color:var(--fg-dim);font-size:11px;margin-right:5px}

main{flex:1;min-width:0;padding:0 40px 120px;max-width:1080px}
header.page{padding:34px 0 8px}
header.page h1{margin:0 0 6px;font-size:30px;letter-spacing:-.01em}
header.page .lede{color:var(--fg-dim);max-width:70ch;margin:0 0 14px}
.meta-row{display:flex;flex-wrap:wrap;gap:8px;margin-bottom:8px}
.pill{font-size:12px;padding:3px 9px;border-radius:99px;background:var(--bg-alt);
  border:1px solid var(--border);color:var(--fg-dim)}

.section{margin-top:38px}
.section>h2{font-size:20px;margin:0 0 4px;padding-bottom:6px;border-bottom:1px solid var(--border)}
.section>p.gdesc{color:var(--fg-dim);margin:8px 0 18px;max-width:78ch}

/* ---------- type cards ---------- */
.type{border:1px solid var(--border);border-radius:9px;margin:16px 0;overflow:hidden;background:var(--bg)}
.type>summary{cursor:pointer;list-style:none;padding:12px 16px;display:flex;
  gap:10px;align-items:baseline;flex-wrap:wrap;background:var(--bg-alt)}
.type>summary::-webkit-details-marker{display:none}
.type>summary:hover{background:var(--bg-code)}
.type[open]>summary{border-bottom:1px solid var(--border)}
.tname{font-family:var(--mono);font-size:15px;font-weight:600}
.badge{font-size:11px;padding:2px 7px;border-radius:4px;font-weight:600;letter-spacing:.03em;text-transform:uppercase}
.b-class{color:var(--k-class);background:var(--k-class-bg)}
.b-interface{color:var(--k-interface);background:var(--k-interface-bg)}
.b-enum{color:var(--k-enum);background:var(--k-enum-bg)}
.b-struct,.b-record{color:var(--k-struct);background:var(--k-struct-bg)}
.tsum{flex:1 1 100%;color:var(--fg-dim);font-size:13.5px;margin-top:2px}
.tbody{padding:12px 16px 18px}
.tmeta{font-size:12px;color:var(--fg-dim);margin:0 0 12px}
.tmeta code{background:var(--bg-code);padding:1px 5px;border-radius:4px}
.tdoc{margin:0 0 14px;max-width:80ch}
.inherits{font-family:var(--mono);font-size:12.5px;color:var(--fg-dim)}

.mgroup{margin-top:14px}
.mgroup h4{margin:0 0 6px;font-size:12px;text-transform:uppercase;letter-spacing:.06em;color:var(--fg-dim)}
table.members{width:100%;border-collapse:collapse;font-size:13.5px}
table.members td{border-top:1px solid var(--border);padding:7px 8px;vertical-align:top}
table.members tr:first-child td{border-top:none}
td.sig{font-family:var(--mono);font-size:12.5px;width:46%;white-space:normal;word-break:break-word}
td.sig .mn{font-weight:600}
td.desc{color:var(--fg-dim)}
td.desc .params{margin:5px 0 0;padding-left:16px;font-size:12.5px}
td.desc .ret{display:block;margin-top:4px;font-size:12.5px}
.tag{font-size:10px;padding:1px 5px;border-radius:3px;margin-left:6px;vertical-align:middle;
  background:var(--bg-code);color:var(--fg-dim);text-transform:uppercase;letter-spacing:.04em}
.tag.prot{color:var(--k-protected);background:var(--k-protected-bg)}
.nodoc{opacity:.55;font-style:italic}

.toolbar{display:flex;gap:10px;align-items:center;flex-wrap:wrap;margin:18px 0 0}
button.tb{font-size:12.5px;padding:5px 11px;border-radius:6px;border:1px solid var(--border);
  background:var(--bg-alt);color:var(--fg);cursor:pointer}
button.tb:hover{border-color:var(--accent);color:var(--accent)}
.hidden{display:none !important}
#noresults{padding:30px 0;color:var(--fg-dim)}

.guide{border:1px solid var(--border);border-radius:9px;padding:4px 18px 14px;background:var(--bg-alt)}
.guide table{width:100%;border-collapse:collapse;font-size:13.5px}
.guide td,.guide th{padding:7px 8px;border-top:1px solid var(--border);text-align:left;vertical-align:top}
.guide th{color:var(--fg-dim);font-size:11.5px;text-transform:uppercase;letter-spacing:.05em;border-top:none}
.guide code{background:var(--bg-code);padding:1px 5px;border-radius:4px;font-size:12.5px}

@media (max-width:960px){
  .layout{flex-direction:column}
  #sidebar{width:100%;flex:none;height:auto;position:static;border-right:none;border-bottom:1px solid var(--border)}
  .side-nav{max-height:300px}
  main{padding:0 18px 80px}
}
"""

JS = """
(function(){
  var q = document.getElementById('q');
  var types = Array.prototype.slice.call(document.querySelectorAll('details.type'));
  var sections = Array.prototype.slice.call(document.querySelectorAll('.section[data-group]'));
  var navLinks = Array.prototype.slice.call(document.querySelectorAll('.side-group li a'));
  var nores = document.getElementById('noresults');

  function norm(s){ return (s||'').toLowerCase(); }

  function filter(){
    var term = norm(q.value).trim();
    var any = false;
    types.forEach(function(t){
      var hit = !term || norm(t.dataset.search).indexOf(term) !== -1;
      t.classList.toggle('hidden', !hit);
      if(hit) any = true;
      if(term && hit && term.length > 2) t.open = true;
      if(!term) t.open = false;
    });
    sections.forEach(function(s){
      var visible = s.querySelectorAll('details.type:not(.hidden)').length;
      s.classList.toggle('hidden', term && visible === 0);
    });
    navLinks.forEach(function(a){
      var hit = !term || norm(a.dataset.search).indexOf(term) !== -1;
      a.parentNode.classList.toggle('hidden', !hit);
    });
    document.querySelectorAll('.side-group').forEach(function(g){
      var visible = g.querySelectorAll('li:not(.hidden)').length;
      g.classList.toggle('hidden', term && visible === 0);
      if(term && visible) g.open = true;
    });
    nores.classList.toggle('hidden', any);
  }

  q.addEventListener('input', filter);
  document.addEventListener('keydown', function(e){
    if(e.key === '/' && document.activeElement !== q){ e.preventDefault(); q.focus(); q.select(); }
    if(e.key === 'Escape' && document.activeElement === q){ q.value=''; filter(); q.blur(); }
  });

  document.getElementById('expandAll').addEventListener('click', function(){
    types.forEach(function(t){ if(!t.classList.contains('hidden')) t.open = true; });
  });
  document.getElementById('collapseAll').addEventListener('click', function(){
    types.forEach(function(t){ t.open = false; });
  });

  // Open the targeted type when arriving via an anchor link.
  function openHash(){
    if(!location.hash) return;
    var el = document.querySelector(location.hash);
    if(el && el.tagName === 'DETAILS'){ el.open = true; el.scrollIntoView({block:'start'}); }
    navLinks.forEach(function(a){ a.classList.toggle('active', a.getAttribute('href') === location.hash); });
  }
  window.addEventListener('hashchange', openHash);
  openHash();
})();
"""


def esc(s):
    return html.escape(s or "", quote=False)


def render_member_row(m):
    sig = esc(m["signature"])
    # bold the member name inside the signature
    nm = re.escape(m["name"])
    sig = re.sub(r"\b(" + nm + r")\b", r'<span class="mn">\1</span>', sig, count=1)
    tags = ""
    if m["static"]:
        tags += '<span class="tag">static</span>'
    if m["visibility"] == "protected":
        tags += '<span class="tag prot">protected</span>'

    doc = m["doc"]
    if doc["summary"]:
        desc = esc(doc["summary"])
    else:
        desc = '<span class="nodoc">No summary in source.</span>'
    if doc["params"]:
        desc += '<ul class="params">' + "".join(
            "<li><code>%s</code> — %s</li>" % (esc(p), esc(d)) for p, d in doc["params"]
        ) + "</ul>"
    if doc["returns"]:
        desc += '<span class="ret"><strong>Returns:</strong> %s</span>' % esc(doc["returns"])
    if doc["remarks"]:
        desc += '<span class="ret">%s</span>' % esc(doc["remarks"])

    return "<tr><td class=\"sig\"><code>%s</code>%s</td><td class=\"desc\">%s</td></tr>" % (sig, tags, desc)


def render_type(t):
    members = t.members
    search_blob = " ".join([t.name, t.namespace, t.kind, t.doc.get("summary", "")] +
                           [m["name"] for m in members])
    parts = []
    parts.append('<details class="type" id="%s" data-search="%s">' % (t.anchor, esc(search_blob)))
    parts.append('<summary>')
    parts.append('<span class="badge b-%s">%s</span>' % (t.kind, t.kind))
    parts.append('<span class="tname">%s</span>' % esc(t.name))
    if "abstract" in t.modifiers:
        parts.append('<span class="tag">abstract</span>')
    if "static" in t.modifiers:
        parts.append('<span class="tag">static</span>')
    summary = t.doc.get("summary", "")
    if summary:
        short = summary if len(summary) <= 190 else summary[:187].rsplit(" ", 1)[0] + "…"
        parts.append('<span class="tsum">%s</span>' % esc(short))
    parts.append('</summary>')

    parts.append('<div class="tbody">')
    meta = "<code>%s</code> &nbsp;·&nbsp; <code>%s:%d</code>" % (esc(t.namespace), esc(t.file), t.line)
    parts.append('<p class="tmeta">%s</p>' % meta)
    if t.bases:
        parts.append('<p class="inherits">: %s</p>' % esc(t.bases))
    if summary:
        parts.append('<p class="tdoc">%s</p>' % esc(summary))
    if t.doc.get("remarks"):
        parts.append('<p class="tdoc">%s</p>' % esc(t.doc["remarks"]))
    if not members:
        parts.append('<p class="nodoc">No public or protected members.</p>')

    for kind in KIND_ORDER:
        group = [m for m in members if m["kind"] == kind]
        if not group:
            continue
        if kind != "enum value":
            group.sort(key=lambda m: (m["name"].lower(), m["signature"]))
        parts.append('<div class="mgroup"><h4>%s</h4><table class="members">' % KIND_LABEL[kind])
        parts.extend(render_member_row(m) for m in group)
        parts.append("</table></div>")

    parts.append("</div></details>")
    return "\n".join(parts)


def build_html(types):
    # group + sort
    grouped = OrderedDict()
    for key in GROUPS:
        grouped[key] = []
    for t in types:
        grouped.setdefault(t.group, []).append(t)
    grouped = OrderedDict((k, v) for k, v in grouped.items() if v)

    total_members = sum(len(t.members) for t in types)
    today = date.today().isoformat()

    out = []
    out.append("<!doctype html>")
    out.append('<html lang="en"><head><meta charset="utf-8">')
    out.append('<meta name="viewport" content="width=device-width,initial-scale=1">')
    out.append("<title>SimuLean Core — API Reference</title>")
    out.append("<style>%s</style></head><body>" % CSS)
    out.append('<div class="layout">')

    # ---- sidebar ----
    out.append('<nav id="sidebar"><div class="side-head">')
    out.append("<h1>SimuLean Core API</h1>")
    out.append("<p>Assets/SimuLean.Net · generated %s</p>" % today)
    out.append('<input id="q" type="search" placeholder="Search types and members  (press /)" autocomplete="off">')
    out.append("</div><div class=\"side-nav\">")
    out.append('<div class="side-group" style="padding:0 8px 8px"><a href="#top">Overview</a></div>')
    for key, items in grouped.items():
        label, _ = GROUPS.get(key, GROUP_FALLBACK)
        out.append('<details class="side-group" open><summary>%s (%d)</summary><ul>' % (esc(label), len(items)))
        for t in sorted(items, key=lambda x: x.name.lower()):
            blob = t.name + " " + t.namespace + " " + " ".join(m["name"] for m in t.members)
            out.append('<li><a href="#%s" data-search="%s"><span class="sk">%s</span>%s</a></li>'
                       % (t.anchor, esc(blob), t.kind[:4], esc(t.name)))
        out.append("</ul></details>")
    out.append("</div></nav>")

    # ---- main ----
    out.append("<main id=\"top\">")
    out.append("<header class=\"page\">")
    out.append("<h1>SimuLean Core — API Reference</h1>")
    out.append('<p class="lede">Every public and protected type of the discrete-event simulation core '
               '(<code>Assets/SimuLean.Net/</code>). Generated directly from the C# sources and their XML '
               'documentation comments, so it never drifts from the code. The Unity integration and the GA '
               'optimization module are deliberately out of scope — they consume this API.</p>')
    out.append('<div class="meta-row">')
    out.append('<span class="pill">%d types</span>' % len(types))
    out.append('<span class="pill">%d documented members</span>' % total_members)
    out.append('<span class="pill">%d subsystems</span>' % len(grouped))
    documented = sum(1 for t in types for m in t.members if m["doc"]["summary"])
    if total_members:
        out.append('<span class="pill">%d%% of members carry a doc comment</span>'
                   % round(100.0 * documented / total_members))
    out.append('<span class="pill">generated %s</span>' % today)
    out.append("</div>")
    out.append('<p class="lede" style="font-size:13.5px">Click any type to expand it. '
               'Press <code>/</code> to search by type or member name. '
               'Regenerate with <code>python docs/api/generate_api_docs.py</code>. '
               'For the narrative view see <a href="../architecture.md">architecture.md</a> and '
               'the modelling guide <a href="../guide/README.md">guide/</a>.</p>')
    out.append('<div class="toolbar"><button class="tb" id="expandAll">Expand all</button>'
               '<button class="tb" id="collapseAll">Collapse all</button></div>')
    out.append("</header>")

    # orientation table
    out.append('<div class="section" data-group="_intro"><h2>Where to start</h2><div class="guide"><table>')
    out.append("<tr><th>If you want to…</th><th>Start with</th></tr>")
    for task, entries in [
        ("Run a model from code", "SimClock, SimContext, Element, GeneralLink"),
        ("Add a new element type", "Element (abstract contract), Link, VElement, IElementHandler"),
        ("Understand timing", "SimClock.AdvanceClock, Eventcs, EventHandle, DoubleMinBinaryHeap"),
        ("Read results", "ElementStatsCollector, StatTimeWeightedVariable, ElementStateTracker"),
        ("Make runs reproducible", "SimClock(int? seed), SimContext.CreateRandomStream, IRandomEngine"),
        ("Run without Unity", "SimulationConfig, HeadlessModelFactory, HeadlessVElement"),
        ("Move material with vehicles", "ITransportSystem, TransportNetwork, TransferPoint"),
        ("Store and retrieve stock", "StorageSystem, StorageLane, EntityList, ILocationPolicy"),
    ]:
        links = ", ".join("<code>%s</code>" % esc(e.strip()) for e in entries.split(","))
        out.append("<tr><td>%s</td><td>%s</td></tr>" % (esc(task), links))
    out.append("</table></div></div>")

    for key, items in grouped.items():
        label, desc = GROUPS.get(key, GROUP_FALLBACK)
        out.append('<section class="section" data-group="%s" id="g-%s">' % (esc(key), esc(key)))
        out.append("<h2>%s</h2>" % esc(label))
        out.append('<p class="gdesc">%s</p>' % esc(desc))
        for t in sorted(items, key=lambda x: (x.kind != "interface", x.name.lower())):
            out.append(render_type(t))
        out.append("</section>")

    out.append('<div id="noresults" class="hidden">No type matches that search.</div>')
    out.append("</main></div>")
    out.append("<script>%s</script>" % JS)
    out.append("</body></html>")
    return "\n".join(out)


def main():
    if not os.path.isdir(SOURCE_ROOT):
        sys.exit("Source root not found: %s" % SOURCE_ROOT)
    types = collect()
    types = [t for t in types if getattr(t, "visibility", "public") == "public"]
    html_doc = build_html(types)
    with io.open(OUTPUT_FILE, "w", encoding="utf-8") as fh:
        fh.write(html_doc)
    members = sum(len(t.members) for t in types)
    print("Wrote %s" % os.path.relpath(OUTPUT_FILE, REPO_ROOT))
    print("  %d types, %d members" % (len(types), members))
    undocumented = [t.name for t in types if not t.doc.get("summary")]
    if undocumented:
        print("  %d types without a <summary>: %s" % (len(undocumented), ", ".join(sorted(undocumented)[:12])))


if __name__ == "__main__":
    main()
