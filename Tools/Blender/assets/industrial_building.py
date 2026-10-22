"""
SimuLean kit: industrial workshop building.  Sandwich-panel walls on a precast
concrete plinth, ribbon windows, sectional and personnel doors, HEB steel
columns with crane brackets, IPE beams, a "SimuLean" sign.  Pieces snap on a
2 m x 1 m wall grid and are combined in Unity by UnityModularWall /
UnitySteelColumn / UnityModularStrip.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/industrial_building.py --
            [--bake 4096] [--save 2048] [--no-render] [--quick]

Outputs
    Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/<Piece>.fbx  (one per piece)
    Assets/SimuLean.Unity/Art/Models/IndustrialBuilding/IndustrialBuilding_*.png  (one shared atlas)
    Tools/Blender/build/IndustrialBuilding.blend
    Tools/Blender/previews/building_*.png

Wall frame (Unity; every wall piece): origin at the bottom of the INTERIOR face
of the cladding, X along the wall, Y up, the exterior faces -Z.  Walls run
counter-clockwise seen from above (interior on the +Z side), so each wall
closes the corner at its END with the Corner_* pieces.

    Wall_Panel       segment 2 x 1 m (tiles along X and Y).  Trapezoidal sheet
    Wall_Panel_Dark  (8 ribs, crest at z -0.115) on a flat liner at z = 0.
    Wall_Window      segment 2 x 1 m.  Ribbon window row (mullion every 1 m).
    Wall_Plinth      segment 2 x 1 m.  Precast concrete plinth + drip flashing;
                     the first row, never stretched vertically.
    Wall_Top         segment 2 m.  Coping on top of the last row (0.18 high).
    Wall_Corner      segment, 1 m along Y.  Corner trim at the wall end (x = 0).
    Corner_Plinth    fixed.  Plinth corner block at the wall end.
    Corner_Top       fixed.  Coping corner at the wall end.
    Door_Sectional   fixed 4 x 4 m = 2 columns x (plinth + 3 rows).  Node Leaf:
                     raise along local Y (0 closed .. 3.9 open).
    Door_Personnel   fixed 2 x 3 m = 1 column x (plinth + 2 rows).  Node Leaf:
                     hinge at the left jamb, localEulerAngles.y 0..+90 opens out.
    Sign_SimuLean    fixed 4.0 x 0.9 m sign box; its back (z = 0) goes on the
                     cladding crest (wall z = -0.115).

Structure (origin on the column axis at floor level; the web lies along Z, the
flanges face the wall; brackets point to +Z)
    Column_Base      fixed.  Grout, base plate, anchors; shaft starts at y 0.35.
    Column_Shaft     segment, 1 m along Y.  HEB300.
    Column_Top       fixed, 0.125 m.  Cap plate (origin at its joint, y = 0).
    Column_Bracket   fixed.  Crane corbel; origin on the column axis at the
                     bracket top = runway beam underside (rail top - 0.455).
                     Runway beam axis at z +0.50.
    Column_Guard     fixed.  Yellow/black U guard, open towards the wall.
    Beam_Segment     segment, 2 m along Z.  IPE400, origin at the top flange.
    Beam_End         cap (joint at z = 0, extends to -Z).  End plate + bolts.
"""

import os
import sys
import argparse
import time
from math import pi

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
from mathutils import Vector, Matrix         # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
import simulean_modular as KM                # noqa: E402
from simulean_kit import V, Part, finish, boolean, cull_faces  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'IndustrialBuilding')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'
PREFIX = 'IndustrialBuilding'

# ---------------------------------------------------------------- dimensions (authored frame, m)
# authored: x along the wall, y = depth (exterior at -y), z up
CELL, ROW_H, PLINTH_H = 2.0, 1.0, 1.0
VALLEY, CREST = -0.080, -0.115               # trapezoidal sheet (liner at y = 0)
RIB_PITCH = 0.25
PL_OUT, PL_TOP = -0.14, 0.98                 # plinth outer face / top
COL_OFF = 0.32                               # column axis from the wall liner
BASE_H, TOP_H = 0.35, 0.125                  # column base / cap heights
BRACKET_E = 0.50                             # runway beam axis from the column axis
HEB = (0.30, 0.30, 0.019, 0.011, 0.027)      # h, b, tf, tw, r
IPE = (0.40, 0.18, 0.0135, 0.0086, 0.021)

YELLOW = (0.80, 0.42, 0.015)
CLAD = (0.50, 0.51, 0.49)                    # RAL 9002-ish grey white
DARK = (0.040, 0.046, 0.052)                 # RAL 7016 anthracite
LINER = (0.62, 0.62, 0.59)
STRUCT = (0.020, 0.060, 0.155)               # steel blue
LEAF = (0.36, 0.38, 0.38)                    # RAL 7035 light grey
INK = (0.90, 0.90, 0.88)

MATS = {}


# =============================================================================
# Materials
# =============================================================================

def per(period, fn, *a, **kw):
    """Create a material with a given periodic-noise frame (tiling axes)."""
    old = K.NB.default_period
    K.NB.default_period = period
    try:
        return fn(*a, **kw)
    finally:
        K.NB.default_period = old


def cladding(name, color):
    """Painted trapezoidal sheet with a row of EPDM-washered screws per metre."""
    def fn(nb):
        x, y, z = nb.xyz(nb.obj)
        dx = nb.m('MULTIPLY', nb.m('SUBTRACT', nb.m('FRACT', nb.m('ADD', nb.m('DIVIDE', x, RIB_PITCH), 0.5)),
                                   0.5), RIB_PITCH)
        dz = nb.m('SUBTRACT', nb.m('FRACT', z), 0.5)
        r = nb.m('SQRT', nb.m('ADD', nb.m('MULTIPLY', dx, dx), nb.m('MULTIPLY', dz, dz)))
        front = nb.mr(y, -0.06, -0.075)
        head = nb.m('MULTIPLY', nb.mr(r, 0.0075, 0.006), front)
        ring = nb.m('MULTIPLY', nb.mr(r, 0.0105, 0.009), front)
        col = nb.mixc(ring, color, (0.02, 0.02, 0.02))
        return nb.mixc(head, col, tuple(c * 0.8 for c in color))
    return M.paint(name, None, rough=0.42, wear=0.12, dirt=0.9, color_fn=fn, edge=0.004)


