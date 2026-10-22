"""
SimuLean asset: warehouse drive-unit AGV (goods-to-person robot, 450 kg class) for an AGV fleet
(UnityResourceTeam, Network travel).  It drives under a shelf pod, lifts it on its turntable
and carries it, or carries a load on the turntable.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/agv.py -- [--bake 4096] [--save 2048]
            [--no-render] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/AGV/AGV.fbx      (+ 5 PNG maps)
    Tools/Blender/build/AGV.blend                     (source, regenerable)
    Tools/Blender/previews/agv_*.png                  (renders)

Real-world reference: square-ish warehouse drive units (0.8-1.0 m, ~0.4 m high, a lifting
turntable on top, longer than wide, the drive wheels showing through side pockets).  Generic
design in the library palette (orange shell, brushed-steel skirt, black segmented turntable,
black camera module and corner panels), no third-party marks.  Footprint 1.05 (Y, travel) x
0.80 (X) m, deck at 0.33 m, turntable top 0.362 m lowered, 60 mm lift stroke, differential drive.

Frame as the forklift: authored front = -Y (Unity +Z, the travel direction), +Z up (Unity +Y),
origin on the floor at the centre of the drive axle.

Animation hierarchy (Unity names).  Every name is a bare Transform; its meshes hang under it
as <name>_LOD0/_LOD1/_LOD2 with identity local transforms, so animate the named node.
    AGV                    root, on the floor at the drive-axle centre
    |- Body                shell, skirt, end panels, e-stop, lamps, casters, status band
    |- Wheel_L / Wheel_R   drive wheels in the side pockets: spin about local X, r = 0.10 m
    |                      (differential drive: opposite spins turn the AGV on the spot)
    '- Lift                turntable, pivot on the deck (z 0.332): localPosition.y = 0 .. 0.06
        '- LoadSocket      parent the carried load here (turntable top, z 0.362 lowered)
The turntable is round, so it has no rotation node of its own: a view that keeps a pod
aligned while the robot turns rotates the carried load under LoadSocket.

The status light band (Body, emissive, round the skirt joint) can be tinted by a view.
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
from mathutils import Euler                 # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import V, Part, rrect, circle, finish, boolean  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'AGV')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

# ---------------------------------------------------------------- dimensions (metres)
WID, LEN = 0.80, 1.05            # footprint (X across, Y along the travel)
Y0, Y1 = -LEN / 2, LEN / 2       # front (-Y) .. rear (+Y)
CORNER = 0.14                    # plan corner radius
Z_SKIRT0, Z_SKIRT1 = 0.025, 0.12    # brushed-steel skirt (floor clearance 25 mm)
H_DECK = 0.33                    # top of the orange shell
Z_LIFT0, Z_TOP = 0.332, 0.362    # turntable underside / top (lowered)
R_TABLE = 0.345                  # turntable radius
LIFT_STROKE = 0.06
W_R, W_HW, W_X = 0.10, 0.032, 0.335  # drive wheel radius, half width, centre x
POCKET = W_R + 0.02              # side pocket radius round the drive wheel
CASTER_R = 0.035
PAINT = (0.85, 0.20, 0.01)       # library orange (forklift --paint orange)

MATS = {}


def build_materials():
    logo, aspect = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_logo.png'),
                                       256, FONT, bold_offset=0.016)
    ink = (0.025, 0.025, 0.028)
    lw = 0.26
    decals = [
        dict(image=logo, origin=(WID / 2, 0.29, 0.225), u=(0, 1, 0), v=(0, 0, 1),
             size=(lw, lw / aspect), normal=(1, 0, 0), color=ink),
        dict(image=logo, origin=(-WID / 2, 0.29, 0.225), u=(0, -1, 0), v=(0, 0, 1),
             size=(lw, lw / aspect), normal=(-1, 0, 0), color=ink),
    ]
    MATS.update({
        'Paint': M.paint('Paint', PAINT, rough=0.32, wear=0.45, decals=decals),
        'Skirt': M.dark_steel('Skirt', color=(0.36, 0.37, 0.38), rough=0.34, metal=0.9),
        'Panel': M.plastic('Panel', color=(0.016, 0.016, 0.018), rough=0.42),
        'Table': M.plastic('Table', color=(0.03, 0.03, 0.033), rough=0.5),
        'Hub': M.dark_steel('Hub'),
        'Seam': M.plastic('Seam', color=(0.10, 0.10, 0.105), rough=0.5),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Tire': M.rubber('Tire', color=(0.06, 0.055, 0.05), rough=0.6),   # polyurethane tread
        'Rubber': M.rubber('Rubber'),
        'Glass': M.glass('Glass'),
        'LensStatus': M.lens('LensStatus', (0.10, 0.85, 0.30)),
        'LensW': M.lens('LensW', (1.0, 0.93, 0.80)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'EStop': M.plastic('EStop', color=(0.55, 0.02, 0.015), rough=0.3),
        'EStopBase': M.plastic('EStopBase', color=(0.80, 0.42, 0.015), rough=0.4),
    })
    return MATS


def mk(name, fn, bevel=0.004, seg=1, angle=40.0, cutters=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        boolean(ob, [c() for c in cutters])
    finish(ob, bevel, seg, angle, sharp)
    return ob


def plan(inset=0.0, z=0.0):
    """The rounded plan outline at height z, inset from the footprint."""
    return [V((x, y, z)) for x, y in rrect(WID - 2 * inset, LEN - 2 * inset, max(CORNER - inset, 0.01), 5)]


# =============================================================================
# Body
# =============================================================================

def build_shell():
    """Steel skirt, a small ledge, the orange shell with a rounded top edge and the deck."""
    def f(p):
        rings = [plan(0.018, Z_SKIRT0), plan(0.012, Z_SKIRT0 + 0.01), plan(0.012, Z_SKIRT1),
                 plan(0.0, Z_SKIRT1 + 0.006), plan(0.0, H_DECK - 0.022), plan(0.006, H_DECK - 0.008),
                 plan(0.022, H_DECK)]
        p.loft(rings, 'Paint', band_mats=['Skirt', 'Skirt', 'Panel', 'Paint', 'Paint', 'Paint'],
               cap_mat='Paint')
        # dark ring round the turntable
        p.lathe([(H_DECK - 0.002, R_TABLE + 0.016), (H_DECK + 0.002, R_TABLE + 0.016),
                 (H_DECK + 0.002, R_TABLE - 0.02)], 64, 'Panel', axis='Z', cap0=False, cap1=False)
    return mk('Shell', f, bevel=0.005, seg=2, cutters=[cut_wheel_pocket(1), cut_wheel_pocket(-1)])


def cut_wheel_pocket(side):
    """A round pocket in the side round each drive wheel, so the wheel shows (as the reference)."""
    def f():
        p = Part('cutter')
        x0, x1 = (W_X - W_HW - 0.012, WID) if side > 0 else (-WID, -W_X + W_HW + 0.012)
        p.lathe([(x0, POCKET), (x1, POCKET)], 40, 'Panel', center=(0, 0, W_R), axis='X')
        return p.build(MATS)
    return f


def _face_panels(p, y_face, out, inner):
    """The black panels of one end: two trapezoids and a centre module (front only).
    y_face: the shell face, out: -1 front / +1 rear (outward direction)."""
    a0, a1 = y_face + out * 0.012, y_face - out * 0.006
    lo, hi = min(a0, a1), max(a0, a1)
    for sx in (-1, 1):
        prof = K.fillet_closed([(sx * 0.255, 0.150), (sx * 0.120, 0.150), (sx * 0.100, 0.290),
                                (sx * 0.255, 0.290)], 0.016, 3)
        p.extrude(prof, 'Y', lo, hi, 'Panel')
    if inner:
        p.box((0, y_face + out * 0.006, 0.19), (0.12, 0.03, 0.27), 'Panel')


def build_panels():
    def f(p):
        _face_panels(p, Y0, -1, True)
        _face_panels(p, Y1, 1, False)
    return mk('Panels', f, bevel=0.004)


def build_details():
    """Lenses, camera window, e-stop and the status band (all small, one object)."""
    def f(p):
        yf = Y0 - 0.012                                   # front panel surface
        yc = Y0 - 0.021                                   # centre module surface
        # centre camera module: status lamp on top, camera window, a small display below
        p.box((0, yc - 0.002, 0.295), (0.07, 0.004, 0.012), 'LensStatus')
        p.box((0, yc - 0.001, 0.245), (0.09, 0.004, 0.06), 'Glass')
        p.cylinder((0, yc - 0.002, 0.245), (0, yc - 0.008, 0.245), 0.016, 'LensW', n=20)
        p.box((0, yc - 0.001, 0.15), (0.07, 0.004, 0.05), 'Glass')
        # head lamps and a sensor on the left panel
        p.cylinder((-0.215, yf + 0.001, 0.25), (-0.215, yf - 0.006, 0.25), 0.018, 'LensW', n=16)
        p.cylinder((-0.165, yf + 0.001, 0.25), (-0.165, yf - 0.006, 0.25), 0.012, 'Glass', n=16)
        p.box((-0.185, yf - 0.002, 0.185), (0.09, 0.006, 0.02), 'LensW')
        # e-stop on the right panel: yellow collar + red mushroom
        ex, ez = 0.185, 0.225
        p.cylinder((ex, yf + 0.001, ez), (ex, yf - 0.010, ez), 0.034, 'EStopBase', n=24)
        p.lathe([(0.0, 0.020), (-0.010, 0.026), (-0.020, 0.028), (-0.027, 0.022), (-0.030, 0.002)],
                24, 'EStop', center=(ex, yf - 0.010, ez), axis='Y')
        p.box((0.185, yf - 0.002, 0.168), (0.08, 0.006, 0.012), 'LensStatus')
        # rear: tail lamps
        yr = Y1 + 0.012
        for x in (-0.185, 0.185):
            p.box((x, yr + 0.002, 0.25), (0.10, 0.006, 0.028), 'LensR')
        # status band round the skirt joint, broken at the wheel pockets
        lo, hi = plan(-0.003, Z_SKIRT1 + 0.010), plan(-0.003, Z_SKIRT1 + 0.022)
        keep = [abs(v.x) < WID / 2 - 0.02 or abs(v.y) > POCKET + 0.01 for v in lo]
        n = len(lo)
        quads = [(j, (j + 1) % n) for j in range(n) if keep[j] and keep[(j + 1) % n]]
        p.add(lo + hi, [(a, b, n + b, n + a) for a, b in quads], 'LensStatus')
    return mk('Details', f, bevel=0.0)


def build_casters():
    def f(p):
        for cx in (-0.27, 0.27):
            for cy in (Y0 + 0.15, Y1 - 0.15):
                p.box((cx, cy, CASTER_R + 0.03), (0.05, 0.05, 0.03), 'DarkSteel')
                p.lathe([(-0.016, CASTER_R), (0.016, CASTER_R)], 16, 'Rubber',
                        center=(cx, cy + 0.015, CASTER_R), axis='X')
    return mk('Casters', f, bevel=0.002)


def build_body_parts():
    return [build_shell(), build_panels(), build_details(), build_casters()]


# =============================================================================
# Movers
# =============================================================================

def build_wheel(name, side):
    """Built round the origin (axle = X) and placed after, as the forklift's wheels."""
    def f(p):
        prof = [(-W_HW, W_R - 0.012), (-W_HW + 0.006, W_R), (W_HW - 0.006, W_R), (W_HW, W_R - 0.012),
                (W_HW, 0.045), (-W_HW, 0.045)]
        p.lathe(prof, 32, 'Tire', axis='X', mats=['Tire', 'Tire', 'Tire', 'Tire', 'Rubber'])
        p.lathe([(-W_HW + 0.004, 0.045), (W_HW - 0.004, 0.045)], 20, 'DarkSteel', axis='X')
    ob = mk(name, f, bevel=0.003)
    ob.location = (side * W_X, 0, W_R)
    bpy.context.view_layer.update()
    return ob


