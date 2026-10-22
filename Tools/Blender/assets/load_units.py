"""
SimuLean kit: load units -- the things the resources carry and the stations hold.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/load_units.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per unit, one shared texture set / material LoadUnits.mat)
    Assets/SimuLean.Unity/Art/Models/LoadUnits/EuroPallet.fbx   EUR pallet 1200 x 800 x 144 mm (wood)
    Assets/SimuLean.Unity/Art/Models/LoadUnits/Tote.fbx         plastic small-load carrier (KLT type)
                                                                600 x 400 x 280 mm, stackable
    Assets/SimuLean.Unity/Art/Models/LoadUnits/RollCage.fbx     roll container 800 x 720 x 1800 mm
                                                                (galvanised, mesh on three sides, 4 casters)
    Assets/SimuLean.Unity/Art/Models/LoadUnits/LoadUnits_*.png
    Tools/Blender/build/LoadUnits.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z (the unit's length:
a pallet's 1200 mm, a tote's 600 mm, a roll cage's 800 mm depth, open at -Z), authored +Z = Unity +Y.
Each root is on the floor at the centre of the footprint.  Generic design, no third-party marks.

Nodes (Unity names, positions in the unit's frame, metres):
    EuroPallet  '- LoadSocket   the deck top (0, 0.144, 0): a load stands here
    Tote        |- LoadSocket   the inside floor (0, 0.02, 0)
                '- StackSocket  the rim (0, 0.28, 0): the next tote stacks here
    RollCage    '- LoadSocket   the deck top (0, 0.174, 0)
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
from simulean_kit import V, Part, rrect, finish, boolean  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'LoadUnits')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'LoadUnits'

MATS = {}


def build_materials():
    MATS.update({
        'Wood': M.wood('Wood', grain='y'),
        'WoodX': M.wood('WoodX', grain='x'),
        'WoodZ': M.wood('WoodZ', color=(0.46, 0.30, 0.16), grain='z'),
        'Tote': M.plastic('Tote', color=(0.06, 0.13, 0.26), rough=0.5, grain=0.05),
        'Galv': M.galvanised('Galv'),
        'Rubber': M.rubber('Rubber'),
        'Dark': M.dark_steel('Dark'),
        'Label': M.plastic('Label', color=(0.75, 0.75, 0.72), rough=0.4),
        'Slot': M.plastic('Slot', color=(0.004, 0.006, 0.01), rough=0.9),
    })


def mk(name, fn, bevel=0.003, seg=1, cutters=None):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        boolean(ob, [c() for c in cutters])
    finish(ob, bevel, seg, 40, 40)
    return ob


# =============================================================================
# EUR pallet
# =============================================================================

PAL_X = (-0.3275, 0.0, 0.3275)          # board / block lines across (800 mm)
PAL_Y = (-0.5275, 0.0, 0.5275)          # block lines along (1200 mm)


def build_pallet():
    def f(p):
        top_x = (-0.3275, -0.165, 0.0, 0.165, 0.3275)
        top_w = (0.145, 0.10, 0.145, 0.10, 0.145)
        for x, w in zip(top_x, top_w):                                       # top deck: 5 boards
            p.box_minmax((x - w / 2, -0.6, 0.122), (x + w / 2, 0.6, 0.144), 'Wood')
        for y in PAL_Y:                                                       # 3 stringer boards
            p.box_minmax((-0.4, y - 0.0725, 0.100), (0.4, y + 0.0725, 0.122), 'WoodX')
        for x in PAL_X:
            for y in PAL_Y:                                                   # 9 blocks (145 x 145)
                p.box_minmax((x - 0.0725, y - 0.0725, 0.022), (x + 0.0725, y + 0.0725, 0.100), 'WoodZ')
            w = 0.10 if x else 0.145                                          # 3 bottom boards
            p.box_minmax((x - w / 2, -0.6, 0.0), (x + w / 2, 0.6, 0.022), 'Wood')
    body = mk('PAL_Body', f, bevel=0.004)
    return KM.Piece('EuroPallet', 'fixed', {None: [body]}, levels=3, lod={'PAL_Body': (0.5, 0.2)},
                    sockets={'LoadSocket': (0.0, 0.0, 0.144)})


# =============================================================================
# Plastic tote (KLT type)
# =============================================================================

def build_tote():
    def ring(w, l, r, z):
        return [V((x, y, z)) for x, y in rrect(w, l, r, 4)]

    def f(p):
        rings = [ring(0.380, 0.580, 0.025, 0.0), ring(0.390, 0.590, 0.030, 0.012),
                 ring(0.396, 0.596, 0.032, 0.252), ring(0.404, 0.604, 0.034, 0.258),
                 ring(0.404, 0.604, 0.034, 0.280), ring(0.372, 0.572, 0.022, 0.280),
                 ring(0.356, 0.556, 0.018, 0.020)]
        p.loft(rings, 'Tote')                                                 # walls + rim + inside, floor caps
        for sx in (1, -1):                                                    # ribs on the long walls
            for y in (-0.22, -0.11, 0.0, 0.11, 0.22):
                p.box((sx * 0.199, y, 0.135), (0.008, 0.014, 0.23), 'Tote')
        for sy in (1, -1):                                                    # ribs + label holder, short walls
            for x in (-0.13, 0.13):
                p.box((x, sy * 0.299, 0.12), (0.014, 0.008, 0.20), 'Tote')
            p.box((0.0, sy * 0.300, 0.10), (0.14, 0.004, 0.07), 'Label')
            p.box((0.0, sy * 0.2985, 0.222), (0.13, 0.004, 0.032), 'Slot')            # hand grip (reads as a hole)
            p.box((0.0, sy * 0.301, 0.2425), (0.15, 0.006, 0.009), 'Tote')           # grip lip above it
    body = mk('KLT_Body', f, bevel=0.002)
    return KM.Piece('Tote', 'fixed', {None: [body]}, levels=3, lod={'KLT_Body': (0.45, 0.15)},
                    sockets={'LoadSocket': (0.0, 0.0, 0.02), 'StackSocket': (0.0, 0.0, 0.28)})


# =============================================================================
# Roll cage
# =============================================================================

RC_X, RC_Y, RC_H, DECK = 0.36, 0.40, 1.80, 0.174


def build_rollcage():
    def frame(p):
        # base frame and deck
        p.box_minmax((-RC_X, -RC_Y, 0.135), (RC_X, RC_Y, 0.17), 'Galv')
        p.box_minmax((-RC_X + 0.01, -RC_Y + 0.01, 0.17), (RC_X - 0.01, RC_Y - 0.01, DECK), 'Galv')
        # wall frames (back +Y, sides +-X): round tubes
        r = 0.011
        posts = [(-RC_X, RC_Y), (RC_X, RC_Y), (-RC_X, -RC_Y), (RC_X, -RC_Y)]
        for x, y in posts:
            p.cylinder((x, y, DECK), (x, y, RC_H), r, 'Galv', n=10)
        for (x0, y0), (x1, y1) in (((-RC_X, RC_Y), (RC_X, RC_Y)), ((-RC_X, -RC_Y), (-RC_X, RC_Y)),
                                   ((RC_X, -RC_Y), (RC_X, RC_Y))):
            for z in (DECK + 0.02, RC_H - 0.01):
                p.cylinder((x0, y0, z), (x1, y1, z), r, 'Galv', n=10)

    def mesh(p):
        w = 0.0035
        zs = [DECK + 0.15 * k for k in range(1, 11)]
        # back wall
        for k in range(1, 9):
            x = -RC_X + k * (2 * RC_X / 9)
            p.box_minmax((x - w, RC_Y - w, DECK + 0.02), (x + w, RC_Y + w, RC_H - 0.01), 'Galv')
        for z in zs:
            p.box_minmax((-RC_X, RC_Y - w, z - w), (RC_X, RC_Y + w, z + w), 'Galv')
        # side walls
        for sx in (1, -1):
            for k in range(1, 10):
                y = -RC_Y + k * (2 * RC_Y / 10)
                p.box_minmax((sx * RC_X - w, y - w, DECK + 0.02), (sx * RC_X + w, y + w, RC_H - 0.01), 'Galv')
            for z in zs:
                p.box_minmax((sx * RC_X - w, -RC_Y, z - w), (sx * RC_X + w, RC_Y, z + w), 'Galv')

    def casters(p):
        for x in (-RC_X + 0.07, RC_X - 0.07):
            for y in (-RC_Y + 0.08, RC_Y - 0.08):
                p.box((x, y, 0.128), (0.07, 0.07, 0.012), 'Dark')                       # top plate
                for sx in (-1, 1):
                    p.box((x + sx * 0.022, y, 0.095), (0.005, 0.04, 0.06), 'Dark')      # fork
                p.lathe([(-0.016, 0.03), (-0.014, 0.05), (0.014, 0.05), (0.016, 0.03)], 18, 'Rubber',
                        center=(x, y, 0.05), axis='X', mats=['Rubber', 'Rubber', 'Rubber'])
                p.cylinder((x - 0.02, y, 0.05), (x + 0.02, y, 0.05), 0.008, 'Dark', n=8)
    parts = [mk('RC_Frame', frame, bevel=0.002), mk('RC_Mesh', mesh, bevel=0.0), mk('RC_Casters', casters, bevel=0.002)]
    return KM.Piece('RollCage', 'fixed', {None: parts}, levels=3,
                    lod={'RC_Frame': (0.5, 0.2), 'RC_Mesh': (1.0, 0.4), 'RC_Casters': (0.4, None)},
                    sockets={'LoadSocket': (0.0, 0.0, DECK)})


def build_kit():
    build_materials()
    return [build_pallet(), build_tote(), build_rollcage()]


def build_preview():
    """Preview (no bake): the units side by side along X."""
    pieces = build_kit()
    for i, pc in enumerate(pieces):
        for o in pc.parts:
            o.location.x += i * 1.6
    return [o for pc in pieces for o in pc.parts]


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=1024)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true', help='(accepted for symmetry: no previews)')
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
    export_one_by_one(pieces)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[units] done in {time.time() - t0:.1f}s')


def export_one_by_one(pieces):
    """Rig and export each piece, then rename its sockets: pieces share socket names (LoadSocket) and
    Blender object names are unique, so the next piece's sockets would otherwise get a .001 suffix."""
    for pc in pieces:
        root = KM.rig(pc)
        K.export_fbx(root, os.path.join(OUT_DIR, f'{pc.name}.fbx'))
        for o in root.children_recursive:
            if o.name in pc.sockets:
                o.name = f'{o.name}_{pc.name}'


if __name__ == '__main__':
    main()