def hazard3(yellow, width=0.06, black=(0.018, 0.018, 0.02)):
    """45 deg stripes continuous over every face orientation (x + y + z)."""
    def fn(nb):
        x, y, z = nb.xyz(nb.obj)
        s = nb.m('DIVIDE', nb.m('ADD', nb.m('ADD', x, y), z), width * 2)
        return nb.mixc(nb.m('LESS_THAN', nb.m('FRACT', s), 0.5), black, yellow)
    return fn


def bollard_fn(nb):
    x, y, z = nb.xyz(nb.obj)
    b1 = nb.m('MULTIPLY', nb.mr(z, 0.80, 0.805), nb.mr(z, 0.885, 0.88))
    b2 = nb.m('MULTIPLY', nb.mr(z, 0.94, 0.945), nb.mr(z, 1.025, 1.02))
    return nb.mixc(nb.m('MAXIMUM', b1, b2), YELLOW, (0.02, 0.02, 0.022))   # yellow, two black bands


def sign_face(name, img, aspect, width, origin):
    """Dark sign face: backlit white lettering (emissive) and a yellow bar."""
    mat = M._new(name)
    nb = K.NB(mat, period={})
    rel = nb.vm('SUBTRACT', nb.obj, origin)
    ux = nb.vm('DOT_PRODUCT', rel, (1, 0, 0))
    vz = nb.vm('DOT_PRODUCT', rel, (0, 0, 1))
    u = nb.m('ADD', nb.m('DIVIDE', ux, width), 0.5)
    v = nb.m('ADD', nb.m('DIVIDE', vz, width / aspect), 0.5)
    _, alpha = nb.image(img, nb.vec(u, v, 0.0), 'CLIP', 'Cubic')
    facing = nb.mr(nb.vm('DOT_PRODUCT', nb.geo.outputs['Normal'], (0, -1, 0)), 0.55, 0.75)
    a = nb.m('MULTIPLY', alpha, facing)
    x, y, z = nb.xyz(nb.obj)
    bar = nb.m('MULTIPLY', nb.m('MULTIPLY', nb.mr(z, 0.115, 0.12), nb.mr(z, 0.155, 0.15)),
               nb.m('MULTIPLY', nb.mr(nb.m('ABSOLUTE', x), 1.72, 1.71), facing))
    base = nb.mixc(nb.noise(2.0, 4, 0.5), (0.010, 0.015, 0.030), (0.016, 0.022, 0.042))
    col = nb.mixc(a, base, INK)
    col = nb.mixc(bar, col, YELLOW)
    emit = nb.mixc(a, (0, 0, 0), (1.0, 1.0, 1.0))
    r = nb.mixf(a, 0.32, 0.2)
    return K.finalize_material(nb, col, r, 0.0, None, emit=emit)


def build_materials():
    K.NB.default_period = {}
    logo, la = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_sign.png'),
                                   256, FONT, bold_offset=0.014)
    sign_w = min(3.0, 0.42 * la)          # la ~ 5.3 for "SimuLean": ~2.2 m of lettering on the 4 m box
    ROWS, ALONG, UP = {'x': CELL, 'z': ROW_H}, {'x': CELL}, {'z': ROW_H}
    MATS.update({
        # rows: periodic along the wall (2 m) and up (1 m)
        'Cladding': per(ROWS, cladding, 'Cladding', CLAD),
        'CladdingDark': per(ROWS, cladding, 'CladdingDark', DARK),
        'Liner': per(ROWS, M.paint, 'Liner', LINER, rough=0.5, wear=0.0, dirt=0.35),
        # single rows: periodic along the wall
        'Concrete': per(ALONG, M.concrete, 'Concrete', splash=0.45),
        'Flashing': per(ALONG, M.paint, 'Flashing', DARK, rough=0.4, wear=0.15, edge=0.003),
        'Alu': per(ALONG, M.dark_steel, 'Alu', color=(0.040, 0.042, 0.046), rough=0.32, metal=0.6),
        'Glass': per(ALONG, M.glass, 'Glass'),
        'Board': per(ALONG, M.paint, 'Board', LINER, rough=0.5, wear=0.05, dirt=0.5),
        # vertical tiles
        'Trim': per(UP, M.paint, 'Trim', DARK, rough=0.4, wear=0.15, edge=0.003),
        'Structure': per(UP, M.paint, 'Structure', STRUCT, rough=0.42, wear=0.35),
        # beams tile along authored y
        'StructureH': per({'y': 2.0}, M.paint, 'StructureH', STRUCT, rough=0.42, wear=0.35),
        # fixed pieces
        'StructureF': M.paint('StructureF', STRUCT, rough=0.42, wear=0.45),
        'ConcreteF': M.concrete('ConcreteF', splash=0.3),
        'Grout': M.concrete('Grout', color=(0.22, 0.215, 0.205), pores=1.4),
        'FlashingF': M.paint('FlashingF', DARK, rough=0.4, wear=0.2, edge=0.003),
        'DoorPanel': M.paint('DoorPanel', DARK, rough=0.42, wear=0.25),
        'DoorFrame': M.paint('DoorFrame', DARK, rough=0.4, wear=0.5),
        'DoorLeaf': M.paint('DoorLeaf', LEAF, rough=0.4, wear=0.35),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=0.9, color_fn=hazard3(YELLOW)),
        'Bollard': M.paint('Bollard', None, rough=0.38, wear=0.8, color_fn=bollard_fn),
        'Cabinet': M.paint('Cabinet', (0.50, 0.51, 0.50), rough=0.45, wear=0.3),
        'Galv': M.galvanised('Galv'),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Rubber': M.rubber('Rubber'),
        'Plastic': M.plastic('Plastic'),
        'BtnGreen': M.plastic('BtnGreen', color=(0.02, 0.30, 0.04), rough=0.35),
        'EStop': M.plastic('EStop', color=(0.60, 0.02, 0.015), rough=0.35),
        'DoorGlass': M.glass('DoorGlass'),
        'SignFace': sign_face('SignFace', logo, la, sign_w, (0.0, -0.12, 0.53)),
        'SignFrame': M.galvanised('SignFrame', rough=0.3),
        'Lamp': M.lens('Lamp', (1.0, 0.86, 0.62), emit=1.0),
    })
    return MATS


# =============================================================================
# Helpers
# =============================================================================

