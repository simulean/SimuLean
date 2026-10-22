"""
SimuLean asset: hand pallet truck (manual pallet jack, 2.5 t class) -- pulled by an operator.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/pallet_truck.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/PalletTruck/PalletTruck.fbx   (+ 5 PNG maps)
    Tools/Blender/build/PalletTruck.blend

Real-world reference: standard hand pallet trucks -- forks 1150 x 540 mm (two 160 mm forks), lowered
height 85 mm, lift ~120 mm, 200 mm steer wheels, tiller ~1.2 m.  Generic design, library yellow, no marks.

Frame as the vehicles: authored front = -Y (Unity +Z = the travel direction: the tiller goes first, the
operator pulls it), +Z up (Unity +Y), origin on the floor under the steer-wheel axle.

Animation hierarchy (Unity names).  Every name is a bare Transform; its meshes hang under it as
<name>_LOD0/_LOD1/_LOD2 with identity local transforms, so animate the named node.
    PalletTruck            root, on the floor under the steer axle
    |- Body                pump housing, hydraulic cylinder (static)
    |- Handle              tiller, pivot (0, 0.40, -0.06): tilt about local X (rest = 20 deg towards the front)
    |- Wheel_L / Wheel_R   steer wheels: spin about local X, r = 0.10 m (UnityVehicleAnimator's AGV radius)
    |- Lift                forks + load rollers: localPosition.y = 0 .. 0.12 (UnityVehicleAnimator.liftStroke)
    |   '- LoadSocket      fork top, mid-length (0, 0.085, -0.70): a carried pallet goes here
    '- DriverSocket        where the operator pulling it stands (0, 0, 0.75)
"""

import os
import sys
import argparse
import time
from math import radians, sin, cos

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import V, Part, circle, finish  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'PalletTruck')
BUILD_DIR = os.path.join(KIT, 'build')

FORK_LEN, FORK_W, FORK_X, FORK_TOP = 1.15, 0.16, 0.19, 0.085
Y_PLATE = 0.06                       # fork back plate (authored +Y = behind the pump)
W_R, W_X = 0.10, 0.065               # steer wheels
PIVOT = V((0.0, 0.06, 0.40))         # tiller pivot (authored)
TILT = 20.0                          # tiller rest tilt towards the front (-Y)
HANDLE_LEN = 0.86
PAINT = (0.80, 0.42, 0.015)          # library yellow

MATS = {}


def build_materials():
    MATS.update({
        'Paint': M.paint('Paint', PAINT, rough=0.38, wear=0.35),
        'Dark': M.dark_steel('Dark'),
        'Chrome': M.chrome('Chrome'),
        'Black': M.plastic('Black', color=(0.02, 0.02, 0.022), rough=0.5),
        'Tire': M.rubber('Tire', color=(0.06, 0.055, 0.05), rough=0.6),
        'Hub': M.dark_steel('Hub', color=(0.12, 0.12, 0.13)),
    })