def build_lift():
    """Black turntable: rounded rim, a groove, a raised steel hub with a centre bore."""
    def f(p):
        prof = [(Z_LIFT0, R_TABLE - 0.01), (Z_LIFT0, R_TABLE), (Z_TOP - 0.006, R_TABLE),
                (Z_TOP, R_TABLE - 0.008), (Z_TOP, R_TABLE - 0.05), (Z_TOP - 0.003, R_TABLE - 0.054),
                (Z_TOP - 0.003, R_TABLE - 0.066), (Z_TOP, R_TABLE - 0.07), (Z_TOP, 0.11),
                (Z_TOP + 0.006, 0.105), (Z_TOP + 0.006, 0.075), (Z_TOP - 0.012, 0.07), (Z_TOP - 0.012, 0.002)]
        mats = ['Table'] * 8 + ['Hub', 'Hub', 'Hub', 'DarkSteel']
        p.lathe(prof, 64, 'Table', axis='Z', mats=mats)
        # eight segments: thin radial seams between the hub and the groove
        r0, r1 = 0.115, R_TABLE - 0.075
        for k in range(8):
            a = radians(22.5 + 45 * k)
            p.box(((r0 + r1) / 2 * cos(a), (r0 + r1) / 2 * sin(a), Z_TOP + 0.0006),
                  (r1 - r0, 0.007, 0.0012), 'Seam', rot=Euler((0, 0, a)))
    return mk('Lift', f, bevel=0.003)


