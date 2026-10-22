"""
SimuLean asset: patient transport stretcher (emergency / transfer trolley) -- moves a lying patient between
the emergency department, imaging and the wards.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/stretcher.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/Stretcher/Stretcher.fbx   (+ 5 PNG maps)
    Tools/Blender/build/Stretcher.blend

Real-world reference: hydraulic emergency stretchers -- platform 1.95 x 0.68 m on a single central column,
deck 0.60 .. 0.90 m, 200 mm castors, folding side rails, push handles and an IV pole at the head end, an oxygen
cylinder holder under the deck.  Hospital palette (hospital_parts), no marks.

Frame as the vehicles: authored front = -Y (Unity +Z = the travel direction: the foot end goes first), +Z up
(Unity +Y), origin on the floor at the stretcher centre.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    Stretcher               root, on the floor at the centre
    |- Body                 base, castors, column shroud, oxygen holder (static)
    |- Lift                 platform, pad, rails, handles, IV pole, pivot (0, 0.80, 0): translate along local Y
    |   |                   (-0.20 .. +0.10)
    |   |- Backrest         head section, hinge (0, 0.80, -0.25): rotate about local X (0 .. +70 deg raises it)
    |   '- PatientSocket    on the pad, hips (0, 0.89, 0.05)
    '- DriverSocket         where the pushing porter stands, behind the head end (0, 0, -1.30)
"""

import os
import sys
from math import radians

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import station_parts as SP                  # noqa: E402
import hospital_parts as HP                 # noqa: E402

DECK_Z = 0.80
PAD_T = 0.08
HALF_W = 0.34
Y_FOOT, Y_HEAD = -0.98, 0.98
HINGE_Y = 0.25
CASTER_R = 0.10

MATS = {}


def build_materials():
    MATS.update(HP.materials(M))


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def build_body():
    def f(p):
        p.extrude(K.rrect(0.62, 0.08, 0.035, 4, 0.0, 0.31), 'Y', -0.85, 0.85, 'Shroud')    # base shroud
        for y in (-0.80, 0.80):
            for sx in (1, -1):
                HP.caster(p, sx * 0.25, y, CASTER_R, top=0.27)
        p.box_minmax((-0.13, -0.16, 0.35), (0.13, 0.16, DECK_Z - 0.30), 'Shroud')          # column bellows
        for k in range(6):
            z = 0.38 + k * (DECK_Z - 0.71) / 5
            p.box_minmax((-0.135, -0.165, z), (0.135, 0.165, z + 0.012), 'Grey')
        for sx in (1, -1):                                                                  # pedals
            p.box_minmax((sx * 0.31 - 0.03, -0.10, 0.30), (sx * 0.31 + 0.03, 0.10, 0.32), 'Teal')
        # oxygen cylinder in its holder, lying under the deck on the base
        p.cylinder((0.18, -0.60, 0.48), (0.18, 0.20, 0.48), 0.07, 'Teal', n=18)
        p.cylinder((0.18, 0.20, 0.48), (0.18, 0.27, 0.48), 0.03, 'Chrome', n=12)
        for y in (-0.45, 0.05):
            p.box_minmax((0.10, y - 0.015, 0.39), (0.26, y + 0.015, 0.42), 'Grey')
    return mk('Body', f)


def side_rail(p, y0, y1, sx, z0):
    """Folding tube rail (raised)."""
    x = sx * (HALF_W + 0.03)
    for z in (z0 + 0.12, z0 + 0.26):
        p.cylinder((x, y0, z), (x, y1, z), 0.012, 'Chrome', n=10)
    for y in (y0 + 0.05, (y0 + y1) / 2, y1 - 0.05):
        p.cylinder((x, y, z0 - 0.02), (x, y, z0 + 0.27), 0.010, 'Chrome', n=8)


