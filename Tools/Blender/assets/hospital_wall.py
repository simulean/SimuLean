"""
SimuLean kit: hospital partition walls (2026-10-09, healthcare §6 "hospital wing kit") -- cut-away walls to lay out
rooms, corridors and waiting areas around the library's stations: a plain panel, a glazed panel, a door and the end
post. Cut away at 1.6 m so the rooms read from above (a demo for clinicians), with the hospital palette.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/hospital_wall.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per piece, one shared texture set / material HospitalWall.mat)
    Assets/SimuLean.Unity/Art/Models/HospitalWall/HWall_Panel.fbx   segment, 1.0 m along the run: a white panel,
                                                                  teal skirting and a cap rail
    Assets/SimuLean.Unity/Art/Models/HospitalWall/HWall_Glass.fbx   segment: a solid lower part, glazing above
    Assets/SimuLean.Unity/Art/Models/HospitalWall/HWall_Door.fbx    segment: a 0.9 m door leaf in its frame
    Assets/SimuLean.Unity/Art/Models/HospitalWall/HWall_End.fbx     cap: the post that closes a run
    Tools/Blender/build/HospitalWall.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z (the run: pieces tile along it,
period 1.0 m), authored +Z = Unity +Y. Walls 0.10 m thick, centred on x = 0.

Nodes: HWall_Door '- Door  the leaf, hinge axis at (0, 0, 0.05) (Unity): rotate about local Y to open.
Runs are laid out like the other kits (UnityModularStrip: n = round(run / 1.0) segments, stretched to fit, an end
post after the last; the door as the strip's centre piece).
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
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'HospitalWall')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'HospitalWall'

PERIOD = 1.0
T = 0.10                     # wall thickness
H = 1.60                     # cut-away height
SKIRT = 0.15                 # teal skirting height
POST = 0.10                  # end post / door frame section
GLASS_Z0 = 0.90              # glazing from this height

MATS = {}


def build_materials():
    K.NB.default_period = {'y': PERIOD}
    MATS.update({
        'White': M.paint('White', (0.80, 0.81, 0.80), rough=0.40, wear=0.15, dirt=0.4),
        'Teal': M.paint('Teal', (0.04, 0.30, 0.28), rough=0.35, wear=0.2, dirt=0.4),
        'Cap': M.paint('Cap', (0.62, 0.64, 0.65), rough=0.35, wear=0.2),
        'Alu': M.dark_steel('Alu', color=(0.62, 0.63, 0.64), rough=0.30, metal=0.9),
        'Glass': M.glass('Glass', tint=(0.030, 0.050, 0.055)),
        'Door': M.paint('Door', (0.66, 0.55, 0.40), rough=0.45, wear=0.2),
        'Dark': M.plastic('Dark', color=(0.02, 0.02, 0.022), rough=0.45),
    })


def mk(name, fn, bevel=0.002):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, 1, 40, 40)
    return ob


def wall(p, y0, y1, z0=0.0, z1=H):
    """A plain wall between y0 and y1 (skirting and cap rail where the wall reaches the floor / the top)."""
    p.box_minmax((-T / 2, y0, z0), (T / 2, y1, z1), 'White')
    if z0 <= 0.0:
        p.box_minmax((-T / 2 - 0.006, y0, 0.0), (T / 2 + 0.006, y1, SKIRT), 'Teal')
    if z1 >= H:
        p.box_minmax((-T / 2 - 0.01, y0, H), (T / 2 + 0.01, y1, H + 0.03), 'Cap')


def build_panel():
    body = mk('HP_Body', lambda p: wall(p, 0.0, PERIOD))
    return KM.Piece('HWall_Panel', 'segment', [body], levels=2, lod={'HP_Body': (1.0,)},
                    ghosts=[('HWall_Panel', (0, PERIOD, 0)), ('HWall_Panel', (0, -PERIOD, 0))])


def build_glass():
    def f(p):
        wall(p, 0.0, PERIOD, 0.0, GLASS_Z0)
        p.box_minmax((-T / 2 - 0.01, 0.0, GLASS_Z0), (T / 2 + 0.01, PERIOD, GLASS_Z0 + 0.04), 'Cap')   # sill
        p.box_minmax((-0.02, 0.0, GLASS_Z0 + 0.04), (0.02, 0.04, H), 'Alu')                           # mullion
        p.box_minmax((-0.008, 0.04, GLASS_Z0 + 0.04), (0.008, PERIOD, H), 'Glass')
        p.box_minmax((-T / 2 - 0.01, 0.0, H), (T / 2 + 0.01, PERIOD, H + 0.03), 'Alu')                # head
    body = mk('HG_Body', f)
    return KM.Piece('HWall_Glass', 'segment', [body], levels=2, lod={'HG_Body': (1.0,)},
                    ghosts=[('HWall_Panel', (0, PERIOD, 0)), ('HWall_Panel', (0, -PERIOD, 0))])


def build_end():
    def f(p):
        p.box_minmax((-POST / 2, -POST / 2, 0.0), (POST / 2, POST / 2, H + 0.03), 'Alu')
    body = mk('HE_Post', f)
    return KM.Piece('HWall_End', 'cap', [body], levels=2, lod={'HE_Post': (1.0,)},
                    ghosts=[('HWall_Panel', (0, -PERIOD, 0))])


def build_door():
    leaf_y0, leaf_y1 = 0.05, 0.95

    def frame(p):
        # jambs, a head over the door, the door's own cap rail; the opening between the jambs
        p.box_minmax((-T / 2, 0.0, 0.0), (T / 2, leaf_y0, H), 'Alu')
        p.box_minmax((-T / 2, leaf_y1, 0.0), (T / 2, PERIOD, H), 'Alu')
        p.box_minmax((-T / 2 - 0.01, 0.0, H), (T / 2 + 0.01, PERIOD, H + 0.03), 'Cap')

    def leaf(p):
        p.box_minmax((-0.02, leaf_y0, 0.01), (0.02, leaf_y1, H - 0.02), 'Door')
        p.box_minmax((-0.022, leaf_y0, 0.01), (0.022, leaf_y1, 0.15), 'Teal')                     # kick plate
        p.box_minmax((-0.035, leaf_y0 + 0.08, 0.98), (-0.02, leaf_y0 + 0.22, 1.02), 'Dark')       # handles
        p.box_minmax((0.02, leaf_y0 + 0.08, 0.98), (0.035, leaf_y0 + 0.22, 1.02), 'Dark')
        p.box_minmax((-0.0225, leaf_y0 + 0.25, 1.05), (0.0225, leaf_y1 - 0.25, 1.45), 'Glass')    # vision panel
    fr = mk('HD_Frame', frame)
    lf = mk('HD_Leaf', leaf)
    return KM.Piece('HWall_Door', 'fixed', {None: [fr], 'Door': [lf]}, levels=2,
                    lod={'HD_Frame': (1.0,), 'HD_Leaf': (1.0,)},
                    pivots={'Door': (0.0, leaf_y1, 0.0)},
                    ghosts=[('HWall_Panel', (0, PERIOD, 0)), ('HWall_Panel', (0, -PERIOD, 0))])


def build_kit():
    build_materials()
    return [build_panel(), build_glass(), build_door(), build_end()]


def build_preview():
    """Preview (no bake): a run -- two panels, the door, a glass panel, a panel, the end post."""
    pieces = build_kit()
    by = {pc.name: pc for pc in pieces}
    order = ['HWall_Panel', 'HWall_Panel', 'HWall_Door', 'HWall_Glass', 'HWall_Panel', 'HWall_End']
    used, out = set(), []
    for i, name in enumerate(order):
        src = by[name].parts
        if name in used:
            src = [o.copy() for o in src]
            for o in src:
                bpy.context.scene.collection.objects.link(o)
        used.add(name)
        for o in src:
            o.location.y = i * PERIOD
            out.append(o)
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
    print(f'[hospital wall] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
