"""
SimuLean asset: electric hospital bed (ward / ICU type) -- a patient's place in a ward, and a transport unit
(a bed pushed by a porter between wards, theatre and imaging).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/hospital_bed.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/HospitalBed/HospitalBed.fbx   (+ 5 PNG maps)
    Tools/Blender/build/HospitalBed.blend

Real-world reference: electric acute-care beds -- mattress platform 2.0 x 0.90 m, overall 2.18 x 1.02 m, deck
height 0.40 .. 0.80 m on two lifting columns, split side rails, 150 mm central-locking castors, removable head /
foot boards.  Hospital palette (hospital_parts), no marks.

Frame as the vehicles: authored front = -Y (Unity +Z = the travel direction: the foot end goes first, the porter
pushes from the head end), +Z up (Unity +Y), origin on the floor at the bed centre.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    HospitalBed             root, on the floor at the bed centre
    |- Body                 chassis, castors, base shrouds, lifting columns (static)
    |- Lift                 platform, mattress, boards and side rails, pivot (0, 0.50, 0): translate along
    |   |                   local Y (-0.10 .. +0.30: deck 0.40 .. 0.80 m)
    |   |- Backrest         head section, hinge (0, 0.50, -0.15): rotate about local X (0 .. +60 deg raises it)
    |   '- PatientSocket    on the mattress, hips (0, 0.66, 0.05): a lying patient goes here (head towards -Z
    |                       of Unity, i.e. the head end)
    '- DriverSocket         where the pushing porter stands, behind the head board (0, 0, -1.40)
"""

import os
import sys

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import station_parts as SP                  # noqa: E402
import hospital_parts as HP                 # noqa: E402

DECK_Z = 0.50                    # platform top at rest
MAT_T = 0.14                     # mattress thickness
HALF_W = 0.45                    # platform half width
Y_FOOT, Y_HEAD = -1.00, 1.00     # platform ends
HINGE_Y = 0.15                   # backrest hinge
CASTER_R = 0.075

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def build_body():
    def f(p):
        for sx in (1, -1):                                                   # chassis rails
            p.box_minmax((sx * 0.30 - 0.03, -0.92, 0.17), (sx * 0.30 + 0.03, 0.92, 0.23), 'Grey')
        for y in (-0.88, 0.88):                                              # cross members + castors
            p.box_minmax((-0.40, y - 0.04, 0.17), (0.40, y + 0.04, 0.23), 'Grey')
            for sx in (1, -1):
                HP.caster(p, sx * 0.38, y, CASTER_R, top=0.17)
        # base shrouds
        p.extrude(K.rrect(0.70, 0.10, 0.03, 3, 0.0, 0.26), 'Y', -0.80, 0.80, 'Shroud')
        for y in (-0.55, 0.55):                                              # lifting columns (outer tubes)
            p.box_minmax((-0.10, y - 0.07, 0.30), (0.10, y + 0.07, DECK_Z - 0.16), 'Shroud')
        p.box_minmax((-0.10, 0.80, 0.24), (0.10, 0.84, 0.36), 'Teal')         # central brake pedal (head end)
        p.box_minmax((-0.10, -0.84, 0.24), (0.10, -0.80, 0.36), 'Teal')       # (foot end)
    return mk('Body', f)


def side_rail(p, y0, y1, sx, z0):
    """A split side rail: a moulded panel with two hand-holes."""
    x = sx * (HALF_W + 0.035)
    p.box_minmax((x - 0.015, y0, z0 + 0.06), (x + 0.015, y1, z0 + 0.36), 'Shroud')
    for t in (0.25, 0.65):                                                   # hand-hole "insets"
        yc = y0 + (y1 - y0) * t
        p.box_minmax((x - 0.0165, yc - 0.10, z0 + 0.22), (x + 0.0165, yc + 0.10, z0 + 0.30), 'Grey')
    p.box_minmax((x - 0.017, y0 + 0.02, z0 + 0.32), (x + 0.017, y1 - 0.02, z0 + 0.34), 'Teal')   # top strip
    for yy in (y0 + 0.08, y1 - 0.08):                                         # arms to the platform
        p.box_minmax((x - 0.012, yy - 0.015, z0 - 0.04), (x + 0.012, yy + 0.015, z0 + 0.06), 'Chrome')