def build_agv():
    build_materials()
    parts = build_body_parts()
    movers = [build_wheel('Wheel_L', 1), build_wheel('Wheel_R', -1), build_lift()]   # left = +X (as the forklift)
    return parts, movers


# =============================================================================
# LODs and rig
# =============================================================================

LOD_PLAN = {'Shell': (0.35, 0.12), 'Panels': (0.4, 0.15), 'Details': (0.5, None),
            'Casters': (0.3, None),
            'Wheel_L': (0.35, 0.12), 'Wheel_R': (0.35, 0.12), 'Lift': (0.35, 0.12)}
TEXEL_WEIGHTS = {'Shell': 1.3, 'Panels': 1.1, 'Details': 1.2, 'Lift': 1.1, 'Casters': 0.3,
                 'Wheel_L': 0.4, 'Wheel_R': 0.4}
WHEELS = ('Wheel_L', 'Wheel_R')


def build_lods(parts, movers):
    """-> {node: [LOD0, LOD1, LOD2]}.  LOD2 folds the wheels into the body."""
    def levels(ob):
        r1, r2 = LOD_PLAN[ob.name]
        l1 = K.make_lod(ob, r1, ob.name + '_LOD1', symmetry='X' if ob.name not in WHEELS else None)
        l2 = K.make_lod(ob, r2, ob.name + '_LOD2') if r2 else None
        return l1, l2

    body = [levels(p) for p in parts]
    m = {o.name: o for o in movers}
    lv = {n: levels(o) for n, o in m.items()}
    for n, o in m.items():
        o.name = o.data.name = n + '_LOD0'
    lods = {
        'Body': [K.join(parts, 'Body_LOD0'),
                 K.join([l1 for l1, _ in body], 'Body_LOD1'),
                 K.join([l2 for _, l2 in body if l2] + [lv[w][1] for w in WHEELS], 'Body_LOD2')],
        'Lift': [m['Lift'], *lv['Lift']],
    }
    for w in WHEELS:
        lods[w] = [m[w], lv[w][0]]
    return lods


