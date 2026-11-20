"""
SimuLean kit: articulated truck for an unloading dock -- a 13.6 m curtain-side semi-trailer (the
curtain of one side drawn open to the ends) and a cab-over 4x2 tractor unit.  Generic design, no
brands or marks.  One atlas / material (TruckTrailer.mat).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/truck_trailer.py --
            [--bake 4096] [--save 2048] [--quick] [--cpu] [--no-render] [--look]

Outputs
    Assets/SimuLean.Unity/Art/Models/TruckTrailer/{SemiTrailer,TractorUnit}.fbx + TruckTrailer_*.png
    Tools/Blender/build/TruckTrailer.blend, Tools/Blender/previews/truck_*.png

Frame (kit rules): authored +X = Unity +X, authored +Y = Unity +Z (FORWARD), authored +Z = Unity +Y.
Pivot = floor centre of each vehicle's footprint.  Metres.  Unity position of (x, y, z) is (x, z, y).

SemiTrailer   13.86 L x 2.55 W x 4.00 H (loading length 13.6 m, inner width 2.48 m, inner height
              2.70 m), wooden deck top 1.27 m, 3 axles (1.31 m spacing, 385/65 R22.5 singles, r 0.53),
              landing legs, side underrun guards, contour marking.  Grey-blue curtains: the +X curtain
              (right-hand side in the travel direction) is drawn open and bunched at both ends, the
              stanchions removed (side loading by forklift); the -X curtain is closed; rear doors closed.
    Axle_1..3   (0, 0.53, -3.40 / -4.71 / -6.02): both wheels of an axle; spin about local X
    Deck        (0, 1.27, 0): centre of the loading floor (13.6 x 2.48 m); pallets go on it
    KingPin     (0, 1.15, 5.33): coupling point; put it on a TractorUnit's FifthWheel
TractorUnit   5.95 L x 2.50 W x 3.90 H (roof spoiler) cab-over 4x2, white cab, graphite bumper.
              Wheelbase 3.80 m; front single tyres, rear twins (r 0.53).
    Steer_FL / Steer_FR  (+-1.03, 0.53, 1.95): steering yaw about local Y
      '- Wheel_FL / Wheel_FR   spin about local X
    Axle_R      (0, 0.53, -1.85): rear twin wheels; spin about local X
    FifthWheel  (0, 1.15, -1.60): coupling plate centre; a SemiTrailer's KingPin goes here
LOD2 of each <= ~1.5 k triangles (wheels merged into the body).
"""

import os
import sys
import argparse
import time
from math import pi, radians

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
from mathutils import Matrix                # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import simulean_modular as KM               # noqa: E402
from simulean_kit import V, Part, NB, rrect, finish, finalize_material, fillet_closed  # noqa: E402
import load_units as LU                     # noqa: E402
import tugger_train as TT                   # noqa: E402  (Vehicle, LOD assembly, export, wheel)

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'TruckTrailer')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
PREFIX = 'TruckTrailer'

WR = 0.53                                   # wheel radius
TL, TW = 13.86, 2.55                        # trailer length / width
DECK = 1.27
ROOF = 4.0
KP = (0.0, 5.33, 1.15)
AXLES = (-3.40, -4.71, -6.02)
FW = (0.0, -1.60, 1.15)
MATS = TT.MATS
WEIGHTS = TT.WEIGHTS


# =============================================================================
# materials
# =============================================================================

