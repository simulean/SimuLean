"""Floor-plan texture for a SimuLean Unity model (FloorPlanUnderlay component).

Turns a CAD drawing into a single-channel line texture + a placement JSON that
``Assets/SimuLean.Unity/Visuals/FloorPlanUnderlay.cs`` lays on the floor under the model.

Pipeline:
  1. DWG -> DXF with LibreDWG ``dwg2dxf`` (not bundled; see README.md). Skipped when you pass ``--dxf``.
  2. DXF -> polylines per layer in metres (ezdxf ``recover``; INSERTs exploded recursively, block entities on
     layer 0 inherit the INSERT's layer, curves flattened), plus optional text labels, cached in
     ``<work>/<name>.segments.npz`` (big DXFs take a while to parse; the cache is instant).
  3. Layers -> style groups (configurable, see ``layer_styles.example.json``) -> anti-aliased grey texture
     (one channel = line coverage x group intensity; sides a multiple of 4 for block compression) +
     ``<name>.json`` with the exact rectangle it covers (drawing metres).

Nothing about a particular plant is hard-coded: layer names, units, crop window and styles come from the command
line and the optional JSON config. Use ``--list-layers`` first to see what the drawing contains.

Usage (repo root; venv: py -3.12 -m venv Tools/FloorPlan/.venv; .venv python -m pip install ezdxf numpy pillow):
  python Tools/FloorPlan/make_floor_texture.py --dwg plan.dwg --list-layers
  python Tools/FloorPlan/make_floor_texture.py --dwg plan.dwg --config my_styles.json --out-dir Assets/MyModel/FloorPlan
  python Tools/FloorPlan/make_floor_texture.py --dxf plan.dxf --crop 0 0 180 120 --max-px 4096 --debug
"""
from __future__ import annotations

import argparse
import fnmatch
import json
import os
import shutil
import subprocess
import sys
import time
from collections import defaultdict
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw, ImageFont

HERE = Path(__file__).resolve().parent
REPO = HERE.parent.parent

# Default style when no --config is given: every layer is drawn as one "line" group, dimensions / hatches /
# texts dropped by common layer-name patterns. Widths are in metres (converted with the texture resolution).
DEFAULT_CONFIG = {
    "units_to_metres": 0.001,           # drawing units -> metres (mm drawings: 0.001)
    "groups": [
        {"name": "line", "patterns": ["*"], "width_m": 0.035, "intensity": 220},
    ],
    "drop": ["*dim*", "*cota*", "*hatch*", "*text*", "*defpoints*"],
    "label_layers": [],                 # TEXT / MTEXT of these layers are drawn as labels
    "label_intensity": 215,
    "flatten_m": 0.01,                  # curve flattening tolerance (m)
}

CURVES = {"LINE", "ARC", "CIRCLE", "ELLIPSE", "LWPOLYLINE", "POLYLINE", "SPLINE", "SOLID", "TRACE", "3DFACE"}


def log(msg: str) -> None:
    print(f"[floorplan {time.strftime('%H:%M:%S')}] {msg}", flush=True)


def load_config(path: Path | None) -> dict:
    cfg = json.loads(json.dumps(DEFAULT_CONFIG))
    if path is not None:
        user = json.loads(path.read_text(encoding="utf-8"))
        cfg.update({k: v for k, v in user.items() if not k.startswith("_")})
    return cfg


def match(layer: str, patterns: list[str]) -> bool:
    low = layer.lower()
    return any(fnmatch.fnmatchcase(low, p.lower()) for p in patterns)


def group_of(layer: str, cfg: dict) -> str | None:
    """Style group of a drawing layer (None = dropped). Drop patterns win; then the first matching group."""
    if match(layer, cfg.get("drop", [])):
        return None
    for g in cfg["groups"]:
        if match(layer, g.get("patterns", [])):
            return g["name"]
    return None


# ── 1. DWG -> DXF ─────────────────────────────────────────────────────────────

