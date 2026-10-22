"""
SimuLean asset: manual assembly workbench -- the visual of an assembly station (Combiner / AssemblyStation): the
container (base part) lies on the worktop, the operator takes the components from the bins and screws them on.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/assembly_bench.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/AssemblyBench/AssemblyBench.fbx   (+ 5 PNG maps)
    Tools/Blender/build/AssemblyBench.blend

Real-world reference: lean / ESD assembly workbenches of aluminium profile (1.6 x 0.8 m, worktop 0.90 m): picking
bins on a rear rail, LED light bar, a tool balancer with an in-line electric screwdriver, a work-instruction
monitor, a drawer unit, a stack light.  Same footprint as the AssemblyWorkstation model (both stay selectable).
Library palette: natural aluminium, grey ESD mat, blue bins, graphite drawers, orange accents.

Frame as the other assets: authored front = -Y (Unity +Z: the operator's side), +Z up (Unity +Y), origin on the
floor at the centre of the footprint.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    AssemblyBench           root, on the floor at the footprint centre
    |- Body                 frame, worktop, bins, light bar, monitor, drawers (static)
    |- Screwdriver          the tool on the balancer cable, pivot at its bit (-0.25, 1.22, 0.10): translate
    |                       along local Y (-0.30 .. 0: down to the work and back up)
    |- Beacon / Lamp_*      stack light on the left upright (station_parts)
    |- WorkSocket           the work position on the mat (-0.10, 0.90, 0.10)
    '- PartsSocket          in the middle lower picking bin (-0.23, 1.13, -0.235): components are drawn from here
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_materials as M              # noqa: E402
import simulean_kit as K                    # noqa: E402
from simulean_kit import Part, finish       # noqa: E402
import station_parts as SP                  # noqa: E402

HX, Y0, Y1 = 0.80, -0.40, 0.40   # bench half length, front, back
TOP_Z = 0.90                     # worktop surface
UP_Z = 1.95                      # rear uprights top
PR = 0.0225                      # profile half size
BIN_ROWS = (1.12, 1.42)          # bin bottoms
BIN_N, BIN_X0, BIN_W, BIN_D, BIN_GAP = 3, -0.72, 0.30, 0.25, 0.04   # bins per row (left of the monitor)
TOOL = (-0.25, -0.10, 1.22)      # screwdriver bit at rest
BEACON_BASE = (-HX + PR, Y1 - PR, UP_Z)

MATS = {}


def build_materials():
    MATS.update({
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.57), rough=0.35, metal=0.9),
        'Mat': M.plastic('Mat', color=(0.16, 0.20, 0.21), rough=0.85, grain=0.15),
        'Edge': M.plastic('Edge', color=(0.02, 0.02, 0.022), rough=0.6),
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.42, wear=0.25, dirt=0.6),
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Bin': M.plastic('Bin', color=(0.03, 0.12, 0.40), rough=0.5),
        'Light': M.lens('Light', (0.92, 0.95, 1.0)),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Black': M.plastic('Black'),
        'Steel': M.dark_steel('Steel', color=(0.30, 0.31, 0.32), rough=0.3, metal=0.9),
    })
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def profile(p, a, b, mat='Alu'):
    """45 mm aluminium profile between two points along one axis."""
    lo = tuple(min(a[i], b[i]) - (PR if a[i] == b[i] else 0.0) for i in range(3))
    hi = tuple(max(a[i], b[i]) + (PR if a[i] == b[i] else 0.0) for i in range(3))
    p.box_minmax(lo, hi, mat)


def frame(p):
    xs, ys = (-HX + PR, HX - PR), (Y0 + PR, Y1 - PR)
    for x in xs:
        for y in ys:
            top = UP_Z if y > 0 else TOP_Z - 0.04
            profile(p, (x, y, 0.03), (x, y, top))
            p.cylinder((x, y, 0.0), (x, y, 0.03), 0.025, 'Black', n=10)                    # levelling foot
        profile(p, (x, ys[0], 0.20), (x, ys[1], 0.20))                                       # side rails
        profile(p, (x, ys[0], TOP_Z - 0.06), (x, ys[1], TOP_Z - 0.06))
    for y in ys:
        profile(p, (xs[0], y, 0.20), (xs[1], y, 0.20))
    profile(p, (xs[0], ys[1], TOP_Z - 0.06), (xs[1], ys[1], TOP_Z - 0.06))
    profile(p, (xs[0], ys[1], UP_Z - PR), (xs[1], ys[1], UP_Z - PR))                         # top crossbar
    for z in BIN_ROWS:                                                                       # bin rails
        profile(p, (xs[0], ys[1], z - 0.03), (xs[1], ys[1], z - 0.03))
    p.box_minmax((xs[0] + PR, Y0 + 0.05, 0.22), (xs[1] - PR, Y1 - 0.05, 0.24), 'Panel')      # lower shelf
    p.box_minmax((xs[0] + PR, Y1 - 0.03, TOP_Z + 0.02), (xs[1] - PR, Y1 - 0.02, UP_Z - 2 * PR), 'Panel')  # back panel
    # light bar on two arms from the top crossbar
    for x in (-0.55, 0.55):
        profile(p, (x, ys[1], UP_Z - PR), (x, Y0 + 0.20, UP_Z - PR))
    p.box_minmax((-0.60, Y0 + 0.14, UP_Z - 2 * PR - 0.04), (0.60, Y0 + 0.24, UP_Z - 2 * PR), 'Alu')
    p.box_minmax((-0.58, Y0 + 0.15, UP_Z - 2 * PR - 0.043), (0.58, Y0 + 0.23, UP_Z - 2 * PR - 0.04), 'Light')
    # balancer rail and balancer
    profile(p, (TOOL[0], ys[1], UP_Z - PR - 0.10), (TOOL[0], TOOL[1], UP_Z - PR - 0.10))
    p.box_minmax((TOOL[0] - PR, ys[1] - PR, UP_Z - PR - 0.10), (TOOL[0] + PR, ys[1] + PR, UP_Z - PR), 'Alu')
    p.cylinder((TOOL[0], TOOL[1], UP_Z - 0.15), (TOOL[0], TOOL[1], UP_Z - 0.30), 0.045, 'Orange', n=18)
    p.cylinder((TOOL[0], TOOL[1], UP_Z - 0.30), (TOOL[0], TOOL[1], UP_Z - 0.33), 0.015, 'Black', n=10)


def worktop(p):
    p.box_minmax((-HX, Y0, TOP_Z - 0.04), (HX, Y1, TOP_Z - 0.004), 'Panel')
    p.box_minmax((-HX + 0.01, Y0 + 0.01, TOP_Z - 0.004), (HX - 0.01, Y1 - 0.04, TOP_Z), 'Mat')
    p.box_minmax((-HX, Y0 - 0.006, TOP_Z - 0.04), (HX, Y0, TOP_Z), 'Edge')                    # front edge
    # earth point and power strip under the front edge
    p.box_minmax((0.10, Y0 + 0.01, TOP_Z - 0.10), (0.60, Y0 + 0.06, TOP_Z - 0.05), 'Black')
    for k in range(4):
        p.box_minmax((0.15 + k * 0.11, Y0 + 0.005, TOP_Z - 0.09), (0.21 + k * 0.11, Y0 + 0.01, TOP_Z - 0.06),
                     'Panel')
    p.cylinder((-0.70, Y0 + 0.002, TOP_Z - 0.06), (-0.70, Y0 - 0.008, TOP_Z - 0.06), 0.012, 'Orange', n=12)


def drawers(p):
    x0, x1 = 0.30, HX - 2 * PR
    p.box_minmax((x0, Y0 + 0.04, 0.24), (x1, Y1 - 0.06, TOP_Z - 0.09), 'Frame')
    for i, (za, zb) in enumerate(((0.26, 0.42), (0.44, 0.60), (0.62, 0.79))):
        p.box_minmax((x0 + 0.015, Y0 + 0.035, za), (x1 - 0.015, Y0 + 0.04, zb), 'Frame')
        p.box_minmax((x0 + 0.12, Y0 + 0.02, zb - 0.05), (x1 - 0.12, Y0 + 0.035, zb - 0.03), 'Alu')


def bins(p):
    """Open-front picking bins hung on the two rear rails."""
    for z in BIN_ROWS:
        for i in range(BIN_N):
            x0 = BIN_X0 + i * (BIN_W + BIN_GAP)
            w, d = BIN_W, BIN_D
            x1, yb, yf = x0 + w, Y1 - 0.04, Y1 - 0.04 - d
            t = 0.006
            p.box_minmax((x0, yf, z), (x1, yb, z + t), 'Bin')                              # floor
            p.box_minmax((x0, yb - t, z), (x1, yb, z + 0.20), 'Bin')                        # back
            p.box_minmax((x0, yf, z), (x1, yf + t, z + 0.09), 'Bin')                        # low front
            for x in (x0, x1 - t):                                                          # sides
                p.box_minmax((x, yf, z), (x + t, yb, z + 0.09), 'Bin')
                p.box_minmax((x, yb - 0.10, z + 0.09), (x + t, yb, z + 0.20), 'Bin')
            p.box_minmax((x0 + 0.08, yf - 0.002, z + 0.03), (x1 - 0.08, yf, z + 0.07), 'Panel')   # label


def monitor(p):
    x = 0.52
    profile(p, (x, Y1 - PR, TOP_Z), (x, Y1 - PR, 1.10), 'Alu')
    p.box_minmax((x - 0.02, Y0 + 0.30, 1.30), (x + 0.02, Y1 - PR, 1.34), 'Alu')               # arm
    p.box_minmax((x - 0.20, Y0 + 0.26, 1.18), (x + 0.20, Y0 + 0.30, 1.44), 'Black')
    p.box_minmax((x - 0.185, Y0 + 0.256, 1.195), (x + 0.185, Y0 + 0.26, 1.425), 'Screen')
    p.box_minmax((x - 0.02, Y0 + 0.30, 1.10), (x + 0.02, Y1 - PR, 1.14), 'Alu')               # lower arm (rigidity)


def build_body():
    def f(p):
        frame(p)
        worktop(p)
        drawers(p)
        bins(p)
        monitor(p)
    return mk('Body', f)


def build_screwdriver():
    tx, ty, tz = TOOL

    def f(p):
        p.cylinder((tx, ty, tz + 0.06), (tx, ty, UP_Z - 0.33), 0.002, 'Black', n=6)          # balancer cable
        p.lathe([(tz + 0.0, 0.002), (tz + 0.03, 0.003), (tz + 0.035, 0.012), (tz + 0.06, 0.018),
                 (tz + 0.20, 0.020), (tz + 0.24, 0.016), (tz + 0.26, 0.010)], 16, 'Black',
                center=(tx, ty, 0), axis='Z', cap0=True, cap1=True)
        p.lathe([(tz + 0.10, 0.0215), (tz + 0.16, 0.0215)], 16, 'Orange', center=(tx, ty, 0), axis='Z',
                cap0=False, cap1=False)                                                    # grip
        p.sweep([(tx, ty + 0.012, tz + 0.25), (tx + 0.01, ty + 0.03, tz + 0.32), (tx + 0.01, ty + 0.03, tz + 0.60)],
                K.circle(0.005, 8), 'Black')                                                  # power cable
        p.box_minmax((tx - 0.012, ty - 0.024, tz + 0.17), (tx + 0.012, ty - 0.018, tz + 0.20), 'Steel')  # trigger
    return mk('Screwdriver', f, bevel=0.001)


def build_bench():
    build_materials()
    objs = [build_body(), build_screwdriver()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.10, size=1.0)
    return objs + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), 'Screwdriver': (0.5, 0.25), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Screwdriver': 2.0, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'AssemblyBench'),
         'Screwdriver': (TOOL, 'AssemblyBench'),
         **SP.beacon_nodes(BEACON_BASE, 'AssemblyBench'),
         'WorkSocket': ((-0.10, -0.10, TOP_Z), 'AssemblyBench'),
         'PartsSocket': ((BIN_X0 + 1.5 * BIN_W + BIN_GAP, Y1 - 0.04 - BIN_D / 2, BIN_ROWS[0] + 0.006),
                         'AssemblyBench')}


def build_preview():
    return build_bench()


if __name__ == '__main__':
    SP.station_main('AssemblyBench', build_bench, LOD_PLAN, NODES, TEXEL_WEIGHTS)