NODE_PIVOTS = {'Body': (0, 0, 0), 'Wheel_L': (W_X, 0, W_R), 'Wheel_R': (-W_X, 0, W_R), 'Lift': (0, 0, Z_LIFT0)}


def rig(lods):
    nodes = {'AGV': K.empty('AGV', (0, 0, 0), size=0.4, display='ARROWS')}
    for name, pivot in NODE_PIVOTS.items():
        nodes[name] = K.empty(name, pivot, size=0.1)
    nodes['LoadSocket'] = K.empty('LoadSocket', (0, 0, Z_TOP), size=0.2, display='CUBE')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for child in NODE_PIVOTS:
        K.parent_keep_local(nodes[child], nodes['AGV'])
    K.parent_keep_local(nodes['LoadSocket'], nodes['Lift'])
    bpy.context.view_layer.update()
    return nodes['AGV']


def show_lod(objs, level):
    for o in objs:
        if o.type == 'MESH' and '_LOD' in o.name:
            o.hide_render = not o.name.endswith(f'_LOD{level}')


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
    FL.render(scene, FL.camera(scene, 'Cam_Hero', (2.2, -2.6, 1.3), (0, 0, 0.2), 50),
              os.path.join(PREVIEW_DIR, 'agv_hero.png'), res, spp)
    FL.render(scene, FL.camera(scene, 'Cam_Rear', (-2.0, 2.4, 1.1), (0, 0, 0.2), 50),
              os.path.join(PREVIEW_DIR, 'agv_rear.png'), res, spp)
    cam = FL.camera(scene, 'Cam_LOD', (2.2, -2.6, 1.3), (0, 0, 0.2), 50)
    for lvl in (1, 2):
        show_lod(objs, lvl)
        FL.render(scene, cam, os.path.join(PREVIEW_DIR, f'agv_lod{lvl}.png'), (800, 500), spp)
    show_lod(objs, 0)


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
    ap.add_argument('--cpu', action='store_true', help='bake / render on the CPU (OptiX unavailable)')
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

    parts, movers = build_agv()
    everything = parts + movers
    K.atlas_unwrap(everything, margin=0.003, weights=TEXEL_WEIGHTS)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.3      # AO bake reach
    except AttributeError:
        pass
    paths = K.bake_texture_set(everything, OUT_DIR, 'AGV', args.bake, args.save)
    runtime = K.make_runtime_material('AGV', paths)
    K.assign_single_material(everything, runtime)

    lods = build_lods(parts, movers)
    print('[agv] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')

    root = rig(lods)
    K.export_fbx(root, os.path.join(OUT_DIR, 'AGV.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'AGV.blend'))
    if not args.no_render:
        preview_renders(scene, root, args.quick)
    print(f'[agv] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