def find_dwg2dxf(explicit: str | None) -> Path | None:
    candidates = []
    if explicit:
        candidates.append(Path(explicit))
    if os.environ.get("LIBREDWG_DWG2DXF"):
        candidates.append(Path(os.environ["LIBREDWG_DWG2DXF"]))
    candidates += [REPO / "Tools" / "LibreDWG" / "dwg2dxf.exe", REPO / "Tools" / "LibreDWG" / "dwg2dxf"]
    on_path = shutil.which("dwg2dxf")
    if on_path:
        candidates.append(Path(on_path))
    return next((p for p in candidates if p.exists()), None)


def dwg_to_dxf(dwg: Path, dxf: Path, exe: Path, force: bool) -> None:
    if dxf.exists() and not force and dxf.stat().st_mtime >= dwg.stat().st_mtime:
        log(f"DXF cached: {dxf.name} ({dxf.stat().st_size / 1e6:.0f} MB)")
        return
    dxf.parent.mkdir(parents=True, exist_ok=True)
    log(f"dwg2dxf {dwg.name} -> {dxf}")
    with open(dxf.with_suffix(".log"), "w", encoding="utf-8", errors="replace") as f:
        r = subprocess.run([str(exe), "-y", "-o", str(dxf), str(dwg)], stdout=f, stderr=subprocess.STDOUT)
    if r.returncode != 0 or not dxf.exists():
        sys.exit(f"dwg2dxf failed ({r.returncode}), see {dxf.with_suffix('.log')}")


# ── 2. DXF -> polylines + labels ──────────────────────────────────────────────

def extract(dxf: Path, cache: Path, cfg: dict, force: bool) -> tuple[dict[str, list[np.ndarray]], list[dict]]:
    """Every curve of every layer (metres) + labels of the label layers; cached next to the DXF's work dir."""
    scale = float(cfg["units_to_metres"])
    key = f"{scale}|{cfg.get('flatten_m')}|{','.join(cfg.get('label_layers', []))}"
    if cache.exists() and not force and cache.stat().st_mtime >= dxf.stat().st_mtime:
        z = np.load(cache, allow_pickle=True)
        if "key" in z.files and str(z["key"]) == key:
            log(f"segments cached: {cache.name}")
            return z["polys"].item(), list(z["texts"].item())
    from ezdxf import recover
    from ezdxf import path as ezpath

    log(f"reading {dxf.name}")
    t = time.time()
    doc, _auditor = recover.readfile(str(dxf))
    log(f"read in {time.time() - t:.0f} s; exploding and flattening")
    polys: dict[str, list[np.ndarray]] = defaultdict(list)
    texts: list[dict] = []
    skipped: dict[str, int] = defaultdict(int)
    tol = float(cfg.get("flatten_m", 0.01)) / scale
    label_layers = set(cfg.get("label_layers", []))

    def emit(e, layer: str) -> None:
        kind = e.dxftype()
        if kind not in CURVES:
            skipped[kind] += 1
            return
        try:
            p = ezpath.make_path(e)
        except Exception:
            skipped[kind + "!"] += 1
            return
        for sub in p.sub_paths():
            pts = np.array([(v.x, v.y) for v in sub.flattening(tol)], dtype=np.float64)
            if len(pts) >= 2:
                polys[layer].append((pts * scale).astype(np.float32))

    def walk(entities, parent_layer: str | None, depth: int) -> None:
        for e in entities:
            layer = e.dxf.layer if e.dxf.hasattr("layer") else "0"
            if parent_layer is not None and layer == "0":
                layer = parent_layer
            kind = e.dxftype()
            if kind == "INSERT":
                if depth > 8:
                    continue
                try:
                    walk(e.virtual_entities(), layer, depth + 1)
                except Exception:
                    skipped["INSERT!"] += 1
                continue
            if kind in ("TEXT", "MTEXT"):
                if layer in label_layers:
                    p = e.dxf.insert
                    txt = e.plain_text() if kind == "MTEXT" else e.dxf.text
                    h = e.dxf.get("char_height" if kind == "MTEXT" else "height", 0)
                    texts.append({"x": p.x * scale, "y": p.y * scale, "h": h * scale, "t": txt, "layer": layer})
                continue
            emit(e, layer)

    walk(doc.modelspace(), None, 0)
    log(f"kept {sum(len(v) for v in polys.values())} polylines in {len(polys)} layers, {len(texts)} labels; "
        f"skipped {dict(skipped)}")
    cache.parent.mkdir(parents=True, exist_ok=True)
    po, to = np.empty((), dtype=object), np.empty((), dtype=object)
    po[()] = dict(polys)
    to[()] = texts
    np.savez_compressed(cache, polys=po, texts=to, key=np.array(key))
    return dict(polys), texts


