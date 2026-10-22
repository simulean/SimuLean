"""
SimuLean asset: compact vertical machining centre (CNC) -- the visual of a processing station (MultiServer): the
part is clamped on the table inside the guarding, the doors close, the spindle comes down and machines it.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/cnc_machine.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/CncMachine/CncMachine.fbx   (+ 5 PNG maps)
    Tools/Blender/build/CncMachine.blend

Real-world reference: compact 3-axis vertical machining centres (table ~600 x 300 mm), scaled to the footprint of
the library's bench stations (1.5 x 1.1 m, 2.1 m high) so it can stand in for the WorkstationProcessor model (both stay selectable).
Full guarding with two sliding doors (open sight windows: the interior shows without transparency), chip conveyor
and coolant tank, a swivel control pendant, a stack light.  Library palette: light grey guarding, graphite base and
interior, orange door handles and accents.

Frame as the other assets: authored front = -Y (Unity +Z: the operator's side, with the doors and the pendant),
+Z up (Unity +Y), origin on the floor at the centre of the footprint.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    CncMachine              root, on the floor at the footprint centre
    |- Body                 base, guarding, interior, column, coolant tank, chip conveyor, pendant (static)
    |- Door_L / Door_R      sliding doors, pivot at their closed centre: translate along local X
    |                       (Door_L 0 .. -0.38, Door_R 0 .. +0.38 to open)
    |- Table                X axis, pivot (-0.10, 0.92, 0.05): translate along local X (-0.18 .. +0.18)
    |   '- WorkSocket       the vise jaws (-0.10, 0.99, 0.05): the part is clamped here
    |- Head                 Z axis (spindle head), pivot (-0.10, 1.30, 0.05): translate along local Y
    |   |                   (-0.20 .. +0.10)
    |   '- Spindle          spindle + tool, pivot on the spindle axis: rotate about local Y (endless)
    '- Beacon / Lamp_*      stack light on the roof (station_parts)
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

X0, X1 = -0.62, 0.45             # guarding (x)
Y0, Y1 = -0.45, 0.50             # guarding (y)
Z0, Z1 = 0.12, 1.95              # guarding bottom (on the base) / roof
OX0, OX1 = -0.52, 0.32           # front opening
OZ0, OZ1 = 0.78, 1.62
CX = (OX0 + OX1) / 2             # machining axis (x)
CY = -0.05                       # machining axis (y)
TABLE_Z = 0.92                   # table top
VISE_Z = 0.99                    # vise jaws top (WorkSocket)
HEAD_Z = 1.30                    # spindle nose at rest
DOOR_W = (OX1 - OX0) / 2 + 0.03  # each door overlaps the opening by 3 cm
DOOR_Y = Y0 - 0.035              # door plane
BEACON_BASE = (X1 - 0.12, Y1 - 0.12, Z1)

MATS = {}


def build_materials():
    MATS.update({
        'Panel': M.paint('Panel', (0.62, 0.63, 0.61), rough=0.40, wear=0.3, dirt=0.7),
        'Frame': M.paint('Frame', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Inner': M.dark_steel('Inner', color=(0.42, 0.43, 0.44), rough=0.35, metal=0.8),   # stainless liner
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Steel': M.dark_steel('Steel', color=(0.30, 0.31, 0.32), rough=0.3, metal=0.9),
        'Bright': M.dark_steel('Bright', color=(0.62, 0.63, 0.64), rough=0.22, metal=0.95),
        'Slot': M.plastic('Slot', color=(0.01, 0.01, 0.012), rough=0.8),
        'Motor': M.paint('Motor', (0.30, 0.31, 0.32), rough=0.38, wear=0.3),
        'Screen': M.lens('Screen', (0.10, 0.25, 0.40)),
        'Light': M.lens('Light', (0.92, 0.95, 1.0)),
        'Black': M.plastic('Black'),
        'Work': M.dark_steel('Work', color=(0.55, 0.57, 0.60), rough=0.25, metal=0.9),
    })
    MATS.update(SP.beacon_materials(M))


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def guarding(p):
    t = 0.02
    p.box_minmax((X0 - 0.03, Y0 - 0.01, 0.0), (X1 + 0.03, Y1 + 0.03, Z0), 'Frame')           # base
    for x in (X0 + 0.05, X1 - 0.05):                                                         # levelling feet
        for y in (Y0 + 0.05, Y1 - 0.05):
            p.cylinder((x, y, -0.0), (x, y, 0.012), 0.035, 'Black', n=10)
    p.box_minmax((X0, Y1 - t, Z0), (X1, Y1, Z1), 'Panel')                                     # back
    p.box_minmax((X0, Y0, Z0), (X0 + t, Y1, Z1), 'Panel')                                     # left
    p.box_minmax((X1 - t, Y0, Z0), (X1, Y1, Z1), 'Panel')                                     # right
    p.box_minmax((X0, Y0, Z1 - t), (X1, Y1, Z1), 'Panel')                                     # roof
    p.box_minmax((X0 + 0.02, Y0 + 0.02, Z1), (X1 - 0.02, Y1 - 0.02, Z1 + 0.012), 'Frame')
    # front: around the opening
    p.box_minmax((X0, Y0, Z0), (X1, Y0 + t, OZ0), 'Panel')
    p.box_minmax((X0, Y0, OZ1), (X1, Y0 + t, Z1), 'Panel')
    p.box_minmax((X0, Y0, OZ0), (OX0, Y0 + t, OZ1), 'Panel')
    p.box_minmax((OX1, Y0, OZ0), (X1, Y0 + t, OZ1), 'Panel')
    p.box_minmax((X0, Y0 - 0.01, Z0 + 0.02), (X1, Y0, Z0 + 0.10), 'Frame')                    # kick plate
    p.box_minmax((X0, Y0 - 0.006, 0.62), (X1, Y0, 0.66), 'Orange')                            # stripe
    p.box_minmax((X0, Y0 - 0.006, OZ1 + 0.10), (X1, Y0, Z1 - 0.06), 'Frame')                  # header band
    # door rails
    for z in (OZ0 - 0.02, OZ1 + 0.02):
        p.box_minmax((X0 + 0.02, DOOR_Y - 0.025, z - 0.012), (X1 - 0.02, Y0, z + 0.012), 'Steel')


def interior(p):
    """Inside the guarding: walls, column, X-axis saddle, chip trough, work light."""
    ix0, ix1, iy0, iy1 = OX0 - 0.02, OX1 + 0.02, Y0 + 0.02, Y1 - 0.02
    p.box_minmax((ix0, iy0, OZ0 - 0.04), (ix1, iy1, OZ0 - 0.02), 'Inner')                     # trough floor
    p.box_minmax((ix0, iy1 - 0.02, OZ0 - 0.04), (ix1, iy1, OZ1 + 0.05), 'Inner')              # back wall
    for x in (ix0, ix1 - 0.02):
        p.box_minmax((x, iy0, OZ0 - 0.04), (x + 0.02, iy1, OZ1 + 0.05), 'Inner')
    p.box_minmax((ix0, iy0, OZ1 + 0.03), (ix1, iy1, OZ1 + 0.05), 'Inner')                    # ceiling
    p.box_minmax((CX - 0.16, CY + 0.18, OZ0 - 0.02), (CX + 0.16, iy1 - 0.02, OZ1 + 0.03), 'Frame')  # column
    for sx in (1, -1):                                                                        # Z guideways
        p.box_minmax((CX + sx * 0.10 - 0.02, CY + 0.17, OZ0 + 0.20), (CX + sx * 0.10 + 0.02, CY + 0.18, OZ1),
                     'Bright')
    p.box_minmax((CX - 0.30, CY - 0.18, OZ0 - 0.02), (CX + 0.30, CY + 0.16, TABLE_Z - 0.07), 'Frame')  # saddle
    for sy in (1, -1):                                                                        # X guideways
        p.box_minmax((CX - 0.32, CY + sy * 0.10 - 0.02, TABLE_Z - 0.07), (CX + 0.32, CY + sy * 0.10 + 0.02,
                                                                            TABLE_Z - 0.05), 'Bright')
    p.box((CX, iy0 + 0.10, OZ1 + 0.025), (0.50, 0.08, 0.01), 'Light')                         # work light


def periphery(p):
    """Coolant tank + chip conveyor on the right, swivel pendant at the front right."""
    p.box_minmax((X1, -0.10, 0.0), (X1 + 0.16, Y1, 0.42), 'Motor')                            # coolant tank
    p.box_minmax((X1, Y1 - 0.25, 0.42), (X1 + 0.16, Y1, 0.70), 'Frame')                       # chip lift
    p.box_minmax((X1 + 0.02, Y1 - 0.24, 0.70), (X1 + 0.14, Y1 - 0.01, 0.74), 'Frame')
    p.box_minmax((X1 + 0.02, -0.08, 0.42), (X1 + 0.14, 0.10, 0.44), 'Steel')                  # chip bin lid
    # control pendant: arm from the right side, box facing the operator
    px, py = X1 + 0.02, Y0 - 0.10
    p.box_minmax((X1, Y0 + 0.02, 1.48), (X1 + 0.06, Y0 + 0.08, 1.52), 'Frame')                # arm
    p.box_minmax((X1 + 0.02, py, 1.48), (X1 + 0.06, Y0 + 0.08, 1.52), 'Frame')
    p.box_minmax((px - 0.06, py - 0.06, 1.10), (px + 0.24, py, 1.58), 'Panel')                # pendant
    p.box_minmax((px - 0.03, py - 0.064, 1.30), (px + 0.21, py - 0.058, 1.55), 'Black')
    p.box_minmax((px - 0.02, py - 0.068, 1.31), (px + 0.20, py - 0.062, 1.54), 'Screen')
    for i in range(4):                                                                        # keys
        for j in range(3):
            p.box((px + 0.0 + i * 0.05, py - 0.064, 1.16 + j * 0.04), (0.035, 0.008, 0.025), 'Black')
    p.cylinder((px + 0.20, py - 0.06, 1.15), (px + 0.20, py - 0.085, 1.15), 0.02, 'Orange', n=14)  # e-stop
    p.cylinder((px + 0.20, py - 0.06, 1.23), (px + 0.20, py - 0.075, 1.23), 0.025, 'Black', n=16)   # handwheel


def build_body():
    def f(p):
        guarding(p)
        interior(p)
        periphery(p)
    return mk('Body', f)


def build_door(name, sx):
    """sx -1: left door (closed over the left half), +1: right door."""
    xa = CX - DOOR_W if sx < 0 else CX
    xb = CX if sx < 0 else CX + DOOR_W
    za, zb = OZ0 - 0.01, OZ1 + 0.01
    wx0, wx1, wz0, wz1 = xa + 0.06, xb - 0.06, za + 0.10, zb - 0.08                           # sight window

    def f(p):
        y0, y1 = DOOR_Y - 0.012, DOOR_Y + 0.012
        p.box_minmax((xa, y0, za), (xb, y1, wz0), 'Panel')
        p.box_minmax((xa, y0, wz1), (xb, y1, zb), 'Panel')
        p.box_minmax((xa, y0, wz0), (wx0, y1, wz1), 'Panel')
        p.box_minmax((wx1, y0, wz0), (xb, y1, wz1), 'Panel')
        for (a, b) in (((wx0, wz0 - 0.012), (wx1, wz0)), ((wx0, wz1), (wx1, wz1 + 0.012)),
                       ((wx0 - 0.012, wz0), (wx0, wz1)), ((wx1, wz0), (wx1 + 0.012, wz1))):
            p.box_minmax((a[0], y0 - 0.006, a[1]), (b[0], y0, b[1]), 'Black')                 # window gasket
        hx = xb - 0.05 if sx < 0 else xa + 0.035
        p.cylinder((hx, y0 - 0.04, wz0 + 0.12), (hx, y0 - 0.04, wz0 + 0.42), 0.011, 'Orange', n=10)   # handle
        for z in (wz0 + 0.12, wz0 + 0.42):
            p.cylinder((hx, y0 - 0.04, z), (hx, y0, z), 0.008, 'Steel', n=8)
    return mk(name, f)


def build_table():
    def f(p):
        p.box_minmax((CX - 0.30, CY - 0.15, TABLE_Z - 0.05), (CX + 0.30, CY + 0.15, TABLE_Z), 'Steel')
        for k in (-1, 0, 1):                                                                  # T-slots
            p.box_minmax((CX - 0.30, CY + k * 0.08 - 0.008, TABLE_Z), (CX + 0.30, CY + k * 0.08 + 0.008,
                                                                       TABLE_Z + 0.0005), 'Slot')
        # machine vise with a workpiece
        p.box_minmax((CX - 0.10, CY - 0.07, TABLE_Z), (CX + 0.10, CY + 0.07, TABLE_Z + 0.03), 'Frame')
        p.box_minmax((CX - 0.10, CY - 0.07, TABLE_Z + 0.03), (CX - 0.06, CY + 0.07, VISE_Z), 'Frame')
        p.box_minmax((CX + 0.04, CY - 0.07, TABLE_Z + 0.03), (CX + 0.08, CY + 0.07, VISE_Z), 'Frame')
        p.cylinder((CX + 0.08, CY, TABLE_Z + 0.05), (CX + 0.16, CY, TABLE_Z + 0.05), 0.008, 'Bright', n=8)
        p.box_minmax((CX + 0.16, CY - 0.01, TABLE_Z + 0.02), (CX + 0.17, CY + 0.01, TABLE_Z + 0.08), 'Bright')
    return mk('Table', f)


def build_head():
    def f(p):
        p.box_minmax((CX - 0.11, CY + 0.02, HEAD_Z + 0.12), (CX + 0.11, CY + 0.18, HEAD_Z + 0.34), 'Frame')
        p.box_minmax((CX - 0.07, CY - 0.07, HEAD_Z + 0.12), (CX + 0.07, CY + 0.07, HEAD_Z + 0.30), 'Panel')
        p.cylinder((CX, CY, HEAD_Z + 0.30), (CX, CY, HEAD_Z + 0.40), 0.05, 'Motor', n=18)        # spindle motor
        p.lathe([(HEAD_Z + 0.06, 0.055), (HEAD_Z + 0.12, 0.06)], 24, 'Frame', center=(CX, CY, 0), axis='Z')
    return mk('Head', f)


def build_spindle():
    def f(p):
        p.lathe([(HEAD_Z - 0.06, 0.006), (HEAD_Z - 0.02, 0.008), (HEAD_Z - 0.015, 0.016), (HEAD_Z + 0.02, 0.030),
                 (HEAD_Z + 0.06, 0.040)], 16, 'Bright', center=(CX, CY, 0), axis='Z', cap0=True)
        p.box_minmax((CX + 0.028, CY - 0.004, HEAD_Z + 0.02), (CX + 0.036, CY + 0.004, HEAD_Z + 0.05), 'Orange')
    return mk('Spindle', f, bevel=0.001)


def build_cnc():
    build_materials()
    objs = [build_body(), build_door('Door_L', -1), build_door('Door_R', 1), build_table(), build_head(),
            build_spindle()]
    bo, lamps, _ = SP.build_beacon(MATS, BEACON_BASE, post=0.12, size=1.1)
    return objs + SP.name_beacon(bo, lamps)


LOD_PLAN = {'Body': (0.45, 0.15), 'Door_L': (0.5, 0.2), 'Door_R': (0.5, 0.2), 'Table': (0.5, 0.2),
            'Head': (0.5, 0.2), 'Spindle': (0.5, 0.25), **SP.BEACON_LODS}
TEXEL_WEIGHTS = {'Body': 1.0, 'Door_L': 1.0, 'Door_R': 1.0, 'Table': 1.5, 'Head': 1.5, 'Spindle': 2.0,
                 'Beacon': 2.0}
NODES = {'Body': ((0, 0, 0), 'CncMachine'),
         'Door_L': ((CX - DOOR_W / 2, DOOR_Y, (OZ0 + OZ1) / 2), 'CncMachine'),
         'Door_R': ((CX + DOOR_W / 2, DOOR_Y, (OZ0 + OZ1) / 2), 'CncMachine'),
         'Table': ((CX, CY, TABLE_Z), 'CncMachine'),
         'WorkSocket': ((CX, CY, VISE_Z), 'Table'),
         'Head': ((CX, CY, HEAD_Z), 'CncMachine'),
         'Spindle': ((CX, CY, HEAD_Z), 'Head'),
         **SP.beacon_nodes(BEACON_BASE, 'CncMachine')}


def build_preview():
    return build_cnc()


if __name__ == '__main__':
    SP.station_main('CncMachine', build_cnc, LOD_PLAN, NODES, TEXEL_WEIGHTS)
