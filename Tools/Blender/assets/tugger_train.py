"""
SimuLean kit: tugger train (route train) for sequenced supply -- an electric stand-on tow tractor,
four sequencing wagons and two sequenced parts, sharing ONE atlas / material (TuggerTrain.mat).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/tugger_train.py --
            [--bake 4096] [--save 2048] [--quick] [--cpu] [--no-render] [--look]

Outputs (a kit: every FBX is a piece using the folder material)
    Assets/SimuLean.Unity/Art/Models/TuggerTrain/{TowTractor,ConsoleWagon,SteeringWheelWagon,ToteTrolley,
        PlatformTrolley,CentralConsole,SteeringWheel}.fbx + TuggerTrain_*.png
    Tools/Blender/build/TuggerTrain.blend, Tools/Blender/previews/tugger_*.png

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z (FORWARD, the
travel direction), authored +Z = Unity +Y.  Pivot = floor centre of the footprint.  Metres.  Every
named node is a bare Transform; meshes hang under it as <Node>_LOD0/1/2 at identity.  An authored
yaw Rz(t) is Unity Euler(0, -t, 0).

Coupling: every hitch is 0.30 m above the floor.  Put a wagon so that its Hitch_Front coincides with
the Hitch_Rear of the unit in front (the drawbar eye drops over the pin).  Drawbar yaw (about local Y,
pivot at the front cross member) follows the bend of the train.

TowTractor        1.86 L (bumper to hitch jaw) x 0.90 W x 1.25 H (console), stand-on electric tow
                  tractor, library orange with graphite chassis.  Wheels r 0.17 m (track 0.72 m),
                  wheelbase 1.05 m.  The driver STANDS on the rear platform (floor 0.22 m) facing +Z.
    Steer_FL / Steer_FR  (+-0.36, 0.17, 0.55): steering yaw about local Y (+-35 deg)
      '- Wheel_FL / Wheel_FR   spin about local X (r 0.17)
    Wheel_RL / Wheel_RR  (+-0.36, 0.17, -0.50): spin about local X
    DriverSocket  (0, 0.22, -0.45): feet of a standing operator (Operator model, identity pose)
    Hitch_Rear    (0, 0.30, -0.95): tow pin centre
    Beacon        (0.38, 1.33, -0.86): amber beacon (emissive) -- toggle / pulse its renderer
Wagons (common chassis: light-grey frame top 0.315 m, castors r 0.10 m; front pair swivelling)
    Castor_FL / Castor_FR  (+-x, 0.235, L/2-0.15): swivel yaw about local Y
      '- Wheel_FL / Wheel_FR   (trail 0.04 m) spin about local X
    Wheel_RL / Wheel_RR    (+-x, 0.10, -(L/2-0.15)): spin about local X
    Drawbar     (0, 0.27, L/2+0.02): yellow tow bar, yaw about local Y
      '- Hitch_Front  (0, 0.30, L/2+0.60): drawbar eye centre
    Hitch_Rear  (0, 0.30, -(L/2+0.07)): rear coupling pin centre
  PlatformTrolley     1.24 L x 1.04 W frame, chequer-plate deck 1200 x 1000 at 0.33 m, corner stops.
                      LoadSocket (0, 0.33, 0).
  ConsoleWagon        1.25 L x 0.80 W x 1.70 H rack, 3 lined shelves (tops 0.34 / 0.80 / 1.26 m),
                      3 positions per shelf between vertical divider bars (pitch 0.38 m).
                      Slot_0..8: level = i // 3 (0 = bottom), position i % 3 from the front (+Z);
                      localRotation Euler(0, -90, 0) so a CentralConsole's long side lies across.
  SteeringWheelWagon  1.40 L x 0.62 W x 1.78 H rack, 3 levels x 12 cradles on a central bar (pitch
                      0.11 m), wheels hang with their axis along the wagon.  Slot_0..35: level =
                      i // 12 (hub heights 0.60 / 1.05 / 1.50 m), position i % 12 from the front;
                      identity rotation (SteeringWheel pivot = hub centre).
  ToteTrolley         1.30 L x 0.70 W, two shelves (tops 0.325 / 0.655 m) for six 600 x 400 totes
                      (LoadUnits SmallLoadCarrier_600x400x280) with low lips, push handle at the rear.
                      Slot_0..5: 0..2 lower, 3..5 upper, front to rear (pitch 0.42 m); localRotation
                      Euler(0, -90, 0) (tote long side across).
Parts (instanced in bulk: hand-made proxies for LOD1 / LOD2, maps baked from LOD0)
  CentralConsole      0.60 L x 0.30 W x 0.35 H generic floor console: gear area, cup holders,
                      armrest; charcoal plastic, satin-silver trim.  Pivot bottom centre.
  SteeringWheel       dia 0.38 m, 3 spokes, airbag hub, axis along local Z; pivot = HUB CENTRE
                      (documented exception: it hangs on a cradle).
"""

import os
import sys
import argparse
import time
from math import pi, cos, sin, radians

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
import load_units as LU                     # noqa: E402  (shader helpers, rings, pin_uvs)

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'TuggerTrain')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
PREFIX = 'TuggerTrain'
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

HZ = 0.30                                   # hitch height (every unit)
ZT = 0.315                                  # wagon frame top
CR = 0.10                                   # castor wheel radius
TR = 0.17                                   # tractor wheel radius
ORANGE = (0.85, 0.20, 0.01)
GAP = 0.0025
SPACING = 4.0                               # bake layout pitch (m)

MATS = {}
WEIGHTS = {}


# =============================================================================
# materials
# =============================================================================

