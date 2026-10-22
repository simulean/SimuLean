"""
SimuLean asset: vision inspection station -- the visual of an inspection / test station: the item stops on the belt
under the camera hood, is lit and photographed, and a rejected item is pushed off the belt into the reject bin.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/inspection_station.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/InspectionStation/InspectionStation.fbx   (+ 5 PNG maps)
    Tools/Blender/build/InspectionStation.blend

Real-world reference: in-line machine-vision stations: an aluminium-profile gantry over a belt conveyor, a top
camera in a light-shield hood, LED bar lights, a side camera, a pneumatic reject pusher and bin, a monitor arm.
Library palette: graphite conveyor, natural aluminium profiles, orange accents, tinted guard panel.

Frame as the other assets: authored front = -Y (Unity +Z: the operator's side, with the monitor), +Z up (Unity
+Y), flow along +X (Unity +X), origin on the floor under the camera.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    InspectionStation       root, on the floor under the camera
    |- Body                 conveyor, gantry, hood, cameras, monitor, reject bin (static)
    |- Lights               the two LED bars (their own renderers: a view can flash them per inspection)
    |- Pusher               reject plate, pivot (0.70, 0.85, 0.36): translate along local Z 0 .. -0.72 (towards
    |                       the back) to push the item into the bin
    |- Beacon / Lamp_*      stack light on the gantry (station_parts)
    |- WorkSocket           on the belt under the camera (0, 0.85, 0)
    '- RejectSocket         in the reject bin (0.70, 0.30, -0.62)
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

BELT_Z = 0.85                    # belt top
BELT_X0, BELT_X1 = -1.10, 1.10
BELT_W = 0.60
GX = 0.40                        # gantry half length (x)
GY = 0.45                        # gantry half width (y)
GH = 1.95                        # gantry top
PR = 0.0225                      # profile half size (45 mm)
PUSH_X, PUSH_Y = 0.70, -0.36     # pusher plate rest
BIN = (0.45, 0.95, 0.42, 0.82, 0.60)   # reject bin x0, x1, y0, y1, height
BEACON_BASE = (-GX, -GY, GH)

MATS = {}


def build_materials():
    MATS.update({
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.57), rough=0.35, metal=0.9),
        'Belt': M.rubber('Belt', color=(0.025, 0.026, 0.028), rough=0.8),
        'Roller': M.dark_steel('Roller', color=(0.45, 0.46, 0.47), rough=0.3, metal=0.95),
        'Guard': M.glass('Guard', tint=(0.02, 0.03, 0.035)),
        'Black': M.plastic('Black'),
        'Lens': M.glass('Lens', tint=(0.005, 0.006, 0.01)),
        'Light': M.lens('Light', (0.92, 0.95, 1.0)),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Bin': M.plastic('Bin', color=(0.55, 0.06, 0.02), rough=0.6),
    })
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def conveyor(p):
    y = BELT_W / 2
    p.box_minmax((BELT_X0 + 0.05, -y + 0.01, BELT_Z - 0.012), (BELT_X1 - 0.05, y - 0.01, BELT_Z), 'Belt')
    for x in (BELT_X0 + 0.05, BELT_X1 - 0.05):                                  # end drums, belt wrap
        p.cylinder((x, -y + 0.01, BELT_Z - 0.05), (x, y - 0.01, BELT_Z - 0.05), 0.05, 'Belt', n=16)
    for sy in (1, -1):                                                          # side frames
        yc = sy * (y + 0.015)
        p.box_minmax((BELT_X0, yc - 0.015, BELT_Z - 0.16), (BELT_X1, yc + 0.015, BELT_Z + 0.02), 'Frame')
        for x in (BELT_X0 + 0.12, BELT_X1 - 0.12):                              # legs
            p.box_minmax((x - 0.03, yc - 0.03, 0.0), (x + 0.03, yc + 0.03, BELT_Z - 0.16), 'Frame')
    for x in (BELT_X0 + 0.12, BELT_X1 - 0.12):
        p.box_minmax((x - 0.02, -y, 0.18), (x + 0.02, y, 0.22), 'Frame')
    p.box_minmax((BELT_X1 - 0.30, -y - 0.22, BELT_Z - 0.20), (BELT_X1 - 0.08, -y - 0.03, BELT_Z - 0.04), 'Frame')
    p.cylinder((BELT_X1 - 0.19, -y - 0.03, BELT_Z - 0.05), (BELT_X1 - 0.19, -y, BELT_Z - 0.05), 0.04, 'Roller')
    for sy in (1, -1):                                                          # side guides before the gantry
        p.box_minmax((BELT_X0, sy * (y - 0.03) - 0.008, BELT_Z + 0.03), (-GX - 0.05, sy * (y - 0.03) + 0.008,
                                                                         BELT_Z + 0.06), 'Alu')


def profile(p, a, b):
    """45 mm aluminium profile between two points along one axis."""
    lo = tuple(min(a[i], b[i]) - (PR if a[i] == b[i] else 0.0) for i in range(3))
    hi = tuple(max(a[i], b[i]) + (PR if a[i] == b[i] else 0.0) for i in range(3))
    p.box_minmax(lo, hi, 'Alu')


def gantry(p):
    for sx in (1, -1):
        for sy in (1, -1):
            profile(p, (sx * GX, sy * GY, 0.0), (sx * GX, sy * GY, GH))
            p.box_minmax((sx * GX - 0.06, sy * GY - 0.06, 0.0), (sx * GX + 0.06, sy * GY + 0.06, 0.01), 'Frame')
    for z in (GH - PR, 0.25):
        for sy in (1, -1):
            profile(p, (-GX, sy * GY, z), (GX, sy * GY, z))
        for sx in (1, -1):
            profile(p, (sx * GX, -GY, z), (sx * GX, GY, z))
    # light-shield hood over the camera: closed on top and three sides, open below and to the operator
    p.box_minmax((-GX + PR, -GY + PR, GH - 2 * PR - 0.01), (GX - PR, GY - PR, GH - 2 * PR), 'Frame')
    for sx in (1, -1):
        xa, xb = sorted((sx * (GX - PR), sx * (GX - PR - 0.01)))
        p.box_minmax((xa, -GY + PR, 1.55), (xb, GY - PR, GH - 2 * PR), 'Frame')
    p.box_minmax((-GX + PR, GY - PR - 0.01, 1.55), (GX - PR, GY - PR, GH - 2 * PR), 'Frame')
    p.box_minmax((-GX + PR, GY - PR - 0.005, 0.30), (GX - PR, GY - PR, 1.55), 'Guard')      # back guard
    # top camera, looking down
    p.box_minmax((-0.05, -0.05, 1.62), (0.05, 0.05, 1.76), 'Black')
    p.lathe([(1.62, 0.035), (1.56, 0.035), (1.555, 0.03)], 20, 'Black', axis='Z', cap0=False)
    p.cylinder((0.0, 0.0, 1.556), (0.0, 0.0, 1.552), 0.028, 'Lens', n=20)
    p.box_minmax((-0.02, -0.02, 1.76), (0.02, 0.02, GH - 2 * PR - 0.01), 'Alu')     # camera hanger
    # side camera on a bracket (front), aimed at the item
    p.box_minmax((GX - 0.02, -GY - 0.12, 1.08), (GX + 0.02, -GY, 1.12), 'Alu')
    p.box((GX - 0.05, -GY - 0.10, 1.10), (0.10, 0.07, 0.07), 'Black')
    p.cylinder((GX - 0.10, -GY - 0.10, 1.10), (GX - 0.13, -GY - 0.10, 1.10), 0.025, 'Black', n=16)
    # monitor on an arm (front, at the entry side)
    p.box_minmax((-GX - 0.02, -GY - 0.30, 1.40), (-GX + 0.02, -GY, 1.44), 'Alu')
    p.box_minmax((-GX - 0.26, -GY - 0.34, 1.22), (-GX + 0.06, -GY - 0.30, 1.58), 'Black')
    p.box_minmax((-GX - 0.24, -GY - 0.342, 1.24), (-GX + 0.04, -GY - 0.335, 1.56), 'Screen')
    # electrical box on the gantry leg
    p.box_minmax((-GX - 0.10, GY + PR, 0.90), (-GX + 0.10, GY + 0.16, 1.25), 'Frame')


def reject(p):
    """Pneumatic pusher cylinder (front) and the reject bin (back)."""
    y = BELT_W / 2
    p.box_minmax((PUSH_X - 0.06, -y - 0.03, BELT_Z - 0.02), (PUSH_X + 0.06, -y - 0.01, BELT_Z + 0.14), 'Alu')
    p.cylinder((PUSH_X, -y - 0.03, BELT_Z + 0.06), (PUSH_X, -y - 0.42, BELT_Z + 0.06), 0.035, 'Roller', n=16)
    p.cylinder((PUSH_X, -y - 0.42, BELT_Z + 0.06), (PUSH_X, -y - 0.45, BELT_Z + 0.06), 0.045, 'Orange', n=16)
    p.box_minmax((PUSH_X - 0.05, -y - 0.46, 0.0), (PUSH_X + 0.05, -y - 0.40, BELT_Z + 0.02), 'Frame')   # stand
    x0, x1, y0, y1, h = BIN
    p.box_minmax((x0, y0, 0.0), (x1, y1, 0.03), 'Bin')
    for (a, b) in (((x0, y0), (x1, y0 + 0.02)), ((x0, y1 - 0.02), (x1, y1)),
                   ((x0, y0), (x0 + 0.02, y1)), ((x1 - 0.02, y0), (x1, y1))):
        p.box_minmax((a[0], a[1], 0.0), (b[0], b[1], h), 'Bin')
    p.box_minmax((x0 + 0.02, y0 - 0.12, BELT_Z - 0.02), (x1 - 0.02, y0 + 0.10, BELT_Z), 'Alu')   # chute lip
    for sx in (x0 + 0.04, x1 - 0.04):
        p.box_minmax((sx - 0.01, y0 - 0.12, h), (sx + 0.01, y0 - 0.10, BELT_Z - 0.02), 'Alu')


def build_body():
    def f(p):
        conveyor(p)
        gantry(p)
        reject(p)
    return mk('Body', f)


def build_lights():
    def f(p):
        for sy in (1, -1):
            y = sy * (GY - 0.08)
            p.box((0.0, y, 1.45), (0.60, 0.05, 0.04), 'Frame')
            p.box((0.0, y - sy * 0.026, 1.45), (0.56, 0.003, 0.03), 'Light')
            p.box_minmax((-0.30, y - 0.01, 1.47), (-0.28, y + 0.01, 1.55), 'Alu')
            p.box_minmax((0.28, y - 0.01, 1.47), (0.30, y + 0.01, 1.55), 'Alu')
    return mk('Lights', f, bevel=0.002)


def build_pusher():
    def f(p):
        p.box_minmax((PUSH_X - 0.10, PUSH_Y - 0.01, BELT_Z + 0.01), (PUSH_X + 0.10, PUSH_Y + 0.01, BELT_Z + 0.12),
                     'Orange')
        p.cylinder((PUSH_X, PUSH_Y - 0.01, BELT_Z + 0.06), (PUSH_X, PUSH_Y - 0.06, BELT_Z + 0.06), 0.012, 'Roller')
    return mk('Pusher', f, bevel=0.002)


def build_station():
    build_materials()
    objs = [build_body(), build_lights(), build_pusher()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.20, size=1.2)
    return objs + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), 'Lights': (0.5, 0.2), 'Pusher': (0.5, 0.3), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.2, 'Lights': 1.0, 'Pusher': 1.0, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'InspectionStation'),
         'Lights': ((0, 0, 1.45), 'InspectionStation'),
         'Pusher': ((PUSH_X, PUSH_Y, BELT_Z), 'InspectionStation'),
         **SP.beacon_nodes(BEACON_BASE, 'InspectionStation'),
         'WorkSocket': ((0, 0, BELT_Z), 'InspectionStation'),
         'RejectSocket': (((BIN[0] + BIN[1]) / 2, (BIN[2] + BIN[3]) / 2, 0.30), 'InspectionStation')}


def build_preview():
    return build_station()


if __name__ == '__main__':
    SP.station_main('InspectionStation', build_station, LOD_PLAN, NODES, TEXEL_WEIGHTS)