def curtain(name, color):
    """PVC tarpaulin: vertical tension straps + buckles every 0.5 m, sag folds, grime low."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    pitch = 0.62
    f = nb.m('FRACT', nb.m('DIVIDE', y, pitch))
    strap = LU.band(nb, f, 0.5, 0.025 / pitch, 0.004 / pitch)
    buckle = LU.mul(nb, LU.band(nb, f, 0.5, 0.03 / pitch, 0.004 / pitch), LU.band(nb, z, 1.45, 0.05, 0.004))
    col = nb.mixc(nb.mr(nb.noise(0.8, 4), 0.3, 0.7), color, tuple(c * 1.15 for c in color))
    col = nb.mixc(strap, col, tuple(c * 0.55 for c in color))
    col = nb.mixc(buckle, col, (0.30, 0.30, 0.30))
    low = LU.mul(nb, nb.mr(z, 2.0, 1.25), nb.mr(nb.noise(3.0, 5, 0.6), 0.3, 0.8, 0.2, 0.9))
    col = nb.mixc(low, col, (0.06, 0.055, 0.05))
    streak = nb.mr(nb.noise(1.0, 5, 0.6, aniso=(1, 30, 1.2)), 0.5, 0.75, 0.0, 0.35)
    col = nb.mixc(streak, col, nb.multc(col, (0.7, 0.7, 0.7)))
    fold = nb.noise(1.0, 3, 0.5, aniso=(1, 9, 0.4))
    h = nb.m('ADD', nb.m('MULTIPLY', fold, 0.8), nb.m('MULTIPLY', strap, 0.4))
    nrm = nb.bump(h, 0.4, 0.01)
    return finalize_material(nb, col, nb.mixf(buckle, 0.5, 0.35), nb.m('MULTIPLY', buckle, 0.8), nrm)


def deck_wood(name):
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    boards = nb.m('FRACT', nb.m('DIVIDE', nb.m('ADD', x, 1.24), 0.155))
    joint = nb.mr(nb.m('MINIMUM', boards, nb.m('SUBTRACT', 1.0, boards)), 0.03, 0.01)
    grain = nb.noise(1.0, 6, 0.6, aniso=(40, 1.5, 40))
    col = nb.mixc(nb.mr(grain, 0.4, 0.7), (0.10, 0.06, 0.03), (0.17, 0.10, 0.05))
    col = nb.mixc(nb.mr(nb.noise(1.2, 5, 0.6), 0.4, 0.75, 0.0, 0.6), col, (0.07, 0.065, 0.06))
    col = nb.mixc(joint, col, (0.02, 0.015, 0.01))
    col = LU.common_dirt(nb, col, cav=0.4, dust=0.4)
    nrm = nb.bump(nb.m('SUBTRACT', nb.m('MULTIPLY', grain, 0.3), joint), 0.4, 0.002)
    return finalize_material(nb, col, 0.8, 0.0, nrm)


def contour(name):
    """Segmented retro-reflective contour marking (red / white along the sides)."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    seg = nb.m('LESS_THAN', nb.m('FRACT', nb.m('DIVIDE', y, 0.5)), 0.5)
    col = nb.mixc(seg, (0.70, 0.70, 0.70), (0.60, 0.02, 0.01))
    return finalize_material(nb, col, 0.25, 0.0, None)


def grille(name):
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    bars = nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', z, 0.05)), 0.5)), 0.3, 0.2)
    col = nb.mixc(bars, (0.01, 0.01, 0.011), (0.06, 0.062, 0.065))
    nrm = nb.bump(bars, 1.0, 0.006)
    return finalize_material(nb, col, nb.mixf(bars, 0.6, 0.35), 0.0, nrm)


def build_materials():
    TT.build_materials()
    MATS.update({
        'Curtain': curtain('Curtain', (0.040, 0.065, 0.11)),
        'DeckWood': deck_wood('DeckWood'),
        'Alu': M.dark_steel('Alu', color=(0.50, 0.51, 0.52), rough=0.35, metal=0.9),
        'White': M.paint('White', (0.72, 0.72, 0.70), rough=0.3, wear=0.35),
        'Chassis': M.paint('Chassis', (0.025, 0.026, 0.028), rough=0.5, wear=0.6),
        'Contour': contour('Contour'),
        'Grille': grille('Grille'),
        'Glass': M.glass('Glass'),
        'TruckTire': M.tire('TruckTire', WR - 0.003, lugs=40, chevron=4.0),
        'Mirror': M.mirror('Mirror'),
    })


bx = LU.bx
mk = TT.mk
tube = TT.tube


