"""
SimuLean asset: self-propelled modular transporter (SPMT) for ship blocks — a member of a block-transport team
(UnityResourceTeam, Network travel) that drives under a block resting on stools, lifts it on its deck and carries it.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/spmt.py -- [--bake 4096] [--save 2048]
            [--no-render] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/SPMT/SPMT.fbx    (+ 5 PNG maps)
    Tools/Blender/build/SPMT.blend                    (source, regenerable)
    Tools/Blender/previews/spmt_*.png                 (renders)

Real-world reference: shipyard block transporters / coupled SPMT modules — a long flat deck on many axle lines of
pendulum bogies with twin tyres, hydraulic suspension giving the deck a lift stroke of ~0.3-0.7 m, a power pack at
one end, remote controlled (no cab).  Generic design in the library palette (yellow deck and power pack, dark steel
running gear, hazard stripes on the ends, rubber pads on the deck), no third-party marks.
Footprint 5.0 (X) x 14.6 (Y, travel) m with the power pack, deck top 1.55 m lowered, 0.35 m lift stroke,
7 axle lines x 4 bogies x 2 tyres (r = 0.36 m).

Frame as the AGV: authored front = -Y (Unity +Z, the travel direction), +Z up (Unity +Y), origin on the floor at
the centre of the deck.

Animation hierarchy (Unity names).  Every name is a bare Transform; its meshes hang under it as
<name>_LOD0/_LOD1/_LOD2 with identity local transforms, so animate the named node.
    SPMT                  root, on the floor at the deck centre
    |- Body               running gear: suspension barrels, axle housings, the spine under the deck
    |- Wheel_<l>_<b>      one bogie's twin tyres (l = axle line 1..7 front to rear, b = bogie 1..4 left to
    |                     right): spin about local X; UnityVehicleAnimator finds every Wheel* node and reads
    |                     its radius from the mesh (architecture §4.2)
    '- Lift               deck, suspension rods, power pack: pivot on the deck underside,
        |                 localPosition.y = 0 .. 0.35 (UnityVehicleAnimator.liftStroke)
        '- LoadSocket     parent the carried block here (deck top, z 1.55 lowered)
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
from simulean_kit import Part, finish       # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'SPMT')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')

# ---------------------------------------------------------------- dimensions (metres)
WID, LEN = 5.0, 14.0             # deck (X across, Y along the travel)
Z_DECK0, Z_DECK1 = 1.15, 1.55    # deck underside / top (lowered)
LIFT_STROKE = 0.35
LINES = 7                        # axle lines
PITCH = 1.9                      # axle-line pitch
BOGIE_X = (-2.05, -0.95, 0.95, 2.05)
T_R, T_HW, T_OFF = 0.36, 0.13, 0.17   # tyre radius, half width, offset of each twin tyre from the bogie centre
BARREL_R, BARREL_Z = 0.11, (0.62, 1.02)   # suspension barrel (Body)
ROD_R, ROD_Z = 0.065, (0.70, Z_DECK0)     # suspension rod (Lift), long enough to stay in the barrel when lifted
PPU_LEN = 0.6                    # power pack beyond the deck's front end
PAINT = (0.80, 0.52, 0.02)       # library yellow

MATS = {}


def line_y(i):
    return -PITCH * (LINES - 1) / 2 + PITCH * i


def build_materials():
    MATS.update({
        'Paint': M.paint('Paint', PAINT, rough=0.34, wear=0.7),
        'Hazard': M.paint('Hazard', PAINT, rough=0.34, wear=0.8, color_fn=M.hazard_fn(PAINT, width=0.12, axis=('x', 'z'))),
        'Deck': M.tread_plate('Deck'),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Frame': M.dark_steel('Frame', color=(0.07, 0.07, 0.075), rough=0.5),
        'Chrome': M.chrome('Chrome'),
        'Tire': M.tire('Tire', T_R, lugs=36, chevron=4.0),
        'Rubber': M.rubber('Rubber'),
        'Pad': M.rubber('Pad', color=(0.03, 0.03, 0.03), rough=0.85),
        'Grille': M.plastic('Grille', color=(0.02, 0.02, 0.022), rough=0.6),
        'LensA': M.lens('LensA', (1.0, 0.45, 0.02)),
        'LensW': M.lens('LensW', (1.0, 0.93, 0.80)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
    })
    return MATS


def mk(name, fn, bevel=0.01, seg=1, angle=40.0, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, angle, sharp)
    return ob


# =============================================================================
# Body: running gear that stays on the floor
# =============================================================================

def build_running_gear():
    def f(p):
        for i in range(LINES):
            y = line_y(i)
            for x in BOGIE_X:
                # axle housing across the twin tyres, a fork up to the barrel, the barrel
                p.box((x, y, T_R), (2 * (T_OFF - T_HW) + 0.04, 0.22, 0.20), 'Frame')
                p.box((x, y, (T_R + BARREL_Z[0]) / 2 + 0.05), (0.18, 0.30, BARREL_Z[0] - T_R + 0.1), 'Frame')
                p.cylinder((x, y, BARREL_Z[0]), (x, y, BARREL_Z[1]), BARREL_R, 'DarkSteel', n=16)
                p.cylinder((x, y, BARREL_Z[1] - 0.04), (x, y, BARREL_Z[1]), BARREL_R + 0.02, 'Frame', n=16)
                # mudguard over the tyres
                p.box((x, y, T_R * 2 + 0.06), (2 * (T_OFF + T_HW) + 0.06, 0.95, 0.02), 'Frame')
                p.box((x, y, T_R * 2 + 0.06), (2 * (T_OFF + T_HW) + 0.07, 0.2, 0.16), 'Frame')     # its bracket on the fork
                p.box((x, y - 0.47, T_R * 2 - 0.02), (2 * (T_OFF + T_HW) + 0.06, 0.02, 0.16), 'Frame')
                p.box((x, y + 0.47, T_R * 2 - 0.02), (2 * (T_OFF + T_HW) + 0.06, 0.02, 0.16), 'Frame')
        # the spine and cross members joining the bogie columns (under the deck, below the lifted rods' reach)
        p.box((0, 0, 0.92), (0.5, LEN - 1.2, 0.16), 'Frame')
        for i in range(LINES):
            p.box((0, line_y(i), 0.92), (2 * BOGIE_X[-1], 0.16, 0.12), 'Frame')
    return mk('RunningGear', f, bevel=0.012)


# =============================================================================
# Wheels: one node per bogie (twin tyres)
# =============================================================================

def build_bogie_wheels(name, x, y):
    """Built round the origin (axle = X) and placed after, so the tyre material is in wheel space."""
    def f(p):
        for s in (-1, 1):
            c = s * T_OFF
            prof = [(c - T_HW, T_R - 0.03), (c - T_HW + 0.02, T_R), (c + T_HW - 0.02, T_R), (c + T_HW, T_R - 0.03),
                    (c + T_HW, 0.20), (c - T_HW, 0.20)]
            p.lathe(prof, 28, 'Tire', axis='X', mats=['Tire', 'Tire', 'Tire', 'Tire', 'Rubber'])
            p.lathe([(c - T_HW + 0.01, 0.20), (c + T_HW - 0.01, 0.20), (c + T_HW - 0.01, 0.08),
                     (c - T_HW + 0.01, 0.08)], 16, 'DarkSteel', axis='X')
    ob = mk(name, f, bevel=0.006)
    ob.location = (x, y, T_R)
    bpy.context.view_layer.update()
    return ob


def build_wheels():
    out = []
    for i in range(LINES):
        for b, x in enumerate(BOGIE_X):
            out.append(build_bogie_wheels(f'Wheel_{i + 1}_{b + 1}', x, line_y(i)))
    return out


# =============================================================================
# Lift: deck, rods and power pack (rise together)
# =============================================================================

def build_deck():
    def f(p):
        y0, y1 = -LEN / 2, LEN / 2
        # main plate and the side beams (yellow), the chequer-plate top
        p.box((0, 0, (Z_DECK0 + Z_DECK1) / 2 - 0.01), (WID, LEN, Z_DECK1 - Z_DECK0 - 0.02), 'Paint')
        p.box((0, 0, Z_DECK1 - 0.005), (WID - 0.12, LEN - 0.12, 0.01), 'Deck')
        # hazard bands on both ends
        for y, s in ((y0, -1), (y1, 1)):
            p.box((0, y + s * 0.012, (Z_DECK0 + Z_DECK1) / 2), (WID - 0.1, 0.03, Z_DECK1 - Z_DECK0 - 0.06), 'Hazard')
        # rubber pads in 4 rows (where the block rests)
        for x in (-1.6, -0.55, 0.55, 1.6):
            for k in range(6):
                p.box((x, -5.5 + k * 2.2, Z_DECK1 + 0.02), (0.5, 0.9, 0.04), 'Pad')
        # tie-down lugs along the sides
        for k in range(8):
            for sx in (-1, 1):
                p.box((sx * (WID / 2 + 0.03), y0 + 0.9 + k * (LEN - 1.8) / 7, Z_DECK1 - 0.12), (0.06, 0.16, 0.12), 'DarkSteel')
        # suspension rods (chrome) and their top mounts
        for i in range(LINES):
            for x in BOGIE_X:
                y = line_y(i)
                p.cylinder((x, y, ROD_Z[0]), (x, y, ROD_Z[1]), ROD_R, 'Chrome', n=12)
                p.box((x, y, Z_DECK0 - 0.03), (0.32, 0.32, 0.06), 'Frame')
        # warning beacons and lamps on the corners
        for sx in (-1, 1):
            for y in (y0, y1):
                p.cylinder((sx * (WID / 2 - 0.2), y + (0.2 if y < 0 else -0.2), Z_DECK1),
                           (sx * (WID / 2 - 0.2), y + (0.2 if y < 0 else -0.2), Z_DECK1 + 0.12), 0.06, 'LensA', n=12)
            p.box((sx * 1.9, y0 - 0.005, Z_DECK0 + 0.12), (0.30, 0.01, 0.10), 'LensW')
            p.box((sx * 1.9, y1 + 0.005, Z_DECK0 + 0.12), (0.30, 0.01, 0.10), 'LensR')
    return mk('Deck', f, bevel=0.012)


def build_power_pack():
    """Engine / hydraulic power pack hung on the front end, below the deck top."""
    def f(p):
        y0 = -LEN / 2
        yc = y0 - PPU_LEN / 2
        p.box((0, yc, 1.1), (2.6, PPU_LEN, 0.85), 'Paint')
        p.box((0, yc, 1.55), (2.4, PPU_LEN - 0.08, 0.04), 'Frame')
        # grilles both sides and in front, the exhaust, a control box with its antenna
        for sx in (-1, 1):
            p.box((sx * 1.302, yc, 1.1), (0.01, PPU_LEN - 0.15, 0.55), 'Grille')
            for k in range(5):                                  # louvres over the grilles
                p.box((sx * 1.31, yc, 0.9 + k * 0.1), (0.02, PPU_LEN - 0.18, 0.025), 'Paint')
            p.box((sx * 1.15, y0 - PPU_LEN - 0.02, 1.62), (0.03, 0.03, 0.3), 'DarkSteel')   # handholds
        for k in range(7):
            p.box((0, y0 - PPU_LEN - 0.012, 0.9 + k * 0.07), (1.7, 0.012, 0.02), 'Paint')
        p.box((0, y0 - PPU_LEN - 0.005, 1.1), (1.8, 0.01, 0.5), 'Grille')
        p.cylinder((0.95, yc, 1.55), (0.95, yc, 1.85), 0.06, 'DarkSteel', n=12)
        p.box((-0.9, yc, 1.62), (0.5, 0.3, 0.14), 'Frame')
        p.cylinder((-0.75, yc, 1.69), (-0.75, yc, 2.05), 0.012, 'DarkSteel', n=6)
        p.cylinder((0.0, y0 - PPU_LEN - 0.01, 1.42), (0.0, y0 - PPU_LEN - 0.03, 1.42), 0.05, 'LensA', n=12)
    return mk('PowerPack', f, bevel=0.015)


# =============================================================================
# LODs and rig
# =============================================================================

LOD_PLAN = {'RunningGear': (0.4, 0.12), 'Deck': (0.4, 0.15), 'PowerPack': (0.4, 0.15)}
WHEEL_LOD = (0.3, 0.1)
TEXEL_WEIGHTS = {'RunningGear': 0.8, 'Deck': 1.6, 'PowerPack': 0.8}


def build_lods(body, lift, wheels):
    """-> {node: [LOD0, LOD1, LOD2]}.  LOD2 folds the wheels into the body."""
    def levels(ob, plan):
        r1, r2 = plan
        return K.make_lod(ob, r1, ob.name + '_LOD1'), K.make_lod(ob, r2, ob.name + '_LOD2')

    b = [levels(o, LOD_PLAN[o.name]) for o in body]
    l = [levels(o, LOD_PLAN[o.name]) for o in lift]
    w = {o.name: levels(o, WHEEL_LOD) for o in wheels}
    names = [o.name for o in wheels]
    for o in wheels:
        o.name = o.data.name = o.name + '_LOD0'
    lods = {
        'Body': [K.join(body, 'Body_LOD0'), K.join([x for x, _ in b], 'Body_LOD1'),
                 K.join([y for _, y in b] + [w[n][1] for n in names], 'Body_LOD2')],
        'Lift': [K.join(lift, 'Lift_LOD0'), K.join([x for x, _ in l], 'Lift_LOD1'), K.join([y for _, y in l], 'Lift_LOD2')],
    }
    for o, n in zip(wheels, names):
        lods[n] = [o, w[n][0]]
    return lods


def rig(lods):
    pivots = {'Body': (0, 0, 0), 'Lift': (0, 0, Z_DECK0)}
    for i in range(LINES):
        for b, x in enumerate(BOGIE_X):
            pivots[f'Wheel_{i + 1}_{b + 1}'] = (x, line_y(i), T_R)
    nodes = {'SPMT': K.empty('SPMT', (0, 0, 0), size=1.0, display='ARROWS')}
    for name, pivot in pivots.items():
        nodes[name] = K.empty(name, pivot, size=0.3)
    nodes['LoadSocket'] = K.empty('LoadSocket', (0, 0, Z_DECK1), size=0.5, display='CUBE')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for child in pivots:
        K.parent_keep_local(nodes[child], nodes['SPMT'])
    K.parent_keep_local(nodes['LoadSocket'], nodes['Lift'])
    bpy.context.view_layer.update()
    return nodes['SPMT']


def show_lod(objs, level):
    for o in objs:
        if o.type == 'MESH' and '_LOD' in o.name:
            o.hide_render = not o.name.endswith(f'_LOD{level}')


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
    FL.render(scene, FL.camera(scene, 'Cam_Hero', (11, -14, 6.5), (0, 0, 0.8), 50),
              os.path.join(PREVIEW_DIR, 'spmt_hero.png'), res, spp)
    FL.render(scene, FL.camera(scene, 'Cam_Side', (12, 2, 1.4), (0, 0, 0.8), 50),
              os.path.join(PREVIEW_DIR, 'spmt_side.png'), res, spp)
    cam = FL.camera(scene, 'Cam_LOD', (11, -14, 6.5), (0, 0, 0.8), 50)
    for lvl in (1, 2):
        show_lod(objs, lvl)
        FL.render(scene, cam, os.path.join(PREVIEW_DIR, f'spmt_lod{lvl}.png'), (800, 500), spp)
    show_lod(objs, 0)


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

    build_materials()
    body = [build_running_gear()]
    lift = [build_deck(), build_power_pack()]
    wheels = build_wheels()
    everything = body + lift + wheels
    weights = dict(TEXEL_WEIGHTS)
    weights.update({o.name: 0.2 for o in wheels})
    K.atlas_unwrap(everything, margin=0.002, weights=weights)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.6      # AO bake reach
    except AttributeError:
        pass
    paths = K.bake_texture_set(everything, OUT_DIR, 'SPMT', args.bake, args.save)
    runtime = K.make_runtime_material('SPMT', paths)
    K.assign_single_material(everything, runtime)

    lods = build_lods(body, lift, wheels)
    print('[spmt] triangles per LOD (renderers):')
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        print(f'    LOD{lvl}  {sum(K.tri_count(o) for o in obs):>7}  ({len(obs)})')

    root = rig(lods)
    K.export_fbx(root, os.path.join(OUT_DIR, 'SPMT.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'SPMT.blend'))
    if not args.no_render:
        preview_renders(scene, root, args.quick)
    print(f'[spmt] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
