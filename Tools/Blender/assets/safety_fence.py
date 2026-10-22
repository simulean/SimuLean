"""
SimuLean kit: machine safety fencing for robot cells (mesh panels on posts, an interlocked door).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/safety_fence.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per piece, one shared texture set / material SafetyFence.mat)
    Assets/SimuLean.Unity/Art/Models/SafetyFence/Fence_Panel.fbx   segment, 1.0 m along the run: a post at
                                                                   0 and a mesh panel up to the next post
    Assets/SimuLean.Unity/Art/Models/SafetyFence/Fence_End.fbx     cap: the post that closes a run
    Assets/SimuLean.Unity/Art/Models/SafetyFence/Fence_Door.fbx    1.0 m hinged door with an interlock switch
    Tools/Blender/build/SafetyFence.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z (the run: pieces tile
along it, period 1.0 m), authored +Z = Unity +Y.  Posts 60 x 60 mm, 2.2 m tall, safety yellow; panels
black, 2.0 m of wire mesh from 0.15 m above the floor (machine-guarding practice).  Generic, no marks.

Nodes: Fence_Door '- Door  the leaf, hinge axis at (0, 0, 0.04) (Unity): rotate about local Y to open.
Runs are laid out like the other kits (ModularKit.Strip: n = round(run / 1.0) panels, stretched to fit,
an end post after the last).
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
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'SafetyFence')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'SafetyFence'

PERIOD = 1.0
POST, POST_H = 0.06, 2.2
PANEL_Z0, PANEL_Z1 = 0.15, 2.15
GAP = 0.012                  # between a post and a panel frame

MATS = {}


def build_materials():
    K.NB.default_period = {'y': PERIOD}
    MATS.update({
        'Post': M.paint('Post', (0.80, 0.42, 0.015), rough=0.38, wear=0.4),
        'Frame': M.paint('Frame', (0.022, 0.022, 0.024), rough=0.45, wear=0.25),
        'Wire': M.paint('Wire', (0.03, 0.03, 0.032), rough=0.5, wear=0.1),
        'Foot': M.dark_steel('Foot'),
        'Switch': M.plastic('Switch', color=(0.80, 0.42, 0.015), rough=0.35),
        'Red': M.plastic('Red', color=(0.55, 0.02, 0.015), rough=0.3),
        'Dark': M.plastic('Dark', color=(0.02, 0.02, 0.022), rough=0.45),
    })


def mk(name, fn, bevel=0.002):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, 1, 40, 40)
    return ob


def post(p, y):
    p.box_minmax((-POST / 2, y - POST / 2, 0.01), (POST / 2, y + POST / 2, POST_H), 'Post')
    p.box_minmax((-0.06, y - 0.06, 0.0), (0.06, y + 0.06, 0.01), 'Foot')                      # foot plate
    p.box_minmax((-POST / 2 - 0.001, y - POST / 2 - 0.001, POST_H - 0.006),
                 (POST / 2 + 0.001, y + POST / 2 + 0.001, POST_H), 'Dark')                    # cap


FRAME_T = 0.02


def panel(p, y0, y1, frame='Frame'):
    """The tube frame of a panel between y0 and y1 (centred on x = 0)."""
    t = FRAME_T
    p.box_minmax((-t / 2, y0, PANEL_Z0), (t / 2, y0 + t, PANEL_Z1), frame)
    p.box_minmax((-t / 2, y1 - t, PANEL_Z0), (t / 2, y1, PANEL_Z1), frame)
    p.box_minmax((-t / 2, y0, PANEL_Z0), (t / 2, y1, PANEL_Z0 + t), frame)
    p.box_minmax((-t / 2, y0, PANEL_Z1 - t), (t / 2, y1, PANEL_Z1), frame)


def mesh(p, y0, y1):
    """The wire mesh inside a panel frame (dropped at the far LOD: it is sub-pixel there)."""
    t, w = FRAME_T, 0.0025
    n_v = int((y1 - y0) / 0.05)
    for k in range(1, n_v):
        y = y0 + (y1 - y0) * k / n_v
        p.box_minmax((-w, y - w, PANEL_Z0 + t), (w, y + w, PANEL_Z1 - t), 'Wire')
    z = PANEL_Z0 + 0.10
    while z < PANEL_Z1 - 0.05:
        p.box_minmax((-w, y0 + t, z - w), (w, y1 - t, z + w), 'Wire')
        z += 0.10


def clamps(p, y_post, side):
    for z in (0.40, 1.90):
        p.box((0.0, y_post + side * (POST / 2 + GAP / 2), z), (0.03, GAP + 0.01, 0.05), 'Dark')


def build_panel():
    y0, y1 = POST / 2 + GAP, PERIOD - POST / 2 - GAP

    def f(p):
        post(p, 0.0)
        panel(p, y0, y1)
        clamps(p, 0.0, 1)
        clamps(p, PERIOD, -1)
    body = mk('FP_Body', f)
    wire = mk('FP_Mesh', lambda p: mesh(p, y0, y1), bevel=0.0)
    return KM.Piece('Fence_Panel', 'segment', [body, wire], levels=2, lod={'FP_Body': (1.0,), 'FP_Mesh': (None,)},
                    ghosts=[('Fence_Panel', (0, PERIOD, 0)), ('Fence_Panel', (0, -PERIOD, 0))])


def build_end():
    body = mk('FE_Post', lambda p: post(p, 0.0))
    return KM.Piece('Fence_End', 'cap', [body], levels=2, lod={'FE_Post': (1.0,)},
                    ghosts=[('Fence_Panel', (0, -PERIOD, 0))])


def build_door():
    hinge_y = POST / 2 + GAP

    def frame(p):
        post(p, 0.0)
        for z in (0.35, 1.95):                                                                # hinges
            p.cylinder((0.0, hinge_y - 0.004, z - 0.04), (0.0, hinge_y - 0.004, z + 0.04), 0.012, 'Dark', n=10)
        # interlock switch on the next post's side (at the run's next post, y = PERIOD)
        p.box((0.045, PERIOD - POST / 2 - 0.02, 1.10), (0.05, 0.04, 0.12), 'Switch')
        p.box((0.071, PERIOD - POST / 2 - 0.02, 1.13), (0.004, 0.02, 0.02), 'Red')

    def leaf(p):
        panel(p, hinge_y, PERIOD - POST / 2 - GAP, frame='Post')
        mesh(p, hinge_y, PERIOD - POST / 2 - GAP)
        y_latch = PERIOD - POST / 2 - GAP - 0.05
        p.box((0.04, y_latch, 1.05), (0.03, 0.02, 0.18), 'Dark')                              # handle
        p.box((0.035, y_latch + 0.03, 1.10), (0.02, 0.03, 0.06), 'Switch')                     # actuator
    fr = mk('FD_Frame', frame)
    lf = mk('FD_Leaf', leaf)
    return KM.Piece('Fence_Door', 'fixed', {None: [fr], 'Door': [lf]}, levels=2,
                    lod={'FD_Frame': (1.0,), 'FD_Leaf': (1.0,)},
                    pivots={'Door': (0.0, hinge_y, 0.0)},
                    ghosts=[('Fence_Panel', (0, PERIOD, 0)), ('Fence_Panel', (0, -PERIOD, 0))])


def build_kit():
    build_materials()
    return [build_panel(), build_end(), build_door()]


def build_preview():
    """Preview (no bake): a short run -- three panels, the door, a panel, the end post."""
    pieces = build_kit()
    by = {pc.name: pc for pc in pieces}
    order = ['Fence_Panel', 'Fence_Panel', 'Fence_Panel', 'Fence_Door', 'Fence_Panel', 'Fence_End']
    used, out = set(), []
    for i, name in enumerate(order):
        src = by[name].parts
        if name in used:                                       # a linked copy for each repeat
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
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[fence] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