# ── 3. render ────────────────────────────────────────────────────────────────

def extents(polys, cfg) -> tuple[float, float, float, float]:
    xs0, ys0, xs1, ys1 = [], [], [], []
    for layer, lst in polys.items():
        if group_of(layer, cfg) is None:
            continue
        for p in lst:
            xs0.append(p[:, 0].min()); ys0.append(p[:, 1].min()); xs1.append(p[:, 0].max()); ys1.append(p[:, 1].max())
    if not xs0:
        sys.exit("no geometry left after the layer filters (check --config / --list-layers)")
    return float(min(xs0)), float(min(ys0)), float(max(xs1)), float(max(ys1))


def canvas(crop, max_px: int):
    """Pixels per metre, size (multiples of 4) and the exact rectangle (metres) the pixels cover."""
    x0, y0, x1, y1 = crop
    ppm = max_px / max(x1 - x0, y1 - y0)
    W, H = max(4, int(round((x1 - x0) * ppm / 4)) * 4), max(4, int(round((y1 - y0) * ppm / 4)) * 4)
    return ppm, W, H, (x0, y0, x0 + W / ppm, y0 + H / ppm)


def render(polys, texts, cfg, crop, max_px: int, ss: int, colour: bool = False):
    ppm, W, H, rect = canvas(crop, max_px)
    x0, y0, x1, y1 = rect
    img = Image.new("RGB" if colour else "L", (W * ss, H * ss), (255, 255, 255) if colour else 0)
    d = ImageDraw.Draw(img)
    palette = [(40, 40, 40), (20, 90, 200), (200, 60, 40), (40, 150, 60), (150, 150, 150), (160, 80, 180)]
    # draw groups in reverse config order so the first (most important) group ends on top
    for gi, g in reversed(list(enumerate(cfg["groups"]))):
        width = max(1, round(float(g.get("width_m", 0.035)) * ppm * ss))
        fill = palette[gi % len(palette)] if colour else int(g.get("intensity", 220))
        for layer, lst in polys.items():
            if group_of(layer, cfg) != g["name"]:
                continue
            for p in lst:
                if p[:, 0].max() < x0 or p[:, 0].min() > x1 or p[:, 1].max() < y0 or p[:, 1].min() > y1:
                    continue
                q = np.empty_like(p)
                q[:, 0] = (p[:, 0] - x0) * ppm * ss
                q[:, 1] = (y1 - p[:, 1]) * ppm * ss      # image row 0 = max y (north)
                d.line([tuple(v) for v in q], fill=fill, width=width)
    for t in texts:
        size = max(8, int(round(t["h"] * ppm * ss * 1.35)))
        try:
            font = ImageFont.truetype("arialbd.ttf", size)
        except OSError:
            font = ImageFont.load_default()
        px, py = (t["x"] - x0) * ppm * ss, (y1 - t["y"]) * ppm * ss
        d.text((px, py), t["t"], fill=(90, 60, 20) if colour else int(cfg.get("label_intensity", 215)), font=font, anchor="ls")
    if ss > 1:
        img = img.resize((W, H), Image.LANCZOS)
    return img, ppm, rect


def list_layers(polys, texts, cfg) -> None:
    rows = sorted(((layer, len(lst)) for layer, lst in polys.items()), key=lambda r: -r[1])
    print(f"{'polylines':>10}  {'group':<10}  layer")
    for layer, n in rows:
        print(f"{n:>10}  {str(group_of(layer, cfg)):<10}  {layer}")
    by_layer = defaultdict(int)
    for t in texts:
        by_layer[t["layer"]] += 1
    if by_layer:
        print("labels:", dict(by_layer))