def build_lift():
    z = DECK_Z

    def f(p):
        for y in (-0.55, 0.55):                                              # inner columns
            p.box_minmax((-0.085, y - 0.055, DECK_Z - 0.40), (0.085, y + 0.055, z - 0.08), 'Chrome')
        p.box_minmax((-HALF_W, Y_FOOT, z - 0.10), (HALF_W, Y_HEAD, z - 0.07), 'Grey')     # frame
        for sx in (1, -1):
            p.box_minmax((sx * HALF_W - 0.03, Y_FOOT, z - 0.10), (sx * HALF_W, Y_HEAD, z - 0.02), 'Grey')
        p.box_minmax((-HALF_W + 0.02, Y_FOOT + 0.02, z - 0.02), (HALF_W - 0.02, HINGE_Y - 0.01, z), 'White')  # deck
        # mattress: seat + legs section (the head section rides on the Backrest)
        p.extrude(K.rrect(2 * HALF_W - 0.04, MAT_T, 0.04, 4, 0.0, z + MAT_T / 2), 'Y', Y_FOOT + 0.02,
                  HINGE_Y - 0.005, 'Vinyl')
        # foot board
        p.extrude(K.rrect(2 * HALF_W + 0.06, 0.42, 0.06, 4, 0.0, z + 0.14), 'Y', Y_FOOT - 0.07, Y_FOOT - 0.03,
                  'White')
        p.box_minmax((-HALF_W, Y_FOOT - 0.075, z + 0.28), (HALF_W, Y_FOOT - 0.025, z + 0.31), 'Teal')
        p.box_minmax((-0.25, Y_FOOT - 0.074, z + 0.02), (0.25, Y_FOOT - 0.07, z + 0.16), 'Grey')   # chart slot
        # head board with push handles
        p.extrude(K.rrect(2 * HALF_W + 0.06, 0.55, 0.06, 4, 0.0, z + 0.20), 'Y', Y_HEAD + 0.03, Y_HEAD + 0.07,
                  'White')
        p.box_minmax((-HALF_W, Y_HEAD + 0.025, z + 0.41), (HALF_W, Y_HEAD + 0.075, z + 0.44), 'Teal')
        for sx in (1, -1):
            HP.tube_path(p, [(sx * 0.38, Y_HEAD + 0.07, z + 0.30), (sx * 0.38, Y_HEAD + 0.15, z + 0.30),
                             (sx * 0.38, Y_HEAD + 0.15, z + 0.55)], 0.016, 'Chrome')
            p.cylinder((sx * (HALF_W + 0.02), Y_HEAD - 0.02, z - 0.08), (sx * (HALF_W + 0.02), Y_HEAD - 0.02,
                                                                        z + 0.02), 0.014, 'Black', n=10)  # IV socket
        # foot-end side rails (on the platform)
        for sx in (1, -1):
            side_rail(p, Y_FOOT + 0.25, HINGE_Y - 0.05, sx, z)
        # hand control hanging on the left foot rail
        p.box_minmax((-HALF_W - 0.08, -0.40, z + 0.05), (-HALF_W - 0.055, -0.32, z + 0.22), 'Grey')
        p.cylinder((-HALF_W - 0.067, -0.36, z + 0.22), (-HALF_W - 0.05, -0.36, z + 0.36), 0.004, 'Black', n=6)
    return mk('Lift', f)


def build_backrest():
    z = DECK_Z

    def f(p):
        p.box_minmax((-HALF_W + 0.02, HINGE_Y + 0.01, z - 0.02), (HALF_W - 0.02, Y_HEAD - 0.02, z), 'White')
        p.extrude(K.rrect(2 * HALF_W - 0.04, MAT_T, 0.04, 4, 0.0, z + MAT_T / 2), 'Y', HINGE_Y + 0.005,
                  Y_HEAD - 0.02, 'Vinyl')
        p.extrude(K.rrect(0.55, 0.10, 0.045, 4, 0.0, z + MAT_T + 0.05), 'Y', Y_HEAD - 0.40, Y_HEAD - 0.08,
                  'Linen')                                                             # pillow
        for sx in (1, -1):                                                             # head-end side rails
            side_rail(p, HINGE_Y + 0.12, Y_HEAD - 0.06, sx, z)
    return mk('Backrest', f)


def build_bed():
    build_materials()
    return [build_body(), build_lift(), build_backrest()]


LOD_PLAN = {'Body': (0.45, 0.15), 'Lift': (0.45, 0.15), 'Backrest': (0.45, 0.15)}
TEXEL_WEIGHTS = {'Body': 0.9, 'Lift': 1.0, 'Backrest': 1.0}
NODES = {'Body': ((0, 0, 0), 'HospitalBed'),
         'Lift': ((0, 0, DECK_Z), 'HospitalBed'),
         'Backrest': ((0, HINGE_Y, DECK_Z), 'Lift'),
         'PatientSocket': ((0, -0.05, DECK_Z + MAT_T + 0.02), 'Lift'),
         'DriverSocket': ((0, 1.40, 0), 'HospitalBed')}


def build_preview():
    objs = build_bed()
    for o in objs:                                                         # show the backrest raised 35 deg
        if o.name == 'Backrest':
            import bpy
            from mathutils import Matrix
            from math import radians
            piv = Matrix.Translation((0, HINGE_Y, DECK_Z))
            o.matrix_world = piv @ Matrix.Rotation(radians(35), 4, 'X') @ piv.inverted() @ o.matrix_world
            bpy.context.view_layer.update()
    return objs


if __name__ == '__main__':
    SP.station_main('HospitalBed', build_bed, LOD_PLAN, NODES, TEXEL_WEIGHTS)