def truck_wheel(p, w=0.385, rim='Alu'):
    """22.5" wheel at the origin, axle along X."""
    h = w / 2
    r = WR
    pts = [(-h * 0.3, 0.0), (-h * 0.3, 0.09), (-h * 0.55, 0.12), (-h * 0.6, 0.26), (-h * 0.85, 0.29),
           (-h, 0.31), (-h, 0.46), (-h * 0.85, r - 0.008), (0.0, r), (h * 0.85, r - 0.008), (h, 0.46),
           (h, 0.31), (h * 0.85, 0.29), (h * 0.6, 0.26), (h * 0.55, 0.12), (h * 0.3, 0.09), (h * 0.3, 0.0)]
    mats = [rim, rim, rim, rim, 'TruckTire', 'TruckTire', 'TruckTire', 'TruckTire', 'TruckTire', 'TruckTire',
            'TruckTire', 'TruckTire', rim, rim, rim, rim]
    p.lathe(pts, 24, rim, axis='X', mats=mats, cap0=False, cap1=False)


# =============================================================================
# semi-trailer
# =============================================================================

def build_trailer():
    y0, y1 = -TL / 2, TL / 2
    xw = TW / 2

    def chassis(p):
        side = [(y0, 0.86), (3.5, 0.86), (4.6, 1.10), (y1 - 0.2, 1.10), (y1 - 0.2, 1.15), (y0, 1.15)]
        for s in (1, -1):
            p.extrude(side, 'X', s * 0.43, s * 0.55, 'Chassis')
        for k in range(14):
            yy = y0 + 0.4 + k * 0.97
            bx(p, -1.22, 1.22, yy - 0.04, yy + 0.04, 1.12, 1.235, 'Chassis')
        bx(p, -1.0, 1.0, KP[1] - 0.6, y1 - 0.2, 1.135, 1.15, 'DarkSteel')                 # kingpin plate
        p.cylinder((0, KP[1], 1.08), (0, KP[1], 1.15), 0.045, 'DarkSteel', n=12)
        for yy in AXLES:                                                                 # axles + springs
            p.cylinder((-0.95, yy, WR), (0.95, yy, WR), 0.07, 'Chassis', n=10)
            for s in (1, -1):
                bx(p, s * 0.42, s * 0.56, yy - 0.55, yy + 0.30, 0.62, 0.86, 'Chassis')
                p.cylinder((s * 0.6, yy - 0.2, 0.62), (s * 0.6, yy - 0.2, 0.86), 0.12, 'Black', n=12)   # air bag
        for s in (1, -1):                                                                # landing legs
            bx(p, s * 0.62, s * 0.74, 4.35, 4.47, 0.12, 1.10, 'Chassis')
            bx(p, s * 0.58, s * 0.78, 4.31, 4.51, 0.0, 0.03, 'Chassis')
        bx(p, -0.74, 0.74, 4.37, 4.45, 0.75, 0.80, 'Chassis')
        for s in (1, -1):                                                                # underrun guards
            for z in (0.48, 0.70):
                bx(p, s * 1.20, s * 1.24, -2.70, 3.95, z, z + 0.06, 'Alu')
            for yy in (-2.6, 0.6, 3.8):
                bx(p, s * 1.0, s * 1.22, yy - 0.03, yy + 0.03, 0.55, 1.12, 'Chassis')
        bx(p, -1.12, 1.12, y0 - 0.06, y0 + 0.06, 0.42, 0.58, 'Chassis')                  # rear underrun bar
        for s in (1, -1):
            bx(p, s * 0.18, s * 0.30, y0 + 0.02, y0 + 0.5, 0.58, 1.12, 'Chassis')
        for s in (1, -1):                                                                # mudguards
            bx(p, s * 0.76, s * 1.26, -6.75, -2.75, 1.12, 1.15, 'Black')
            bx(p, s * 0.80, s * 1.24, -6.80, -6.76, 0.25, 1.12, 'Black')

    def deck(p):
        bx(p, -1.24, 1.24, y0 + 0.08, y1 - 0.06, 1.235, DECK, 'DeckWood')

    def rails(p):
        for s in (1, -1):
            bx(p, s * 1.22, s * xw, y0, y1, 1.10, DECK + 0.01, 'Alu')                     # bottom rail
            bx(p, s * (xw - 0.002), s * (xw + 0.002), y0 + 0.2, y1 - 0.2, 1.13, 1.18, 'Contour')
            bx(p, s * 1.22, s * xw, y0, y1, ROOF - 0.15, ROOF - 0.03, 'Alu')               # top rail
            for yy in (-5.6, -3.4, -1.2, 1.0, 3.2, 5.4):                                 # side marker lights
                bx(p, s * xw, s * (xw + 0.012), yy - 0.04, yy + 0.04, 1.14, 1.20, 'LensA')
        bx(p, -xw, xw, y0, y1, ROOF - 0.03, ROOF, 'Curtain')                             # roof tarp

    def ends(p):
        bx(p, -xw, xw, y1 - 0.06, y1, DECK, ROOF - 0.03, 'White')                         # headboard
        for s in (1, -1):
            bx(p, s * 1.16, s * xw, y0, y0 + 0.08, DECK, ROOF - 0.03, 'Alu')               # rear posts
            bx(p, s * 1.16, s * xw, y1 - 0.14, y1 - 0.06, DECK, ROOF - 0.03, 'Alu')        # front posts
        bx(p, -1.16, 1.16, y0, y0 + 0.08, ROOF - 0.32, ROOF - 0.03, 'Alu')                 # rear header
        for s in (1, -1):                                                                # rear doors
            bx(p, s * 0.004, s * 1.16, y0 - 0.002, y0 + 0.04, DECK + 0.02, ROOF - 0.32, 'White')
            for xx in (0.30, 0.86):
                tube(p, (s * xx, y0 - 0.03, DECK + 0.05), (s * xx, y0 - 0.03, ROOF - 0.35), 0.016, 'Alu', n=8)
        for s in (1, -1):                                                                # rear lights
            bx(p, s * 0.70, s * 1.15, y0 - 0.08, y0 - 0.06, 0.95, 1.07, 'LensR')
            bx(p, s * 0.95, s * 1.12, y0 - 0.081, y0 - 0.075, 0.97, 1.05, 'LensA')
        bx(p, -1.0, 1.0, y0 - 0.07, y0 - 0.06, 0.62, 1.08, 'Contour')

    def closed_curtain(p):
        n = 56
        ys = [y0 + 0.08 + (TL - 0.22) * i / n for i in range(n + 1)]
        xo = [-(xw - 0.01) - (0.012 if i % 2 else 0.0) for i in range(n + 1)]
        for zb, zt in ((DECK - 0.02, ROOF - 0.15),):
            outer = [[V((xo[i], ys[i], zb)) for i in range(n + 1)], [V((xo[i], ys[i], zt)) for i in range(n + 1)]]
            vs = outer[0] + outer[1]
            faces = [(i, i + 1, n + 2 + i, n + 1 + i) for i in range(n)]
            p.add(vs, faces, 'Curtain', recalc=False)
            inner = [V((v.x + 0.008, v.y, v.z)) for v in vs]
            p.add(inner, [tuple(reversed(f)) for f in faces], 'Curtain', recalc=False)

    def bunched(p):
        """The +X curtain drawn open: two gathered bundles, each a few deep folds."""
        for ya, yb in ((y1 - 0.70, y1 - 0.15), (y0 + 0.15, y0 + 0.65)):
            n = 9
            pts = []
            for i in range(n + 1):
                yy = ya + (yb - ya) * i / n
                pts.append((xw - 0.02 - (0.16 if i % 2 else 0.0), yy))
            ring_b = [V((x_, y_, DECK - 0.02)) for x_, y_ in pts]
            ring_t = [V((x_, y_, ROOF - 0.15)) for x_, y_ in pts]
            vs = ring_b + ring_t
            faces = [(i + 1, i, n + 1 + i, n + 2 + i) for i in range(n)]
            p.add(vs, faces, 'Curtain', recalc=False)
            p.add([V((v.x - 0.006, v.y, v.z)) for v in vs], [tuple(reversed(f)) for f in faces], 'Curtain',
                  recalc=False)

    parts = [mk('TR_Chassis', chassis, 0.004, 1, weight=0.4), mk('TR_Deck', deck, 0.0, weight=0.35),
             mk('TR_Rails', rails, 0.004, 1, weight=0.35), mk('TR_Ends', ends, 0.004, 1, weight=0.4),
             mk('TR_Curtain', closed_curtain, 0.0, weight=0.3), mk('TR_Bunched', bunched, 0.0, weight=0.4)]
    nodes = {None: parts}
    pivots, plan, merge = {}, {p.name: (0.35, 0.10) for p in parts}, []
    plan['TR_Curtain'] = (1.0, 0.5)
    plan['TR_Deck'] = (1.0, 1.0)
    for i, yy in enumerate(AXLES, 1):
        n = f'Axle_{i}'
        ws = [mk(f'TR_{n}{s}', lambda p: truck_wheel(p), 0.0, weight=0.35, loc=(sx * 1.02, yy, WR))
              for s, sx in (('L', -1), ('R', 1))]
        nodes[n] = ws
        pivots[n] = (0, yy, WR)
        for w in ws:
            plan[w.name] = (0.35, 0.08)
        merge.append(n)
    sockets = {'Deck': (0, 0, DECK), 'KingPin': KP}
    return TT.Vehicle('SemiTrailer', nodes, pivots, {}, sockets, plan, merge)


