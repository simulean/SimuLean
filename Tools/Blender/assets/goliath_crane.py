"""
SimuLean asset: travelling-leg gantry cranes for the shipbuilding library — the dock's Goliath crane (block erection,
heavy lifts) and the panel line's welding gantry.  Both are crane resources (UnityResourceTeam, Axes travel) driven
by CraneResourceView through TravellingGantryRig (architecture §4.2): the bridge with its legs travels on two ground
rails, the trolley along the girder, the head hangs on ropes (Goliath) / slides on a mast (welding gantry).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/goliath_crane.py -- --variant goliath|welding
            [--bake 4096] [--save 2048] [--no-render] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/GoliathCrane/GoliathCrane.fbx      (--variant goliath, + 5 PNG maps)
    Assets/SimuLean.Unity/Art/Models/WeldingGantry/WeldingGantry.fbx    (--variant welding, + 5 PNG maps)
    Tools/Blender/build/<Name>.blend, Tools/Blender/previews/<name>_*.png

Real-world reference: shipyard Goliath gantry cranes (A-frame legs on bogies, twin box girders, a trolley on top with
the hoist winches, the hook block between the girders, a machinery house at the top of one leg) and panel-line
welding gantries (a light portal on rails, a welding carriage with wire feeders and a torch slide).  Generic design
in the library palette, no third-party marks.
    Goliath: span 40 m (rail centres), girder underside 26 m, rails modelled 60 m long, spreader beam under the hook.
    Welding gantry: span 12 m, girder underside 3.2 m, rails modelled 24 m long.

Frame: +Z up (Unity +Y), the runway along Y (Unity Z), the span along X; origin on the floor at the centre.

Animation hierarchy (Unity names).  Every name is a bare Transform; its meshes hang under it as
<name>_LOD0/_LOD1/_LOD2 with identity local transforms.
    <Name>               root
    |- Rail_W / Rail_E   ground rails at x = -/+ span/2, modelled `rail length` long (stretched along Z by the rig)
    '- Bridge            travels along the runway (Unity local Z)
        |- Leg_W / Leg_E legs, sill beams and bogies at x = -/+ span/2 (moved by the rig for another span)
        |- Girder        girder(s), pivot at the centre (stretched along X by the rig)
        '- Trolley       travels along the girder (Unity local X)
            |- Ropes     hoist ropes / torch mast: pivot at the top, modelled at the rest length (scaled along Y)
            '- Head      hook block + spreader / torch head: moves down
                '- HookSocket  where a load's top hangs (spreader underside) / the torch tip
"""

import os
import sys
import argparse
import time

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import V, Part, finish    # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')

VARIANTS = {
    'goliath': dict(name='GoliathCrane', span=40.0, girder_z=26.0, girder_h=4.0, rail_len=60.0, paint=(0.80, 0.52, 0.02),
                    twin=True, girder_w=1.6, girder_gap=2.4, overhang=3.0, leg_w=1.5, sill_len=13.0, rest_drop=6.0,
                    wheel_r=0.4, bevel=0.04),
    'welding': dict(name='WeldingGantry', span=12.0, girder_z=3.2, girder_h=0.8, rail_len=24.0, paint=(0.08, 0.20, 0.42),
                    twin=False, girder_w=0.8, girder_gap=0.0, overhang=0.6, leg_w=0.45, sill_len=2.4, rest_drop=1.4,
                    wheel_r=0.18, bevel=0.012),
}

C = {}          # the chosen variant
MATS = {}


