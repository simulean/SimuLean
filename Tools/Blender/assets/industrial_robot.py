"""
SimuLean asset: 6-axis articulated industrial robot (150-210 kg class, ~2.7 m reach) for a robot
resource team (UnityResourceTeam, Travel = Axes): the tool centre point follows the team's x / y / z
axis profiles and RobotArmRig / RobotArmView (Unity) solve the joint angles.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/industrial_robot.py -- [--bake 4096]
            [--save 2048] [--no-render] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/IndustrialRobot/IndustrialRobot.fbx   (+ 5 PNG maps)
    Tools/Blender/build/IndustrialRobot.blend                              (source, regenerable)
    Tools/Blender/previews/robot_*.png                                     (renders)

Generic design in the library palette (orange arm, graphite pedestal, grey motors, black hoses),
no third-party marks.  One model for floor and ceiling mounting: a ceiling robot is the same
model with its root turned 180 deg about its forward axis (RobotArmRig.mount); the kinematics
are solved in the frame of the root, so they hold for both.

Frame as the other assets: authored front = -Y (Unity +Z), +Z up (Unity +Y), origin on the floor
on the A1 axis.  Below, positions are in UNITY coordinates of the zero pose (x right, y up,
z forward) and every node has an identity local rotation in the zero pose:
upper arm vertical, forearm horizontal pointing forward (+Z), flange facing +Z.

Kinematic chain (Unity names).  Every name is a bare Transform; its meshes hang under it as
<name>_LOD0/_LOD1/_LOD2 with identity local transforms, so animate the named node.
    IndustrialRobot          root, on the floor on the A1 axis (mount point)
    |- Base                  pedestal (static)
    '- A1                    pivot (0, 0, 0)          rotate about local Y   (base yaw)
        '- A2                pivot (0, 0.675, 0.35)   rotate about local X   (shoulder)
            '- A3            pivot A2 + (0, 1.15, 0)  rotate about local X   (elbow)
                '- A4        pivot A3 + (0, 0, 0.30)  rotate about local Z   (forearm roll)
                    '- A5    pivot A3 + (0, 0, 1.20)  rotate about local X   (wrist pitch)
                        '- A6      pivot A5 + (0, 0, 0.12)  rotate about local Z (flange roll)
                            '- Flange   A5 + (0, 0, 0.215): the flange face
                                '- ToolSocket   tool / gripped item mount (= flange face)
    Link lengths: D1 = 0.675 (floor -> A2 height), A1OFF = 0.35 (A1 axis -> A2, forward),
    L2 = 1.15 (A2 -> A3), L3 = 1.20 (A3 -> A5, along the forearm), L6 = 0.215 (A5 -> flange).
    The A2 / A3 / A5 axes are the X axis of their node, A4 / A6 the Z axis (forearm / flange).
    Signs are Unity's (left-handed) Quaternion.AngleAxis about the local axis: +A2 tilts the
    upper arm forward (towards +Z), +A3 lowers the forearm, +A5 points the flange down.
    Joint ranges (deg, used by RobotArmRig): A1 +-185, A2 -140..+5 (in this sign: -5..+140
    forward is the working side; see RobotArmRig), A3 -120..+155, A4 +-350, A5 +-125, A6 +-350.
"""

import os
import sys
import argparse
import time
from math import radians, sin, cos, pi

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
from mathutils import Euler                 # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import V, Part, rrect, circle, finish  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'IndustrialRobot')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

# ---------------------------------------------------------------- kinematics (metres, authored)
D1, A1OFF, L2, L3, L6 = 0.675, 0.35, 1.15, 1.20, 0.215
A4_AT, A6_AT = 0.30, 0.12          # node positions along the forearm / beyond A5 (on the roll axes)
Y2 = -A1OFF                        # authored y of the A2 / A3 / forearm axis line (front = -Y)
Z2, Z3 = D1, D1 + L2               # A2, A3 heights
Y5 = Y2 - L3                       # A5
YF = Y5 - L6                       # flange face
PAINT = (0.85, 0.20, 0.01)         # library orange (forklift --paint orange)

