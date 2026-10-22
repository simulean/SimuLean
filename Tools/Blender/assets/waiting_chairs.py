"""
SimuLean kit: beam seating for waiting areas (hospital waiting rooms, receptions) -- a row of seats of any length.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/waiting_chairs.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per piece, one shared texture set / material WaitingChairs.mat)
    Assets/SimuLean.Unity/Art/Models/WaitingChairs/Seat_Row.fbx     segment, 0.60 m along the run: one seat on
                                                                    the beam, the armrest and leg before it
    Assets/SimuLean.Unity/Art/Models/WaitingChairs/Seat_Table.fbx   segment, 0.60 m: a side table on the beam
    Assets/SimuLean.Unity/Art/Models/WaitingChairs/Seat_End.fbx     cap: the end leg + the last armrest
    Tools/Blender/build/WaitingChairs.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X (the seats face +X), authored +Y = Unity +Z (the run:
pieces tile along it, period 0.60 m), authored +Z = Unity +Y.  Moulded vinyl seats in the hospital palette on a
chrome beam, a T-leg at every armrest (each segment starts with one; the end cap closes the run).  Generic, no marks.

Sockets: Seat_Row '- Seat   the seat top (0.03, 0.46, 0.30) (Unity): a waiting patient sits here, facing +X.
Runs are laid out like the other kits (ModularKit.Strip: n = round(run / 0.60) segments, an end leg after the
last, and one before the first).
"""

import os
import sys
import argparse
import time

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import simulean_modular as KM               # noqa: E402
import hospital_parts as HP                 # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'WaitingChairs')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'WaitingChairs'

PERIOD = 0.60
SEAT_Z = 0.46
BEAM_Z = 0.36
BEAM_X = -0.05

MATS = {}


def build_materials():
    K.NB.default_period = {'y': PERIOD}
    MATS.update(HP.materials(M))


def mk(name, fn, bevel=0.003):
    return HP.mk(MATS, name, fn, bevel)


def beam(p, y0, y1):
    p.box_minmax((BEAM_X - 0.03, y0, BEAM_Z - 0.03), (BEAM_X + 0.03, y1, BEAM_Z + 0.03), 'Chrome')


def armrest(p, y):
    """Armrest between two seats, at y."""
    p.box_minmax((BEAM_X - 0.02, y - 0.012, BEAM_Z + 0.03), (BEAM_X + 0.02, y + 0.012, 0.62), 'Chrome')
    p.box_minmax((BEAM_X - 0.02, y - 0.03, 0.62), (0.22, y + 0.03, 0.65), 'Black')


def seat(p, yc):
    """Moulded seat + back shell centred at yc."""
    w = 0.50
    p.extrude(K.rrect(0.46, 0.06, 0.025, 3, 0.03, SEAT_Z - 0.03), 'Y', yc - w / 2, yc + w / 2, 'Vinyl')  # seat
    # back, slightly reclined, behind the beam
    from mathutils import Euler
    from math import radians
    p.box((BEAM_X - 0.20, yc, SEAT_Z + 0.30), (0.05, w, 0.46), 'Vinyl', rot=Euler((0, radians(-10), 0)))
    p.box_minmax((BEAM_X - 0.02, yc - 0.03, BEAM_Z + 0.03), (BEAM_X + 0.02, yc + 0.03, SEAT_Z - 0.06), 'Chrome')
    p.box_minmax((BEAM_X - 0.22, yc - 0.03, BEAM_Z - 0.02), (BEAM_X + 0.02, yc + 0.03, BEAM_Z + 0.02), 'Chrome')
    p.box_minmax((BEAM_X - 0.25, yc - 0.03, BEAM_Z - 0.02), (BEAM_X - 0.21, yc + 0.03, SEAT_Z + 0.15), 'Chrome')


def leg(p, y):
    """T-leg at y: post from the beam to the floor and a foot along X."""
    p.box_minmax((BEAM_X - 0.025, y - 0.025, 0.03), (BEAM_X + 0.025, y + 0.025, BEAM_Z - 0.03), 'Chrome')
    p.box_minmax((-0.30, y - 0.03, 0.0), (0.22, y + 0.03, 0.04), 'Chrome')
    for x in (-0.28, 0.20):
        p.box_minmax((x - 0.02, y - 0.025, -0.0), (x + 0.02, y + 0.025, 0.008), 'Black')


def build_row():
    def f(p):
        beam(p, 0.0, PERIOD)
        armrest(p, 0.0)
        leg(p, 0.0)
        seat(p, PERIOD / 2)
    body = mk('SR_Body', f)
    return KM.Piece('Seat_Row', 'segment', [body], levels=2, lod={'SR_Body': (1.0,)},
                    sockets={'Seat': (0.03, PERIOD / 2, SEAT_Z)},
                    ghosts=[('Seat_Row', (0, PERIOD, 0)), ('Seat_Row', (0, -PERIOD, 0))])


def build_table():
    def f(p):
        beam(p, 0.0, PERIOD)
        armrest(p, 0.0)
        leg(p, 0.0)
        p.box_minmax((-0.20, 0.04, SEAT_Z - 0.01), (0.22, PERIOD - 0.04, SEAT_Z + 0.02), 'Birch')
        p.box_minmax((BEAM_X - 0.02, PERIOD / 2 - 0.03, BEAM_Z + 0.03), (BEAM_X + 0.02, PERIOD / 2 + 0.03,
                                                                         SEAT_Z - 0.01), 'Chrome')
    body = mk('ST_Body', f)
    return KM.Piece('Seat_Table', 'segment', [body], levels=2, lod={'ST_Body': (1.0,)},
                    ghosts=[('Seat_Row', (0, PERIOD, 0)), ('Seat_Row', (0, -PERIOD, 0))])


def build_end():
    def f(p):
        armrest(p, 0.0)
        leg(p, 0.0)
    body = mk('SE_Body', f)
    return KM.Piece('Seat_End', 'cap', [body], levels=2, lod={'SE_Body': (1.0,)},
                    ghosts=[('Seat_Row', (0, -PERIOD, 0))])


def build_kit():
    build_materials()
    return [build_row(), build_table(), build_end()]


def build_preview():
    """Preview (no bake): end, 3 seats, a table, 2 seats, end."""
    pieces = build_kit()
    by = {pc.name: pc for pc in pieces}
    order = ['Seat_End', 'Seat_Row', 'Seat_Row', 'Seat_Row', 'Seat_Table', 'Seat_Row', 'Seat_Row', 'Seat_End']
    used, out = set(), []
    y = 0.0
    for name in order:
        src = by[name].parts
        if name in used:
            src = [o.copy() for o in src]
            for o in src:
                bpy.context.scene.collection.objects.link(o)
        used.add(name)
        for o in src:
            o.location.y = y
            out.append(o)
        if by[name].kind == 'segment':
            y += PERIOD
    return out


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=1024)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    pieces = build_kit()
    KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, ao_distance=0.2)
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[waiting chairs] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