def build_materials():
    MATS.update({
        'Paint': M.paint('Paint', C['paint'], rough=0.36, wear=0.6),
        'Hazard': M.paint('Hazard', (0.80, 0.52, 0.02), rough=0.36, wear=0.7,
                          color_fn=M.hazard_fn((0.80, 0.52, 0.02), width=0.15 if C['twin'] else 0.05, axis=('x', 'z'))),
        'White': M.paint('White', (0.62, 0.62, 0.60), rough=0.4, wear=0.5),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Frame': M.dark_steel('Frame', color=(0.07, 0.07, 0.075), rough=0.5),
        'Galv': M.galvanised('Galv'),
        'Rail': M.dark_steel('Rail', color=(0.30, 0.30, 0.31), rough=0.3, metal=0.95),
        'Concrete': M.concrete('Concrete'),
        'Rope': M.dark_steel('Rope', color=(0.12, 0.12, 0.12), rough=0.4, metal=0.8),
        'Chrome': M.chrome('Chrome'),
        'Glass': M.glass('Glass'),
        'LensA': M.lens('LensA', (1.0, 0.45, 0.02)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'LensW': M.lens('LensW', (1.0, 0.93, 0.80)),
        'Copper': M.paint('Copper', (0.55, 0.25, 0.08), rough=0.3, wear=0.2),
    })


def mk(name, fn, bevel=None, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, C['bevel'] if bevel is None else bevel, seg, 40.0, 40.0)
    return ob


def column(p, a, b, w0, w1, mat):
    """A tapered square column from point a (width w0) to point b (width w1)."""
    def ring(c, w):
        h = w / 2
        return [V((c[0] - h, c[1] - h, c[2])), V((c[0] + h, c[1] - h, c[2])),
                V((c[0] + h, c[1] + h, c[2])), V((c[0] - h, c[1] + h, c[2]))]
    p.loft([ring(a, w0), ring(b, w1)], mat)


# =============================================================================
# Fixed: the rails
# =============================================================================

def build_rail(name, x):
    L = C['rail_len']
    def f(p):
        p.box((x, 0, 0.06), (1.2 if C['twin'] else 0.5, L, 0.12), 'Concrete')
        p.box((x, 0, 0.14), (0.12 if C['twin'] else 0.06, L, 0.08), 'Rail')
        for y in (-L / 2 + 0.3, L / 2 - 0.3):                     # end stops
            p.box((x, y, 0.45 if C['twin'] else 0.2), (0.8 if C['twin'] else 0.3, 0.4, 0.8 if C['twin'] else 0.3), 'Hazard')
    return mk(name, f, bevel=0.01)


# =============================================================================
# Bridge: legs, girder, trolley, ropes, head
# =============================================================================

RAIL_TOP = 0.18


def lerp(a, b, f):
    return tuple(a[i] + (b[i] - a[i]) * f for i in range(3))


def ladder(p, x, y, z0, z1, cage=True):
    """A fixed ladder along Z at (x, y): two stiles, rungs every 0.3 m and (tall ladders) a back cage."""
    for dy in (-0.22, 0.22):
        p.box((x, y + dy, (z0 + z1) / 2), (0.06, 0.05, z1 - z0), 'Galv')
    n = int((z1 - z0) / 0.3)
    for k in range(1, n):
        p.box((x, y, z0 + k * 0.3), (0.03, 0.44, 0.03), 'Galv')
    if cage:
        s = 1 if x > 0 else -1
        for k in range(int((z1 - z0 - 2.5) / 1.2) + 1):
            z = z0 + 2.5 + k * 1.2
            p.box((x + s * 0.75, y, z), (0.04, 0.8, 0.05), 'Galv')
            for dy in (-0.4, 0.4):
                p.box((x + s * 0.375, y + dy, z), (0.75, 0.04, 0.05), 'Galv')
        for dy in (-0.3, 0.0, 0.3):
            p.box((x + s * 0.75, y + dy, (z0 + 2.5 + z1) / 2), (0.03, 0.03, z1 - z0 - 2.5), 'Galv')


def railing(p, x0, x1, y, z, posts=2.0):
    """A handrail along X at (y, z): posts, mid rail, top rail, toe board."""
    L = x1 - x0
    p.box(((x0 + x1) / 2, y, z + 1.1), (L, 0.05, 0.05), 'Galv')
    p.box(((x0 + x1) / 2, y, z + 0.55), (L, 0.04, 0.04), 'Galv')
    p.box(((x0 + x1) / 2, y, z + 0.08), (L, 0.02, 0.15), 'Galv')
    for k in range(int(L / posts) + 1):
        p.box((x0 + k * L / int(L / posts), y, z + 0.55), (0.05, 0.05, 1.1), 'Galv')


def build_leg(name, x, side):
    gz, gh, lw, sl, wr = C['girder_z'], C['girder_h'], C['leg_w'], C['sill_len'], C['wheel_r']
    def f(p):
        sill_z = RAIL_TOP + 2 * wr + (0.5 if C['twin'] else 0.12)
        # bogies: wheel pairs under equaliser beams, flanged wheels on the rail
        for y in ((-sl / 2 + 2.0, sl / 2 - 2.0) if C['twin'] else (-sl / 2 + 0.4, sl / 2 - 0.4)):
            p.box((x, y, RAIL_TOP + wr + 0.1), (lw * 0.8, 3.2 if C['twin'] else 0.7, 2 * wr), 'Frame')
            if C['twin']:
                p.box((x, y, sill_z - 0.55), (lw * 0.7, 1.6, 0.5), 'Frame')         # equaliser pin block
                for sx in (-1, 1):
                    p.box((x + sx * lw * 0.42, y, RAIL_TOP + wr + 0.1), (0.05, 3.0, 2 * wr - 0.1), 'Hazard')
            for dy in ((-0.9, 0.9) if C['twin'] else (-0.2, 0.2)):
                c = (x, y + dy, RAIL_TOP + wr)
                p.lathe([(-0.13, wr), (-0.09, wr), (-0.09, wr + 0.05), (0.13, wr + 0.05)], 24, 'DarkSteel', center=c, axis='X')
        p.box((x, 0, sill_z), (lw, sl, 1.2 if C['twin'] else 0.3), 'Paint')
        if C['twin']:
            pts = []
            for sy in (-1, 1):
                a = (x, sy * (sl / 2 - 1.2), sill_z + 0.5)
                b = (x, sy * (C['girder_gap'] / 2 + C['girder_w'] / 2), gz)
                column(p, a, b, lw * 1.1, lw * 1.4, 'Paint')
                pts.append((a, b))
                # diaphragm bands round the column
                for fr in (0.2, 0.4, 0.6, 0.8):
                    c = lerp(a, b, fr)
                    w = lw * (1.1 + 0.3 * fr) + 0.1
                    p.box(c, (w, w, 0.22), 'Paint')
            for fr in (0.42, 0.75):                                              # cross ties between the columns
                ca, cb = lerp(*pts[0], fr), lerp(*pts[1], fr)
                p.box((x, 0, ca[2]), (lw * 0.6, cb[1] - ca[1], 0.8), 'Paint')
            # the ladder up the leg (between the columns), rest platforms at the ties
            lx = x + side * (lw * 0.55 + 0.25)
            ladder(p, lx, 0.0, sill_z + 0.6, gz + gh)
            for fr in (0.42, 0.75):
                z = lerp(*pts[0], fr)[2] - 0.4
                p.box((lx + side * 0.2, 0, z), (1.2, 2.0, 0.06), 'Galv')
            # the power cable reel beside the west leg
            if side < 0:
                c = (x + side * (lw * 0.5 + 0.9), sl / 2 - 2.8, sill_z + 1.4)
                p.lathe([(-0.55, 1.5), (-0.45, 1.5), (-0.45, 0.6), (0.45, 0.6), (0.45, 1.5), (0.55, 1.5)], 32, 'Paint',
                        center=c, axis='X', mats=['Paint', 'Paint', 'Rope', 'Paint', 'Paint'])
                p.box((c[0], c[1], sill_z + 0.4), (0.9, 1.0, 1.0), 'Frame')
            # machinery house on top of the west leg, the leg head
            if side < 0:
                hx, hz = x - 2.5, gz + gh + 1.6
                p.box((hx, 0, hz), (6.0, 6.0, 3.2), 'White')
                p.box((hx, -3.01, hz - 0.3), (1.0, 0.04, 2.2), 'Frame')                  # door
                p.box((hx + 1.8, -3.01, hz + 0.5), (1.6, 0.03, 0.8), 'Glass')
                for k in range(6):                                                    # louvres
                    p.box((hx - 3.01, -1.5 + k * 0.6, hz), (0.04, 0.4, 1.8), 'DarkSteel')
                p.box((hx + 1.0, 1.0, hz + 1.9), (1.8, 1.4, 0.6), 'Galv')                # roof unit
                p.box((hx, 0, hz + 1.62), (6.2, 6.2, 0.06), 'Frame')
            p.box((x, 0, gz + gh * 0.5), (lw * 1.3, C['girder_gap'] + 2 * C['girder_w'] + 0.6, gh), 'Paint')   # leg head
            p.cylinder((x, 0, gz + gh), (x, 0, gz + gh + 0.4), 0.15, 'LensA', n=12)
        else:
            column(p, (x, 0, sill_z), (x, 0, gz + gh), lw, lw, 'Paint')
            p.box((x, 0, sill_z + 0.25), (lw + 0.12, lw + 0.12, 0.12), 'Frame')
            p.box((x - side * 0.6, 0, 1.2), (0.6, 0.5, 1.0), 'Frame')         # drive / control cabinet
            p.box((x - side * 0.6, -0.26, 1.35), (0.3, 0.01, 0.2), 'Glass')
            p.cylinder((x - side * 0.5, -0.26, 1.05), (x - side * 0.5, -0.29, 1.05), 0.03, 'LensR', n=10)
            p.cylinder((x - side * 0.7, -0.26, 1.05), (x - side * 0.7, -0.29, 1.05), 0.03, 'LensA', n=10)
    return mk(name, f)


def build_girder():
    s, gz, gh, gw, gap, oh = C['span'], C['girder_z'], C['girder_h'], C['girder_w'], C['girder_gap'], C['overhang']
    L = s + 2 * oh
    def f(p):
        ys = (-(gap + gw) / 2, (gap + gw) / 2) if C['twin'] else (0.0,)
        for y in ys:
            p.box((0, y, gz + gh / 2), (L, gw, gh), 'Paint')
            p.box((0, y, gz + gh + 0.06), (L - 0.4, 0.14 if C['twin'] else 0.08, 0.12), 'Rail')     # trolley rail
            out = 1 if y > 0 else -1
            if C['twin']:
                # stiffeners on the outer web, the walkway with its railing, buffers at the ends
                for k in range(int(L / 2.5) + 1):
                    p.box((-L / 2 + 0.3 + k * (L - 0.6) / int(L / 2.5), y + out * (gw / 2 + 0.03), gz + gh / 2),
                          (0.12, 0.06, gh - 0.3), 'Paint')
                o = y + out * (gw / 2 + 0.55)
                p.box((0, o, gz + gh - 0.05), (L - 1.0, 1.1, 0.06), 'Galv')
                for k in range(int((L - 1) / 3) + 1):                               # walkway brackets
                    p.box((-L / 2 + 0.5 + k * (L - 1) / int((L - 1) / 3), o, gz + gh - 0.35), (0.1, 1.0, 0.5), 'Frame')
                railing(p, -L / 2 + 0.5, L / 2 - 0.5, o + out * 0.52, gz + gh - 0.02)
                for sx in (-1, 1):
                    p.box((sx * (L / 2 - 0.4), y, gz + gh + 0.4), (0.4, 0.5, 0.6), 'Hazard')
            else:
                # a cable tray along the girder and the end stops of the carriage
                p.box((0, y + gw / 2 + 0.1, gz + gh - 0.15), (L - 0.4, 0.2, 0.08), 'Galv')
                for sx in (-1, 1):
                    p.box((sx * (L / 2 - 0.25), y, gz + gh + 0.15), (0.12, 0.3, 0.2), 'Hazard')
        if C['twin']:
            for k in range(int(L // 8) + 1):                                    # cross bracing between the girders
                p.box((-L / 2 + 1 + k * (L - 2) / int(L // 8), 0, gz + 0.4), (0.3, gap, 0.4), 'Frame')
    return mk('Girder', f)


def build_trolley():
    gz, gh = C['girder_z'], C['girder_h']
    top = gz + gh + 0.12
    def f(p):
        if C['twin']:
            gy = (C['girder_gap'] + C['girder_w']) / 2
            W = C['girder_gap'] + 2 * C['girder_w'] + 1.0
            # end carriages on the girder rails, the frame across them
            for sx in (-1, 1):
                for sy in (-1, 1):
                    p.box((sx * 2.6, sy * gy, top + 0.4), (1.2, 0.9, 0.8), 'Frame')
                    p.lathe([(-0.1, 0.32), (0.1, 0.32)], 16, 'DarkSteel', center=(sx * 2.6, sy * gy, top + 0.32), axis='Y')
            p.box((0, 0, top + 1.0), (6.4, W, 0.5), 'Paint')
            # two hoist drums with their gearbox and motor, the winch house beside them
            for dx in (-1.6, 0.2):
                p.lathe([(-1.8, 0.85), (-1.7, 0.85), (-1.7, 0.6), (1.7, 0.6), (1.7, 0.85), (1.8, 0.85)], 28, 'DarkSteel',
                        center=(dx, 0, top + 2.1), axis='Y', mats=['DarkSteel', 'DarkSteel', 'Rope', 'DarkSteel', 'DarkSteel'])
                for sy in (-1, 1):
                    p.box((dx, sy * 1.9, top + 1.7), (1.9, 0.2, 1.4), 'Paint')       # drum bearings
            p.box((-0.7, 2.6, top + 1.9), (1.4, 0.8, 1.2), 'Frame')                 # gearbox
            p.cylinder((-0.7, 3.0, top + 1.9), (-0.7, 3.5, top + 1.9), 0.4, 'Paint', n=16)   # motor
            p.box((2.3, 0, top + 2.4), (1.6, W - 0.4, 2.3), 'White')
            p.box((2.3, -W / 2 + 0.19, top + 2.2), (0.8, 0.03, 1.5), 'Frame')
            railing(p, -3.1, 3.1, -W / 2 + 0.05, top + 1.25, posts=1.5)
            railing(p, -3.1, 3.1, W / 2 - 0.05, top + 1.25, posts=1.5)
            p.cylinder((2.8, 2.6, top + 3.55), (2.8, 2.6, top + 3.95), 0.12, 'LensA', n=12)
        else:
            # welding carriage beside the girder: frame on its wheels, wire-feed spools on a stand, the control box
            p.box((0, -0.75, gz + gh / 2), (1.1, 0.6, gh + 0.5), 'Paint')
            p.box((0, -0.3, top + 0.15), (1.1, 1.2, 0.3), 'Paint')
            for sx in (-0.4, 0.4):
                p.lathe([(-0.04, 0.09), (0.04, 0.09)], 16, 'DarkSteel', center=(sx, 0, top + 0.02), axis='Y')
            for sx in (-0.28, 0.28):
                c = (sx, -0.75, gz + gh + 0.62)
                p.lathe([(-0.07, 0.2), (-0.05, 0.2), (-0.05, 0.08), (0.05, 0.08), (0.05, 0.2), (0.07, 0.2)], 24, 'Frame',
                        center=c, axis='X', mats=['Frame', 'Frame', 'Copper', 'Frame', 'Frame'])
                p.box((sx, -0.75, gz + gh + 0.38), (0.04, 0.06, 0.5), 'Frame')       # spool stand
            p.box((0, -0.75, gz + gh + 0.62), (0.8, 0.03, 0.03), 'Chrome')           # spool axle
            p.box((0.0, -1.1, gz + gh / 2 + 0.1), (0.6, 0.1, 0.5), 'Frame')
            p.box((0.0, -1.16, gz + gh / 2 + 0.2), (0.25, 0.01, 0.15), 'Glass')
            for k, m in enumerate(('LensA', 'LensR', 'LensW')):
                p.cylinder((-0.2 + k * 0.2, -1.15, gz + gh / 2 - 0.05), (-0.2 + k * 0.2, -1.18, gz + gh / 2 - 0.05), 0.025, m, n=10)
    return mk('Trolley', f)


def ropes_top():
    return C['girder_z'] + C['girder_h'] * (0.5 if C['twin'] else 0.0)


def head_top():
    return ropes_top() - C['rest_drop']


def build_ropes():
    z0, z1 = ropes_top(), head_top()
    def f(p):
        if C['twin']:
            for x in (-0.5, 0.5):
                for y in (-0.35, 0.35):
                    p.cylinder((x, y, z0), (x, y, z1), 0.05, 'Rope', n=10)
        else:
            p.box((0, -0.75, (z0 + z1) / 2), (0.14, 0.14, z0 - z1), 'Chrome')      # the torch mast
    return mk('Ropes', f, bevel=0.0)


SLING_Z = 3.5          # hook point below the head top (twin)


def build_head():
    z = head_top()
    def f(p):
        if C['twin']:
            p.box((0, 0, z - 0.8), (2.0, 0.9, 1.6), 'Hazard')                      # hook block
            for x in (-0.5, 0.5):                                                   # sheaves showing at the sides
                p.lathe([(-0.52, 0.5), (0.52, 0.5)], 24, 'DarkSteel', center=(x, 0, z - 0.4), axis='Y')
            p.lathe([(z - 1.6, 0.2), (z - 2.4, 0.2)], 16, 'DarkSteel', axis='Z')     # shank and nut
            p.lathe([(z - 1.75, 0.32), (z - 1.6, 0.32)], 16, 'DarkSteel', axis='Z')
            # the hook: a thick bent bar
            import math
            r, cz = 0.45, z - 2.9
            path = [(r * math.sin(a), 0, cz - r * math.cos(a)) for a in [math.radians(t) for t in range(-60, 241, 20)]]
            path = [(0.0, 0, z - 2.4)] + path
            p.sweep(path, [(0.13 * math.cos(t * math.pi / 5), 0.13 * math.sin(t * math.pi / 5)) for t in range(10)], 'DarkSteel')
            # slings from the hook to the spreader's lugs, the spreader beam
            hz, bz = cz - r, z - 6.1 + 0.6
            for x in (-4.5, 4.5):
                for y in (-0.25, 0.25):
                    p.cylinder((0, y * 0.3, hz), (x, y, bz + 0.25), 0.04, 'Rope', n=8)
                p.box((x, 0, bz + 0.12), (0.3, 0.12, 0.3), 'DarkSteel')
            p.box((0, 0, bz - 0.3), (10.0, 0.8, 0.6), 'Paint')
            for x in (-4.5, -1.5, 1.5, 4.5):
                p.box((x, 0, bz - 0.68), (0.2, 0.12, 0.16), 'DarkSteel')
        else:
            p.box((0, -0.75, z - 0.12), (0.3, 0.3, 0.24), 'Frame')                  # torch head
            p.box((0.16, -0.75, z - 0.12), (0.02, 0.2, 0.16), 'Hazard')
            p.cylinder((0, -0.75, z - 0.24), (0, -0.75, z - 0.55), 0.035, 'Copper', n=12)
            p.cylinder((0, -0.75, z - 0.55), (0, -0.75, z - 0.6), 0.02, 'Chrome', n=10)
            p.cylinder((0.1, -0.75, z - 0.24), (0.1, -0.75, z - 0.5), 0.012, 'Rope', n=6)   # seam tracker
    return mk('Head', f, bevel=0.01 if C['twin'] else 0.004)


def socket_z():
    return head_top() - (6.1 if C['twin'] else 0.6)


# =============================================================================
# LODs and rig
# =============================================================================

def build_lods(objs):
    lods = {}
    for o in objs:
        n = o.name
        l1 = K.make_lod(o, 0.4, n + '_LOD1')
        l2 = K.make_lod(o, 0.15, n + '_LOD2')
        o.name = o.data.name = n + '_LOD0'
        lods[n] = [o, l1, l2]
    return lods


def rig(lods):
    hs = C['span'] / 2
    gz, gh = C['girder_z'], C['girder_h']
    pivots = {
        'Rail_W': ((-hs, 0, 0), None), 'Rail_E': ((hs, 0, 0), None), 'Bridge': ((0, 0, 0), None),
        'Leg_W': ((-hs, 0, 0), 'Bridge'), 'Leg_E': ((hs, 0, 0), 'Bridge'), 'Girder': ((0, 0, gz), 'Bridge'),
        'Trolley': ((0, 0, gz + gh), 'Bridge'), 'Ropes': ((0, -0.75 if not C['twin'] else 0, ropes_top()), 'Trolley'),
        'Head': ((0, -0.75 if not C['twin'] else 0, head_top()), 'Trolley'),
    }
    root = K.empty(C['name'], (0, 0, 0), size=2.0, display='ARROWS')
    nodes = {n: K.empty(n, pv, size=0.5) for n, (pv, _) in pivots.items()}
    nodes['HookSocket'] = K.empty('HookSocket', (0, -0.75 if not C['twin'] else 0, socket_z()), size=0.4, display='CUBE')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for n in ('Bridge', 'Rail_W', 'Rail_E'):
        K.parent_keep_local(nodes[n], root)
    for n in ('Leg_W', 'Leg_E', 'Girder', 'Trolley'):
        K.parent_keep_local(nodes[n], nodes['Bridge'])
    for n in ('Ropes', 'Head'):
        K.parent_keep_local(nodes[n], nodes['Trolley'])
    K.parent_keep_local(nodes['HookSocket'], nodes['Head'])
    bpy.context.view_layer.update()
    return root


def preview_renders(scene, root, quick):
    import forklift as FL
    objs = [root] + list(root.children_recursive)
    FL.studio(scene)
    try:
        scene.view_settings.view_transform = 'AgX'
        scene.view_settings.look = 'AgX - Punchy'
    except TypeError:
        pass
    spp = 32 if quick else 128
    res = (800, 500) if quick else (1600, 1000)
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    k = C['span'] / 40.0 if C['twin'] else C['span'] / 12.0 * 0.35
    tag = C['name'].lower()
    FL.render(scene, FL.camera(scene, 'Cam_Hero', (55 * k, -70 * k, 35 * k), (0, 0, C['girder_z'] * 0.5), 45),
              os.path.join(PREVIEW_DIR, f'{tag}_hero.png'), res, spp)
    FL.render(scene, FL.camera(scene, 'Cam_Low', (14 * k, -24 * k, 4 * k), (0, 0, C['girder_z'] * 0.6), 45),
              os.path.join(PREVIEW_DIR, f'{tag}_low.png'), res, spp)


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--variant', choices=sorted(VARIANTS), default='goliath')
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true', help='small bake + fast renders')
    ap.add_argument('--cpu', action='store_true', help='bake / render on the CPU (OptiX unavailable)')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    C.update(VARIANTS[args.variant])
    out_dir = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', C['name'])
    t0 = time.time()

    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)

    build_materials()
    hs = C['span'] / 2
    objs = [build_rail('Rail_W', -hs), build_rail('Rail_E', hs), build_leg('Leg_W', -hs, -1), build_leg('Leg_E', hs, 1),
            build_girder(), build_trolley(), build_ropes(), build_head()]
    weights = {'Rail_W': 0.5, 'Rail_E': 0.5, 'Leg_W': 1.4, 'Leg_E': 1.2, 'Girder': 1.6, 'Trolley': 1.0, 'Ropes': 0.2, 'Head': 0.8}
    K.atlas_unwrap(objs, margin=0.004, weights=weights)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 3.0 if C['twin'] else 0.6
    except AttributeError:
        pass
    paths = K.bake_texture_set(objs, out_dir, C['name'], args.bake, args.save)
    runtime = K.make_runtime_material(C['name'], paths)
    K.assign_single_material(objs, runtime)

    lods = build_lods(objs)
    print(f"[{C['name']}] triangles per LOD:")
    for lvl in range(3):
        print(f'    LOD{lvl}  {sum(K.tri_count(ls[lvl]) for ls in lods.values()):>7}')

    root = rig(lods)
    K.export_fbx(root, os.path.join(out_dir, C['name'] + '.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, C['name'] + '.blend'))
    if not args.no_render:
        preview_renders(scene, root, args.quick)
    print(f"[{C['name']}] done in {time.time() - t0:.1f}s")


if __name__ == '__main__':
    main()
