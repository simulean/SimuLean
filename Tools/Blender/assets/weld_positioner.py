"""
SimuLean asset: two-axis welding positioner (tilt + rotate table) -- the station a welding robot works at:
the part is clamped on the turntable and the positioner turns it so the robot welds in the flat position.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/weld_positioner.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/WeldPositioner/WeldPositioner.fbx   (+ 5 PNG maps)
    Tools/Blender/build/WeldPositioner.blend

Real-world reference: 2-axis tilt-rotate positioners of the 500 kg class (table 1.0 m, tilt axis ~0.9 m).
Library palette (graphite base, orange columns and cradle as the IndustrialRobot, dark steel table).

Frame as the other assets: authored front = -Y (Unity +Z: the side the robot works from), +Z up (Unity +Y),
origin on the floor at the centre of the base.

Animation hierarchy (Unity names; every name is a bare Transform with <name>_LOD0/1/2 meshes under it):
    WeldPositioner          root, on the floor at the base centre
    |- Body                 base frame, two columns, tilt drive (static)
    '- Tilt                 cradle, pivot (0, 0.90, 0): rotate about local X (-90 .. +90 deg; + tips the table
        |                   top towards the front)
        '- Turntable        faceplate, pivot (0, 0.62, 0): rotate about local Y (endless)
            '- WorkSocket   the faceplate top (0, 0.67, 0): the part is clamped here
"""

import os
import sys
import argparse
import time
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'WeldPositioner')
BUILD_DIR = os.path.join(KIT, 'build')

TILT_Z = 0.90                    # tilt axis height
COL_X = 0.62                     # column centres
TABLE_Z0, TABLE_Z1 = 0.62, 0.67  # faceplate bottom / top
TABLE_R = 0.50

MATS = {}


def build_materials():
    MATS.update({
        'Base': M.paint('Base', (0.028, 0.030, 0.033), rough=0.45, wear=0.4),
        'Orange': M.paint('Orange', (0.85, 0.20, 0.01), rough=0.32, wear=0.35),
        'Table': M.dark_steel('Table', color=(0.16, 0.16, 0.17), rough=0.35, metal=0.9),
        'Slot': M.plastic('Slot', color=(0.01, 0.01, 0.012), rough=0.8),
        'Motor': M.paint('Motor', (0.30, 0.31, 0.32), rough=0.38, wear=0.3),
        'Clamp': M.paint('Clamp', (0.80, 0.42, 0.015), rough=0.35, wear=0.4),
        'Copper': M.dark_steel('Copper', color=(0.62, 0.34, 0.18), rough=0.3, metal=0.95),
        'Bolt': M.dark_steel('Bolt', color=(0.12, 0.12, 0.125)),
    })


def mk(name, fn, bevel=0.004, seg=1):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    return ob


def build_body():
    def f(p):
        p.box_minmax((-0.80, -0.50, 0.0), (0.80, 0.50, 0.12), 'Base')                        # base frame
        for sx in (1, -1):
            x = sx * COL_X
            p.box_minmax((x - 0.10, -0.18, 0.12), (x + 0.10, 0.18, TILT_Z - 0.05), 'Orange')   # column
            p.lathe([(x - sx * 0.10, 0.14), (x + sx * 0.10, 0.14)], 32, 'Orange', center=(0, 0, TILT_Z), axis='X')
            for k in range(6):                                                                  # anchor bolts
                a = 2 * pi * k / 6
                p.cylinder((x + sx * 0.10, 0.10 * cos(a), TILT_Z + 0.10 * sin(a)),
                           (x + sx * 0.108, 0.10 * cos(a), TILT_Z + 0.10 * sin(a)), 0.008, 'Bolt', n=8)
        # tilt drive: motor + gearbox on the right column
        p.box_minmax((COL_X + 0.10, -0.12, TILT_Z - 0.12), (COL_X + 0.24, 0.12, TILT_Z + 0.12), 'Motor')
        p.cylinder((COL_X + 0.24, 0.0, TILT_Z), (COL_X + 0.46, 0.0, TILT_Z), 0.07, 'Motor', n=20)
        # earth / ground return block on the base
        p.box_minmax((-0.70, 0.30, 0.12), (-0.55, 0.42, 0.20), 'Copper')
    return mk('Body', f)


