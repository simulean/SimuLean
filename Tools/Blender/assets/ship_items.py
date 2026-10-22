"""
SimuLean kit: the steel items of a shipyard's block process -- what the sources release and the first shops
handle before the parametric block look takes over (ParametricBlockLook in Unity).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/ship_items.py -- [--bake 2048] [--save 2048] [--quick] [--cpu]

Outputs (a kit: one FBX per item, one shared texture set / material ShipItems.mat)
    Assets/SimuLean.Unity/Art/Models/ShipItems/SteelPlate.fbx     a shop-primed plate 12 x 2.5 m, 15 mm
    Assets/SimuLean.Unity/Art/Models/ShipItems/PlateKit.fbx       a sub-block kit: cut plates and a few profiles
                                                                  stacked on timber dunnage, 8 x 3 m
    Assets/SimuLean.Unity/Art/Models/ShipItems/ProfileBundle.fbx  ten bulb flats 12 m long, strapped, on two blocks
    Assets/SimuLean.Unity/Art/Models/ShipItems/ShipItems_*.png
    Tools/Blender/build/ShipItems.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X (the width), authored +Y = Unity +Z (the length), authored
+Z = Unity +Y.  Each root is on the floor at the centre of the footprint.  Generic design, no marks.

Nodes: LoadSocket on top of each item (where the next one rests).
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
from simulean_kit import Part, finish       # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'ShipItems')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'ShipItems'

MATS = {}
PRIMER = (0.21, 0.23, 0.22)          # grey shop primer
CUT = (0.30, 0.30, 0.31)             # cut edges


def build_materials():
    MATS.update({
        'Primer': M.paint('Primer', PRIMER, rough=0.6, wear=0.5, dirt=0.7),
        'PrimerRed': M.paint('PrimerRed', (0.30, 0.07, 0.04), rough=0.55, wear=0.4, dirt=0.6),
        'Edge': M.dark_steel('Edge', color=CUT, rough=0.35, metal=0.9),
        'Wood': M.wood('Wood', color=(0.36, 0.25, 0.14), grain='x', dirt=1.4),
        'Band': M.galvanised('Band'),
        'Chalk': M.paint('Chalk', (0.75, 0.75, 0.70), rough=0.8, wear=0.1, dirt=0.2),
    })


def mk(name, fn, bevel=0.002):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, 1, 40, 40)
    return ob


def plate(p, cx, cy, z, w, l, t, mat='Primer', marks=True):
    """A plate w (x) by l (y), t thick, its underside at z; cut edges, chalk marking lines on top."""
    p.box((cx, cy, z + t / 2), (w, l, t), mat)
    for sx in (-1, 1):                                                   # the cut edges, a touch brighter
        p.box((cx + sx * (w / 2 + 0.0005), cy, z + t / 2), (0.001, l, t * 0.9), 'Edge')
    if marks:
        p.box((cx - w * 0.3, cy, z + t + 0.0004), (0.02, l * 0.8, 0.0008), 'Chalk')
        p.box((cx + w * 0.1, cy + l * 0.2, z + t + 0.0004), (w * 0.5, 0.02, 0.0008), 'Chalk')


def bulb_flat(p, x, y0, y1, z, h=0.24, t=0.011, mat='Primer'):
    """A bulb flat (HP profile) lying on its side: the web along y, the bulb at one edge."""
    p.box((x, (y0 + y1) / 2, z + t / 2), (h, y1 - y0, t), mat)
    p.box((x + h / 2 - 0.015, (y0 + y1) / 2, z + t + 0.012), (0.03, y1 - y0, 0.024), mat)


def dunnage(p, xs, ys, h=0.12, w=0.15):
    for y in ys:
        p.box((0, y, h / 2), (xs, w, h), 'Wood')


# =============================================================================
# items
# =============================================================================

def build_plate():
    T = 0.015
    body = mk('SP_Body', lambda p: plate(p, 0, 0, 0.0, 2.5, 12.0, T), bevel=0.0)
    return KM.Piece('SteelPlate', 'fixed', {None: [body]}, levels=2, lod={'SP_Body': (0.5,)},
                    sockets={'LoadSocket': (0.0, 0.0, T)})


def build_kit():
    def f(p):
        dunnage(p, 3.2, (-3.2, 0.0, 3.2))
        z = 0.12
        # cut plates of a sub-block, largest first, slightly offset like a real stack
        for i, (w, l, t, dx, dy) in enumerate([(2.9, 7.8, 0.016, 0.0, 0.0), (2.6, 7.2, 0.014, 0.08, -0.1),
                                              (2.2, 6.0, 0.014, -0.12, 0.3), (1.8, 5.2, 0.012, 0.1, -0.6),
                                              (1.4, 3.6, 0.012, -0.2, 1.4), (1.2, 2.4, 0.012, 0.3, -2.0)]):
            plate(p, dx, dy, z, w, l, t, 'Primer' if i % 3 else 'PrimerRed', marks=i < 3)
            z += t
        for k, x in enumerate((-0.9, -0.4, 0.4, 0.9)):                      # profiles on top, on cross bearers
            pass
        p.box((0, -2.5, z + 0.04), (2.6, 0.08, 0.08), 'Wood')
        p.box((0, 2.5, z + 0.04), (2.6, 0.08, 0.08), 'Wood')
        for x in (-0.9, -0.35, 0.35, 0.9):
            bulb_flat(p, x, -3.4, 3.4, z + 0.08)
        return z
    top = [0.0]
    def g(p):
        top[0] = f(p)
    body = mk('PK_Body', g, bevel=0.0)
    return KM.Piece('PlateKit', 'fixed', {None: [body]}, levels=3, lod={'PK_Body': (0.5, 0.2)},
                    sockets={'LoadSocket': (0.0, 0.0, top[0] + 0.12)})


def build_bundle():
    def f(p):
        for y in (-4.0, 4.0):
            p.box((0, y, 0.06), (1.0, 0.2, 0.12), 'Wood')
        z = 0.12
        for row in range(2):                                                  # two layers of five
            for k in range(5):
                bulb_flat(p, -0.5 + k * 0.25, -6.0, 6.0, z + row * 0.05, h=0.22)
        zt = z + 0.1 + 0.04
        for y in (-4.5, 0.0, 4.5):                                            # steel straps round the bundle
            p.box((0, y, zt + 0.002), (1.4, 0.03, 0.003), 'Band')
            for sx in (-1, 1):
                p.box((sx * 0.70, y, (z + zt) / 2), (0.003, 0.03, zt - z + 0.01), 'Band')
            p.box((0, y, z - 0.002), (1.4, 0.03, 0.003), 'Band')
    body = mk('PB_Body', f, bevel=0.0)
    return KM.Piece('ProfileBundle', 'fixed', {None: [body]}, levels=3, lod={'PB_Body': (0.5, 0.2)},
                    sockets={'LoadSocket': (0.0, 0.0, 0.27)})


def build_kit_pieces():
    build_materials()
    return [build_plate(), build_kit(), build_bundle()]


def build_preview():
    """Preview (no bake): the items side by side along X."""
    pieces = build_kit_pieces()
    for i, pc in enumerate(pieces):
        for o in pc.parts:
            o.location.x += i * 4.0
    return [o for pc in pieces for o in pc.parts]


def export_one_by_one(pieces):
    """Rig and export each piece; rename its sockets after (the pieces share LoadSocket)."""
    for pc in pieces:
        root = KM.rig(pc)
        K.export_fbx(root, os.path.join(OUT_DIR, f'{pc.name}.fbx'))
        for o in root.children_recursive:
            if o.name in pc.sockets:
                o.name = f'{pc.name}_{o.name}'


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=2048)
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
    pieces = build_kit_pieces()
    old_slot, KM.SLOT = KM.SLOT, 16.0                                         # the items are up to 12 m long
    try:
        KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, ao_distance=0.3)
    finally:
        KM.SLOT = old_slot
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    export_one_by_one(pieces)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[ship items] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