# =============================================================================
# tractor unit
# =============================================================================

def build_tractor():
    yr, yf = -2.95, 2.95
    yc0 = 0.45                                       # cab rear

    def cab(p):
        rings = []
        for z, k, dy in ((1.05, 1.0, 0.0), (1.95, 1.0, 0.0), (2.10, 1.0, 0.02), (2.95, 0.985, 0.14),
                         (3.25, 0.96, 0.20)):
            L = (yf - dy - 0.12) - yc0
            rings.append([V((u, yc0 + L / 2 + v, z)) for u, v in rrect(2.48 * k, L, 0.18, 4)])
        p.loft(rings, 'White')

    def cab_front(p):
        bx(p, -1.0, 1.0, yf - 0.125, yf - 0.105, 1.30, 1.90, 'Grille')            # flush on the cab front
        wz = (2.14, 2.92)                                                          # windscreen on the slope
        wy = [yf - 0.12 - 0.02 - (0.12 * (z - 2.10) / 0.85) + 0.012 for z in wz]
        for s0, s1 in ((-1.10, 1.10),):
            v = [V((s0, wy[0], wz[0])), V((s1, wy[0], wz[0])), V((s1, wy[1], wz[1])), V((s0, wy[1], wz[1]))]
            p.add(v + [V((q.x, q.y - 0.01, q.z)) for q in v],
                  [(3, 2, 1, 0), (4, 5, 6, 7), (0, 1, 5, 4), (1, 2, 6, 5), (2, 3, 7, 6), (3, 0, 4, 7)], 'Glass')
        for s in (1, -1):
            bx(p, s * 1.241, s * 1.25, 1.6, 2.4, 2.0, 2.85, 'Glass')                     # door windows
            bx(p, s * 1.20, s * 1.25, 1.55, 2.45, 1.15, 1.9, 'White')                     # door
            bx(p, s * 1.0, s * 1.22, 1.45, 2.4, 0.45, 0.50, 'Black')                      # steps
            bx(p, s * 1.0, s * 1.22, 1.45, 2.4, 0.80, 0.85, 'Black')
            tube(p, (s * 1.27, 2.55, 2.35), (s * 1.42, 2.65, 2.35), 0.02, 'Black', n=8)  # mirror arm
            p.box((s * 1.45, 2.62, 2.55), (0.05, 0.10, 0.42), 'Black')
            bx(p, s * 1.425, s * 1.426, 2.60, 2.66, 2.36, 2.74, 'Mirror')
        bx(p, -1.25, 1.25, yf - 0.20, yf, 0.55, 1.05, 'Chassis')                         # bumper
        for s in (1, -1):
            bx(p, s * 0.75, s * 1.15, yf + 0.0, yf + 0.01, 0.80, 0.95, 'LensW')
        bx(p, -1.15, 1.15, yf - 0.30, yf - 0.06, 2.98, 3.03, 'Black')                    # sun visor
        p.loft([[V((u, 1.2 + v, 3.25)) for u, v in rrect(2.3, 1.4, 0.15, 2)],
                [V((u, 1.0 + v, 3.90)) for u, v in rrect(2.3, 1.0, 0.15, 2)]], 'White')   # roof spoiler

    def chassis(p):
        for s in (1, -1):
            bx(p, s * 0.40, s * 0.50, yr, 2.6, 0.72, 1.02, 'Chassis')
        for yy in (-2.8, -1.0, 0.6, 2.4):
            bx(p, -0.40, 0.40, yy - 0.04, yy + 0.04, 0.80, 0.98, 'Chassis')
        bx(p, -1.25, 1.25, yr, yr + 0.08, 0.45, 0.60, 'Chassis')                          # rear bar
        p.loft([[V((u, FW[1] + v, 1.10)) for u, v in K.circle(0.45, 20)],
                [V((u, FW[1] + v, FW[2])) for u, v in K.circle(0.45, 20)]], 'Black')        # fifth wheel
        bx(p, -0.06, 0.06, FW[1] - 0.45, FW[1] - 0.05, FW[2] - 0.004, FW[2] + 0.001, 'DarkSteel')
        p.loft([[V((0.98 + u, -0.15 + v, 0.75)) for u, v in rrect(0.45, 1.1, 0.12, 3)],
                [V((0.98 + u, -0.15 + v, 1.20)) for u, v in rrect(0.45, 1.1, 0.12, 3)]], 'Alu')   # fuel tank
        bx(p, -1.22, -0.70, -0.65, 0.25, 0.65, 1.15, 'Chassis')                            # battery box
        tube(p, (1.05, 0.30, 1.1), (1.05, 0.30, 3.2), 0.07, 'Chassis', n=12)              # exhaust stack
        for s in (1, -1):                                                                # rear fenders
            bx(p, s * 0.55, s * 1.28, -2.45, -1.25, 1.12, 1.16, 'Black')
            bx(p, s * 0.58, s * 1.25, -2.49, -2.45, 0.35, 1.16, 'Black')
            bx(p, s * 0.80, s * 1.25, 1.50, 2.40, 1.06, 1.10, 'Black')                    # front arches

    parts = [mk('TU_Cab', cab, 0.01, 2, weight=0.6), mk('TU_Front', cab_front, 0.004, 1, weight=0.7),
             mk('TU_Chassis', chassis, 0.004, 1, weight=0.45)]
    plan = {'TU_Cab': (0.35, 0.12), 'TU_Front': (0.35, 0.08), 'TU_Chassis': (0.3, 0.06)}
    nodes, pivots, parents, merge = {None: parts}, {}, {}, []
    for side, sx in (('L', -1), ('R', 1)):
        n = f'Wheel_F{side}'
        nodes[n] = [mk(f'TU_{n}', lambda p: truck_wheel(p, 0.315), 0.0, weight=0.4, loc=(sx * 1.03, 1.95, WR))]
        pivots[n] = (sx * 1.03, 1.95, WR)
        parents[n] = f'Steer_F{side}'
        plan[f'TU_{n}'] = (0.35, 0.08)
        merge.append(n)
    rw = []
    for s, x in (('LO', -1.08), ('LI', -0.75), ('RI', 0.75), ('RO', 1.08)):
        rw.append(mk(f'TU_Wheel_{s}', lambda p: truck_wheel(p, 0.315), 0.0, weight=0.4, loc=(x, -1.85, WR)))
        plan[rw[-1].name] = (0.35, 0.08)
    nodes['Axle_R'] = rw
    pivots['Axle_R'] = (0, -1.85, WR)
    merge.append('Axle_R')
    sockets = {'Steer_FL': (-1.03, 1.95, WR), 'Steer_FR': (1.03, 1.95, WR), 'FifthWheel': FW}
    return TT.Vehicle('TractorUnit', nodes, pivots, parents, sockets, plan, merge)


