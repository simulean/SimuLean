"""
SimuLean asset: unpacking / decanting workstation -- the visual of a Separator: the container (carton, tote, pallet)
arrives on a short roller section, is opened there, its contents are laid out on the worktop, the empty container goes
onto the empties chute.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/unpacking_station.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/UnpackingStation/UnpackingStation.fbx   (+ 5 PNG maps)
    Tools/Blender/build/UnpackingStation.blend

Real-world reference: decanting stations of goods-in / e-commerce warehouses: a gravity or powered roller section at
working height where the inbound case or pallet is opened (a pop-up stop at its end), a worktop beside it, an inclined
roller chute behind for the empty totes, a mesh cage for the cardboard waste, a light bar, a terminal with a hand
scanner and a stack light.  2.7 x 1.5 m, roller top 0.75 m (a EUR pallet 1200 x 800 fits on it), worktop 0.90 m.
Library palette: graphite frames, natural aluminium, light-grey worktop, orange stop and accents.

Model frame (2026-11-02): authored front = -Y (the operator's side), flow along +X (in on -X: the rollers; out on +X:
the worktop), +Z up; exported turned 180 deg (station_parts.STATION_TURN), so Unity (x, y, z) = authored (x, z, y):
front on Unity -Z, flow along Unity +X.  Origin on the floor at the centre of the footprint.  Coordinates below are
Unity ones (x along the flow, y up, z towards the back).

Animation hierarchy (every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    UnpackingStation        root, on the floor at the footprint centre
    |- Body                 roller section, worktop, empties chute, waste cage, light bar, terminal (static)
    |- Stop                 the pop-up stop at the end of the rollers, pivot (-0.06, 0.75, -0.30): translate along
    |                       local Y (0 = up, holding the container; -0.06 = down, letting it through)
    |- Beacon / Lamp_*      stack light on the right upright (station_parts)
    |- WorkSocket           where the container stands to be opened: the roller top centre (-0.725, 0.75, -0.30)
    |- PiecesSocket         where the separated pieces wait, side by side along x: the worktop centre (0.65, 0.90, -0.30)
    |- EmptiesSocket        the empties chute's stop end, where an empty tote waits (-0.725, 0.46, 0.62)
    '- StaffSocket          where the operator stands, in front of the junction (-0.10, 0, -1.15)
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

Y0, Y1 = -0.75, 0.15             # front and back of the work line (rollers, worktop)
YC = (Y0 + Y1) / 2               # the work line's centre
RX0, RX1 = -1.35, -0.10          # roller section
RZ = 0.75                        # roller top
RR = 0.025                       # roller radius
RPITCH = 0.10
FW = 0.45                        # half the inner width between the roller side frames
TX0, TX1 = -0.05, 1.35           # worktop
TZ = 0.90                        # worktop surface
UP_Z = 2.00                      # rear uprights top
PR = 0.0225                      # 45 mm aluminium profile half size
CY0, CY1 = 0.28, 0.75            # empties chute (along y, falling towards the back)
CZ0, CZ1 = 0.56, 0.42            # its roller top at the front / back end
CAGE = (0.75, 1.30, 0.28, 0.75, 1.00)   # waste cage x0, x1, y0, y1, height
STOP = (RX1 + 0.04, YC, RZ)      # pop-up stop pivot (its top at rest is 0.06 above the rollers)
BEACON_BASE = (TX1 - PR, Y1 - PR, UP_Z)

MATS = {}


def build_materials():
    MATS.update({
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.57), rough=0.35, metal=0.9),
        'Roller': M.dark_steel('Roller', color=(0.62, 0.63, 0.64), rough=0.25, metal=0.95),
        'Top': M.paint('Top', (0.66, 0.67, 0.65), rough=0.55, wear=0.2, dirt=0.7),
        'Edge': M.plastic('Edge', color=(0.02, 0.02, 0.022), rough=0.6),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.42, wear=0.25, dirt=0.6),
        'Mesh': M.galvanised('Mesh'),
        'Light': M.lens('Light', (0.92, 0.95, 1.0)),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Black': M.plastic('Black'),
        'Yellow': M.paint('Yellow', (0.95, 0.70, 0.02), rough=0.4, wear=0.3),
        'Cardboard': M.paint('Cardboard', (0.45, 0.30, 0.16), rough=0.8, wear=0.0, dirt=0.4),
    })
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def profile(p, a, b, mat='Alu'):
    """45 mm profile between two points along one axis."""
    lo = tuple(min(a[i], b[i]) - (PR if a[i] == b[i] else 0.0) for i in range(3))
    hi = tuple(max(a[i], b[i]) + (PR if a[i] == b[i] else 0.0) for i in range(3))
    p.box_minmax(lo, hi, mat)


def rollers(p):
    """Gravity roller section: side channels, rollers across the flow, legs with feet, a side guide at the back."""
    for sy in (-1, 1):
        ya, yb = sorted((YC + sy * FW, YC + sy * (FW + 0.03)))
        p.box_minmax((RX0, ya, RZ - 0.10), (RX1, yb, RZ + 0.01), 'Frame')           # side channel
        for x in (RX0 + 0.10, RX1 - 0.10):
            p.box_minmax((x - 0.03, ya, 0.03), (x + 0.03, yb, RZ - 0.10), 'Frame')   # leg
            p.cylinder((x, (ya + yb) / 2, 0.0), (x, (ya + yb) / 2, 0.03), 0.03, 'Black', n=10)
    for x in (RX0 + 0.10, RX1 - 0.10):
        p.box_minmax((x - 0.02, YC - FW, 0.20), (x + 0.02, YC + FW, 0.24), 'Frame')   # cross brace
    p.box_minmax((RX0 + 0.10, YC - FW, 0.20), (RX1 - 0.10, YC - FW + 0.03, 0.24), 'Frame')
    n = int(round((RX1 - RX0 - 0.06) / RPITCH))
    for i in range(n):
        x = RX0 + 0.05 + i * RPITCH
        p.cylinder((x, YC - FW, RZ - RR), (x, YC + FW, RZ - RR), RR, 'Roller', n=12)
        p.cylinder((x, YC - FW - 0.004, RZ - RR), (x, YC - FW, RZ - RR), 0.007, 'Alu', n=6)   # axle ends
        p.cylinder((x, YC + FW, RZ - RR), (x, YC + FW + 0.004, RZ - RR), 0.007, 'Alu', n=6)
    # back side guide (the container is pushed against it), low enough to reach over
    gy = YC + FW + 0.03
    for x in (RX0 + 0.20, RX1 - 0.20):
        p.box_minmax((x - 0.01, gy, RZ + 0.01), (x + 0.01, gy + 0.02, RZ + 0.10), 'Alu')
    p.box_minmax((RX0 + 0.05, gy, RZ + 0.08), (RX1 - 0.05, gy + 0.025, RZ + 0.11), 'Yellow')
    # stop housing at the end (the plate itself is the Stop node)
    p.box_minmax((STOP[0] - 0.025, YC - 0.22, RZ - 0.16), (STOP[0] + 0.025, YC + 0.22, RZ - 0.06), 'Frame')
    p.cylinder((STOP[0], YC + 0.10, RZ - 0.30), (STOP[0], YC + 0.10, RZ - 0.16), 0.02, 'Roller', n=12)  # cylinder


def worktop(p):
    xs, ys = (TX0 + PR, TX1 - PR), (Y0 + PR + 0.05, Y1 - PR)
    for x in xs:
        for y in ys:
            top = UP_Z if y > YC else TZ - 0.04
            profile(p, (x, y, 0.03), (x, y, top))
            p.cylinder((x, y, 0.0), (x, y, 0.03), 0.025, 'Black', n=10)
        profile(p, (x, ys[0], 0.20), (x, ys[1], 0.20))
        profile(p, (x, ys[0], TZ - 0.07), (x, ys[1], TZ - 0.07))
    for y in ys:
        profile(p, (xs[0], y, 0.20), (xs[1], y, 0.20))
    profile(p, (xs[0], ys[1], TZ - 0.07), (xs[1], ys[1], TZ - 0.07))
    profile(p, (xs[0], ys[1], UP_Z - PR), (xs[1], ys[1], UP_Z - PR))                 # top crossbar
    p.box_minmax((xs[0] + PR, ys[0], 0.22), (xs[1] - PR, ys[1], 0.24), 'Panel')      # lower shelf
    # worktop slab with a laminate top and a dark front edge
    p.box_minmax((TX0, Y0 + 0.05, TZ - 0.04), (TX1, Y1, TZ - 0.004), 'Panel')
    p.box_minmax((TX0 + 0.01, Y0 + 0.06, TZ - 0.004), (TX1 - 0.01, Y1 - 0.02, TZ), 'Top')
    p.box_minmax((TX0, Y0 + 0.044, TZ - 0.04), (TX1, Y0 + 0.05, TZ), 'Edge')
    # flattened cartons on the lower shelf (cardboard brown, as a panel colour)
    p.box_minmax((xs[0] + 0.10, ys[0] + 0.05, 0.24), (xs[0] + 0.70, ys[1] - 0.06, 0.27), 'Cardboard')
    # rear tool rail with a knife holder and a tape dispenser
    profile(p, (xs[0], ys[1], 1.25), (xs[1], ys[1], 1.25))
    p.box_minmax((0.15, ys[1] - PR - 0.05, 1.18), (0.25, ys[1] - PR, 1.30), 'Black')
    p.box_minmax((0.18, ys[1] - PR - 0.045, 1.20), (0.22, ys[1] - PR - 0.04, 1.32), 'Orange')   # knife handle
    p.box_minmax((0.40, ys[1] - PR - 0.08, 1.16), (0.60, ys[1] - PR, 1.24), 'Black')
    p.cylinder((0.45, ys[1] - PR - 0.04, 1.27), (0.55, ys[1] - PR - 0.04, 1.27), 0.045, 'Panel', n=16)   # tape roll
    # light bar on two arms from the top crossbar
    for x in (0.10, TX1 - 0.25):
        profile(p, (x, ys[1], UP_Z - PR), (x, Y0 + 0.25, UP_Z - PR))
    p.box_minmax((0.0, Y0 + 0.19, UP_Z - 2 * PR - 0.04), (TX1 - 0.15, Y0 + 0.29, UP_Z - 2 * PR), 'Alu')
    p.box_minmax((0.02, Y0 + 0.20, UP_Z - 2 * PR - 0.043), (TX1 - 0.17, Y0 + 0.28, UP_Z - 2 * PR - 0.04), 'Light')


def terminal(p):
    """Terminal on an arm at the junction and a hand scanner in its holster."""
    x = TX0 + PR
    y = Y1 - PR
    p.box_minmax((x - 0.02, Y0 + 0.40, 1.42), (x + 0.02, y, 1.46), 'Alu')                    # arm
    p.box_minmax((x - 0.15, Y0 + 0.36, 1.30), (x + 0.15, Y0 + 0.40, 1.52), 'Black')
    p.box_minmax((x - 0.135, Y0 + 0.356, 1.315), (x + 0.135, Y0 + 0.36, 1.505), 'Screen')
    # holster on the front of the worktop, scanner in it
    hx = 1.15
    p.box_minmax((hx - 0.05, Y0 + 0.02, TZ - 0.16), (hx + 0.05, Y0 + 0.044, TZ - 0.02), 'Black')
    p.box_minmax((hx - 0.03, Y0 + 0.005, TZ - 0.12), (hx + 0.03, Y0 + 0.04, TZ + 0.02), 'Yellow')
    p.box_minmax((hx - 0.035, Y0 + 0.0, TZ + 0.02), (hx + 0.035, Y0 + 0.07, TZ + 0.07), 'Yellow')
    p.box_minmax((hx - 0.03, Y0 - 0.002, TZ + 0.03), (hx + 0.03, Y0 + 0.0, TZ + 0.06), 'Black')   # window


def empties(p):
    """Inclined roller chute behind the roller section (empty totes roll to its back stop) and a mesh cage for the
    cardboard behind the worktop."""
    x0, x1 = RX0 + 0.08, RX1 - 0.08
    slope = (CZ1 - CZ0) / (CY1 - CY0)

    def cz(y):                                   # roller top of the chute at y
        return CZ0 + slope * (y - CY0)
    for x in (x0, x1):
        p.sweep([(x, CY0, CZ0 - 0.04), (x, CY1, CZ1 - 0.04)], [(-0.015, -0.05), (0.015, -0.05), (0.015, 0.04),
                                                              (-0.015, 0.04)], 'Frame', side=(1, 0, 0))   # side rail
        for y in (CY0 + 0.06, CY1 - 0.06):
            p.box_minmax((x - 0.025, y - 0.025, 0.03), (x + 0.025, y + 0.025, cz(y) - 0.09), 'Frame')   # leg
            p.cylinder((x, y, 0.0), (x, y, 0.03), 0.028, 'Black', n=10)
    p.box_minmax((x0, CY1 - 0.08, 0.18), (x1, CY1 - 0.04, 0.22), 'Frame')                   # brace
    n = int((CY1 - CY0 - 0.04) / 0.09)
    for i in range(n):
        y = CY0 + 0.05 + i * 0.09
        z = cz(y) - RR
        p.cylinder((x0 + 0.015, y, z), (x1 - 0.015, y, z), RR, 'Roller', n=12)
    p.box_minmax((x0, CY1 - 0.02, CZ1 - 0.02), (x1, CY1 + 0.0, CZ1 + 0.10), 'Orange')        # end stop
    # cardboard waste cage: mesh walls (wires), open top, open front towards the worktop's back
    cx0, cx1, cy0, cy1, h = CAGE
    for x in (cx0, cx1):
        for y in (cy0, cy1):
            p.box_minmax((x - 0.015, y - 0.015, 0.0), (x + 0.015, y + 0.015, h), 'Mesh')
    for z in (0.05, h):
        for (a, b) in (((cx0, cy0), (cx1, cy0)), ((cx0, cy1), (cx1, cy1)), ((cx0, cy0), (cx0, cy1)),
                       ((cx1, cy0), (cx1, cy1))):
            p.box_minmax((min(a[0], b[0]) - 0.008, min(a[1], b[1]) - 0.008, z - 0.008),
                         (max(a[0], b[0]) + 0.008, max(a[1], b[1]) + 0.008, z + 0.008), 'Mesh')
    p.box_minmax((cx0, cy0, 0.0), (cx1, cy1, 0.02), 'Mesh')                                  # floor
    k = 7
    for i in range(1, k):                                                                    # vertical wires
        x = cx0 + (cx1 - cx0) * i / k
        for y in (cy0, cy1):
            p.cylinder((x, y, 0.05), (x, y, h), 0.003, 'Mesh', n=4)
        y = cy0 + (cy1 - cy0) * i / k
        p.cylinder((cx1, y, 0.05), (cx1, y, h), 0.003, 'Mesh', n=4)
        p.cylinder((cx0, y, 0.05), (cx0, y, h), 0.003, 'Mesh', n=4)
    for z in (0.30, 0.55, 0.80):
        for (a, b) in (((cx0, cy0), (cx1, cy0)), ((cx0, cy1), (cx1, cy1)), ((cx0, cy0), (cx0, cy1)),
                       ((cx1, cy0), (cx1, cy1))):
            p.cylinder((a[0], a[1], z), (b[0], b[1], z), 0.003, 'Mesh', n=4)
    # some cardboard in it
    p.box((cx0 + 0.25, cy0 + 0.22, 0.18), (0.40, 0.30, 0.32), 'Cardboard')
    p.box((cx0 + 0.30, cy0 + 0.25, 0.40), (0.38, 0.02, 0.30), 'Cardboard')


def build_body():
    def f(p):
        rollers(p)
        worktop(p)
        terminal(p)
        empties(p)
    return mk('Body', f)


def build_stop():
    x, y, z = STOP

    def f(p):
        p.box_minmax((x - 0.012, y - 0.20, z - 0.10), (x + 0.012, y + 0.20, z + 0.06), 'Orange')
        p.box_minmax((x - 0.013, y - 0.20, z + 0.035), (x + 0.013, y + 0.20, z + 0.045), 'Black')
    return mk('Stop', f, bevel=0.002)


def build_station():
    build_materials()
    objs = [build_body(), build_stop()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.10, size=1.0)
    return objs + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.40, 0.14), 'Stop': (0.5, 0.3), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Stop': 1.5, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'UnpackingStation'),
         'Stop': (STOP, 'UnpackingStation'),
         **SP.beacon_nodes(BEACON_BASE, 'UnpackingStation'),
         'WorkSocket': (((RX0 + RX1) / 2, YC, RZ), 'UnpackingStation'),
         'PiecesSocket': (((TX0 + TX1) / 2, YC, TZ), 'UnpackingStation'),
         'EmptiesSocket': (((RX0 + RX1) / 2, CY1 - 0.13, CZ1 + 0.04), 'UnpackingStation'),
         'StaffSocket': ((-0.10, Y0 - 0.40, 0.0), 'UnpackingStation')}


def build_preview():
    return build_station()


if __name__ == '__main__':
    SP.station_main('UnpackingStation', build_station, LOD_PLAN, NODES, TEXEL_WEIGHTS, turn=SP.STATION_TURN)
