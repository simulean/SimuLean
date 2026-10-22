"""
SimuLean asset: hospital entrance / exit -- automatic sliding glass doors, the visual of where patients come in (a
Source) and leave (a Sink) in the healthcare library (2026-10-09).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/hospital_door.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/HospitalDoor/HospitalDoor.fbx   (+ 5 PNG maps)
    Tools/Blender/build/HospitalDoor.blend

Real-world reference: hospital main-entrance automatic sliding doors -- a 1.8 m clear opening (two 0.95 m leaves),
fixed glazed side lights, 2.4 m door head, an operator box above with the hospital band, a recessed entrance mat.
Hospital palette (hospital_parts): white, teal band, satin aluminium frames, dark glazing.

Frame: authored front = -Y (Unity +Z: the inside of the building, where patients walk away / arrive from), +Z up,
origin on the floor at the centre of the opening.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    HospitalDoor            root, on the floor at the centre of the opening
    |- Body                 frames, side lights, operator box, canopy, mat (static)
    |- Door_L / Door_R      the sliding leaves: slide outwards along X (local X -/+ 0.95 m) to open
    '- ItemSocket           where patients appear / disappear, inside, 0.9 m in front of the opening
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

OPEN_HX = 0.95                   # half the opening (two 0.95 m leaves)
SIDE_W = 0.85                    # fixed side light width
HEAD_Z = 2.40                    # top of the opening
BOX_Z = 2.85                     # top of the operator box
T = 0.06                         # frame section
LEAF_Y = -0.02                   # the leaves slide in front of the frame plane

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))
    MATS['Glass'] = M.glass('Glass', tint=(0.020, 0.040, 0.045))
    MATS['Alu'] = M.dark_steel('Alu', color=(0.62, 0.63, 0.64), rough=0.30, metal=0.9)
    MATS['Mat'] = M.rubber('Mat', color=(0.035, 0.037, 0.04), rough=0.9)


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def build_body():
    def f(p):
        x_out = OPEN_HX + SIDE_W
        # posts at both ends and between the side lights and the opening
        for x in (-x_out, -OPEN_HX - T, OPEN_HX, x_out - T):
            p.box_minmax((x, -T / 2, 0.0), (x + T, T / 2, BOX_Z), 'Alu')
        # transom under the operator box, sill rails of the side lights
        p.box_minmax((-x_out, -T / 2, HEAD_Z), (x_out, T / 2, HEAD_Z + T), 'Alu')
        for x0, x1 in ((-x_out + T, -OPEN_HX - T), (OPEN_HX + T, x_out - T)):
            p.box_minmax((x0, -T / 2, 0.0), (x1, T / 2, 0.10), 'Alu')
            p.box_minmax((x0, -0.008, 0.10), (x1, 0.008, HEAD_Z), 'Glass')                 # fixed side light
            p.box_minmax((x0, -0.012, 1.00), (x1, 0.012, 1.06), 'Teal')                    # manifestation band
        # operator box (white) with the teal band, on both faces
        p.box_minmax((-x_out, -0.16, HEAD_Z + T), (x_out, 0.16, BOX_Z), 'White')
        for y in (-0.165, 0.16):
            p.box_minmax((-x_out, y, BOX_Z - 0.22), (x_out, y + 0.005, BOX_Z - 0.10), 'Teal')
        # a small canopy outside (+Y authored = Unity -Z, the street side)
        p.box_minmax((-x_out - 0.2, 0.16, BOX_Z), (x_out + 0.2, 1.40, BOX_Z + 0.12), 'White')
        p.box_minmax((-x_out - 0.2, 1.36, BOX_Z - 0.10), (x_out + 0.2, 1.40, BOX_Z), 'Teal')
        # recessed entrance mat across the opening, both sides
        p.box_minmax((-OPEN_HX - 0.2, -1.20, 0.0), (OPEN_HX + 0.2, 1.20, 0.012), 'Mat')
        # sensor domes over the opening
        for y in (-0.17, 0.17):
            p.box_minmax((-0.12, y - 0.03 if y < 0 else y, BOX_Z - 0.32), (0.12, y if y < 0 else y + 0.03, BOX_Z - 0.26),
                         'Black')
    return mk('Body', f)


def build_leaf(name, x0, x1):
    def f(p):
        # aluminium frame and the glass, a vertical pull bar
        p.box_minmax((x0, LEAF_Y - 0.03, 0.0), (x1, LEAF_Y + 0.03, 0.10), 'Alu')                    # bottom rail
        p.box_minmax((x0, LEAF_Y - 0.03, HEAD_Z - 0.06), (x1, LEAF_Y + 0.03, HEAD_Z), 'Alu')        # top rail
        p.box_minmax((x0, LEAF_Y - 0.03, 0.10), (x0 + 0.05, LEAF_Y + 0.03, HEAD_Z - 0.06), 'Alu')   # stiles
        p.box_minmax((x1 - 0.05, LEAF_Y - 0.03, 0.10), (x1, LEAF_Y + 0.03, HEAD_Z - 0.06), 'Alu')
        p.box_minmax((x0 + 0.05, LEAF_Y - 0.008, 0.10), (x1 - 0.05, LEAF_Y + 0.008, HEAD_Z - 0.06), 'Glass')
        p.box_minmax((x0 + 0.05, LEAF_Y - 0.012, 1.00), (x1 - 0.05, LEAF_Y + 0.012, 1.06), 'Teal')
    return mk(name, f)


def build_door():
    build_materials()
    body = build_body()
    left = build_leaf('Door_L', -OPEN_HX, 0.0)
    right = build_leaf('Door_R', 0.0, OPEN_HX)
    return [body, left, right]


LOD_PLAN = {'Body': (0.45, 0.15), 'Door_L': (0.5, 0.2), 'Door_R': (0.5, 0.2)}
TEXEL_WEIGHTS = {'Body': 1.0, 'Door_L': 1.0, 'Door_R': 1.0}
NODES = {'Body': ((0, 0, 0), 'HospitalDoor'),
         'Door_L': ((-OPEN_HX / 2, LEAF_Y, 0), 'HospitalDoor'),
         'Door_R': ((OPEN_HX / 2, LEAF_Y, 0), 'HospitalDoor'),
         'ItemSocket': ((0, -0.90, 0), 'HospitalDoor')}


def build_preview():
    return build_door()


if __name__ == '__main__':
    SP.station_main('HospitalDoor', build_door, LOD_PLAN, NODES, TEXEL_WEIGHTS)