# =============================================================================
# main
# =============================================================================

def preview_renders(units, quick):
    scene = bpy.context.scene
    by = {u.name: u for u in units}
    for u in units:
        for o in [u.piece.root] + list(u.piece.root.children_recursive):
            o.hide_render = True
    KM.studio(scene, k=4.0, floor=200)
    spp = 32 if quick else 128
    res = (1000, 625) if quick else (1800, 1100)
    out = lambda n: os.path.join(PREVIEW_DIR, f'truck_{n}.png')         # noqa: E731
    T = Matrix.Translation
    pal = TT.load_unit_mesh('PalletLoad_Cartons') if hasattr(TT, 'load_unit_mesh') else None
    R180 = Matrix.Rotation(pi, 4, 'Z')

    def rig(coll, level):
        tr = by['SemiTrailer']
        tu = by['TractorUnit']
        base_tr = Matrix.Identity(4)
        KM.place(tr.piece, base_tr, coll, level)
        kp = V(tr.auth_sockets['KingPin'])
        fw = V(tu.auth_sockets['FifthWheel'])
        KM.place(tu.piece, T(kp - fw), coll, level)
        if pal:
            for k in range(8):
                for xx in (-0.41, 0.41):
                    c = pal.copy()
                    coll.objects.link(c)
                    c.parent = None
                    c.matrix_world = T((xx, 6.0 - 1.25 - k * 1.25 + 0.6, DECK)) @ Matrix.Rotation(pi / 2, 4, 'Z') @ R180
    coll = K.collection('PreviewTruck')
    rig(coll, 0)
    KM.scale_figure(coll, (3.0, -3.0, 0), 2.0)
    hero = ((11.0, -12.0, 4.5), (0.0, -0.5, 2.0), 30)
    TT._render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    TT._render(scene, KM.camera(scene, 'Cam_Front', (-7.5, 16.0, 3.0), (0.0, 7.0, 1.8), 32), out('front'), res, spp)
    TT._render(scene, KM.camera(scene, 'Cam_Rear', (-6.0, -14.0, 2.5), (0.0, -6.0, 1.8), 32), out('rear'), res, spp)
    coll.hide_render = True
    for lvl in (1, 2):
        c = K.collection(f'PreviewTruck_LOD{lvl}')
        rig(c, lvl)
        TT._render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (1000, 625), spp)
        c.hide_render = True


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--look', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene, backends=('CUDA', 'OPTIX', 'HIP', 'ONEAPI', 'METAL'))
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    build_materials()
    units = [build_trailer(), build_tractor()]
    if args.look:
        KM.studio(scene, k=4.0, floor=200)
        for o in units[1].parts:
            o.location.x += 4.0
        for u in units:
            for ps in u.piece.nodes.values():
                for o in ps:
                    if o not in u.parts:
                        o.location.x += 4.0 if u is units[1] else 0.0
        KM.render(scene, KM.camera(scene, 'Cam_Look', (11.0, 12.0, 3.2), (3.0, 3.0, 1.8), 30),
                  os.path.join(PREVIEW_DIR, 'truck_look.png'), (1600, 900), 24 if args.quick else 96)
        return
    TT.OUT_DIR, TT.PREFIX, TT.SPACING = OUT_DIR, PREFIX, 20.0
    TT.bake(units, args.bake, args.save)
    TT.build_lods(units)
    pieces = [u.piece for u in units]
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    TT.export(units)
    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(units, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[truck] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