def build_tilt():
    def f(p):
        for sx in (1, -1):                                                                      # cradle arms
            x = sx * (COL_X - 0.16)
            p.lathe([(x - 0.05, 0.12), (x + 0.05, 0.12)], 28, 'Orange', center=(0, 0, TILT_Z), axis='X')
            p.box_minmax((x - 0.05, -0.12, TABLE_Z0 - 0.22), (x + 0.05, 0.12, TILT_Z), 'Orange')
        p.box_minmax((-(COL_X - 0.11), -0.25, TABLE_Z0 - 0.25), (COL_X - 0.11, 0.25, TABLE_Z0 - 0.12), 'Orange')
        p.cylinder((0.0, 0.0, TABLE_Z0 - 0.12), (0.0, 0.0, TABLE_Z0 - 0.02), 0.16, 'Motor', n=28)  # rotary drive
        p.cylinder((0.0, 0.30, TABLE_Z0 - 0.20), (0.0, 0.42, TABLE_Z0 - 0.20), 0.06, 'Motor', n=18)
    return mk('Tilt', f)


def build_turntable():
    def f(p):
        p.lathe([(TABLE_Z0, TABLE_R - 0.01), (TABLE_Z0 + 0.01, TABLE_R), (TABLE_Z1 - 0.004, TABLE_R),
                 (TABLE_Z1, TABLE_R - 0.006)], 64, 'Table', axis='Z', cap0=True, cap1=True)
        for k in range(4):                                                                      # T-slots
            a = pi / 4 * k
            p.box((0.0, 0.0, TABLE_Z1 + 0.0004), (2 * TABLE_R - 0.08, 0.02, 0.001), 'Slot',
                  rot=__import__('mathutils').Euler((0, 0, a)))
        for k in range(4):                                                                      # clamps
            a = pi / 2 * k + pi / 4
            x, y = 0.36 * cos(a), 0.36 * sin(a)
            p.box((x, y, TABLE_Z1 + 0.03), (0.10, 0.05, 0.06), 'Clamp',
                  rot=__import__('mathutils').Euler((0, 0, a)))
            p.cylinder((x, y, TABLE_Z1 + 0.06), (x, y, TABLE_Z1 + 0.10), 0.012, 'Bolt', n=10)
    return mk('Turntable', f, bevel=0.002)


def build_positioner():
    build_materials()
    return [build_body()], [build_tilt(), build_turntable()]


LOD_PLAN = {'Body': (0.4, 0.15), 'Tilt': (0.4, 0.15), 'Turntable': (0.4, 0.15)}
TEXEL_WEIGHTS = {'Turntable': 1.3, 'Tilt': 1.0}
NODES = {'Body': ((0, 0, 0), 'WeldPositioner'), 'Tilt': ((0, 0, TILT_Z), 'WeldPositioner'),
         'Turntable': ((0, 0, TABLE_Z0), 'Tilt'), 'WorkSocket': ((0, 0, TABLE_Z1), 'Turntable')}


def build_lods(parts, movers):
    lods = {}
    for ob in parts + movers:
        n = ob.name
        r1, r2 = LOD_PLAN[n]
        l1, l2 = K.make_lod(ob, r1, n + '_LOD1'), K.make_lod(ob, r2, n + '_LOD2')
        ob.name = ob.data.name = n + '_LOD0'
        lods[n] = [ob, l1, l2]
    return lods


def rig(lods):
    nodes = {'WeldPositioner': K.empty('WeldPositioner', (0, 0, 0), size=0.4, display='ARROWS')}
    for name, (pivot, _) in NODES.items():
        nodes[name] = K.empty(name, pivot, size=0.12 if name != 'WorkSocket' else 0.2,
                              display='CUBE' if name == 'WorkSocket' else 'PLAIN_AXES')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for name, (_, parent) in NODES.items():
        K.parent_keep_local(nodes[name], nodes[parent])
        bpy.context.view_layer.update()
    return nodes['WeldPositioner']


def build_preview():
    parts, movers = build_positioner()
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
    parts, movers = build_positioner()
    everything = parts + movers
    K.atlas_unwrap(everything, margin=0.003, weights=TEXEL_WEIGHTS)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.3
    except AttributeError:
        pass
    paths = K.bake_texture_set(everything, OUT_DIR, 'WeldPositioner', args.bake, args.save)
    K.assign_single_material(everything, K.make_runtime_material('WeldPositioner', paths))
    lods = build_lods(parts, movers)
    print('[positioner] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')
    root = rig(lods)
    K.export_fbx(root, os.path.join(OUT_DIR, 'WeldPositioner.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'WeldPositioner.blend'))
    print(f'[positioner] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
