"""
SimuLean asset: column palletizer -- the visual of a palletizing station (a Combiner: cases + a pallet -> a loaded
pallet).  Cases arrive on the roller infeed, the slewing column picks each one at the infeed end and sets it on
the pallet on the other side.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/palletizer.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/Palletizer/Palletizer.fbx   (+ 5 PNG maps)
    Tools/Blender/build/Palletizer.blend

Real-world reference: compact column (slewing-arm) palletizers for cases up to ~30 kg: a slewing column ~3 m
high, a lifting carriage with a horizontal arm, a case gripper; roller infeed, one pallet place on the floor.
Library palette: graphite base and frames, orange column and arm, yellow floor marking.

Frame as the other assets: authored front = -Y (Unity +Z: the operator's side, with the cabinet), +Z up (Unity
+Y), flow along +X (cases in from -X, pallet place at +X), origin on the floor at the column axis.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    Palletizer              root, on the floor at the column axis
    |- Body                 slewing base, infeed conveyor, pallet place, cabinet (static)
    |- Slew                 column + arm, pivot (0, 0, 0): rotate about local Y (0 = arm towards the pallet
    |   |                   (+X); 180 = towards the infeed)
    |   '- Lift             carriage, pivot (0, 1.80, 0): translate along local Y (0.30 .. 2.40)
    |       '- Reach        gripper trolley, pivot (1.30, 1.80, 0): translate along local X (0.60 .. 1.70)
    |           '- LoadSocket   under the gripper (1.30, 1.20, 0): the case hangs here
    |- Beacon / Lamp_*      stack light on the cabinet (station_parts)
    |- PickSocket           the case at the infeed end (-1.30, 0.75, 0): Slew 180, Reach 1.30, Lift so that the
    |                       LoadSocket meets it
    '- PalletSocket         the pallet place on the floor (1.30, 0, 0)
"""

import os
import sys
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402
import station_parts as SP                  # noqa: E402

COL_H = 3.10                     # column top
ARM_Z = 1.80                     # arm (Lift pivot) height at rest
REACH = 1.30                     # trolley at rest / pick and place radius
GRIP_DROP = 0.60                 # arm pivot -> gripper face
CONV_Z = 0.75                    # infeed roller top
CONV_X0, CONV_X1 = -3.40, -1.00  # infeed extent
CONV_W = 0.70
CAB = (-0.20, 0.40, -1.65, -1.35, 1.70)
BEACON_BASE = (0.10, -1.50, CAB[4])

MATS = {}