def main() -> None:
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--dwg", type=Path, help="drawing to convert with LibreDWG dwg2dxf")
    src.add_argument("--dxf", type=Path, help="DXF file (skips the DWG conversion)")
    ap.add_argument("--config", type=Path, help="layer styles / units JSON (see layer_styles.example.json)")
    ap.add_argument("--out-dir", type=Path, default=HERE / "work" / "out", help="where <name>.png/.json are written")
    ap.add_argument("--name", default="floorplan", help="output base name")
    ap.add_argument("--work", type=Path, default=HERE / "work", help="cache folder (DXF, segments)")
    ap.add_argument("--crop", type=float, nargs=4, metavar=("X0", "Y0", "X1", "Y1"),
                    help="texture window in drawing metres (default: extents of the kept layers)")
    ap.add_argument("--units-to-metres", type=float, help="override the config's drawing-unit scale")
    ap.add_argument("--max-px", type=int, default=8192, help="longest side of the texture (px)")
    ap.add_argument("--ss", type=int, default=2, help="supersampling factor (anti-aliasing)")
    ap.add_argument("--dwg2dxf", help="path of LibreDWG dwg2dxf (else env LIBREDWG_DWG2DXF, Tools/LibreDWG/, PATH)")
    ap.add_argument("--force-dxf", action="store_true")
    ap.add_argument("--force-extract", action="store_true")
    ap.add_argument("--list-layers", action="store_true", help="print layers, polyline counts and groups, then stop")
    ap.add_argument("--debug", action="store_true", help="also write <work>/<name>_debug.png (groups in colour)")
    a = ap.parse_args()

    cfg = load_config(a.config)
    if a.units_to_metres:
        cfg["units_to_metres"] = a.units_to_metres
    a.work.mkdir(parents=True, exist_ok=True)

    if a.dwg:
        if not a.dwg.exists():
            sys.exit(f"DWG not found: {a.dwg}")
        exe = find_dwg2dxf(a.dwg2dxf)
        if exe is None:
            sys.exit("LibreDWG dwg2dxf not found: pass --dwg2dxf, set LIBREDWG_DWG2DXF or unpack it into Tools/LibreDWG/ "
                     "(see Tools/FloorPlan/README.md), or convert the drawing to DXF yourself and use --dxf")
        dxf = a.work / f"{a.name}.dxf"
        dwg_to_dxf(a.dwg, dxf, exe, a.force_dxf)
    else:
        dxf = a.dxf
        if not dxf.exists():
            sys.exit(f"DXF not found: {dxf}")

    polys, texts = extract(dxf, a.work / f"{a.name}.segments.npz", cfg, a.force_extract or a.force_dxf)
    if a.list_layers:
        list_layers(polys, texts, cfg)
        return

    crop = tuple(a.crop) if a.crop else extents(polys, cfg)
    kept = defaultdict(int)
    for layer, lst in polys.items():
        kept[group_of(layer, cfg)] += len(lst)
    log(f"polylines per group: {dict(kept)}; window {tuple(round(v, 2) for v in crop)} m")

    img, ppm, rect = render(polys, texts, cfg, crop, a.max_px, a.ss)
    a.out_dir.mkdir(parents=True, exist_ok=True)
    png = a.out_dir / f"{a.name}.png"
    img.save(png, optimize=True)
    meta = {
        "source": (a.dwg or a.dxf).name,
        "rect_m": [round(v, 5) for v in rect],
        "pixels_per_metre": ppm,
        "size_px": list(img.size),
        "units_to_metres": cfg["units_to_metres"],
        "note": "rect_m = drawing metres covered by the texture (x0, y0, x1, y1); image row 0 = max y; grey = line coverage",
        "generated": time.strftime("%Y-%m-%d %H:%M:%S"),
    }
    (a.out_dir / f"{a.name}.json").write_text(json.dumps(meta, indent=1, ensure_ascii=False), encoding="utf-8")
    log(f"wrote {png} ({img.size[0]}x{img.size[1]}, {ppm:.1f} px/m, {png.stat().st_size / 1e6:.1f} MB) + {a.name}.json")

    if a.debug:
        dbg, _, _ = render(polys, texts, cfg, crop, min(4096, a.max_px), 1, colour=True)
        dbg.save(a.work / f"{a.name}_debug.png")
        log(f"wrote {a.work / (a.name + '_debug.png')}")


if __name__ == "__main__":
    main()