MATS = {}


def build_materials():
    logo, aspect = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_logo.png'),
                                       256, FONT, bold_offset=0.016)
    ink = (0.025, 0.025, 0.028)
    lw = 0.42
    decals = [  # along the upper arm, outer (+X) face, reading upwards
        dict(image=logo, origin=(0.36, Y2 + 0.07, Z2 + 0.55), u=(0, 0, 1), v=(0, -1, 0),
             size=(lw, lw / aspect), normal=(1, 0, 0), color=ink),
    ]
    MATS.update({
        'Paint': M.paint('Paint', PAINT, rough=0.30, wear=0.35, decals=decals),
        'BasePaint': M.paint('BasePaint', (0.028, 0.030, 0.033), rough=0.45, wear=0.5),
        'Motor': M.paint('Motor', (0.30, 0.31, 0.32), rough=0.38, wear=0.3),
        'Joint': M.dark_steel('Joint'),
        'Flange': M.dark_steel('Flange', color=(0.20, 0.20, 0.21), rough=0.3, metal=0.95),
        'Hose': M.rubber('Hose', color=(0.03, 0.03, 0.032), rough=0.5),
        'Bolt': M.dark_steel('Bolt', color=(0.12, 0.12, 0.125)),
    })
    return MATS


def mk(name, fn, bevel=0.006, seg=2, angle=40.0, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def ring_xy(w, h, r, z, cx=0.0, cy=0.0, seg=4):
    return [V((x + cx, y + cy, z)) for x, y in rrect(w, h, r, seg)]


def hub_x(p, x0, x1, r, y, z, mat, n=40, cap_mat=None):
    """A short cylinder along X (joint housing), slightly rounded at both ends."""
    e = min(0.012, (x1 - x0) * 0.2)
    p.lathe([(x0, r - e), (x0 + e, r), (x1 - e, r), (x1, r - e)], n, mat, center=(0, y, z), axis='X',
            mats=[cap_mat or mat, mat, cap_mat or mat])


# =============================================================================
# Parts (one per node)
# =============================================================================

def build_base():
    def f(p):
        p.loft([ring_xy(0.86, 0.86, 0.14, 0.0), ring_xy(0.86, 0.86, 0.14, 0.05),
                ring_xy(0.80, 0.80, 0.12, 0.065)], 'BasePaint')
        p.lathe([(0.065, 0.36), (0.10, 0.35), (0.22, 0.31), (0.245, 0.31)], 48, 'BasePaint', axis='Z',
                cap0=False)
        for sx in (-1, 1):
            for sy in (-1, 1):
                c = (sx * 0.33, sy * 0.33)
                p.cylinder((c[0], c[1], 0.05), (c[0], c[1], 0.085), 0.035, 'Bolt', n=12)
    return mk('Base', f, bevel=0.008)


def build_a1():
    """Carousel: turntable ring, a body leaning forward to the shoulder boss, the A1 motor."""
    def f(p):
        p.lathe([(0.245, 0.315), (0.275, 0.315)], 48, 'Joint', axis='Z', cap0=False, cap1=False)
        p.lathe([(0.275, 0.32), (0.29, 0.33), (0.40, 0.33), (0.42, 0.31)], 48, 'Paint', axis='Z',
                cap0=False)
        rings = [ring_xy(0.56, 0.60, 0.18, 0.40, cy=-0.02), ring_xy(0.50, 0.56, 0.16, 0.52, cy=-0.10),
                 ring_xy(0.38, 0.46, 0.14, 0.64, cy=-0.24)]
        p.loft(rings, 'Paint', cap0=False)
        hub_x(p, -0.15, 0.15, 0.27, Y2, Z2, 'Paint', cap_mat='Joint')
        # A1 motor at the back, A2 motor on the left of the shoulder
        p.cylinder((0, 0.20, 0.40), (0, 0.20, 0.62), 0.075, 'Motor', n=24)
        p.cylinder((0, 0.20, 0.62), (0, 0.20, 0.65), 0.06, 'Joint', n=24)
        p.cylinder((-0.15, Y2, Z2), (-0.33, Y2, Z2), 0.09, 'Motor', n=24)
        p.cylinder((-0.33, Y2, Z2), (-0.36, Y2, Z2), 0.07, 'Joint', n=24)
    return mk('A1', f, bevel=0.008)


def build_a2():
    """Upper arm on the +X side of the shoulder / elbow, slightly bowed back, with a hose."""
    def f(p):
        hub_x(p, 0.15, 0.36, 0.27, Y2, Z2, 'Paint', cap_mat='Joint')
        hub_x(p, 0.15, 0.33, 0.20, Y2, Z3, 'Paint', cap_mat='Joint')
        prof = [(Z2 + 0.10, 0.0, 0.21, 0.44), (Z2 + 0.35, 0.06, 0.20, 0.38), (Z2 + 0.60, 0.08, 0.19, 0.34),
                (Z2 + 0.85, 0.06, 0.18, 0.31), (Z3 - 0.08, 0.0, 0.18, 0.30)]
        rings = [ring_xy(w, d, 0.06, z, cx=0.255, cy=Y2 + dy) for z, dy, w, d in prof]
        p.loft(rings, 'Paint')
        # dress pack: a hose along the back of the arm
        path = [V((0.255, Y2 + 0.25, Z2 + 0.05)), V((0.255, Y2 + 0.29, Z2 + 0.45)),
                V((0.255, Y2 + 0.27, Z2 + 0.85)), V((0.255, Y2 + 0.19, Z3 - 0.02))]
        p.sweep(path, circle(0.032, 12), 'Hose', side=(1, 0, 0))
        for z in (Z2 + 0.30, Z2 + 0.70):
            p.box((0.255, Y2 + 0.23, z), (0.08, 0.07, 0.03), 'Joint')
    return mk('A2', f, bevel=0.008)


def build_a3():
    """Elbow housing with the wrist motors behind it and the forearm collar up to A4."""
    def f(p):
        hub_x(p, -0.15, 0.15, 0.19, Y2, Z3, 'Paint', cap_mat='Joint')
        p.loft([ring_xy(0.28, 0.30, 0.06, Z3 - 0.13, cy=Y2 + 0.22), ring_xy(0.28, 0.30, 0.06, Z3 + 0.15, cy=Y2 + 0.22)],
               'Paint')
        for x in (-0.08, 0.0, 0.08):
            p.cylinder((x, Y2 + 0.37, Z3 + 0.03), (x, Y2 + 0.52, Z3 + 0.03), 0.045, 'Motor', n=18)
            p.cylinder((x, Y2 + 0.52, Z3 + 0.03), (x, Y2 + 0.54, Z3 + 0.03), 0.035, 'Joint', n=18)
        p.lathe([(Y2 - 0.12, 0.165), (Y2 - A4_AT, 0.155)], 40, 'Paint', center=(0, 0, Z3), axis='Y',
                cap0=False, cap1=False)
        p.lathe([(Y2 - A4_AT + 0.02, 0.16), (Y2 - A4_AT, 0.16)], 40, 'Joint', center=(0, 0, Z3), axis='Y',
                cap0=False)
    return mk('A3', f, bevel=0.006)


def build_a4():
    """Forearm tube and the wrist fork round A5."""
    def f(p):
        y0 = Y2 - A4_AT
        p.lathe([(y0, 0.145), (y0 - 0.10, 0.14), (Y5 + 0.30, 0.115), (Y5 + 0.20, 0.11)], 40, 'Paint',
                center=(0, 0, Z3), axis='Y', cap0=False)
        for sx in (-1, 1):
            x0, x1 = (0.07, 0.12) if sx > 0 else (-0.12, -0.07)
            p.box(((x0 + x1) / 2, Y5 + 0.13, Z3), (x1 - x0, 0.18, 0.16), 'Paint')
            hub_x(p, x0, x1, 0.085, Y5, Z3, 'Paint', n=28, cap_mat='Joint')
    return mk('A4', f, bevel=0.005)


def build_a5():
    """Wrist body between the fork cheeks and its nose up to A6."""
    def f(p):
        hub_x(p, -0.07, 0.07, 0.075, Y5, Z3, 'Paint', n=28, cap_mat='Joint')
        p.lathe([(Y5 - 0.03, 0.072), (Y5 - A6_AT + 0.01, 0.068), (Y5 - A6_AT, 0.06)], 32, 'Paint',
                center=(0, 0, Z3), axis='Y', cap0=False)
    return mk('A5', f, bevel=0.004)


def build_a6():
    """Flange: a turning collar and the ISO flange disc with bolt holes."""
    def f(p):
        y0 = Y5 - A6_AT
        p.lathe([(y0, 0.058), (YF + 0.035, 0.058), (YF + 0.035, 0.07), (YF, 0.07), (YF, 0.02)], 36, 'Joint',
                center=(0, 0, Z3), axis='Y', mats=['Joint', 'Flange', 'Flange', 'Flange'], cap0=False)
        for k in range(6):
            a = 2 * pi * k / 6
            x, z = 0.05 * cos(a), Z3 + 0.05 * sin(a)
            p.cylinder((x, YF + 0.0005, z), (x, YF - 0.002, z), 0.006, 'Bolt', n=8)
    return mk('A6', f, bevel=0.003)


PART_BUILDERS = {'Base': build_base, 'A1': build_a1, 'A2': build_a2, 'A3': build_a3, 'A4': build_a4,
                 'A5': build_a5, 'A6': build_a6}


def build_robot():
    build_materials()
    return [fn() for fn in PART_BUILDERS.values()]


# =============================================================================
# LODs and rig
# =============================================================================

LOD_PLAN = {'Base': (0.35, 0.12), 'A1': (0.35, 0.12), 'A2': (0.35, 0.12), 'A3': (0.35, 0.12),
            'A4': (0.35, 0.12), 'A5': (0.4, 0.15), 'A6': (0.4, None)}
TEXEL_WEIGHTS = {'Base': 0.6, 'A1': 1.0, 'A2': 1.1, 'A3': 1.0, 'A4': 1.0, 'A5': 1.2, 'A6': 1.2}

# node -> (authored pivot, parent)
NODES = {
    'Base': ((0, 0, 0), 'IndustrialRobot'),
    'A1': ((0, 0, 0), 'IndustrialRobot'),
    'A2': ((0, Y2, Z2), 'A1'),
    'A3': ((0, Y2, Z3), 'A2'),
    'A4': ((0, Y2 - A4_AT, Z3), 'A3'),
    'A5': ((0, Y5, Z3), 'A4'),
    'A6': ((0, Y5 - A6_AT, Z3), 'A5'),
    'Flange': ((0, YF, Z3), 'A6'),
    'ToolSocket': ((0, YF, Z3), 'Flange'),
}


def build_lods(parts):
    """-> {node: [LOD0, LOD1, (LOD2)]}."""
    lods = {}
    for ob in parts:
        name = ob.name
        r1, r2 = LOD_PLAN[name]
        l1 = K.make_lod(ob, r1, name + '_LOD1')
        l2 = K.make_lod(ob, r2, name + '_LOD2') if r2 else None
        ob.name = ob.data.name = name + '_LOD0'
        lods[name] = [ob, l1] + ([l2] if l2 else [])
    return lods


def rig(lods):
    nodes = {'IndustrialRobot': K.empty('IndustrialRobot', (0, 0, 0), size=0.5, display='ARROWS')}
    for name, (pivot, _) in NODES.items():
        nodes[name] = K.empty(name, pivot, size=0.12 if name != 'ToolSocket' else 0.08,
                              display='CUBE' if name == 'ToolSocket' else 'PLAIN_AXES')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for name, (_, parent) in NODES.items():
        K.parent_keep_local(nodes[name], nodes[parent])
        bpy.context.view_layer.update()
    return nodes['IndustrialRobot']


def build_preview(a=(0, 0, 0, 0, 0, 0)):
    """Live preview (no bake / LODs): the rigged chain in pose `a` (degrees, Unity signs)."""
    parts = build_robot()
    lods = {}
    for ob in parts:
        name = ob.name
        ob.name = ob.data.name = name + '_LOD0'
        lods[name] = [ob]
    root = rig(lods)
    pose(root, a)
    return [root] + list(root.children_recursive)


def show_lod(objs, level):
    for o in objs:
        if o.type == 'MESH' and '_LOD' in o.name:
            o.hide_render = not o.name.endswith(f'_LOD{level}')


def pose(root, a=(0, 0, 0, 0, 0, 0)):
    """Preview pose (degrees, Unity signs) on the authored nodes.  A Unity (left-handed) angle about
    Unity X / Y / Z is the same angle about Blender +X, minus it about Blender Z, the same about +Y."""
    objs = {o.name: o for o in root.children_recursive}
    axes = {'A1': ('Z', -1), 'A2': ('X', 1), 'A3': ('X', 1), 'A4': ('Y', 1), 'A5': ('X', 1), 'A6': ('Y', 1)}
    for i, n in enumerate(('A1', 'A2', 'A3', 'A4', 'A5', 'A6')):
        ax, s = axes[n]
        e = [0.0, 0.0, 0.0]
        e['XYZ'.index(ax)] = radians(a[i]) * s
        objs[n].rotation_euler = Euler(e)
    bpy.context.view_layer.update()


# =============================================================================
# Previews (the forklift's studio)
# =============================================================================

def preview_renders(scene, root, quick):
    import forklift as FL
    objs = [root] + list(root.children_recursive)
    FL.studio(scene)
    try:
        scene.view_settings.view_transform = 'AgX'
        scene.view_settings.look = 'AgX - Punchy'
    except TypeError:
        pass
    spp = 32 if quick else 128
    res = (800, 500) if quick else (1600, 1000)
    os.makedirs(PREVIEW_DIR, exist_ok=True)
    show_lod(objs, 0)
    pose(root, (30, 25, 20, 0, 45, 0))
    FL.render(scene, FL.camera(scene, 'Cam_Hero', (4.2, -4.6, 2.6), (0, -0.6, 1.2), 50),
              os.path.join(PREVIEW_DIR, 'robot_hero.png'), res, spp)
    pose(root)
    FL.render(scene, FL.camera(scene, 'Cam_Side', (5.5, -0.8, 1.6), (0, -0.6, 1.2), 50),
              os.path.join(PREVIEW_DIR, 'robot_zero.png'), res, spp)


# =============================================================================
# Main
# =============================================================================

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true', help='small bake + fast renders')
    ap.add_argument('--cpu', action='store_true', help='bake / render on the CPU')
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

    parts = build_robot()
    K.atlas_unwrap(parts, margin=0.003, weights=TEXEL_WEIGHTS)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.4      # AO bake reach
    except AttributeError:
        pass
    paths = K.bake_texture_set(parts, OUT_DIR, 'IndustrialRobot', args.bake, args.save)
    runtime = K.make_runtime_material('IndustrialRobot', paths)
    K.assign_single_material(parts, runtime)

    lods = build_lods(parts)
    print('[robot] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')

    root = rig(lods)
    K.export_fbx(root, os.path.join(OUT_DIR, 'IndustrialRobot.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'IndustrialRobot.blend'))
    if not args.no_render:
        preview_renders(scene, root, args.quick)
    print(f'[robot] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