def build_materials():
    MATS.update({
        'Base': M.paint('Base', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.42, wear=0.25, dirt=0.6),
        'Roller': M.dark_steel('Roller', color=(0.45, 0.46, 0.47), rough=0.3, metal=0.95),
        'Motor': M.paint('Motor', (0.30, 0.31, 0.32), rough=0.38, wear=0.3),
        'Mark': M.paint('Mark', (0.85, 0.52, 0.0), rough=0.5, wear=0.3, dirt=0.4),
        'Rubber': M.rubber('Rubber'),
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


def infeed(p):
    y = CONV_W / 2
    for sy in (1, -1):                                                          # side frames
        yc = sy * (y - 0.015)
        p.box_minmax((CONV_X0, yc - 0.015, CONV_Z - 0.12), (CONV_X1, yc + 0.015, CONV_Z + 0.04), 'Frame')
    for x in (CONV_X0 + 0.10, (CONV_X0 + CONV_X1) / 2, CONV_X1 - 0.10):          # legs
        for sy in (1, -1):
            p.box_minmax((x - 0.03, sy * (y - 0.015) - 0.03, 0.0), (x + 0.03, sy * (y - 0.015) + 0.03, CONV_Z - 0.12),
                         'Frame')
        p.box_minmax((x - 0.02, -y, 0.15), (x + 0.02, y, 0.19), 'Frame')
    n = 26
    for i in range(n):                                                          # rollers
        x = CONV_X0 + 0.06 + i * (CONV_X1 - CONV_X0 - 0.12) / (n - 1)
        p.cylinder((x, -y + 0.03, CONV_Z - 0.025), (x, y - 0.03, CONV_Z - 0.025), 0.025, 'Roller', n=10)
    p.box_minmax((CONV_X1 - 0.02, -y, CONV_Z), (CONV_X1 + 0.02, y, CONV_Z + 0.12), 'Orange')   # end stop
    p.box_minmax((CONV_X1 - 0.30, -y - 0.20, CONV_Z - 0.20), (CONV_X1 - 0.10, -y - 0.02, CONV_Z - 0.05), 'Motor')
    for sy in (1, -1):                                                          # side guides
        p.box_minmax((CONV_X0, sy * (y - 0.06) - 0.01, CONV_Z + 0.08), (CONV_X1, sy * (y - 0.06) + 0.01,
                                                                         CONV_Z + 0.10), 'Roller')
        for x in (CONV_X0 + 0.4, CONV_X0 + 1.3, CONV_X1 - 0.3):
            p.box_minmax((x - 0.01, sy * (y - 0.03) - 0.01, CONV_Z + 0.02), (x + 0.01, sy * (y - 0.03) + 0.01,
                                                                             CONV_Z + 0.10), 'Roller')


def pallet_place(p):
    """Yellow floor marks around the EUR pallet place (1.2 x 0.8, long side along X)."""
    x0, x1, y0, y1, w = REACH - 0.68, REACH + 0.68, -0.48, 0.48, 0.06
    for (a, b) in (((x0, y0), (x0 + 0.30, y0 + w)), ((x0, y0), (x0 + w, y0 + 0.30)),
                   ((x1 - 0.30, y0), (x1, y0 + w)), ((x1 - w, y0), (x1, y0 + 0.30)),
                   ((x0, y1 - w), (x0 + 0.30, y1)), ((x0, y1 - 0.30), (x0 + w, y1)),
                   ((x1 - 0.30, y1 - w), (x1, y1)), ((x1 - w, y1 - 0.30), (x1, y1))):
        p.box_minmax((a[0], a[1], 0.0), (b[0], b[1], 0.005), 'Mark')
    for sy in (1, -1):                                                          # pallet guides (back stops)
        p.box_minmax((x1 + 0.02, sy * 0.30 - 0.04, 0.0), (x1 + 0.10, sy * 0.30 + 0.04, 0.25), 'Orange')


def slew_base(p):
    p.box_minmax((-0.55, -0.55, 0.0), (0.55, 0.55, 0.08), 'Base')                 # floor plate
    p.lathe([(0.08, 0.45), (0.28, 0.45), (0.30, 0.43)], 40, 'Base', axis='Z', cap0=False)
    for k in range(8):                                                          # anchor bolts
        a = 2 * pi * k / 8 + pi / 8
        p.cylinder((0.50 * cos(a) * 0.98, 0.50 * sin(a) * 0.98, 0.08), (0.50 * cos(a) * 0.98, 0.50 * sin(a) * 0.98,
                                                                        0.10), 0.015, 'Roller', n=8)
    p.box_minmax((0.30, -0.20, 0.08), (0.52, 0.20, 0.26), 'Motor')                # slew drive


def build_body():
    def f(p):
        slew_base(p)
        infeed(p)
        pallet_place(p)
        SP.control_cabinet(p, *CAB)
    return mk('Body', f)


def build_slew():
    def f(p):
        p.lathe([(0.30, 0.42), (0.36, 0.42), (0.38, 0.38)], 40, 'Orange', axis='Z', cap0=True)   # turret
        p.box_minmax((-0.18, -0.18, 0.38), (0.18, 0.18, COL_H), 'Orange')           # column
        p.box_minmax((-0.20, -0.20, COL_H), (0.20, 0.20, COL_H + 0.06), 'Base')      # top cap
        p.box_minmax((-0.20, 0.18, 0.60), (-0.06, 0.24, COL_H - 0.10), 'Roller')     # cable track
        p.box_minmax((-0.34, -0.16, COL_H - 0.30), (-0.18, 0.16, COL_H - 0.04), 'Motor')   # lift motor
        p.cylinder((-0.26, 0.0, COL_H - 0.04), (-0.26, 0.0, COL_H + 0.06), 0.05, 'Motor', n=14)
        for sy in (1, -1):                                                      # linear guides
            p.box_minmax((0.18, sy * 0.10 - 0.015, 0.45), (0.20, sy * 0.10 + 0.015, COL_H - 0.05), 'Roller')
    return mk('Slew', f)


def build_lift():
    def f(p):
        p.box_minmax((-0.26, -0.24, ARM_Z - 0.20), (0.30, 0.24, ARM_Z + 0.20), 'Base')   # carriage
        p.box_minmax((0.30, -0.10, ARM_Z - 0.08), (1.85, 0.10, ARM_Z + 0.14), 'Orange')  # arm
        p.box_minmax((-0.70, -0.12, ARM_Z - 0.10), (-0.26, 0.12, ARM_Z + 0.10), 'Orange')  # counter arm
        p.box_minmax((-0.75, -0.18, ARM_Z - 0.20), (-0.50, 0.18, ARM_Z + 0.10), 'Base')  # counterweight
        for sy in (1, -1):                                                      # trolley rails
            p.box_minmax((0.40, sy * 0.10 - 0.005, ARM_Z - 0.05), (1.82, sy * 0.10 + 0.005, ARM_Z - 0.02),
                         'Roller')
        p.box_minmax((1.85, -0.11, ARM_Z - 0.09), (1.88, 0.11, ARM_Z + 0.15), 'Base')   # end cap
    return mk('Lift', f)


def build_reach():
    def f(p):
        x = REACH
        p.box_minmax((x - 0.14, -0.15, ARM_Z - 0.10), (x + 0.14, 0.15, ARM_Z + 0.20), 'Base')   # trolley
        p.cylinder((x, 0.0, ARM_Z - 0.10), (x, 0.0, ARM_Z - GRIP_DROP + 0.14), 0.035, 'Roller', n=14)  # mast
        p.cylinder((x, 0.0, ARM_Z - GRIP_DROP + 0.14), (x, 0.0, ARM_Z - GRIP_DROP + 0.08), 0.07, 'Motor', n=18)
        g = ARM_Z - GRIP_DROP
        p.box_minmax((x - 0.25, -0.20, g + 0.04), (x + 0.25, 0.20, g + 0.08), 'Orange')   # gripper head
        for sy in (1, -1):                                                      # clamp jaws
            p.box_minmax((x - 0.22, sy * 0.20 - 0.02, g - 0.25), (x + 0.22, sy * 0.20 + 0.02, g + 0.04), 'Base')
            p.box_minmax((x - 0.20, sy * 0.18 - 0.005, g - 0.24), (x + 0.20, sy * 0.18 + 0.005, g - 0.04), 'Rubber')
            p.cylinder((x, sy * 0.20, g + 0.06), (x, sy * 0.30, g + 0.06), 0.025, 'Roller', n=12)  # clamp cylinder
        p.box_minmax((x - 0.20, -0.16, g + 0.0), (x + 0.20, 0.16, g + 0.04), 'Base')   # top plate
    return mk('Reach', f, bevel=0.003)


def build_palletizer():
    build_materials()
    objs = [build_body(), build_slew(), build_lift(), build_reach()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.30, size=1.3)
    return objs + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), 'Slew': (0.5, 0.2), 'Lift': (0.5, 0.2), 'Reach': (0.5, 0.2), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Slew': 1.0, 'Lift': 1.0, 'Reach': 1.3, 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'Palletizer'),
         'Slew': ((0, 0, 0), 'Palletizer'),
         'Lift': ((0, 0, ARM_Z), 'Slew'),
         'Reach': ((REACH, 0, ARM_Z), 'Lift'),
         'LoadSocket': ((REACH, 0, ARM_Z - GRIP_DROP), 'Reach'),
         **SP.beacon_nodes(BEACON_BASE, 'Palletizer'),
         'PickSocket': ((-REACH, 0, CONV_Z), 'Palletizer'),
         'PalletSocket': ((REACH, 0, 0), 'Palletizer')}


def build_preview():
    return build_palletizer()


if __name__ == '__main__':
    SP.station_main('Palletizer', build_palletizer, LOD_PLAN, NODES, TEXEL_WEIGHTS)