def mk(name, fn, bevel=0.004, seg=1, angle=40.0, cull=None, cutters=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        boolean(ob, [c() for c in cutters])
    if cull:
        cull_faces(ob, cull)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def ends(axis, *vals, eps=1e-4):
    """Cull predicate: faces lying in the planes axis = vals (open tile joints)."""
    i = 'xyz'.index(axis)
    return lambda c, n: abs(n[i]) > 0.99 and any(abs(c[i] - v) < eps for v in vals)


def any_of(*preds):
    return lambda c, n: any(p(c, n) for p in preds)


def bx(p, x0, x1, y0, y1, z0, z1, mat):
    p.box_minmax((min(x0, x1), min(y0, y1), min(z0, z1)), (max(x0, x1), max(y0, y1), max(z0, z1)), mat)


def hexbolt(p, c, axis, h, r=0.012, washer=True, mat='DarkSteel'):
    c, d = V(c), V(axis).normalized()
    if washer:
        p.cylinder(c, c + d * 0.003, r * 1.3, mat, n=10)
    p.cylinder(c + d * 0.003, c + d * h, r, mat, n=6)


def i_profile(h, b, tf, tw, r, top, seg=3):
    """Rolled I-section outline (u = across the flanges, v = height), top at v = top."""
    t, bt = top, top - h
    pts = [(-b / 2, t), (b / 2, t), (b / 2, t - tf), (tw / 2, t - tf), (tw / 2, bt + tf),
           (b / 2, bt + tf), (b / 2, bt), (-b / 2, bt), (-b / 2, bt + tf), (-tw / 2, bt + tf),
           (-tw / 2, t - tf), (-b / 2, t - tf)]
    rad = [0, 0, 0, r, r, 0, 0, 0, 0, r, r, 0]
    return K.fillet_closed(pts, rad, seg)


def thick(path, t):
    """Closed outline of a folded sheet: the polyline `path` thickened by t to
    its left (mitred)."""
    pts = [V(q) for q in path]
    n = len(pts)
    offs = []
    for i in range(n):
        if i == 0 or i == n - 1:
            d = (pts[1] - pts[0]) if i == 0 else (pts[-1] - pts[-2])
            d.normalize()
            nrm = V((-d.y, d.x))
        else:
            d1 = (pts[i] - pts[i - 1]).normalized()
            d2 = (pts[i + 1] - pts[i]).normalized()
            n1, n2 = V((-d1.y, d1.x)), V((-d2.y, d2.x))
            m = (n1 + n2).normalized()
            nrm = m / max(m.dot(n1), 0.3)
        offs.append(pts[i] + nrm * t)
    return [tuple(q) for q in pts] + [tuple(q) for q in reversed(offs)]


def tube(p, prof, mats, axis, a0, a1):
    """Open extrusion (no caps) of a closed outline with one material per edge."""
    def P(u, v, a):
        return {'X': V((a, u, v)), 'Y': V((u, a, v)), 'Z': V((u, v, a))}[axis]
    n = len(prof)
    verts = [P(u, v, a0) for u, v in prof] + [P(u, v, a1) for u, v in prof]
    faces = [(i, (i + 1) % n, n + (i + 1) % n, n + i) for i in range(n)]
    p.add(verts, faces, mats if isinstance(mats, list) else [mats] * n)


def sheet_line():
    pts = [(0.0, VALLEY)]
    for i in range(int(round(CELL / RIB_PITCH))):
        c = RIB_PITCH * (i + 0.5)
        pts += [(c - 0.05, VALLEY), (c - 0.02, CREST), (c + 0.02, CREST), (c + 0.05, VALLEY)]
    pts.append((CELL, VALLEY))
    return pts


def clip_line(pts, x0, x1):
    out = []

    def push(q):
        if not out or abs(out[-1][0] - q[0]) > 1e-7 or abs(out[-1][1] - q[1]) > 1e-7:
            out.append(q)
    for a, b in zip(pts, pts[1:]):
        if b[0] < x0 or a[0] > x1 or b[0] - a[0] < 1e-9:
            continue

        def at(x):
            t = (x - a[0]) / (b[0] - a[0])
            return (x, a[1] + (b[1] - a[1]) * t)
        push(a if a[0] >= x0 else at(x0))
        push(b if b[0] <= x1 else at(x1))
    return out


def sheet(p, x0, x1, z0, z1, mat):
    """Sandwich panel section between x0..x1: trapezoidal skin + flat liner."""
    ext = clip_line(sheet_line(), x0, x1)
    prof = ext + [(x1, 0.0), (x0, 0.0)]
    mats = [mat] * (len(ext) - 1) + ['Liner', 'Liner', 'Liner']
    tube(p, prof, mats, 'Z', z0, z1)


# flashing / coping outlines in (depth y, z)
PLINTH_FLASH = [(-0.075, 1.06), (-0.075, 1.0), (-0.155, 0.99), (-0.155, 0.955)]
COPING = [(-0.15, -0.10), (-0.15, 0.16), (0.012, 0.18), (0.012, -0.06)]
SILL_FLASH = [(-0.095, 0.03), (-0.165, 0.012), (-0.165, -0.03)]


# =============================================================================
# Wall rows
# =============================================================================

def build_panel(name, mat):
    body = mk(f'{name}_Sheet', lambda p: sheet(p, 0.0, CELL, 0.0, ROW_H, mat), bevel=0.0,
              cull=ends('x', 0.0, CELL))
    return KM.Piece(name, 'segment', [body], levels=1,
                    ghosts=[(name, (CELL, 0, 0)), (name, (-CELL, 0, 0)),
                            (name, (0, 0, ROW_H)), (name, (0, 0, -ROW_H))])


def plinth(p, x0, x1, mat='Concrete'):
    bx(p, x0, x1, PL_OUT, 0.0, -0.05, PL_TOP, mat)
    bx(p, x0, x1, -0.07, 0.0, PL_TOP - 0.001, PLINTH_H, mat)       # upstand under the liner


def plinth_flash(p, x0, x1, mat='Flashing'):
    p.extrude(thick(PLINTH_FLASH, 0.003), 'X', x0, x1, mat)


def build_plinth():
    conc = mk('WPL_Concrete', lambda p: plinth(p, 0.0, CELL), bevel=0.008, seg=2,
              cull=ends('x', 0.0, CELL))
    flash = mk('WPL_Flash', lambda p: plinth_flash(p, 0.0, CELL), bevel=0.0, cull=ends('x', 0.0, CELL))
    return KM.Piece('Wall_Plinth', 'segment', [conc, flash], levels=1,
                    ghosts=[('Wall_Plinth', (CELL, 0, 0)), ('Wall_Plinth', (-CELL, 0, 0)),
                            ('Wall_Panel', (0, 0, PLINTH_H))])


def build_top():
    cop = mk('WT_Coping', lambda p: p.extrude(thick(COPING, 0.003), 'X', 0.0, CELL, 'Flashing'),
             bevel=0.0, cull=ends('x', 0.0, CELL))
    return KM.Piece('Wall_Top', 'segment', [cop], levels=1,
                    ghosts=[('Wall_Top', (CELL, 0, 0)), ('Wall_Top', (-CELL, 0, 0)),
                            ('Wall_Panel', (0, 0, -ROW_H))])


def build_window():
    def frame(p):
        yo, yi = -0.10, -0.03
        bx(p, 0.0, CELL, yo, yi, 0.03, 0.09, 'Alu')
        bx(p, 0.0, CELL, yo, yi, 0.91, 0.97, 'Alu')
        for x0, x1 in ((0.0, 0.03), (0.97, 1.03), (CELL - 0.03, CELL)):
            bx(p, x0, x1, yo, yi, 0.09, 0.91, 'Alu')
        for x0, x1 in ((0.0, 0.015), (0.985, 1.015), (CELL - 0.015, CELL)):
            bx(p, x0, x1, -0.118, yo, 0.06, 0.94, 'Alu')          # pressure caps
    fr = mk('WW_Frame', frame, bevel=0.003, cull=ends('x', 0.0, CELL))
    gl = mk('WW_Glass', lambda p: bx(p, 0.0, CELL, -0.068, -0.062, 0.09, 0.91, 'Glass'), bevel=0.0,
            cull=ends('x', 0.0, CELL))

    def closures(p):
        p.extrude(thick(SILL_FLASH, 0.003), 'X', 0.0, CELL, 'Flashing')
        bx(p, 0.0, CELL, -0.11, 0.02, 0.0, 0.03, 'Board')           # sill board, closes the row below
        bx(p, 0.0, CELL, -0.13, 0.02, 0.97, 1.0, 'Board')           # head, closes the row above
        p.extrude(thick([(-0.10, 0.97), (-0.135, 0.97), (-0.135, 0.955)], 0.003), 'X', 0.0, CELL, 'Flashing')
    cl = mk('WW_Closures', closures, bevel=0.0, cull=ends('x', 0.0, CELL))
    return KM.Piece('Wall_Window', 'segment', [fr, gl, cl], levels=1,
                    ghosts=[('Wall_Window', (CELL, 0, 0)), ('Wall_Window', (-CELL, 0, 0)),
                            ('Wall_Panel', (0, 0, ROW_H)), ('Wall_Panel', (0, 0, -ROW_H))])


def build_corner_trim():
    L = [(-0.04, -0.135), (0.135, -0.135), (0.135, 0.04)]
    trim = mk('WC_Trim', lambda p: p.extrude(thick(L, 0.003), 'Z', 0.0, ROW_H, 'Trim'), bevel=0.0015,
              cull=ends('z', 0.0, ROW_H))
    return KM.Piece('Wall_Corner', 'segment', [trim], levels=1,
                    ghosts=[('Wall_Corner', (0, 0, ROW_H)), ('Wall_Corner', (0, 0, -ROW_H))])


def turn(prof):
    """Outline in (depth y, z) of the NEXT wall, seen in this wall's (x, z)."""
    return [(-y, z) for y, z in prof]


def build_corner_plinth():
    def block(p):
        bx(p, 0.0, -PL_OUT, PL_OUT, 0.0, -0.05, PL_TOP, 'ConcreteF')
        bx(p, 0.0, 0.07, -0.07, 0.0, PL_TOP - 0.001, PLINTH_H, 'ConcreteF')
    blk = mk('CP_Block', block, bevel=0.008, seg=2, cull=any_of(ends('x', 0.0), ends('y', 0.0)))

    def flash(p):
        p.extrude(thick(PLINTH_FLASH, 0.003), 'X', 0.0, 0.158, 'FlashingF')
        p.extrude(thick(turn(PLINTH_FLASH), -0.003), 'Y', -0.158, 0.0, 'FlashingF')
    fl = mk('CP_Flash', flash, bevel=0.0)
    return KM.Piece('Corner_Plinth', 'cap', [blk, fl], levels=1)


def build_corner_top():
    def cop(p):
        p.extrude(thick(COPING, 0.003), 'X', 0.0, 0.153, 'FlashingF')
        p.extrude(thick(turn(COPING), -0.003), 'Y', -0.153, 0.0, 'FlashingF')
    c = mk('CT_Coping', cop, bevel=0.0)
    return KM.Piece('Corner_Top', 'cap', [c], levels=1)


# =============================================================================
# Doors and sign
# =============================================================================

DOOR_W, DOOR_H = 4.0, PLINTH_H + 3 * ROW_H


def leaf_profile(y0=0.02, y1=0.06, z0=0.03, z1=DOOR_H):
    """Sectional door leaf outline (y, z): section joints every metre and two
    microlines per section on the exterior (-y) face."""
    grooves = []
    for s in range(4):
        zs = s * 1.0
        if s:
            grooves.append((zs, 0.012, 0.012))
        for f in (1 / 3, 2 / 3):
            grooves.append((zs + f, 0.004, 0.004))
    ext = [(y0, z0)]
    for zc, hw, d in grooves:
        ext += [(y0, zc - hw), (y0 + d, zc - hw * 0.3), (y0 + d, zc + hw * 0.3), (y0, zc + hw)]
    ext.append((y0, z1))
    return ext + [(y1, z1), (y1, z0)]


def build_door_sectional():
    W, H = DOOR_W, DOOR_H

    def frame(p):
        bx(p, 0.0, 0.12, -0.15, 0.0, -0.0, H - 0.15, 'Hazard')
        bx(p, W - 0.12, W, -0.15, 0.0, -0.0, H - 0.15, 'Hazard')
        bx(p, 0.0, W, -0.15, 0.0, H - 0.15, H, 'DoorFrame')
        p.extrude(thick([(-0.02, H - 0.15), (-0.165, H - 0.15), (-0.165, H - 0.175)], -0.003), 'X',
                  -0.02, W + 0.02, 'FlashingF')
    fr = mk('DS_Frame', frame, bevel=0.004)

    def bollards(p):
        for x in (-0.45, W + 0.45):
            p.cylinder((x, -0.55, -0.05), (x, -0.55, 1.08), 0.084, 'Bollard', n=18)
            p.cylinder((x, -0.55, 1.08), (x, -0.55, 1.10), 0.07, 'Bollard', n=18)
    bo = mk('DS_Bollards', bollards, bevel=0.004, seg=2)

    def guides(p):
        for sx, x0 in ((1, -0.10), (-1, W + 0.10)):
            prof = [(x0, 0.0), (x0 + sx * 0.08, 0.0), (x0 + sx * 0.08, 0.012), (x0 + sx * 0.012, 0.012),
                    (x0 + sx * 0.012, 0.10), (x0, 0.10)]
            if sx < 0:
                prof = prof[::-1]
            p.extrude(prof, 'Z', 0.0, H + 0.35, 'Galv')
    gd = mk('DS_Guides', guides, bevel=0.002)

    def drive(p):
        zc, yc = H + 0.22, 0.22
        p.cylinder((-0.15, yc, zc), (W + 0.15, yc, zc), 0.0254, 'Galv', n=10)
        for x0, x1 in ((1.1, 1.85), (2.15, 2.9)):
            p.cylinder((x0, yc, zc), (x1, yc, zc), 0.048, 'DarkSteel', n=14)
        for x0, x1 in ((-0.10, -0.02), (W + 0.02, W + 0.10)):
            p.cylinder((x0, yc, zc), (x1, yc, zc), 0.085, 'Galv', n=18)
        for x in (-0.14, W + 0.12):
            bx(p, x, x + 0.02, 0.0, yc + 0.06, zc - 0.14, zc + 0.12, 'Galv')
        bx(p, W + 0.16, W + 0.40, yc - 0.12, yc + 0.12, zc - 0.15, zc + 0.15, 'DoorFrame')
        p.cylinder((W + 0.40, yc, zc), (W + 0.68, yc, zc), 0.075, 'DoorFrame', n=16)
        # push-button station on the inner wall face
        bx(p, W + 0.45, W + 0.70, 0.0, 0.10, 1.25, 1.62, 'Cabinet')
        cx = W + 0.575
        p.cylinder((cx, 0.10, 1.52), (cx, 0.115, 1.52), 0.02, 'BtnGreen', n=14)
        p.cylinder((cx, 0.10, 1.45), (cx, 0.115, 1.45), 0.02, 'Plastic', n=14)
        p.cylinder((cx, 0.10, 1.35), (cx, 0.112, 1.35), 0.018, 'Plastic', n=14)
        p.cylinder((cx, 0.112, 1.35), (cx, 0.13, 1.35), 0.03, 'EStop', n=16)
    dr = mk('DS_Drive', drive, bevel=0.003)

    leaf = mk('DS_Leaf', lambda p: p.extrude(leaf_profile(), 'X', -0.05, W + 0.05, 'DoorPanel'), bevel=0.0)

    def detail(p):
        bx(p, -0.05, W + 0.05, 0.018, 0.062, 0.0, 0.03, 'Rubber')
        for i in range(6):
            xc = 0.45 + i * (W - 0.9) / 5
            bx(p, xc - 0.28, xc + 0.28, 0.008, 0.02, 2.35, 2.65, 'Rubber')
            bx(p, xc - 0.25, xc + 0.25, 0.012, 0.02, 2.37, 2.63, 'DoorGlass')
        bx(p, W / 2 - 0.10, W / 2 + 0.10, 0.008, 0.02, 0.40, 0.44, 'DarkSteel')
    dt = mk('DS_Detail', detail, bevel=0.002)
    return KM.Piece('Door_Sectional', 'fixed', {None: [fr, bo, gd, dr], 'Leaf': [leaf, dt]}, levels=3,
                    pivots={'Leaf': (W / 2, 0.04, 0.0)},
                    lod={'DS_Frame': (1.0, 0.6), 'DS_Bollards': (0.5, None), 'DS_Guides': (0.6, None),
                         'DS_Drive': (0.4, None), 'DS_Leaf': (1.0, 0.4), 'DS_Detail': (0.6, None)})


def build_door_personnel():
    X0, X1, HEAD = 0.40, 1.60, 2.30
    TOP = PLINTH_H + 2 * ROW_H

    def wall(p):
        sheet(p, 0.0, X0, PLINTH_H, TOP, 'Cladding')
        sheet(p, X1, CELL, PLINTH_H, TOP, 'Cladding')
        sheet(p, X0, X1, HEAD, TOP, 'Cladding')
    wl = mk('DP_Wall', wall, bevel=0.0, cull=ends('x', 0.0, CELL))

    def base(p):
        plinth(p, 0.0, X0)
        plinth(p, X1, CELL)
    bs = mk('DP_Plinth', base, bevel=0.008, seg=2, cull=ends('x', 0.0, CELL))

    def flash(p):
        plinth_flash(p, 0.0, X0)
        plinth_flash(p, X1, CELL)
    bf = mk('DP_Flash', flash, bevel=0.0, cull=ends('x', 0.0, CELL))

    def frame(p):
        bx(p, X0, X0 + 0.07, -0.135, 0.01, 0.0, HEAD - 0.07, 'DoorFrame')
        bx(p, X1 - 0.07, X1, -0.135, 0.01, 0.0, HEAD - 0.07, 'DoorFrame')
        bx(p, X0, X1, -0.135, 0.01, HEAD - 0.07, HEAD, 'DoorFrame')
    fr = mk('DP_Frame', frame, bevel=0.004)

    def canopy(p):
        bx(p, 0.15, CELL - 0.15, -0.95, -0.12, 2.62, 2.68, 'FlashingF')
        bx(p, 0.15, CELL - 0.15, -0.95, -0.93, 2.56, 2.70, 'FlashingF')
        bx(p, 0.15, CELL - 0.15, -0.14, -0.12, 2.50, 2.75, 'FlashingF')
        for x in (0.30, CELL - 0.30):
            p.cylinder((x, -0.90, 2.68), (x, -0.13, 3.12), 0.008, 'Galv', n=8)
            bx(p, x - 0.03, x + 0.03, -0.13, -0.115, 3.05, 3.17, 'Galv')
        # lamp under the canopy
        bx(p, 0.90, 1.10, -0.40, -0.30, 2.585, 2.62, 'DarkSteel')
    cn = mk('DP_Canopy', canopy, bevel=0.003)
    lamp = mk('DP_Lamp', lambda p: bx(p, 0.915, 1.085, -0.39, -0.31, 2.575, 2.586, 'Lamp'), bevel=0.0)
    step = mk('DP_Step', lambda p: bx(p, 0.25, CELL - 0.25, -0.75, PL_OUT, -0.05, 0.10, 'ConcreteF'),
              bevel=0.01, seg=2)

    lf = mk('DP_Leaf', lambda p: bx(p, X0 + 0.075, X1 - 0.075, -0.105, -0.055, 0.01, HEAD - 0.075,
                                    'DoorLeaf'), bevel=0.004, seg=2)

    def hardware(p):
        bx(p, X0 + 0.10, X1 - 0.10, -0.108, -0.105, 0.03, 0.30, 'Galv')           # kick plate
        bx(p, 0.86, 1.14, -0.109, -0.104, 1.28, 1.92, 'DarkSteel')               # vision panel frame
        bx(p, 0.88, 1.12, -0.110, -0.106, 1.30, 1.90, 'DoorGlass')
        hx = X1 - 0.16
        bx(p, hx - 0.025, hx + 0.025, -0.112, -0.105, 0.98, 1.22, 'DarkSteel')     # escutcheon
        p.cylinder((hx, -0.112, 1.10), (hx, -0.16, 1.10), 0.009, 'Galv', n=8)
        p.cylinder((hx - 0.004, -0.16, 1.10), (hx - 0.13, -0.16, 1.10), 0.009, 'Galv', n=8)
    hw = mk('DP_Hardware', hardware, bevel=0.0015)
    return KM.Piece('Door_Personnel', 'fixed', {None: [wl, bs, bf, fr, cn, lamp, step], 'Leaf': [lf, hw]},
                    levels=3, pivots={'Leaf': (X0 + 0.075, -0.08, 0.0)},
                    lod={'DP_Frame': (0.6, 0.3), 'DP_Canopy': (0.5, 0.3), 'DP_Lamp': (1.0, None),
                         'DP_Step': (1.0, 0.5), 'DP_Leaf': (0.6, 0.3), 'DP_Hardware': (0.5, None)},
                    ghosts=[('Wall_Panel', (sx * CELL, 0, z)) for sx in (1, -1) for z in (1.0, 2.0)] +
                           [('Wall_Plinth', (CELL, 0, 0)), ('Wall_Plinth', (-CELL, 0, 0)),
                            ('Wall_Panel', (0, 0, TOP))])


def build_sign():
    W, H, D = 4.0, 0.9, 0.12

    def box(p):
        p.extrude(K.rrect(W, H, 0.035, 3, 0.0, H / 2), 'Y', -D, 0.0, 'SignFrame', cap_mat='SignFace')
    b = mk('SG_Box', box, bevel=0.004)

    def brackets(p):
        for x in (-1.5, 1.5):
            for z in (0.12, H - 0.12):
                hexbolt(p, (x, -D, z), (0, -1, 0), 0.006, 0.01, washer=False, mat='SignFrame')
    br = mk('SG_Screws', brackets, bevel=0.0)
    return KM.Piece('Sign_SimuLean', 'fixed', [b, br], levels=2, lod={'SG_Screws': (None,)})


# =============================================================================
# Structure
# =============================================================================

def heb(top):
    return i_profile(*HEB, top=top)


def build_column_base():
    grout = mk('CB_Grout', lambda p: bx(p, -0.27, 0.27, -0.29, 0.29, -0.02, 0.025, 'Grout'), bevel=0.006)
    plate = mk('CB_Plate', lambda p: bx(p, -0.25, 0.25, -0.275, 0.275, 0.025, 0.055, 'StructureF'),
               bevel=0.003)
    stub = mk('CB_Stub', lambda p: p.extrude([(u, v) for u, v in heb(0.15)], 'Z', 0.055, BASE_H, 'StructureF'),
              bevel=0.0, cull=ends('z', BASE_H))

    def gussets(p):
        for sy in (1, -1):
            prof = [(sy * 0.15, 0.055), (sy * 0.265, 0.055), (sy * 0.265, 0.075), (sy * 0.15, 0.24)]
            p.extrude(prof if sy > 0 else prof[::-1], 'X', -0.006, 0.006, 'StructureF')
    gu = mk('CB_Gussets', gussets, bevel=0.002)

    def bolts(p):
        for sx in (1, -1):
            for sy in (1, -1):
                c = (sx * 0.175, sy * 0.215, 0.055)
                bx(p, c[0] - 0.035, c[0] + 0.035, c[1] - 0.035, c[1] + 0.035, 0.055, 0.065, 'StructureF')
                hexbolt(p, (c[0], c[1], 0.065), (0, 0, 1), 0.028, 0.02, washer=False)
                p.cylinder((c[0], c[1], 0.093), (c[0], c[1], 0.125), 0.011, 'Galv', n=8)
    bo = mk('CB_Bolts', bolts, bevel=0.0015)
    return KM.Piece('Column_Base', 'fixed', [grout, plate, stub, gu, bo], levels=3,
                    lod={'CB_Grout': (1.0, 1.0), 'CB_Plate': (1.0, 1.0), 'CB_Stub': (1.0, 1.0),
                         'CB_Gussets': (1.0, None), 'CB_Bolts': (0.5, None)},
                    ghosts=[('Column_Shaft', (0, 0, BASE_H))])


def build_column_shaft():
    s = mk('CS_Shaft', lambda p: p.extrude(heb(0.15), 'Z', 0.0, 1.0, 'Structure'), bevel=0.0,
           cull=ends('z', 0.0, 1.0))
    return KM.Piece('Column_Shaft', 'segment', [s], levels=1,
                    ghosts=[('Column_Shaft', (0, 0, 1.0)), ('Column_Shaft', (0, 0, -1.0))])


def build_column_top():
    stub = mk('CT_Stub', lambda p: p.extrude(heb(0.15), 'Z', 0.0, 0.10, 'StructureF'), bevel=0.0,
              cull=ends('z', 0.0))
    plate = mk('CT_Plate', lambda p: bx(p, -0.17, 0.17, -0.17, 0.17, 0.10, TOP_H, 'StructureF'), bevel=0.003)

    def bolts(p):
        for sx in (1, -1):
            for sy in (1, -1):
                hexbolt(p, (sx * 0.10, sy * 0.10, 0.10), (0, 0, -1), 0.016, 0.014)
    bo = mk('CT_Bolts', bolts, bevel=0.0)
    return KM.Piece('Column_Top', 'fixed', [stub, plate, bo], levels=2,
                    lod={'CT_Stub': (1.0,), 'CT_Plate': (1.0,), 'CT_Bolts': (None,)},
                    ghosts=[('Column_Shaft', (0, 0, -1.0))])


def build_column_bracket():
    y0, y1, zt = 0.15, BRACKET_E + 0.20, -0.012

    def body(p):
        bx(p, -0.15, 0.15, y0, y1, zt - 0.022, zt, 'StructureF')                         # top plate
        p.extrude([(y0, zt - 0.022), (y1, zt - 0.022), (y1, -0.22), (y0, -0.55)], 'X', -0.006, 0.006,
                  'StructureF')
        p.extrude([(y0, -0.55), (y1, -0.22), (y1, -0.24), (y0, -0.57)], 'X', -0.10, 0.10, 'StructureF')
        bx(p, -0.15, 0.15, y1, y1 + 0.015, -0.24, zt, 'StructureF')                       # end plate
        bx(p, -0.17, 0.17, BRACKET_E - 0.16, BRACKET_E + 0.16, zt, 0.0, 'StructureF')     # bearing plate
    b = mk('BR_Body', body, bevel=0.003)

    def stiff(p):
        for sx in (1, -1):
            for yc in (BRACKET_E, y0 + 0.012):
                zb = -0.55 + (yc - y0) / (y1 - y0) * 0.33
                x0, x1 = sx * 0.006, sx * 0.10
                p.extrude([(min(x0, x1), -0.034), (max(x0, x1), -0.034), (max(x0, x1), zb),
                           (min(x0, x1), zb)], 'Y', yc - 0.006, yc + 0.006, 'StructureF')
    st = mk('BR_Stiff', stiff, bevel=0.002)
    return KM.Piece('Column_Bracket', 'fixed', [b, st], levels=2, lod={'BR_Body': (0.6,), 'BR_Stiff': (None,)})


def build_column_guard():
    Hg, t = 0.90, 0.012
    X, Y0, Y1 = 0.29, -0.16, 0.31

    def body(p):
        bx(p, -X, X, Y1 - t, Y1, 0.0, Hg, 'Hazard')
        for sx in (1, -1):
            bx(p, sx * X, sx * (X - t), Y0, Y1, 0.0, Hg, 'Hazard')
            bx(p, sx * X, sx * (X + 0.06), Y0, Y1, 0.0, 0.012, 'Hazard')
        bx(p, -X, X, Y1, Y1 + 0.06, 0.0, 0.012, 'Hazard')
    b = mk('CG_Body', body, bevel=0.004, seg=2)

    def bolts(p):
        for sx in (1, -1):
            for y in (Y0 + 0.06, Y1 - 0.08):
                hexbolt(p, (sx * (X + 0.03), y, 0.012), (0, 0, 1), 0.012, 0.011)
        for x in (-0.18, 0.18):
            hexbolt(p, (x, Y1 + 0.03, 0.012), (0, 0, 1), 0.012, 0.011)
    bo = mk('CG_Bolts', bolts, bevel=0.0)
    return KM.Piece('Column_Guard', 'fixed', [b, bo], levels=2, lod={'CG_Body': (0.6,), 'CG_Bolts': (None,)})


def build_beam():
    s = mk('BM_Beam', lambda p: p.extrude(i_profile(*IPE, top=0.0), 'Y', 0.0, 2.0, 'StructureH'), bevel=0.0,
           cull=ends('y', 0.0, 2.0))
    return KM.Piece('Beam_Segment', 'segment', [s], levels=1,
                    ghosts=[('Beam_Segment', (0, 2.0, 0)), ('Beam_Segment', (0, -2.0, 0))])


def build_beam_end():
    pl = mk('BE_Plate', lambda p: bx(p, -0.10, 0.10, -0.02, 0.0, -0.43, 0.02, 'StructureF'), bevel=0.003)

    def bolts(p):
        for x in (-0.055, 0.055):
            for z in (-0.07, -0.20, -0.33):
                hexbolt(p, (x, -0.02, z), (0, -1, 0), 0.014, 0.013)
    bo = mk('BE_Bolts', bolts, bevel=0.0)
    return KM.Piece('Beam_End', 'fixed', [pl, bo], levels=2, lod={'BE_Plate': (1.0,), 'BE_Bolts': (None,)},
                    ghosts=[('Beam_Segment', (0, 0, 0))])


# =============================================================================
# Kit assembly
# =============================================================================

# One atlas for the whole kit.  Wall rows and the door leaf are big flat
# painted areas (low density); small hardware stays readable (high density).
TEXEL_WEIGHTS = {
    'Wall_Panel_Sheet': 0.34, 'Wall_Panel_Dark_Sheet': 0.34, 'WPL_Concrete': 0.3, 'WPL_Flash': 0.9,
    'WT_Coping': 0.55, 'WW_Frame': 1.1, 'WW_Glass': 0.3, 'WW_Closures': 0.7, 'WC_Trim': 0.9,
    'CP_Block': 0.8, 'CP_Flash': 1.0, 'CT_Coping': 1.0,
    'DS_Frame': 0.5, 'DS_Leaf': 0.3, 'DS_Detail': 0.9, 'DS_Guides': 0.6, 'DS_Drive': 1.2, 'DS_Bollards': 1.0,
    'DP_Wall': 0.34, 'DP_Plinth': 0.3, 'DP_Flash': 0.9, 'DP_Frame': 0.9, 'DP_Leaf': 0.7, 'DP_Hardware': 1.4,
    'DP_Canopy': 0.7, 'DP_Lamp': 1.2, 'DP_Step': 0.6,
    'SG_Box': 1.1, 'SG_Screws': 1.0,
    'CB_Grout': 0.9, 'CB_Plate': 1.0, 'CB_Stub': 1.0, 'CB_Gussets': 1.2, 'CB_Bolts': 1.3,
    'CS_Shaft': 0.7, 'CT_Stub': 1.0, 'CT_Plate': 1.1, 'CT_Bolts': 1.2, 'BR_Body': 0.9, 'BR_Stiff': 1.0,
    'CG_Body': 0.9, 'CG_Bolts': 1.2, 'BM_Beam': 0.5, 'BE_Plate': 1.1, 'BE_Bolts': 1.2,
}


def build_kit():
    return [build_panel('Wall_Panel', 'Cladding'), build_panel('Wall_Panel_Dark', 'CladdingDark'),
            build_window(), build_plinth(), build_top(), build_corner_trim(), build_corner_plinth(),
            build_corner_top(), build_door_sectional(), build_door_personnel(), build_sign(),
            build_column_base(), build_column_shaft(), build_column_top(), build_column_bracket(),
            build_column_guard(), build_beam(), build_beam_end()]


# =============================================================================
# Preview (mirrors UnityModularWall / UnitySteelColumn)
# =============================================================================

T = Matrix.Translation


def Rz(a):
    return Matrix.Rotation(a, 4, 'Z')


def Sc(sx, sy, sz):
    return Matrix.Diagonal((sx, sy, sz, 1.0))


def wall(coll, pcs, base, L, rows=7, windows=(), dark=(), doors=(), corner=True, level=0):
    n = max(1, round(L / CELL))
    kx = L / (n * CELL)
    kz = 1.0
    covered = set()
    for kind, c in doors:
        span, nr = (2, 3) if kind == 'Door_Sectional' else (1, 2)
        covered |= {(j, r) for j in range(c, c + span) for r in range(-1, nr)}
        hz = (PLINTH_H + nr * ROW_H * kz) / (PLINTH_H + nr * ROW_H)
        KM.place(pcs[kind], base @ T((c * CELL * kx, 0, 0)) @ Sc(kx, 1, hz), coll, level)
    ztop = PLINTH_H + rows * ROW_H * kz
    for j in range(n):
        x = j * CELL * kx
        if (j, -1) not in covered:
            KM.place(pcs['Wall_Plinth'], base @ T((x, 0, 0)) @ Sc(kx, 1, 1), coll, level)
        for r in range(rows):
            if (j, r) in covered:
                continue
            name = 'Wall_Window' if r in windows else ('Wall_Panel_Dark' if r in dark else 'Wall_Panel')
            KM.place(pcs[name], base @ T((x, 0, PLINTH_H + r * ROW_H * kz)) @ Sc(kx, 1, kz), coll, level)
        KM.place(pcs['Wall_Top'], base @ T((x, 0, ztop)) @ Sc(kx, 1, 1), coll, level)
    if corner:
        KM.place(pcs['Corner_Plinth'], base @ T((L, 0, 0)), coll, level)
        for r in range(rows):
            KM.place(pcs['Wall_Corner'], base @ T((L, 0, PLINTH_H + r * ROW_H * kz)) @ Sc(1, 1, kz), coll, level)
        KM.place(pcs['Corner_Top'], base @ T((L, 0, ztop)), coll, level)


def column(coll, pcs, M, H, bracket=None, guard=True, level=0):
    KM.place(pcs['Column_Base'], M, coll, level)
    run = H - TOP_H - BASE_H
    n = max(1, round(run))
    k = run / n
    for j in range(n):
        KM.place(pcs['Column_Shaft'], M @ T((0, 0, BASE_H + j * k)) @ Sc(1, 1, k), coll, level)
    KM.place(pcs['Column_Top'], M @ T((0, 0, H - TOP_H)), coll, level)
    if bracket is not None:
        KM.place(pcs['Column_Bracket'], M @ T((0, 0, bracket)), coll, level)
    if guard:
        KM.place(pcs['Column_Guard'], M, coll, level)


BLD_W, BLD_D, COL_H, RAIL = 24.0, 18.0, 7.6, 6.0


def building(coll, pcs, level=0):
    """24 x 18 m workshop, interior corner at (-12, -9); walls run CCW."""
    x0, y0 = -BLD_W / 2, -BLD_D / 2
    wall(coll, pcs, T((x0, y0, 0)), BLD_W, windows=(5,), dark=(6,),
         doors=(('Door_Sectional', 1), ('Door_Personnel', 6)), level=level)
    wall(coll, pcs, T((-x0, y0, 0)) @ Rz(pi / 2), BLD_D, windows=(5,), dark=(6,), level=level)
    wall(coll, pcs, T((-x0, -y0, 0)) @ Rz(pi), BLD_W, windows=(5,), dark=(6,),
         doors=(('Door_Sectional', 4),), level=level)
    wall(coll, pcs, T((x0, -y0, 0)) @ Rz(-pi / 2), BLD_D, windows=(5,), dark=(6,), level=level)
    KM.place(pcs['Sign_SimuLean'], T((x0, y0, 0)) @ T((4.0, CREST, 4.05)), coll, level)

    xs = [x0 + COL_OFF, -4.0, 4.0, -x0 - COL_OFF]              # 8 m bays: doors fit between columns
    bz = RAIL - 0.455
    for x in xs:
        column(coll, pcs, T((x, y0 + COL_OFF, 0)), COL_H, bz, level=level)
        column(coll, pcs, T((x, -y0 - COL_OFF, 0)) @ Rz(pi), COL_H, bz, level=level)
        L = BLD_D - 2 * COL_OFF + 0.30
        KM.strip(coll, L, pcs['Beam_Segment'], cap=pcs['Beam_End'], axis='y', level=level,
                 base=T((x, y0 + COL_OFF - 0.15, COL_H + 0.40)), seg_len=2.0,
                 cap_len=KM.extent(pcs['Beam_End'], 'y'))
    for sx in (1, -1):
        for y in (-3.0, 3.0):
            column(coll, pcs, T((sx * (-x0 - COL_OFF), y, 0)) @ Rz(pi / 2 if sx > 0 else -pi / 2), COL_H,
                   guard=False, level=level)


def preview_renders(scene, pieces, quick=False):
    pcs = {pc.name: pc for pc in pieces}
    KM.hide_kit(pieces)
    props = KM.studio(scene, k=6.0, floor=160)
    spp = 32 if quick else 160
    res = (800, 500) if quick else (1600, 1000)
    out = lambda n: os.path.join(PREVIEW_DIR, f'building_{n}.png')        # noqa: E731

    coll = K.collection('PreviewBuilding')
    building(coll, pcs)
    KM.scale_figure(coll, (-5.2, -10.6, 0), 0.4)
    KM.scale_figure(coll, (-5.0, 2.0, 0), 2.2)
    cams = {
        'hero': ((21.0, -29.0, 2.2), (0.0, -4.0, 3.4), 30),
        'doors': ((-4.5, -19.5, 1.7), (-6.2, -9.0, 2.7), 30),
        'corner': ((14.3, -11.4, 1.4), (12.0, -9.0, 1.6), 26),
        'interior': ((-10.0, 7.0, 1.7), (3.0, -7.0, 4.6), 20),
        'column': ((-10.1, 1.9, 0.95), (-11.68, 3.0, 0.3), 30),
        'bracket': ((-2.6, -6.6, 4.7), (-4.0, -8.4, 5.4), 30),
    }
    for name, c in cams.items():
        KM.render(scene, KM.camera(scene, f'Cam_{name}', *c), out(name), res, spp)
    return props


# =============================================================================
# Main
# =============================================================================

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()

    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    os.makedirs(OUT_DIR, exist_ok=True)
    build_materials()
    pieces = build_kit()
    KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, weights=TEXEL_WEIGHTS)
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(scene, pieces, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[building] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
