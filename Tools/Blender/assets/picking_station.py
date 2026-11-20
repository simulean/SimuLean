"""
SimuLean asset: pick-to-light workstation -- the visual of a Picking Station: the order tote stands on the worktop, the
stock is in a shelf of bins facing the operator, a light under each bin shows what to take and how many, the operator
picks into the tote and confirms on the screen.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/picking_station.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/PickingStation/PickingStation.fbx   (+ 5 PNG maps)
    Tools/Blender/build/PickingStation.blend

Real-world reference: pick-to-light / kitting workstations of order-picking and line-feeding areas: an aluminium-
profile frame, a 1.7 x 0.7 m worktop at 0.90 m with the order tote's place marked on it, a shelf of 3 x 4 open-fronted
bins (0.38 x 0.50 m) behind it, a pick module (display and confirm button) on the rail under every bin, a terminal on
an arm, a light bar and a stack light.  1.8 x 1.4 m overall.
Library palette: natural aluminium, grey ESD mat, blue bins, graphite pick modules, orange accents.

Model frame (2026-11-02): authored front = -Y (the operator's side), flow along +X, +Z up; exported turned 180 deg
(station_parts.STATION_TURN), so Unity (x, y, z) = authored (x, z, y): front on Unity -Z, flow along Unity +X.  Origin
on the floor at the centre of the footprint.  Coordinates below are Unity ones (x along the flow, y up, z towards
the back).

Animation hierarchy (every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    PickingStation          root, on the floor at the footprint centre
    |- Body                 frame, worktop, bin shelf, terminal, light bar (static)
    |- PickLights           the pick modules' displays and buttons (their own renderers: a view can light them;
    |                       baked lit)
    |- Beacon / Lamp_*      stack light on the left upright (station_parts)
    |- WorkSocket           the order tote's place on the worktop (0, 0.90, -0.33)
    |- StockSocket_0..11    the bins' floors, the centre of each bin, row by row from the bottom, left to right
    |                       (as seen from the front): x = -0.6 + 0.4 k, y = 0.956 / 1.306 / 1.656, z = 0.40;
    |                       a bin is 0.38 wide and 0.50 deep inside its walls
    '- StaffSocket          where the operator stands (0, 0, -1.05)
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402
import station_parts as SP                  # noqa: E402

HX = 0.85                        # half length of the worktop and the shelf
Y0, Y1 = -0.70, 0.00             # worktop front, back
SY0, SY1 = 0.10, 0.70            # bin shelf front, back
TOP_Z = 0.90                     # worktop surface
UP_Z = 2.05                      # uprights top
PR = 0.0225                      # 45 mm profile half size
LEVELS = (0.95, 1.30, 1.65)      # bin floors (underside)
BIN_N, BIN_W, BIN_P, BIN_D = 4, 0.38, 0.40, 0.50   # bins per level, width, pitch, depth
BIN_T = 0.006                    # wall
BIN_Y0 = SY0 + 0.05              # bin front
TOTE = (0.0, -0.33)              # the order tote's place (x, y)
BEACON_BASE = (-HX + PR, SY1 - PR, UP_Z)

MATS = {}


def build_materials():
    MATS.update({
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.57), rough=0.35, metal=0.9),
        'Mat': M.plastic('Mat', color=(0.16, 0.20, 0.21), rough=0.85, grain=0.15),
        'Edge': M.plastic('Edge', color=(0.02, 0.02, 0.022), rough=0.6),
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.42, wear=0.25, dirt=0.6),
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Yellow': M.paint('Yellow', (0.95, 0.70, 0.02), rough=0.4, wear=0.2),
        'Bin': M.plastic('Bin', color=(0.03, 0.12, 0.40), rough=0.5),
        'Light': M.lens('Light', (0.92, 0.95, 1.0)),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Digits': M.lens('Digits', (1.00, 0.08, 0.02)),
        'Button': M.lens('Button', (0.10, 0.85, 0.30)),
        'Black': M.plastic('Black'),
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


def bin_x(k):
    """Centre x of bin column k (0 = left as seen from the front, -X)."""
    return (k - (BIN_N - 1) / 2) * BIN_P


def frame(p):
    xs = (-HX + PR, HX - PR)
    for x in xs:
        for y, top in ((Y0 + PR + 0.04, TOP_Z - 0.04), (SY0 + PR, UP_Z), (SY1 - PR, UP_Z)):
            profile(p, (x, y, 0.03), (x, y, top))
            p.cylinder((x, y, 0.0), (x, y, 0.03), 0.025, 'Black', n=10)
        profile(p, (x, Y0 + PR + 0.04, 0.20), (x, SY1 - PR, 0.20))                          # side rails
        profile(p, (x, Y0 + PR + 0.04, TOP_Z - 0.06), (x, SY0 + PR, TOP_Z - 0.06))
        profile(p, (x, SY0 + PR, UP_Z - PR), (x, SY1 - PR, UP_Z - PR))
        for z in LEVELS:
            profile(p, (x, SY0 + PR, z - PR), (x, SY1 - PR, z - PR))                       # shelf side beams
    for y in (Y0 + PR + 0.04, SY0 + PR, SY1 - PR):
        profile(p, (xs[0], y, 0.20), (xs[1], y, 0.20))
    for y in (SY0 + PR, SY1 - PR):
        profile(p, (xs[0], y, UP_Z - PR), (xs[1], y, UP_Z - PR))
        for z in LEVELS:
            profile(p, (xs[0], y, z - PR), (xs[1], y, z - PR))                             # shelf beams
    p.box_minmax((xs[0] + PR, Y0 + 0.08, 0.22), (xs[1] - PR, SY1 - 0.05, 0.24), 'Panel')   # lower shelf
    # shelf decks (perforated panels in the bake: a plain panel)
    for z in LEVELS:
        p.box_minmax((xs[0] + PR, SY0 + 2 * PR, z - 0.004), (xs[1] - PR, SY1 - 2 * PR, z), 'Panel')
    # back panel of the shelf
    p.box_minmax((xs[0] + PR, SY1 - PR - 0.005, LEVELS[0]), (xs[1] - PR, SY1 - PR, UP_Z - 2 * PR), 'Panel')
    # light bar over the worktop on two arms
    for x in (-0.55, 0.55):
        profile(p, (x, SY0 + PR, UP_Z - PR), (x, Y0 + 0.25, UP_Z - PR))
    p.box_minmax((-0.62, Y0 + 0.19, UP_Z - 2 * PR - 0.04), (0.62, Y0 + 0.29, UP_Z - 2 * PR), 'Alu')
    p.box_minmax((-0.60, Y0 + 0.20, UP_Z - 2 * PR - 0.043), (0.60, Y0 + 0.28, UP_Z - 2 * PR - 0.04), 'Light')


def worktop(p):
    p.box_minmax((-HX, Y0, TOP_Z - 0.04), (HX, Y1, TOP_Z - 0.004), 'Panel')
    p.box_minmax((-HX + 0.01, Y0 + 0.01, TOP_Z - 0.004), (HX - 0.01, Y1 - 0.02, TOP_Z), 'Mat')
    p.box_minmax((-HX, Y0 - 0.006, TOP_Z - 0.04), (HX, Y0, TOP_Z), 'Edge')
    # the tote's place: yellow corner marks on the mat (600 x 400)
    tx, ty = TOTE
    hw, hd, a, w = 0.31, 0.21, 0.08, 0.012
    for sx in (-1, 1):
        for sy in (-1, 1):
            cx, cy = tx + sx * hw, ty + sy * hd
            p.box_minmax((min(cx, cx - sx * a), cy - w / 2, TOP_Z), (max(cx, cx - sx * a), cy + w / 2, TOP_Z + 0.001),
                         'Yellow')
            p.box_minmax((cx - w / 2, min(cy, cy - sy * a), TOP_Z), (cx + w / 2, max(cy, cy - sy * a), TOP_Z + 0.001),
                         'Yellow')
    # confirm push button box at the front right
    p.box_minmax((0.52, Y0 + 0.04, TOP_Z), (0.66, Y0 + 0.16, TOP_Z + 0.05), 'Frame')
    p.cylinder((0.59, Y0 + 0.10, TOP_Z + 0.05), (0.59, Y0 + 0.10, TOP_Z + 0.065), 0.035, 'Orange', n=18)


def bins(p):
    """Open-fronted bins on the shelf levels, with a label card on each front."""
    for z in LEVELS:
        for k in range(BIN_N):
            x0, x1 = bin_x(k) - BIN_W / 2, bin_x(k) + BIN_W / 2
            yf, yb = BIN_Y0, BIN_Y0 + BIN_D
            t, h = BIN_T, 0.22
            p.box_minmax((x0, yf, z), (x1, yb, z + t), 'Bin')                    # floor
            p.box_minmax((x0, yb - t, z), (x1, yb, z + h), 'Bin')                # back
            p.box_minmax((x0, yf, z), (x1, yf + t, z + 0.09), 'Bin')             # low front (open grip)
            for x in (x0, x1 - t):
                p.box_minmax((x, yf, z), (x + t, yb, z + 0.09), 'Bin')            # sides: low at the front...
                p.box_minmax((x, yf + 0.18, z + 0.09), (x + t, yb, z + h), 'Bin')   # ...full at the back
            p.box_minmax((x0 + 0.10, yf - 0.002, z + 0.025), (x1 - 0.10, yf, z + 0.07), 'Panel')   # label


def pick_rails(p):
    """The rail under each level that carries the pick modules (the modules' bodies; their lit faces are
    PickLights)."""
    for z in LEVELS:
        zr = z - 2 * PR - 0.03
        p.box_minmax((-HX + 2 * PR, SY0 - 0.01, zr), (HX - 2 * PR, SY0 + 0.01, zr + 0.05), 'Alu')
        for k in range(BIN_N):
            x = bin_x(k)
            p.box_minmax((x - 0.07, SY0 - 0.045, zr), (x + 0.07, SY0 - 0.01, zr + 0.05), 'Frame')


def terminal(p):
    x = HX - PR
    p.box_minmax((x - 0.02, Y0 + 0.30, 1.32), (x + 0.02, SY0 + PR, 1.36), 'Alu')                # arm
    p.box_minmax((x - 0.20, Y0 + 0.26, 1.20), (x + 0.10, Y0 + 0.30, 1.42), 'Black')
    p.box_minmax((x - 0.185, Y0 + 0.256, 1.215), (x + 0.085, Y0 + 0.26, 1.405), 'Screen')
    # hand scanner in a holster on the left front leg
    hx, hy = -HX + PR, Y0 + PR + 0.04
    p.box_minmax((hx - 0.05, hy - PR - 0.05, TOP_Z - 0.20), (hx + 0.05, hy - PR, TOP_Z - 0.06), 'Black')
    p.box_minmax((hx - 0.03, hy - PR - 0.04, TOP_Z - 0.16), (hx + 0.03, hy - PR - 0.01, TOP_Z - 0.02), 'Yellow')


def build_body():
    def f(p):
        frame(p)
        worktop(p)
        bins(p)
        pick_rails(p)
        terminal(p)
    return mk('Body', f)


def build_pick_lights():
    def f(p):
        for z in LEVELS:
            zr = z - 2 * PR - 0.03
            for k in range(BIN_N):
                x = bin_x(k)
                yf = SY0 - 0.045
                p.box_minmax((x - 0.060, yf - 0.003, zr + 0.012), (x - 0.005, yf, zr + 0.040), 'Digits')
                p.cylinder((x + 0.035, yf, zr + 0.025), (x + 0.035, yf - 0.008, zr + 0.025), 0.014, 'Button', n=14)
    return mk('PickLights', f, bevel=0.001)


def build_station():
    build_materials()
    objs = [build_body(), build_pick_lights()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.10, size=1.0)
    return objs + SP.name_beacon(bo, lamps)


def stock_sockets():
    out = {}
    for lv, z in enumerate(LEVELS):
        for k in range(BIN_N):
            out[f'StockSocket_{lv * BIN_N + k}'] = ((bin_x(k), BIN_Y0 + BIN_D / 2, z + BIN_T), 'PickingStation')
    return out


LOD_PLAN = {'Body': (0.40, 0.14), 'PickLights': (0.5, 0.25), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'PickLights': 2.0, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'PickingStation'),
         'PickLights': ((0, SY0, LEVELS[0]), 'PickingStation'),
         **SP.beacon_nodes(BEACON_BASE, 'PickingStation'),
         'WorkSocket': ((TOTE[0], TOTE[1], TOP_Z), 'PickingStation'),
         **stock_sockets(),
         'StaffSocket': ((0.0, Y0 - 0.35, 0.0), 'PickingStation')}


def build_preview():
    return build_station()


if __name__ == '__main__':
    SP.station_main('PickingStation', build_station, LOD_PLAN, NODES, TEXEL_WEIGHTS, turn=SP.STATION_TURN)