def build_lift():
    z = DECK_Z

    def f(p):
        p.box_minmax((-0.10, -0.13, DECK_Z - 0.45), (0.10, 0.13, z - 0.08), 'Chrome')       # inner column
        p.extrude(K.rrect(2 * HALF_W + 0.04, 0.10, 0.03, 3, 0.0, z - 0.06), 'Y', Y_FOOT, Y_HEAD, 'White')  # frame
        p.extrude(K.rrect(2 * HALF_W, PAD_T, 0.035, 4, 0.0, z + PAD_T / 2), 'Y', Y_FOOT + 0.02, HINGE_Y - 0.005,
                  'Vinyl')
        for sx in (1, -1):
            side_rail(p, Y_FOOT + 0.20, Y_HEAD - 0.20, sx, z)
        # push handles at the head end, bumpers at the corners
        for sx in (1, -1):
            HP.tube_path(p, [(sx * 0.28, Y_HEAD - 0.02, z - 0.04), (sx * 0.28, Y_HEAD + 0.12, z - 0.04),
                             (sx * 0.28, Y_HEAD + 0.12, z + 0.22)], 0.016, 'Chrome')
            p.cylinder((sx * 0.28, Y_HEAD + 0.12, z + 0.08), (sx * 0.28, Y_HEAD + 0.12, z + 0.20), 0.020, 'Black',
                       n=12)                                                                  # grips
            for y in (Y_FOOT, Y_HEAD):
                p.cylinder((sx * (HALF_W + 0.02), y, z - 0.10), (sx * (HALF_W + 0.02), y, z - 0.02), 0.035,
                           'Grey', n=14)
        # IV pole (head, right) with a two-hook top
        x, y = HALF_W + 0.02, Y_HEAD - 0.03
        p.cylinder((x, y, z - 0.02), (x, y, z + 1.00), 0.012, 'Chrome', n=10)
        HP.tube_path(p, [(x - 0.10, y, z + 0.94), (x - 0.10, y, z + 1.00), (x + 0.10, y, z + 1.00),
                         (x + 0.10, y, z + 0.94)], 0.006, 'Chrome', n=8)
        p.box_minmax((-0.18, Y_FOOT - 0.006, z - 0.08), (0.18, Y_FOOT, z - 0.03), 'Teal')     # foot label strip
    return mk('Lift', f)


def build_backrest():
    z = DECK_Z

    def f(p):
        p.extrude(K.rrect(2 * HALF_W, PAD_T, 0.035, 4, 0.0, z + PAD_T / 2), 'Y', HINGE_Y + 0.005, Y_HEAD - 0.02,
                  'Vinyl')
        p.box_minmax((-HALF_W + 0.02, HINGE_Y + 0.01, z - 0.015), (HALF_W - 0.02, Y_HEAD - 0.03, z), 'Grey')
        p.extrude(K.rrect(0.45, 0.08, 0.035, 4, 0.0, z + PAD_T + 0.04), 'Y', Y_HEAD - 0.36, Y_HEAD - 0.08,
                  'Linen')                                                                    # pillow
    return mk('Backrest', f)


def build_stretcher():
    build_materials()
    return [build_body(), build_lift(), build_backrest()]


LOD_PLAN = {'Body': (0.45, 0.15), 'Lift': (0.45, 0.15), 'Backrest': (0.45, 0.15)}
TEXEL_WEIGHTS = {'Body': 0.9, 'Lift': 1.0, 'Backrest': 1.0}
NODES = {'Body': ((0, 0, 0), 'Stretcher'),
         'Lift': ((0, 0, DECK_Z), 'Stretcher'),
         'Backrest': ((0, HINGE_Y, DECK_Z), 'Lift'),
         'PatientSocket': ((0, -0.05, DECK_Z + PAD_T + 0.01), 'Lift'),
         'DriverSocket': ((0, 1.30, 0), 'Stretcher')}


def build_preview():
    objs = build_stretcher()
    import bpy
    from mathutils import Matrix
    for o in objs:
        if o.name == 'Backrest':
            piv = Matrix.Translation((0, HINGE_Y, DECK_Z))
            o.matrix_world = piv @ Matrix.Rotation(radians(30), 4, 'X') @ piv.inverted() @ o.matrix_world
    bpy.context.view_layer.update()
    return objs


if __name__ == '__main__':
    SP.station_main('Stretcher', build_stretcher, LOD_PLAN, NODES, TEXEL_WEIGHTS)