def console_mat(name):
    """Charcoal interior plastic with a fine grain, two cup holders, a button row and an
    armrest seam (object space = the console's own frame)."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    top = nb.mr(nz, 0.7, 0.9)
    col = nb.mixc(nb.mr(nb.noise(3.0, 3), 0.3, 0.7), (0.045, 0.046, 0.05), (0.06, 0.061, 0.066))
    cups = None
    for xc in (-0.06, 0.06):
        d = nb.vm('LENGTH', nb.vec(nb.m('SUBTRACT', x, xc), nb.m('SUBTRACT', y, 0.10), 0.0))
        c = nb.mr(d, 0.040, 0.036)
        cups = c if cups is None else nb.m('MAXIMUM', cups, c)
    cups = nb.m('MULTIPLY', cups, top)
    btn = LU.mul(nb, top, LU.rect(nb, x, y, 0.0, 0.21, 0.09, 0.012, 0.002),
                 nb.m('LESS_THAN', nb.m('FRACT', nb.m('MULTIPLY', nb.m('ADD', x, 0.2), 1 / 0.03)), 0.7))
    seam = LU.mul(nb, LU.band(nb, y, -0.035, 0.0015, 0.001), nb.mr(z, 0.32, 0.33))
    arm = LU.mul(nb, top, nb.mr(z, 0.33, 0.345), nb.mr(y, -0.03, -0.05))
    col = nb.mixc(nb.m('MULTIPLY', arm, 0.9), col, (0.02, 0.02, 0.022))
    col = nb.mixc(cups, col, (0.004, 0.004, 0.005))
    col = nb.mixc(btn, col, (0.10, 0.10, 0.11))
    col = nb.mixc(seam, col, (0.003, 0.003, 0.003))
    col = LU.common_dirt(nb, col, cav=0.4, dust=0.15)
    rough = nb.mixf(arm, 0.5, 0.42)
    h = nb.m('SUBTRACT', nb.m('MULTIPLY', nb.noise(800, 2), 0.2), nb.m('ADD', cups, seam))
    h = nb.m('ADD', h, nb.m('MULTIPLY', btn, 0.5))
    h = nb.m('ADD', h, nb.m('MULTIPLY', nb.noise(260, 3, 0.7), nb.m('MULTIPLY', arm, 0.4)))
    nrm = nb.bump(h, 0.5, 0.003)
    return finalize_material(nb, col, rough, 0.0, nrm)


def build_materials():
    MATS.clear()
    os.makedirs(BUILD_DIR, exist_ok=True)
    logo, aspect = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_logo_tug.png'),
                                       256, FONT, bold_offset=0.016)
    lw = 0.30
    decals = [dict(image=logo, origin=(s * 0.45, 0.42, 0.66), u=(0, s, 0), v=(0, 0, 1),
                   size=(lw, lw / aspect), normal=(s, 0, 0), color=(0.95, 0.95, 0.95)) for s in (1, -1)]
    MATS.update({
        'Paint': M.paint('Paint', ORANGE, rough=0.32, wear=0.5, decals=decals),
        'Graphite': M.paint('Graphite', (0.032, 0.034, 0.038), rough=0.45, wear=0.3),
        'Frame': M.paint('Frame', (0.30, 0.31, 0.32), rough=0.42, wear=0.9),
        'Yellow': M.paint('Yellow', (0.80, 0.50, 0.02), rough=0.38, wear=1.1),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=1.2,
                          color_fn=M.hazard_fn((0.80, 0.50, 0.02), width=0.03, axis=('x', 'z'))),
        'Zinc': M.galvanised('Zinc'),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Tire': M.tire('Tire', TR - 0.002, lugs=32),
        'PU': M.rubber('PU', color=(0.03, 0.03, 0.032), rough=0.55),
        'Hub': M.paint('Hub', (0.36, 0.36, 0.35), rough=0.4, wear=0.6, edge=0.004),
        'Rubber': M.rubber('Rubber'),
        'Mat': M.rubber('Mat', ribs=('y', 0.012)),
        'Vinyl': M.vinyl('Vinyl'),
        'Black': M.plastic('Black', color=(0.018, 0.018, 0.02), rough=0.45),
        'Liner': M.plastic('Liner', color=(0.045, 0.047, 0.05), rough=0.65, grain=0.12),
        'Deck': M.tread_plate('Deck'),
        'LensW': M.lens('LensW', (1.0, 0.93, 0.80)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'LensA': M.lens('LensA', (1.0, 0.45, 0.02)),
        'Screen': M.screen('Screen', (-0.09, -0.10, 1.115), (1, 0, 0), (0, 0.45, 0.89), (0.18, 0.09)),
        'EStop': M.plastic('EStop', color=(0.55, 0.02, 0.015), rough=0.3),
        'Console': console_mat('Console'),
        'Trim': M.dark_steel('Trim', color=(0.40, 0.40, 0.41), rough=0.3, metal=0.85),
        'SWRim': M.vinyl('SWRim', color=(0.014, 0.014, 0.015)),
        'SWHub': M.plastic('SWHub', color=(0.02, 0.02, 0.022), rough=0.5),
        'Proxy': M.plastic('Proxy'),
    })


# =============================================================================
# build helpers
# =============================================================================

def mk(name, fn, bevel=0.004, seg=1, weight=1.0, loc=None, cutters=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        cs = []
        for c in cutters:
            cp = Part(name + '_cut')
            c(cp)
            cs.append(cp.build({'Cut': MATS['Proxy']}))
        K.boolean(ob, cs)
    finish(ob, bevel, seg, 40, sharp)
    if loc is not None:
        ob.location = loc
    WEIGHTS[ob.name] = weight
    return ob


def proxy(name, fn, weight):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, 0, 1, 40, 30)
    WEIGHTS[ob.name] = weight
    return ob


bx = LU.bx


def tube(p, a, b, r, mat, n=10):
    p.cylinder(a, b, r, mat, n=n)


def sq(p, a, b, s, mat):
    """Square tube between two points on an axis-aligned line."""
    a, b = V(a), V(b)
    lo = V((min(a.x, b.x) - s / 2, min(a.y, b.y) - s / 2, min(a.z, b.z) - s / 2))
    hi = V((max(a.x, b.x) + s / 2, max(a.y, b.y) + s / 2, max(a.z, b.z) + s / 2))
    for i in range(3):
        if abs(a[i] - b[i]) > 1e-6:
            lo[i], hi[i] = min(a[i], b[i]), max(a[i], b[i])
    p.box_minmax(lo, hi, mat)


def wheel(p, r, w, tread, rim, seg=24):
    """Wheel at the origin, axle along X: tyre / tread band + dished rim + hub."""
    h = w / 2
    pts = [(-h * 0.5, 0.0), (-h * 0.5, r * 0.22), (-h * 0.75, r * 0.30), (-h * 0.75, r * 0.56),
           (-h, r * 0.62), (-h, r * 0.86), (-h * 0.85, r * 0.98), (0.0, r), (h * 0.85, r * 0.98), (h, r * 0.86),
           (h, r * 0.62), (h * 0.75, r * 0.56), (h * 0.75, r * 0.30), (h * 0.5, r * 0.22), (h * 0.5, 0.0)]
    mats = [rim, rim, rim, tread, tread, tread, tread, tread, tread, tread, tread, rim, rim, rim]
    p.lathe(pts, seg, rim, axis='X', mats=mats, cap0=False, cap1=False)


class Vehicle:
    """A rigged kit piece: nodes {node or None: [parts]}, decimation plan, LOD2 merges."""

    def __init__(self, name, nodes, pivots, parents, sockets, plan, merge=(), yaw=None):
        self.piece = KM.Piece(name, 'fixed', nodes, levels=3, pivots=pivots, parents=parents, sockets=sockets)
        self.name = name
        self.parts = [p for ps in nodes.values() for p in ps]
        self.proxies = []
        self.plan = plan                    # part name -> (r1, r2); None drops the part
        self.merge = set(merge)             # nodes whose LOD2 joins the body (no own LOD2)
        self.yaw = yaw or {}                # socket -> authored yaw (rad)
        self.auth_sockets = dict(sockets)


class Item(LU.Unit):
    """A bulk part with hand-made proxies (as the load units)."""


# =============================================================================
# tow tractor
# =============================================================================

def build_tractor():
    W2 = 0.45
    yf, ywr = 0.55, -0.50
    xw = 0.36

    def shell(p):
        side = [(-0.10, 0.12), (0.86, 0.12), (0.92, 0.20), (0.92, 0.56), (0.87, 0.72), (0.66, 0.93),
                (0.10, 0.98), (-0.10, 0.98)]
        prof = fillet_closed(side, [0, 0.03, 0.04, 0.03, 0.08, 0.10, 0.03, 0], 3)
        p.extrude([(u, v) for u, v in prof], 'X', -W2, W2, 'Paint')

    def skirt(p):
        bx(p, -W2 - 0.004, W2 + 0.004, -0.10, 0.88, 0.10, 0.26, 'Graphite')

    def front(p):
        bx(p, -0.40, 0.40, 0.92, 0.95, 0.18, 0.30, 'Rubber')                          # bumper
        for s in (1, -1):
            bx(p, s * 0.22, s * 0.38, 0.915, 0.93, 0.46, 0.53, 'LensW')                # headlights

    def arch(s, y):
        def f(p):
            p.cylinder((s * 0.25, y, TR), (s * 0.52, y, TR), 0.205, 'Cut', n=24)
        return f
    arches_f = [arch(1, yf), arch(-1, yf)]
    arches_r = [arch(1, ywr), arch(-1, ywr)]

    def base(p):
        bx(p, -W2, W2, -0.90, -0.10, 0.10, 0.20, 'Graphite')

    def fender(s):
        def f(p):
            bx(p, s * 0.28, s * W2, -0.76, -0.26, 0.20, 0.40, 'Paint')
        return f

    def fender_top(p):
        for s in (1, -1):
            bx(p, s * 0.28, s * W2, -0.77, -0.25, 0.40, 0.42, 'Rubber')

    def console(p):
        bx(p, -0.22, 0.22, -0.13, 0.12, 0.96, 1.10, 'Graphite')
        p.box((0, -0.06, 1.118), (0.20, 0.11, 0.02), 'Screen', rot=Matrix.Rotation(radians(-27), 3, 'X'))
        tube(p, (0, -0.10, 1.05), (0, -0.20, 1.17), 0.025, 'Black')                  # column
        p.cylinder((0.15, -0.05, 1.10), (0.15, -0.05, 1.125), 0.022, 'EStop', n=12)
        p.cylinder((0.15, -0.05, 1.095), (0.15, -0.05, 1.10), 0.03, 'Yellow', n=12)

    def steering(p):
        c = V((0, -0.21, 1.18))
        R = Matrix.Rotation(radians(-30), 3, 'X')
        ring = [c + R @ V((0.14 * cos(2 * pi * i / 20), 0.0, 0.14 * sin(2 * pi * i / 20))) for i in range(20)]
        p.sweep(ring, K.circle(0.016, 6), 'Black', closed=True)
        p.cylinder(c + R @ V((0, -0.02, 0)), c + R @ V((0, 0.03, 0)), 0.045, 'Black', n=12)
        for a in (0, pi):
            p.sweep([c, c + R @ V((0.13 * cos(a), 0, 0.13 * sin(a)))], K.circle(0.012, 6), 'Black')

    def platform(p):
        bx(p, -0.27, 0.27, -0.82, -0.10, 0.20, 0.22, 'Mat')
        bx(p, -W2, W2, -0.92, -0.82, 0.10, 0.62, 'Paint')                               # rear wall
        bx(p, -W2 - 0.004, W2 + 0.004, -0.925, -0.82, 0.10, 0.26, 'Graphite')
        for s in (1, -1):
            tube(p, (s * 0.42, -0.84, 0.62), (s * 0.42, -0.84, 1.10), 0.017, 'Graphite')   # grab handles
            tube(p, (s * 0.42, -0.84, 1.10), (s * 0.42, -0.70, 1.10), 0.017, 'Graphite')
            bx(p, s * 0.34, s * 0.42, -0.926, -0.92, 0.50, 0.58, 'LensR')
        tube(p, (-0.12, -0.86, 0.62), (-0.12, -0.86, 0.82), 0.02, 'Graphite')
        tube(p, (0.12, -0.86, 0.62), (0.12, -0.86, 0.82), 0.02, 'Graphite')
        p.box((0, -0.855, 0.94), (0.40, 0.07, 0.22), 'Vinyl')                          # backrest pad
        tube(p, (0.38, -0.86, 0.62), (0.38, -0.86, 1.26), 0.012, 'Graphite')            # beacon post
        # tow jaw + pin
        bx(p, -0.07, 0.07, -0.99, -0.92, 0.25, 0.27, 'DarkSteel')
        bx(p, -0.07, 0.07, -0.99, -0.92, 0.33, 0.35, 'DarkSteel')
        bx(p, -0.07, -0.05, -0.99, -0.92, 0.27, 0.33, 'DarkSteel')
        bx(p, 0.05, 0.07, -0.99, -0.92, 0.27, 0.33, 'DarkSteel')
        p.cylinder((0, -0.95, 0.22), (0, -0.95, 0.39), 0.012, 'Zinc', n=10)
        p.cylinder((0, -0.95, 0.39), (0, -0.95, 0.41), 0.03, 'Yellow', n=12)

    def beacon(p):
        p.lathe([(1.26, 0.0), (1.26, 0.035), (1.275, 0.035), (1.275, 0.03), (1.36, 0.028), (1.37, 0.0)], 14,
                'LensA', center=(0.38, -0.86, 0), axis='Z', mats=['Black', 'Black', 'Black', 'LensA', 'LensA'])

    parts = [mk('TT_Body', shell, 0.008, 2, weight=0.9, cutters=arches_f),
             mk('TT_Skirt', skirt, 0.004, 1, weight=0.6, cutters=arches_f),
             mk('TT_Front', front, 0.004, 1, weight=0.6),
             mk('TT_Base', base, 0.004, 1, weight=0.6, cutters=arches_r),
             mk('TT_FenderL', fender(-1), 0.006, 1, weight=0.7, cutters=arches_r[1:]),
             mk('TT_FenderR', fender(1), 0.006, 1, weight=0.7, cutters=arches_r[:1]),
             mk('TT_FenderTop', fender_top, 0.003, 1, weight=0.5, cutters=arches_r),
             mk('TT_Console', console, 0.005, 1, weight=1.0),
             mk('TT_Steering', steering, 0.0, weight=0.6),
             mk('TT_Platform', platform, 0.005, 1, weight=0.8)]
    bea = [mk('TT_Beacon', beacon, 0.0, weight=0.6)]
    wheels = {}
    for nm, (x, y) in {'Wheel_FL': (-xw, yf), 'Wheel_FR': (xw, yf), 'Wheel_RL': (-xw, ywr),
                       'Wheel_RR': (xw, ywr)}.items():
        wheels[nm] = [mk(f'TT_{nm}', lambda p: wheel(p, TR, 0.12, 'Tire', 'Hub'), 0.0, weight=0.7,
                         loc=(x, y, TR))]
    nodes = {None: parts, 'Beacon': bea, **wheels}
    pivots = {'Beacon': (0.38, -0.86, 1.33), **{n: tuple(wheels[n][0].location) for n in wheels}}
    sockets = {'Steer_FL': (-xw, yf, TR), 'Steer_FR': (xw, yf, TR), 'Hitch_Rear': (0, -0.95, HZ),
               'DriverSocket': (0, -0.45, 0.22)}
    parents = {'Wheel_FL': 'Steer_FL', 'Wheel_FR': 'Steer_FR'}
    plan = {'TT_Body': (0.35, 0.10), 'TT_Console': (0.4, 0.15), 'TT_Steering': (0.4, None),
            'TT_Skirt': (0.5, 0.2), 'TT_Front': (0.5, None), 'TT_Base': (0.5, 0.2), 'TT_FenderL': (0.5, 0.2),
            'TT_FenderR': (0.5, 0.2), 'TT_FenderTop': (0.5, None),
            'TT_Platform': (0.35, 0.10), 'TT_Beacon': (0.6, None),
            **{f'TT_{n}': (0.35, 0.10) for n in wheels}}
    return Vehicle('TowTractor', nodes, pivots, parents, sockets, plan, merge=list(wheels))


# =============================================================================
# wagons
# =============================================================================

def chassis_parts(tag, L, W):
    """Frame, castors, drawbar, rear coupling: returns (nodes, pivots, parents, sockets, plan, merge)."""
    xc = W / 2 - 0.08
    yc = L / 2 - 0.15

    def frame(p):
        for s in (1, -1):
            bx(p, s * (W / 2 - 0.055), s * (W / 2 - 0.005), -L / 2, L / 2, ZT - 0.08, ZT, 'Frame')
        for yy in (-L / 2 + 0.025, 0.0, L / 2 - 0.025):
            bx(p, -W / 2 + 0.055, W / 2 - 0.055, yy - 0.025, yy + 0.025, ZT - 0.07, ZT - 0.01, 'Frame')
        for sx in (1, -1):
            for sy in (1, -1):
                p.cylinder((sx * (W / 2 + 0.0), sy * (L / 2 + 0.0), ZT - 0.04),
                           (sx * (W / 2 + 0.0), sy * (L / 2 + 0.035), ZT - 0.04), 0.035, 'Rubber', n=12)
                bx(p, sx * xc - 0.06, sx * xc + 0.06, sy * yc - 0.06, sy * yc + 0.06, ZT - 0.09, ZT - 0.08, 'Zinc')
        # drawbar bracket + rear coupling
        bx(p, -0.06, 0.06, L / 2, L / 2 + 0.04, 0.24, 0.30, 'DarkSteel')
        bx(p, -0.07, 0.07, -L / 2 - 0.10, -L / 2, 0.25, 0.27, 'DarkSteel')
        bx(p, -0.07, 0.07, -L / 2 - 0.10, -L / 2, 0.33, 0.35, 'DarkSteel')
        bx(p, -0.03, 0.03, -L / 2 - 0.02, -L / 2, 0.27, 0.33, 'DarkSteel')
        p.cylinder((0, -L / 2 - 0.07, 0.22), (0, -L / 2 - 0.07, 0.39), 0.012, 'Zinc', n=10)
        p.cylinder((0, -L / 2 - 0.07, 0.39), (0, -L / 2 - 0.07, 0.41), 0.028, 'Yellow', n=12)

    def fork(swivel):
        def f(p):
            if swivel:
                p.cylinder((0, 0, ZT - 0.09), (0, 0, ZT - 0.115), 0.04, 'Zinc', n=14)       # swivel bearing
            yo = -0.04 if swivel else 0.0
            bx(p, -0.05, 0.05, -0.05 + yo / 2, 0.05 + yo / 2, ZT - 0.125, ZT - 0.115, 'Zinc')
            for s in (1, -1):
                bx(p, s * 0.033, s * 0.039, yo - 0.03, yo + 0.03, CR - 0.01, ZT - 0.12, 'Zinc')
            p.cylinder((-0.042, yo, CR), (0.042, yo, CR), 0.008, 'DarkSteel', n=8)
        return f

    nodes, pivots, parents, sockets, plan = {}, {}, {}, {}, {}
    nodes[None] = [mk(f'{tag}_Frame', frame, 0.004, 1, weight=0.7)]
    plan[f'{tag}_Frame'] = (0.4, 0.06)
    merge = []
    for side, sx in (('L', -1), ('R', 1)):
        cn = f'Castor_F{side}'
        nodes[cn] = [mk(f'{tag}_{cn}', fork(True), 0.002, weight=0.5, loc=(sx * xc, yc, 0))]
        pivots[cn] = (sx * xc, yc, ZT - 0.08)
        plan[f'{tag}_{cn}'] = (0.4, None)
        wn = f'Wheel_F{side}'
        nodes[wn] = [mk(f'{tag}_{wn}', lambda p: wheel(p, CR, 0.05, 'PU', 'Hub', 16), 0.0, weight=0.5,
                        loc=(sx * xc, yc - 0.04, CR))]
        pivots[wn] = (sx * xc, yc - 0.04, CR)
        parents[wn] = cn
        plan[f'{tag}_{wn}'] = (0.4, 0.08)
        rn = f'Wheel_R{side}'
        rf = mk(f'{tag}_Fork_R{side}', fork(False), 0.002, weight=0.5, loc=(sx * xc, -yc, 0))
        nodes[None].append(rf)
        plan[rf.name] = (0.4, None)
        nodes[rn] = [mk(f'{tag}_{rn}', lambda p: wheel(p, CR, 0.05, 'PU', 'Hub', 16), 0.0, weight=0.5,
                        loc=(sx * xc, -yc, CR))]
        pivots[rn] = (sx * xc, -yc, CR)
        plan[f'{tag}_{rn}'] = (0.4, 0.08)
        merge += [cn, wn, rn]

    def drawbar(p):
        y0 = L / 2 + 0.02
        bx(p, -0.035, 0.035, y0, y0 + 0.06, 0.25, 0.29, 'DarkSteel')
        p.sweep([(0, y0 + 0.04, 0.27), (0, y0 + 0.50, 0.30)], K.rrect(0.04, 0.04, 0.006, 1), 'Yellow')
        p.lathe([(0.285, 0.022), (0.285, 0.045), (0.315, 0.045), (0.315, 0.022)], 16, 'DarkSteel',
                center=(0, y0 + 0.58, 0), axis='Z', cap0=False, cap1=False)
        p.lathe([(0.315, 0.022), (0.285, 0.022)], 16, 'DarkSteel', center=(0, y0 + 0.58, 0), axis='Z',
                cap0=False, cap1=False)
    nodes['Drawbar'] = [mk(f'{tag}_Drawbar', drawbar, 0.002, weight=0.6)]
    pivots['Drawbar'] = (0, L / 2 + 0.02, 0.27)
    plan[f'{tag}_Drawbar'] = (0.5, 0.15)
    sockets['Hitch_Front'] = (0, L / 2 + 0.60, HZ)
    parents['Hitch_Front'] = 'Drawbar'
    sockets['Hitch_Rear'] = (0, -L / 2 - 0.07, HZ)
    return nodes, pivots, parents, sockets, plan, merge


def wagon(name, L, W, body_fns, slots=None, yaw=0.0, extra_sockets=None):
    tag = name
    nodes, pivots, parents, sockets, plan, merge = chassis_parts(tag, L, W)
    for i, (fn, bevel, r) in enumerate(body_fns):
        ob = mk(f'{tag}_Body{i}', fn, bevel, 1, weight=0.7)
        nodes[None].append(ob)
        plan[ob.name] = r
    ys = {}
    for k, pos in enumerate(slots or []):
        sockets[f'Slot_{k}'] = pos
        if yaw:
            ys[f'Slot_{k}'] = yaw
    sockets.update(extra_sockets or {})
    return Vehicle(name, nodes, pivots, parents, sockets, plan, merge, yaw=ys)


def build_platform():
    L, W, DZ = 1.24, 1.04, 0.33

    def deck(p):
        bx(p, -W / 2 + 0.01, W / 2 - 0.01, -L / 2 + 0.01, L / 2 - 0.01, ZT, DZ, 'Deck')
        for sx in (1, -1):
            for sy in (1, -1):
                bx(p, sx * (W / 2 - 0.01), sx * (W / 2 - 0.07), sy * (L / 2 - 0.01), sy * (L / 2 - 0.07),
                   DZ, DZ + 0.07, 'Hazard')
    return wagon('PlatformTrolley', L, W, [(deck, 0.003, (0.4, 0.06))], extra_sockets={'LoadSocket': (0, 0, DZ)})


def build_console_wagon():
    L, W, H = 1.25, 0.80, 1.70
    shelves = (0.34, 0.80, 1.26)
    pos = (0.38, 0.0, -0.38)

    def rack(p):
        for sx in (1, -1):
            for sy in (1, -1):
                sq(p, (sx * (W / 2 - 0.02), sy * (L / 2 - 0.02), ZT), (sx * (W / 2 - 0.02), sy * (L / 2 - 0.02), H),
                   0.04, 'Frame')
        for s in (1, -1):
            sq(p, (s * (W / 2 - 0.02), -L / 2 + 0.04, H - 0.02), (s * (W / 2 - 0.02), L / 2 - 0.04, H - 0.02), 0.04,
               'Frame')
            sq(p, (-W / 2 + 0.04, s * (L / 2 - 0.02), H - 0.02), (W / 2 - 0.04, s * (L / 2 - 0.02), H - 0.02), 0.04,
               'Frame')
        for z in shelves:
            for s in (1, -1):
                sq(p, (s * (W / 2 - 0.02), -L / 2 + 0.04, z - 0.035), (s * (W / 2 - 0.02), L / 2 - 0.04, z - 0.035),
                   0.03, 'Frame')
            for yy in (-L / 2 + 0.02, L / 2 - 0.02):
                sq(p, (-W / 2 + 0.04, yy, z - 0.035), (W / 2 - 0.04, yy, z - 0.035), 0.03, 'Frame')

    def dividers(p):
        """Vertical divider bars (the 'slots') on both sides; dropped at LOD2."""
        for z in shelves:
            for yd in (0.19, -0.19):
                for s in (1, -1):
                    tube(p, (s * (W / 2 - 0.03), yd, z), (s * (W / 2 - 0.03), yd, z + 0.30), 0.011, 'Yellow', n=8)
                tube(p, (-W / 2 + 0.03, yd, z + 0.30), (W / 2 - 0.03, yd, z + 0.30), 0.011, 'Yellow', n=8)

    def liners(p):
        for z in shelves:
            bx(p, -W / 2 + 0.035, W / 2 - 0.035, -L / 2 + 0.035, L / 2 - 0.035, z - 0.02, z, 'Liner')
            for yp in pos:                                                  # foam pads per position
                bx(p, -0.25, 0.25, yp - 0.12, yp + 0.12, z, z + 0.008, 'Rubber')
    slots = [(0, y, z + 0.008) for z in shelves for y in pos]
    return wagon('ConsoleWagon', L, W, [(rack, 0.002, (0.35, 0.04)), (dividers, 0.0, (0.5, None)),
                                         (liners, 0.002, (0.4, 0.03))],
                 slots=slots, yaw=pi / 2)


def build_steering_wagon():
    L, W, H = 1.40, 0.62, 1.78
    hubs = (0.60, 1.05, 1.50)
    ys = [0.605 - 0.11 * j for j in range(12)]

    def rack(p):
        for sx in (1, -1):
            for sy in (1, -1):
                sq(p, (sx * (W / 2 - 0.02), sy * (L / 2 - 0.02), ZT), (sx * (W / 2 - 0.02), sy * (L / 2 - 0.02), H),
                   0.04, 'Frame')
        for s in (1, -1):
            sq(p, (s * (W / 2 - 0.02), -L / 2 + 0.04, H - 0.02), (s * (W / 2 - 0.02), L / 2 - 0.04, H - 0.02), 0.04,
               'Frame')
            sq(p, (-W / 2 + 0.04, s * (L / 2 - 0.02), H - 0.02), (W / 2 - 0.04, s * (L / 2 - 0.02), H - 0.02), 0.04,
               'Frame')
        for z in hubs:
            zb = z - 0.035 - 0.025 - 0.015
            sq(p, (0, -L / 2 + 0.04, zb), (0, L / 2 - 0.04, zb), 0.03, 'Frame')
            for s in (1, -1):
                sq(p, (-W / 2 + 0.04, s * (L / 2 - 0.02), zb), (W / 2 - 0.04, s * (L / 2 - 0.02), zb), 0.03, 'Frame')
                tube(p, (s * (W / 2 - 0.02), -L / 2 + 0.04, z), (s * (W / 2 - 0.02), L / 2 - 0.04, z), 0.012,
                     'Yellow', n=8)

    def cradles(p):
        for z in hubs:
            zb = z - 0.035
            for y in ys:
                bx(p, -0.03, 0.03, y - 0.015, y + 0.015, zb - 0.025, zb - 0.008, 'Black')
                for s in (1, -1):
                    bx(p, s * 0.018, s * 0.03, y - 0.015, y + 0.015, zb - 0.008, zb + 0.012, 'Black')
    slots = [(0, y, z) for z in hubs for y in ys]
    return wagon('SteeringWheelWagon', L, W, [(rack, 0.002, (0.35, 0.05)), (cradles, 0.0, (0.5, None))],
                 slots=slots)


def build_tote_trolley():
    L, W = 1.30, 0.70
    decks = (0.325, 0.655)
    pos = (0.42, 0.0, -0.42)

    def frame(p):
        for sx in (1, -1):
            for yy in (-L / 2 + 0.02, 0.0, L / 2 - 0.02):
                sq(p, (sx * (W / 2 - 0.02), yy, ZT), (sx * (W / 2 - 0.02), yy, 0.70), 0.035, 'Frame')
        for z in decks:
            bx(p, -W / 2 + 0.005, W / 2 - 0.005, -L / 2 + 0.005, L / 2 - 0.005, z - 0.012, z, 'Frame')
            for s in (1, -1):
                bx(p, s * (W / 2 - 0.005), s * (W / 2 - 0.015), -L / 2 + 0.005, L / 2 - 0.005, z, z + 0.035, 'Frame')
                bx(p, -W / 2 + 0.005, W / 2 - 0.005, s * (L / 2 - 0.005), s * (L / 2 - 0.015), z, z + 0.035, 'Frame')
        for s in (1, -1):                                                     # push handle (rear)
            tube(p, (s * (W / 2 - 0.02), -L / 2 + 0.02, 0.70), (s * (W / 2 - 0.02), -L / 2 - 0.04, 1.0), 0.014,
                 'Frame')
        tube(p, (-W / 2 + 0.02, -L / 2 - 0.04, 1.0), (W / 2 - 0.02, -L / 2 - 0.04, 1.0), 0.016, 'Black')
    slots = [(0, y, z) for z in decks for y in pos]
    return wagon('ToteTrolley', L, W, [(frame, 0.002, (0.4, 0.05))], slots=slots, yaw=pi / 2)


# =============================================================================
# sequenced parts
# =============================================================================

def build_console():
    name = 'CentralConsole'
    side = [(-0.30, 0.0), (0.30, 0.0), (0.30, 0.15), (0.25, 0.215), (0.05, 0.235), (0.0, 0.25),
            (-0.03, 0.33), (-0.07, 0.35), (-0.27, 0.35), (-0.30, 0.32)]
    prof = fillet_closed(side, [0.005, 0.01, 0.04, 0.04, 0.03, 0.02, 0.04, 0.03, 0.03, 0.03], 1)

    def body(p):
        rings = []
        for x, k in ((-0.15, 0.97), (-0.142, 1.0), (0.142, 1.0), (0.15, 0.97)):
            rings.append([V((x, u, v * k if v > 0.01 else v)) for u, v in prof])
        p.loft(rings, 'Console')
        bx(p, -0.152, 0.152, -0.30, 0.30, 0.0, 0.02, 'Black')                     # base flange
        p.lathe([(0.235, 0.022), (0.27, 0.018), (0.29, 0.024), (0.305, 0.018), (0.31, 0.0)], 10, 'Trim',
                center=(0, 0.16, 0), axis='Z', cap0=True, cap1=False)               # gear knob
        p.sweep([(-0.143, 0.24, 0.17), (-0.143, -0.02, 0.21), (-0.143, -0.27, 0.27)], K.circle(0.004, 4), 'Trim')
        p.sweep([(0.143, 0.24, 0.17), (0.143, -0.02, 0.21), (0.143, -0.27, 0.27)], K.circle(0.004, 4), 'Trim')
    parts = [mk(name, body, 0.003, 1, weight=1.6)]

    def lod1(p):
        lp = [(-0.30, 0.0), (0.30, 0.0), (0.30, 0.15), (0.25, 0.215), (0.0, 0.25), (-0.05, 0.35),
              (-0.30, 0.35)]
        p.extrude(lp, 'X', -0.152, 0.152, 'Proxy')
    l1 = proxy(f'{name}_P1', lod1, 0.35)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -0.152, 0.152, -0.30, 0.30, 0, 0.35, 'Proxy'), 0.2)
    return Item(name, parts, l1, l2, (0.05, 0.2), {})


def build_steering_wheel():
    name = 'SteeringWheel'
    R, r = 0.172, 0.017

    def body(p):
        ring = [V((R * cos(2 * pi * i / 24), 0.0, R * sin(2 * pi * i / 24))) for i in range(24)]
        p.sweep(ring, [(a * 1.0, b * 1.25) for a, b in K.circle(r, 6)], 'SWRim', closed=True, side=(0, 1, 0))
        p.loft([[V((u, y, v)) for u, v in rrect(0.15, 0.12, 0.035, 2)] for y in (-0.055, -0.02)], 'SWHub')
        p.cylinder((0, -0.02, 0), (0, 0.05, 0), 0.035, 'SWHub', n=12)
        for a in (0, pi, -pi / 2):
            d = V((cos(a), 0, sin(a)))
            p.sweep([d * 0.065 + V((0, -0.03, 0)), d * (R - 0.005) + V((0, -0.005, 0))],
                    rrect(0.012 if a != -pi / 2 else 0.05, 0.035 if a != -pi / 2 else 0.012, 0.004, 1), 'Trim',
                    side=(0, 1, 0))
    parts = [mk(name, body, 0.0, weight=1.3)]

    def lod1(p):
        ring = [V((R * cos(2 * pi * i / 10), 0.0, R * sin(2 * pi * i / 10))) for i in range(10)]
        p.sweep(ring, K.circle(r * 1.3, 4), 'Proxy', closed=True, side=(0, 1, 0))
        bx(p, -0.075, 0.075, -0.058, 0.05, -0.06, 0.06, 'Proxy')
        bx(p, 0.07, R, -0.035, 0.0, -0.02, 0.02, 'Proxy')
        bx(p, -R, -0.07, -0.035, 0.0, -0.02, 0.02, 'Proxy')
        bx(p, -0.03, 0.03, -0.035, 0.0, -R, -0.06, 'Proxy')

    def lod2(p):
        oc = [(R + r) * 1.05 * V((cos(pi / 8 + k * pi / 4), sin(pi / 8 + k * pi / 4), 0)) for k in range(8)]
        p.extrude([(v.x, v.y) for v in oc], 'Y', -0.06, 0.05, 'Proxy')
    l1 = proxy(f'{name}_P1', lod1, 0.35)
    l2 = proxy(f'{name}_P2', lod2, 0.2)
    return Item(name, parts, l1, l2, (0.04, 0.12), {})


# =============================================================================
# kit, bake, LODs
# =============================================================================

def build_kit():
    build_materials()
    return [build_tractor(), build_platform(), build_console_wagon(), build_steering_wagon(),
            build_tote_trolley(), build_console(), build_steering_wheel()]


def bake(units, bake_size, save_size):
    parts = [p for u in units for p in u.parts]
    proxies = [p for u in units for p in u.proxies]
    K.atlas_unwrap(parts + proxies, margin=GAP, weights=WEIGHTS)
    offs = []
    for i, u in enumerate(units):
        d = V(((i % 4) * SPACING, (i // 4) * SPACING, 0))
        for o in u.parts + u.proxies:
            o.location = o.location + d
        offs.append(d)
    scene = bpy.context.scene
    scene.world = scene.world or bpy.data.worlds.new('BakeWorld')
    scene.world.light_settings.distance = 0.3
    for p in proxies:
        p.hide_render = True
    margin = max(2, int(GAP * bake_size / 2))
    paths = LU.gpu_or_cpu(K.bake_texture_set, parts, OUT_DIR, PREFIX, bake_size, save_size, margin=margin)
    for p in proxies:
        p.hide_render = False
    jobs = [(prx, u.parts, reach) for u in units if isinstance(u, Item) for prx, reach in zip(u.proxies, u.reach)]
    dim = (0.02, 0.02, 0.022, 1.0)
    maps = [(paths['albedo'], 'rgb', dim), (paths['metallic'], 'rgba', (0, 0, 0, 0.4)),
            (paths['normal'], 'normal', (0.5, 0.5, 1.0, 1)), (paths['occlusion'], 'rgb', (0.5, 0.5, 0.5, 1))]
    if jobs:
        LU.gpu_or_cpu(K.transfer_to_proxies, jobs, maps, margin=max(1, int(GAP * save_size / 2)))
    for d, u in zip(offs, units):
        for o in u.parts + u.proxies:
            o.location = o.location - d
    mat = K.make_runtime_material(PREFIX, paths)
    K.assign_single_material(parts + proxies, mat)
    return paths


def flatten(o):
    if o.location.length > 0:
        o.data.transform(Matrix.Translation(o.location))
        o.location = (0, 0, 0)


def build_lods(units):
    for u in units:
        pc = u.piece
        if isinstance(u, Item):
            for o in u.parts + u.proxies:
                flatten(o)
            lod0 = K.join(u.parts, f'{u.name}__{u.name}_LOD0')        # exported as <Name>_LODn
            for i, p in enumerate(u.proxies, 1):
                p.name = p.data.name = f'{u.name}__{u.name}_LOD{i}'
            pc.meshes[None] = [lod0] + u.proxies
            continue
        extra2 = []
        for node, parts in pc.nodes.items():
            stem = f'{u.name}__{node or u.name}'
            levels = []
            for lvl in (1, 2):
                keep = []
                for p in parts:
                    rule = u.plan.get(p.name, (1.0, 1.0))[lvl - 1]
                    if rule is not None:
                        keep.append(K.make_lod(p, rule, f'{p.name}_L{lvl}', sharp=40))
                if lvl == 2 and node in u.merge:
                    extra2 += keep
                    continue
                if keep:
                    levels.append(K.join(keep, f'{stem}_LOD{lvl}'))
            pc.meshes[node] = [K.join(parts, f'{stem}_LOD0')] + levels
        if extra2:
            body2 = pc.meshes[None][2]
            pc.meshes[None][2] = K.join([body2] + extra2, body2.name)
        for meshes in pc.meshes.values():
            for m in meshes:
                flatten(m)


def export(units):
    for u in units:
        pc = u.piece
        root = KM.rig(pc)
        for name, a in u.yaw.items() if isinstance(u, Vehicle) else ():
            next(o for o in root.children_recursive if o.name == name).rotation_euler.z = a
        bpy.context.view_layer.update()
        meshes = [m for ms in pc.meshes.values() for m in ms]
        for m in meshes:
            m.name = m.name.split('__', 1)[1]
        K.export_fbx(root, os.path.join(OUT_DIR, f'{pc.name}.fbx'))
        for o in root.children_recursive:
            o.name = f'{pc.name}__{o.name}'


# =============================================================================
# previews
# =============================================================================

def _render(scene, cam, path, res, spp):
    try:
        KM.render(scene, cam, path, res, spp)
    except RuntimeError as ex:
        print(f'[tugger] GPU render failed ({ex}); retrying on the CPU')
        from rail_shuttle import use_cpu
        use_cpu(scene)
        KM.render(scene, cam, path, res, spp)


def load_unit_mesh(name):
    """LOD0 mesh of a LoadUnits piece (from build/LoadUnits.blend), for previews only."""
    blend = os.path.join(BUILD_DIR, 'LoadUnits.blend')
    if not os.path.exists(blend):
        return None
    with bpy.data.libraries.load(blend, link=False) as (src, dst):
        dst.objects = [n for n in src.objects if n == f'{name}_LOD0']
    return dst.objects[0] if dst.objects else None


def preview_renders(units, quick):
    scene = bpy.context.scene
    by = {u.name: u for u in units}
    for u in units:
        for o in [u.piece.root] + list(u.piece.root.children_recursive):
            o.hide_render = True
    KM.studio(scene, k=1.5, floor=120)
    spp = 32 if quick else 128
    res = (1000, 625) if quick else (1800, 1100)
    out = lambda n: os.path.join(PREVIEW_DIR, f'tugger_{n}.png')        # noqa: E731
    T, Rz = Matrix.Translation, lambda a: Matrix.Rotation(a, 4, 'Z')    # noqa: E731
    R180 = Matrix.Rotation(pi, 4, 'Z')
    tote = load_unit_mesh('SmallLoadCarrier_600x400x280')
    load = load_unit_mesh('PalletLoad_Cartons')

    def items(coll, u, base, level):
        for name, pos in u.auth_sockets.items():
            if not name.startswith('Slot_'):
                continue
            M_ = base @ T(pos) @ Rz(u.yaw.get(name, 0.0))
            if u.name == 'ConsoleWagon':
                KM.place(by['CentralConsole'].piece, M_, coll, level)
            elif u.name == 'SteeringWheelWagon':
                KM.place(by['SteeringWheel'].piece, M_, coll, level)
            elif u.name == 'ToteTrolley' and tote:
                c = tote.copy()
                coll.objects.link(c)
                c.parent = None
                c.hide_render = False
                c.matrix_world = M_ @ R180
        if u.name == 'PlatformTrolley' and load:
            c = load.copy()
            coll.objects.link(c)
            c.parent = None
            c.hide_render = False
            c.matrix_world = base @ T(u.auth_sockets['LoadSocket']) @ R180

    def train(coll, level=0, bend=0.0):
        order = ['TowTractor', 'ConsoleWagon', 'SteeringWheelWagon', 'ToteTrolley', 'PlatformTrolley']
        base = Matrix.Identity(4)
        prev = None
        for i, n in enumerate(order):
            u = by[n]
            if prev is not None:
                hr = prev[0] @ V(prev[1].auth_sockets['Hitch_Rear'])
                hf = V(u.auth_sockets['Hitch_Front'])
                yaw = bend * i
                base = T(hr) @ Rz(yaw) @ T(-hf)
            KM.place(u.piece, base, coll, level)
            items(coll, u, base, level)
            prev = (base, u)
    coll = K.collection('PreviewTrain')
    train(coll)
    KM.scale_figure(coll, (0.0, -0.45, 0.22), 0.0)
    hero = ((4.2, 3.0, 2.3), (0.0, -2.6, 0.7), 30)
    _render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Side', (-6.5, -3.3, 1.4), (0.0, -3.3, 0.8), 28), out('side'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Tractor', (2.0, 2.4, 1.6), (0.0, 0.0, 0.6), 40), out('tractor'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Rear', (-1.6, -9.0, 1.9), (0.0, -6.0, 0.6), 35), out('rear'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Parts', (1.5, -2.2, 1.6), (0.0, -2.6, 1.0), 40), out('parts'), res, spp)
    coll.hide_render = True
    for lvl in (1, 2):
        c = K.collection(f'PreviewTrain_LOD{lvl}')
        train(c, lvl)
        _render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (1000, 625), spp)
        c.hide_render = True
    coll.hide_render = False


def look(units, quick):
    scene = bpy.context.scene
    KM.studio(scene, k=1.5)
    for u in units:
        for p in u.proxies:
            p.hide_render = True
    x = 0.0
    for u in units:
        w = max(max(o.bound_box[i][0] for i in range(8)) + o.location.x for o in u.parts) - \
            min(min(o.bound_box[i][0] for i in range(8)) + o.location.x for o in u.parts)
        x += w / 2 + 0.3
        for o in u.parts:
            o.location.x += x
        x += w / 2
    spp = 24 if quick else 96
    KM.render(scene, KM.camera(scene, 'Cam_Look', (x / 2 + 1.0, -6.5, 2.6), (x / 2, 0, 0.6), 35),
              os.path.join(PREVIEW_DIR, 'tugger_look.png'), (1600, 800), spp)
    KM.render(scene, KM.camera(scene, 'Cam_LookT', (2.2, -2.0, 1.6), (0.55, 0, 0.6), 40),
              os.path.join(PREVIEW_DIR, 'tugger_look_tractor.png'), (1000, 700), spp)


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
    K.setup_cycles_gpu(scene, backends=('CUDA', 'OPTIX', 'HIP', 'ONEAPI', 'METAL'))   # OptiX faulted on long bakes
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    units = build_kit()
    if args.look:
        look(units, args.quick)
        return
    bake(units, args.bake, args.save)
    build_lods(units)
    pieces = [u.piece for u in units]
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    export(units)
    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(units, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[tugger] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