def mk(name, fn, bevel=0.003, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def build_body():
    def f(p):
        p.box_minmax((-0.13, -0.10, 0.13), (0.13, 0.05, 0.25), 'Paint')                    # pump base
        p.lathe([(0.25, 0.075), (0.36, 0.07), (0.40, 0.05)], 20, 'Paint', center=(0, -0.02, 0), axis='Z')
        p.cylinder((0, -0.02, 0.36), (0, -0.02, 0.43), 0.028, 'Chrome', n=16)                # ram
        for sx in (1, -1):                                                                    # fork yoke cheeks
            p.box_minmax((sx * 0.10 - 0.012, -0.08, 0.10), (sx * 0.10 + 0.012, 0.06, 0.32), 'Paint')
        p.cylinder((-0.11, 0.0, W_R), (0.11, 0.0, W_R), 0.018, 'Dark', n=12)                  # steer axle
    return mk('Body', f)


def build_lift():
    def f(p):
        # back plate joining the forks
        p.box_minmax((-0.27, Y_PLATE, 0.05), (0.27, Y_PLATE + 0.06, 0.33), 'Paint')
        p.box_minmax((-0.27, Y_PLATE - 0.01, 0.05), (0.27, Y_PLATE, 0.09), 'Dark')
        for sx in (1, -1):
            x0, x1 = sx * FORK_X - FORK_W / 2, sx * FORK_X + FORK_W / 2
            y0, y1 = Y_PLATE + 0.06, Y_PLATE + 0.06 + FORK_LEN
            # fork: an inverted channel, tapering at the tip
            prof = [(x0, 0.03), (x0, FORK_TOP), (x1, FORK_TOP), (x1, 0.03), (x1 - 0.006, 0.03),
                    (x1 - 0.006, FORK_TOP - 0.006), (x0 + 0.006, FORK_TOP - 0.006), (x0 + 0.006, 0.03)]
            p.extrude(prof, 'Y', y0, y1 - 0.12, 'Paint')
            tip = [V((x, y1 - 0.12, z)) for x, z in prof]
            end = [V((x, y1, 0.03 + (z - 0.03) * 0.45)) for x, z in prof]       # tip ramps down for pallet entry
            p.loft([tip, end], 'Paint', cap0=False)
            # load rollers under the fork tip
            for yy in (y1 - 0.17, y1 - 0.09):
                p.lathe([(-0.035, 0.036), (0.035, 0.036)], 14, 'Tire', center=(sx * FORK_X, yy, 0.038), axis='X')
            p.box_minmax((sx * FORK_X - 0.05, y1 - 0.20, 0.035), (sx * FORK_X + 0.05, y1 - 0.06, 0.05), 'Dark')
            p.box_minmax((sx * FORK_X - 0.02, y0 + 0.05, 0.03), (sx * FORK_X + 0.02, y1 - 0.2, 0.045), 'Dark')  # push rod
    return mk('Lift', f)


def build_handle():
    def f(p):
        up = V((0.0, -sin(radians(TILT)), cos(radians(TILT))))
        top = PIVOT + up * HANDLE_LEN
        p.cylinder(PIVOT, top, 0.018, 'Paint', n=12)
        p.box(tuple(PIVOT), (0.10, 0.06, 0.06), 'Paint')                                       # pivot block
        # loop grip at the top
        side = V((1, 0, 0))
        g0, g1 = top + side * 0.14, top - side * 0.14
        p.sweep([top - up * 0.10 + side * 0.03, top + side * 0.12, g0 + up * 0.12, g1 + up * 0.12,
                 top - side * 0.12, top - up * 0.10 - side * 0.03], circle(0.016, 10), 'Black', side=(0, 1, 0))
        p.box(tuple(top + up * 0.05), (0.06, 0.02, 0.03), 'Dark')                              # lowering lever
    return mk('Handle', f)


def build_wheel(name, side):
    def f(p):
        p.lathe([(-0.028, W_R - 0.012), (-0.022, W_R), (0.022, W_R), (0.028, W_R - 0.012), (0.028, 0.04),
                 (-0.028, 0.04)], 28, 'Tire', axis='X', mats=['Tire', 'Tire', 'Tire', 'Tire', 'Hub'])
        p.lathe([(-0.024, 0.04), (0.024, 0.04)], 16, 'Hub', axis='X')
    ob = mk(name, f)
    ob.location = (side * W_X, 0.0, W_R)
    bpy.context.view_layer.update()
    return ob


def build_truck():
    build_materials()
    return [build_body()], [build_lift(), build_handle(), build_wheel('Wheel_L', 1), build_wheel('Wheel_R', -1)]


# =============================================================================
# LODs and rig
# =============================================================================

LOD_PLAN = {'Body': (0.4, 0.15), 'Lift': (0.4, 0.15), 'Handle': (0.4, 0.15),
            'Wheel_L': (0.35, None), 'Wheel_R': (0.35, None)}
TEXEL_WEIGHTS = {'Lift': 1.2, 'Handle': 0.8, 'Wheel_L': 0.5, 'Wheel_R': 0.5}
NODE_PIVOTS = {'Body': (0, 0, 0), 'Lift': (0, 0, 0), 'Handle': tuple(PIVOT),
               'Wheel_L': (W_X, 0, W_R), 'Wheel_R': (-W_X, 0, W_R)}


def build_lods(parts, movers):
    lods = {}
    for ob in parts + movers:
        n = ob.name
        r1, r2 = LOD_PLAN[n]
        l1 = K.make_lod(ob, r1, n + '_LOD1')
        l2 = K.make_lod(ob, r2, n + '_LOD2') if r2 else None
        ob.name = ob.data.name = n + '_LOD0'
        lods[n] = [ob, l1] + ([l2] if l2 else [])
    return lods


def rig(lods):
    nodes = {'PalletTruck': K.empty('PalletTruck', (0, 0, 0), size=0.4, display='ARROWS')}
    for name, pivot in NODE_PIVOTS.items():
        nodes[name] = K.empty(name, pivot, size=0.1)
    nodes['LoadSocket'] = K.empty('LoadSocket', (0, Y_PLATE + 0.06 + FORK_LEN * 0.55, FORK_TOP), size=0.2, display='CUBE')
    nodes['DriverSocket'] = K.empty('DriverSocket', (0, -0.75, 0), size=0.2, display='CUBE')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for child in list(NODE_PIVOTS) + ['DriverSocket']:
        K.parent_keep_local(nodes[child], nodes['PalletTruck'])
    K.parent_keep_local(nodes['LoadSocket'], nodes['Lift'])
    bpy.context.view_layer.update()
    return nodes['PalletTruck']


def build_preview():
    parts, movers = build_truck()
    return parts + movers


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
    parts, movers = build_truck()
    everything = parts + movers
    K.atlas_unwrap(everything, margin=0.003, weights=TEXEL_WEIGHTS)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.2
    except AttributeError:
        pass
    paths = K.bake_texture_set(everything, OUT_DIR, 'PalletTruck', args.bake, args.save)
    K.assign_single_material(everything, K.make_runtime_material('PalletTruck', paths))
    lods = build_lods(parts, movers)
    print('[truck] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')
    root = rig(lods)
    K.export_fbx(root, os.path.join(OUT_DIR, 'PalletTruck.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'PalletTruck.blend'))
    print(f'[truck] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
