"""
SimuLean asset: industrial paint booth (drive-through spray booth) -- the visual of a processing station: the item
enters through the roll-up shutter on one end, is painted on the grating floor and leaves through the other end.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/paint_booth.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/PaintBooth/PaintBooth.fbx   (+ 5 PNG maps)
    Tools/Blender/build/PaintBooth.blend

Real-world reference: drive-through industrial spray booths for parts and small assemblies (inside 4 x 3 x 3 m,
roof plenum with exhaust stack, back-wall dry filters, side windows, end roll-up doors).  Library palette:
light grey panels, graphite frame, orange door frames, stack light (station_parts) on the control cabinet.

Frame as the other assets: authored front = -Y (Unity +Z: the operator's side, with the windows and the cabinet),
+Z up (Unity +Y), flow along +X (Unity +X: in at -X, out at +X), origin on the floor at the booth centre.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    PaintBooth              root, on the floor at the booth centre
    |- Body                 walls, roof, plenum, stack, filters, lights, floor grating, cabinet (static)
    |- Door_In              entry shutter, pivot at the top of the opening (-2.06, 2.20, 0): scale local Y
    |                       1 (closed) -> 0.05 (rolled up into the drum)
    |- Door_Out             exit shutter, pivot (+2.06, 2.20, 0): as Door_In
    |- Beacon               stack light on the cabinet (base (-1.60, 1.70, 1.72))
    |   |- Lamp_Green       working
    |   |- Lamp_Amber       waiting / blocked
    |   '- Lamp_Red         stopped / failed
    '- WorkSocket           the floor centre (0, 0.05, 0): the item stands here while it is painted

The build / bake / export pipeline is station_parts.station_main.
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

HX, HY, H = 2.0, 1.5, 3.0        # half length (flow), half depth, wall height
T = 0.06                         # panel thickness
OPEN_Y, OPEN_Z = 1.0, 2.2        # end openings: half width, height
FLOOR = 0.05                     # grating top
DOOR_X = HX + 0.06               # shutter plane (outside the end wall)
CAB = (-1.90, -1.30, -1.88, -1.56, 1.70)   # control cabinet x0, x1, y0, y1, top
BEACON_BASE = (-1.60, -1.72, CAB[4])

MATS = {}


def build_materials():
    MATS.update({
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.42, wear=0.25, dirt=0.6),
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Shutter': M.paint('Shutter', (0.45, 0.46, 0.46), rough=0.38, wear=0.3),
        'Glass': M.glass('Glass'),
        'Filter': M.plastic('Filter', color=(0.30, 0.27, 0.20), rough=0.95, grain=0.4),
        'Grate': M.dark_steel('Grate', color=(0.07, 0.07, 0.075), rough=0.6),
        'Duct': M.dark_steel('Duct', color=(0.40, 0.41, 0.42), rough=0.35, metal=0.9),
        'Light': M.lens('Light', (0.95, 0.95, 0.90)),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Black': M.plastic('Black'),
    })
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.004, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def end_wall(p, sx):
    """End wall at x = sx * HX with the item opening and the orange door frame."""
    x0, x1 = sorted((sx * HX, sx * (HX - T)))
    for y0, y1 in ((-HY, -OPEN_Y), (OPEN_Y, HY)):
        p.box_minmax((x0, y0, 0.0), (x1, y1, H), 'Panel')
    p.box_minmax((x0, -OPEN_Y, OPEN_Z), (x1, OPEN_Y, H), 'Panel')
    xo = sx * HX
    xa, xb = sorted((xo, xo + sx * 0.10))
    for sy in (1, -1):                                                         # guide rails
        p.box_minmax((xa, sy * OPEN_Y - 0.06, 0.0), (xb, sy * OPEN_Y + 0.06, OPEN_Z), 'Orange')
    xa, xb = sorted((xo, xo + sx * 0.30))
    p.box_minmax((xa, -OPEN_Y - 0.08, OPEN_Z), (xb, OPEN_Y + 0.08, OPEN_Z + 0.34), 'Orange')   # drum housing
    p.box_minmax((xa, -OPEN_Y - 0.08, OPEN_Z + 0.34), (xb, -OPEN_Y + 0.10, OPEN_Z + 0.36), 'Frame')


def front_wall(p):
    """Operator side (-Y): a window band between two panel rows."""
    y0, y1 = -HY, -HY + T
    p.box_minmax((-HX, y0, 0.0), (HX, y1, 1.0), 'Panel')
    p.box_minmax((-HX, y0, 2.0), (HX, y1, H), 'Panel')
    for xa, xb in ((-HX, -1.6), (-0.4, 0.4), (1.6, HX)):
        p.box_minmax((xa, y0, 1.0), (xb, y1, 2.0), 'Panel')
    for xa, xb in ((-1.6, -0.4), (0.4, 1.6)):
        p.box_minmax((xa, y0 + 0.02, 1.0), (xb, y1 - 0.02, 2.0), 'Glass')
        p.box_minmax((xa, y0 - 0.01, 0.97), (xb, y0 + 0.01, 1.0), 'Frame')      # sills
        p.box_minmax((xa, y0 - 0.01, 2.0), (xb, y0 + 0.01, 2.03), 'Frame')


def back_wall(p):
    """Back (+Y): solid wall, the dry filter bank inside."""
    p.box_minmax((-HX, HY - T, 0.0), (HX, HY, H), 'Panel')
    for i in range(4):                                                          # filter bank, 4 x 2 frames
        xa = -1.80 + i * 0.90
        for za in (0.25, 1.35):
            p.box_minmax((xa, HY - T - 0.06, za), (xa + 0.86, HY - T, za + 1.05), 'Frame')
            p.box_minmax((xa + 0.04, HY - T - 0.065, za + 0.04), (xa + 0.82, HY - T - 0.02, za + 1.01), 'Filter')


def frame_posts(p):
    """Graphite corner posts and roof edge."""
    for sx in (1, -1):
        for sy in (1, -1):
            p.box_minmax((sx * HX - 0.05, sy * HY - 0.05, 0.0), (sx * HX + 0.05, sy * HY + 0.05, H + 0.05), 'Frame')
    for sy in (1, -1):
        p.box_minmax((-HX - 0.05, sy * HY - 0.05, H - 0.05), (HX + 0.05, sy * HY + 0.05, H + 0.05), 'Frame')
    for sx in (1, -1):
        p.box_minmax((sx * HX - 0.05, -HY, H - 0.05), (sx * HX + 0.05, HY, H + 0.05), 'Frame')
    for x in (-1.0, 0.0, 1.0):                                                  # back panel joints, outside
        p.box_minmax((x - 0.02, HY, 0.0), (x + 0.02, HY + 0.01, H), 'Frame')


def roof(p):
    p.box_minmax((-HX, -HY, H - T), (HX, HY, H), 'Panel')
    p.box_minmax((-1.85, -1.30, H), (1.85, 1.30, H + 0.45), 'Panel')           # supply plenum
    p.box_minmax((-1.87, -1.32, H + 0.45), (1.87, 1.32, H + 0.48), 'Frame')
    # exhaust fan + stack at the back right
    p.box_minmax((0.90, 0.70, H + 0.48), (1.70, 1.30, H + 0.90), 'Duct')
    p.lathe([(H + 0.90, 0.26), (H + 1.90, 0.26)], 24, 'Duct', center=(1.30, 1.00, 0), axis='Z', cap0=False)
    p.lathe([(H + 2.00, 0.0), (H + 2.00, 0.40), (H + 2.10, 0.30), (H + 2.18, 0.0)], 24, 'Duct',
            center=(1.30, 1.00, 0), axis='Z', cap0=False, cap1=False)                 # rain cap
    for k in range(4):                                                          # cap struts
        dx, dy = ((0.18, 0.0), (-0.18, 0.0), (0.0, 0.18), (0.0, -0.18))[k]
        p.box((1.30 + dx, 1.00 + dy, H + 1.95), (0.02, 0.02, 0.10), 'Duct')
    # air make-up unit at the front left
    p.box_minmax((-1.70, -1.20, H + 0.48), (-0.70, -0.40, H + 0.85), 'Duct')
    for k in range(6):
        p.box_minmax((-1.66 + k * 0.16, -1.205, H + 0.53), (-1.60 + k * 0.16, -1.195, H + 0.80), 'Frame')


def inside(p):
    """Floor grating, light fixtures along the roof edges."""
    p.box_minmax((-HX + T, -HY + T, 0.0), (HX - T, HY - T, FLOOR - 0.01), 'Grate')
    n = 24
    for i in range(n):                                                          # grating bars
        x = -HX + T + 0.04 + i * (2 * (HX - T) - 0.08) / (n - 1)
        p.box_minmax((x - 0.012, -HY + T + 0.02, FLOOR - 0.01), (x + 0.012, HY - T - 0.02, FLOOR), 'Grate')
    for sy in (1, -1):
        for xc in (-1.0, 1.0):
            y = sy * (HY - T - 0.10)
            p.box((xc, y, H - 0.18), (1.40, 0.18, 0.12), 'Frame')
            p.box((xc, y - sy * 0.091, H - 0.18), (1.32, 0.004, 0.08), 'Light')


def cabinet(p):
    SP.control_cabinet(p, *CAB)
    p.cylinder((CAB[1], -1.72, CAB[4] - 0.20), (-HX, -1.72, CAB[4] - 0.20), 0.025, 'Black', n=10)  # conduit


def build_body():
    def f(p):
        for sx in (1, -1):
            end_wall(p, sx)
        front_wall(p)
        back_wall(p)
        frame_posts(p)
        roof(p)
        inside(p)
        cabinet(p)
    return mk('Body', f)


def build_door(name, sx):
    def f(p):
        n = 20
        hs = OPEN_Z / n
        for i in range(n):                                                      # slats
            z = i * hs
            p.box_minmax((sx * DOOR_X - 0.015, -OPEN_Y - 0.02, z + 0.004), (sx * DOOR_X + 0.015, OPEN_Y + 0.02,
                                                                             z + hs), 'Shutter')
        p.box_minmax((sx * DOOR_X - 0.025, -OPEN_Y - 0.02, 0.0), (sx * DOOR_X + 0.025, OPEN_Y + 0.02, 0.05),
                     'Orange')                                                  # bottom bar
        p.box_minmax((sx * (DOOR_X + 0.016), -0.6, 1.25), (sx * (DOOR_X + 0.017), 0.6, 1.55), 'Glass')  # vision strip
    return mk(name, f, bevel=0.002)


def build_booth():
    build_materials()
    body = build_body()
    doors = [build_door('Door_In', -1), build_door('Door_Out', 1)]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.30, size=1.5)
    return [body] + doors + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.5, 0.2), 'Door_In': (0.5, 0.15), 'Door_Out': (0.5, 0.15), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Door_In': 0.8, 'Door_Out': 0.8, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'PaintBooth'),
         'Door_In': ((-DOOR_X, 0, OPEN_Z), 'PaintBooth'), 'Door_Out': ((DOOR_X, 0, OPEN_Z), 'PaintBooth'),
         **SP.beacon_nodes(BEACON_BASE, 'PaintBooth'),
         'WorkSocket': ((0, 0, FLOOR), 'PaintBooth')}


def build_preview():
    return build_booth()


if __name__ == '__main__':
    SP.station_main('PaintBooth', build_booth, LOD_PLAN, NODES, TEXEL_WEIGHTS)
