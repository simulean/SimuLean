"""
SimuLean asset: a small corrugated carton, 255 L x 170 W x 120 H mm (drawn 260 x 175: the bulge and the proxies), the part a tote is packed with (4 = one 2 x 2
layer inside the library tote 600 x 400, interior 556 x 356: the Tote Packer preset).  Same carton as the
WarehouseLoadUnits kit (its `carton` material and `build_carton` geometry, LOD1 / LOD2 proxies baked from LOD0), but
a model of its own with its own small texture set, so the kit's shared atlas is not rebaked.

Run (Blender 5.x, no add-ons):
    blender -b --factory-startup -P Tools/Blender/assets/small_carton.py -- [--bake 2048] [--save 512] [--quick]
            [--cpu] [--no-render]
or, inside an open Blender (the MCP), with sys.argv set to ['small_carton.py', '--', ...] and the file exec'd.

Outputs
    Assets/SimuLean.Unity/Art/Models/SmallCarton/SmallCarton.fbx
    Assets/SimuLean.Unity/Art/Models/SmallCarton/SmallCarton_{Albedo,MetallicSmoothness,Normal,Occlusion,Emission}.png
    Tools/Blender/build/SmallCarton.blend, Tools/Blender/previews/smallcarton.png

Frame: the WarehouseLoadUnits kit's (length along Unity +Z, width along +X, pivot = bottom centre, metres).
TopSocket (0, 0.12, 0).
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
import warehouse_load_units as WL           # noqa: E402

NAME = 'SmallCarton'
L, W, H = 255, 170, 120                     # mm (length, width, height)
REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', NAME)


def build():
    WL.MATS.clear()
    WL.WEIGHTS.clear()
    WL.MATS[f'CardboardCarton_{L}x{W}x{H}'] = WL.carton(f'CardboardCarton_{L}x{W}x{H}', W / 1000, L / 1000, H / 1000)
    WL.MATS['Lid'] = M.plastic('Lid', color=(0.12, 0.125, 0.13), rough=0.55, grain=0.1)   # the proxies' stand-in
    unit = WL.build_carton(L, W, H)
    unit.piece.name = unit.name = NAME
    return unit


def preview(unit, quick):
    """Four cartons as one 2 x 2 layer (the tote's), and one on its own."""
    scene = bpy.context.scene
    for o in [unit.piece.root] + list(unit.piece.root.children_recursive):
        o.hide_render = True                # the exported piece (every LOD at the origin)
    KM.studio(scene, k=1.4, floor=40)
    T = lambda x, y: __import__('mathutils').Matrix.Translation((x, y, 0))   # noqa: E731
    coll = K.collection('Preview')
    for dx in (-0.0865, 0.0865):
        for dy in (-0.1315, 0.1315):
            KM.place(unit.piece, T(dx, dy), coll)
    KM.place(unit.piece, T(0.45, 0.0), coll)
    KM.render(scene, KM.camera(scene, 'Cam_Small', (0.55, -0.9, 0.55), (0.15, 0.0, 0.06), 40),
              os.path.join(WL.PREVIEW_DIR, 'smallcarton.png'), (900, 600) if quick else (1400, 900), 32 if quick else 128)


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=512)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 512
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene, backends=('CUDA', 'OPTIX', 'HIP', 'ONEAPI', 'METAL'))
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(WL.BUILD_DIR, exist_ok=True)
    os.makedirs(OUT_DIR, exist_ok=True)
    unit = build()
    WL.PREFIX = NAME                        # the texture set's name (WL.bake writes <PREFIX>_*.png)
    WL.bake([unit], OUT_DIR, args.bake, args.save, variant=False)
    WL.assemble_lods([unit])
    KM.report([unit.piece])
    KM.rotate_to_export([unit.piece])
    root = KM.rig(unit.piece)
    K.export_fbx(root, os.path.join(OUT_DIR, f'{NAME}.fbx'))
    for o in root.children_recursive:
        if o.type == 'EMPTY':
            o.name = f'{o.name}_{NAME}'
    blend = os.path.join(WL.BUILD_DIR, f'{NAME}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview(unit, args.quick)
    print(f'[smallcarton] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
