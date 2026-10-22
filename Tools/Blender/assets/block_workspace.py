"""
SimuLean kit: shipyard block workspace ("puesto de bloque") - a bay of the
block-assembly workshop where a hull block (assets/ship_block.py) rests on
steel stools while it is welded and outfitted.  Lifting in and out of the bay
uses the existing OverheadCrane kit.

Two FBX sharing one atlas in Art/Models/BlockWorkspace/:

    BlockWorkspace  floor pad (16 x 20 m by default) with the yellow bay border,
                    hazard corners, a white dashed outline of the block
                    footprint and a yellow square around every stool, plus
                    8 adjustable steel stools (base plate, outer tube, yellow
                    adjusting collar and pin, inner tube, head plate, red keel
                    block).  Node BlockSocket: the stools' top at the bay
                    centre - put a ShipBlock root there and it rests on them.
    BlockScaffold   one free-standing scaffold bay (2.5 x 0.75 m, deck at 2.0 m,
                    guard rails, toe boards, base jacks) to line the sides of a
                    block; place / repeat it along its local X.

Built live through the blender-ship MCP:

    import runpy; ns = runpy.run_path('<repo>/Tools/Blender/assets/block_workspace.py')
    ns['build_scene']()          # workspace + scaffolds + a ShipBlock on the stools
    ns['export']()               # bake, LODs, FBX

Frame (Blender, before export): length along X, breadth along Y, up +Z, pivot
at the floor centre (z = 0 is the shop floor; the pad is 20 mm thick).
"""

import os
import sys
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
if KIT not in sys.path:
    sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
from simulean_kit import V, Part             # noqa: E402

PREFIX = 'BlockWorkspace'
REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'BlockWorkspace')
BUILD_DIR = os.path.join(KIT, 'build')

# ---------------------------------------------------------------- colours (linear RGB)
YELLOW = (0.80, 0.42, 0.015)
WHITE = (0.70, 0.70, 0.67)
STOOL_GREY = (0.085, 0.095, 0.105)
KEEL_RED = (0.20, 0.032, 0.020)

PAD_T = 0.02             # floor pad thickness
MARK_Z = PAD_T + 0.003   # painted lines sit 3 mm above the pad
LINE_W = 0.20


# =============================================================================
# Workspace (floor pad + stools)
# =============================================================================

def stool_layout(block_length=12.0, block_breadth=16.0, nx=4, ny=2):
    """Stools under the block's frame stations: nx along X, ny rows along Y."""
    xs = [-block_length * 0.40 + i * (block_length * 0.80) / (nx - 1) for i in range(nx)]
    ys = [-block_breadth * 0.28 + j * (block_breadth * 0.56) / (ny - 1) for j in range(ny)]
    return [(x, y) for x in xs for y in ys]


def stool(p, x, y, top):
    """Adjustable block stool standing on the pad, keel block top at `top`."""
    z0 = PAD_T
    head = top - 0.20                         # head plate top (keel block 0.20 high)
    tube_top = z0 + (head - z0) * 0.58
    p.box_minmax((x - 0.35, y - 0.35, z0), (x + 0.35, y + 0.35, z0 + 0.03), 'stool')
    for sx, sy in ((1, 0), (-1, 0), (0, 1), (0, -1)):                  # gussets
        prof = [(0.125, z0 + 0.03), (0.32, z0 + 0.03), (0.125, z0 + 0.30)]
        if sx:
            p.extrude([(x + sx * a, b) for a, b in prof], 'Y', y - 0.008, y + 0.008, 'stool')
        else:
            p.extrude([(y + sy * a, b) for a, b in prof], 'X', x - 0.008, x + 0.008, 'stool')
    p.box_minmax((x - 0.125, y - 0.125, z0 + 0.03), (x + 0.125, y + 0.125, tube_top), 'stool')
    p.box_minmax((x - 0.16, y - 0.16, tube_top), (x + 0.16, y + 0.16, tube_top + 0.06), 'collar')
    p.cylinder((x - 0.30, y, tube_top + 0.12), (x + 0.30, y, tube_top + 0.12), 0.022, 'collar', n=8)
    p.box_minmax((x - 0.095, y - 0.095, tube_top + 0.06), (x + 0.095, y + 0.095, head - 0.03), 'stool')
    p.box_minmax((x - 0.26, y - 0.26, head - 0.03), (x + 0.26, y + 0.26, head), 'stool')
    p.box_minmax((x - 0.24, y - 0.16, head), (x + 0.24, y + 0.16, top), 'keel')


def line(p, a, b, mat, w=LINE_W):
    """Painted floor line from a to b (x, y)."""
    (x0, y0), (x1, y1) = a, b
    dx, dy = x1 - x0, y1 - y0
    n = (dx * dx + dy * dy) ** 0.5
    ox, oy = -dy / n * w / 2, dx / n * w / 2
    z = MARK_Z
    p.add([V((x0 + ox, y0 + oy, z)), V((x0 - ox, y0 - oy, z)),
           V((x1 - ox, y1 - oy, z)), V((x1 + ox, y1 + oy, z))], [(0, 1, 2, 3)], mat)


def rect_lines(p, x0, y0, x1, y1, mat, w=LINE_W, dash=None):
    edges = [((x0, y0), (x1, y0)), ((x1, y0), (x1, y1)), ((x1, y1), (x0, y1)), ((x0, y1), (x0, y0))]
    for a, b in edges:
        if not dash:
            # extend by w/2 so the corners close
            ax, ay = a
            bx, by = b
            ux, uy = (bx - ax), (by - ay)
            L = (ux * ux + uy * uy) ** 0.5
            ux, uy = ux / L * w / 2, uy / L * w / 2
            line(p, (ax - ux, ay - uy), (bx + ux, by + uy), mat, w)
            continue
        on, off = dash
        ax, ay = a
        bx, by = b
        L = ((bx - ax) ** 2 + (by - ay) ** 2) ** 0.5
        k = max(1, round((L + off) / (on + off)))
        step = L / k
        for i in range(k):
            s0 = (i * step + off / 2) / L
            s1 = ((i + 1) * step - off / 2) / L
            line(p, (ax + (bx - ax) * s0, ay + (by - ay) * s0),
                 (ax + (bx - ax) * s1, ay + (by - ay) * s1), mat, w)


def build_workspace(length=16.0, breadth=20.0, stool_height=1.20, block_length=12.0,
                    block_breadth=16.0, coll=None, mats=None):
    hx, hy = length / 2, breadth / 2
    pad = Part('WS_Pad')
    pad.box_minmax((-hx, -hy, 0.0), (hx, hy, PAD_T), 'floor')
    marks = Part('WS_Marks')
    inset = 0.30
    rect_lines(marks, -hx + inset, -hy + inset, hx - inset, hy - inset, 'yellow')
    # hazard corners: L-shaped striped patches inside the border
    c = 1.2
    for sx in (1, -1):
        for sy in (1, -1):
            x0 = sx * (hx - inset - LINE_W / 2)
            y0 = sy * (hy - inset - LINE_W / 2)
            line(marks, (x0, y0 - sy * 0.25), (x0 - sx * c, y0 - sy * 0.25), 'hazard', 0.5)
            line(marks, (x0 - sx * 0.25, y0 - sy * 0.5), (x0 - sx * 0.25, y0 - sy * c), 'hazard', 0.5)
    rect_lines(marks, -block_length / 2, -block_breadth / 2, block_length / 2, block_breadth / 2,
               'white', 0.10, dash=(0.6, 0.4))
    stools = Part('WS_Stools')
    for x, y in stool_layout(block_length, block_breadth):
        stool(stools, x, y, stool_height)
        rect_lines(marks, x - 0.55, y - 0.55, x + 0.55, y + 0.55, 'yellow', 0.08)
    obs = []
    for part, bevel in ((pad, 0.01), (marks, 0.0), (stools, 0.004)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=35.0)
        obs.append(ob)
    return obs, V((0, 0, stool_height))


# =============================================================================
# Scaffold bay
# =============================================================================

def build_scaffold(length=2.5, width=0.75, deck=2.0, coll=None, mats=None):
    """One scaffold bay standing on the floor, along X from 0 to `length`,
    across Y from 0 to `width`; the guard rails are on the y = 0 side, the open
    y = width side faces the block."""
    r = 0.024
    top = deck + 1.0
    tubes = Part('SC_Tubes')
    for x in (0.0, length):
        for y in (0.0, width):
            tubes.cylinder((x, y, 0.12), (x, y, top), r, 'galv', n=8)          # standards
            tubes.cylinder((x, y, 0.0), (x, y, 0.12), 0.016, 'galv', n=8)      # base jack
        for z in (0.20, deck):                                                  # transoms
            tubes.cylinder((x, -0.05, z), (x, width + 0.05, z), r, 'galv', n=8)
    for y in (0.0, width):                                                      # ledgers
        for z in (0.20, deck):
            tubes.cylinder((-0.05, y, z), (length + 0.05, y, z), r, 'galv', n=8)
    for z in (deck + 0.5, deck + 1.0):                                          # guard rails
        tubes.cylinder((-0.05, 0.0, z), (length + 0.05, 0.0, z), r, 'galv', n=8)
        for x in (0.0, length):
            tubes.cylinder((x, -0.05, z), (x, width + 0.05, z), r, 'galv', n=8)
    tubes.cylinder((0.0, 0.0, 0.20), (length, 0.0, deck), r, 'galv', n=8)       # face brace
    plates = Part('SC_Deck')
    for x in (0.0, length):
        for y in (0.0, width):
            plates.box_minmax((x - 0.075, y - 0.075, 0.0), (x + 0.075, y + 0.075, 0.008), 'galv')
    nb = 2
    pw = (width - 0.04) / nb
    for i in range(nb):                                                         # steel planks
        y0 = 0.02 + i * pw
        plates.box_minmax((0.03, y0 + 0.005, deck + r), (length - 0.03, y0 + pw - 0.005, deck + r + 0.045),
                          'plank')
    for y in (0.0, width):                                                      # toe boards
        yy = y + (0.03 if y == 0.0 else -0.03)
        plates.box_minmax((0.03, yy - 0.012, deck + r), (length - 0.03, yy + 0.012, deck + r + 0.15),
                          'toe')
    plates.box_minmax((0.0 - 0.03, 0.03, deck + r), (0.0 + 0.03, width - 0.03, deck + r + 0.15), 'toe')
    obs = []
    for part, bevel in ((tubes, 0.0), (plates, 0.004)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    return obs


# =============================================================================
# Materials / scene
# =============================================================================

def make_materials():
    return {
        'floor': M.concrete('BW_Pad', color=(0.20, 0.197, 0.188), pores=0.6),
        'yellow': M.paint('BW_LineYellow', YELLOW, rough=0.55, wear=0.1, dirt=0.4),
        'white': M.paint('BW_LineWhite', WHITE, rough=0.55, wear=0.1, dirt=0.4),
        'hazard': M.paint('BW_Hazard', YELLOW, rough=0.55, wear=0.1, dirt=0.4,
                          color_fn=M.hazard_fn(YELLOW, width=0.12, axis=('x', 'y'))),
        'stool': M.paint('BW_Stool', STOOL_GREY, rough=0.5, wear=0.8, dirt=0.8),
        'collar': M.paint('BW_Collar', YELLOW, rough=0.42, wear=0.9, dirt=0.6),
        'keel': M.paint('BW_Keel', KEEL_RED, rough=0.72, wear=0.3, dirt=0.9),
        'galv': M.galvanised('BW_Galv'),
        'plank': M.paint('BW_Plank', (0.30, 0.31, 0.30), rough=0.5, wear=1.0, dirt=0.8, flake=0.4),
        'toe': M.paint('BW_Toe', YELLOW, rough=0.5, wear=0.8, dirt=0.7),
    }


def _clear(names):
    for ob in [o for o in bpy.data.objects if o.name.startswith(names)]:
        bpy.data.objects.remove(ob, do_unlink=True)


def build_scene(with_block=True, scaffolds=True, **params):
    """Preview: the workspace, a ShipBlock on its stools and scaffold bays
    along the block's side."""
    _clear(('BlockWorkspace', 'BlockScaffold', 'WS_', 'SC_'))
    coll = K.collection(PREFIX)
    mats = make_materials()
    root = K.empty(PREFIX, (0, 0, 0), coll, size=1.0)
    obs, socket = build_workspace(coll=coll, mats=mats, **params)
    for ob in obs:
        K.parent_keep_local(ob, root)
    K.parent_keep_local(K.empty(f'{PREFIX}_BlockSocket', socket, coll, size=0.6), root)
    print(f'[BlockWorkspace] {sum(K.tri_count(o) for o in obs)} tris')
    if scaffolds:
        sroot = K.empty('BlockScaffold', (0, 0, 0), coll, size=0.5)
        sc = build_scaffold(coll=coll, mats=mats)
        for ob in sc:
            K.parent_keep_local(ob, sroot)
        print(f'[BlockScaffold] {sum(K.tri_count(o) for o in sc)} tris')
        # preview copies along the block's +Y side, rails outboard (turned 180 deg)
        sroot.location = (-2.5, 8.9, 0)
        sroot.rotation_euler = (0, 0, pi)
        bpy.context.view_layer.update()
        for i in range(1, 4):
            for ob in sc:
                cp = ob.copy()
                cp.name = f'SC_Preview_{i}_{ob.name}'
                coll.objects.link(cp)
                cp.parent = None
                cp.matrix_world = ob.matrix_world.copy()
                cp.location = cp.location + V((2.5 * i, 0, 0))
    if with_block:
        ns = __import__('runpy').run_path(os.path.join(HERE, 'ship_block.py'))
        blk = ns['build_block']('curved', location=tuple(socket))
    return root


# =============================================================================
# Export (two FBX, one shared atlas)
# =============================================================================

# the pad is 320 m2 of plain concrete: give the stools and the scaffold the texels
TEXEL_WEIGHTS = {'WS_Pad': 0.22, 'WS_Marks': 0.7, 'WS_Stools': 1.8, 'SC_Tubes': 1.4, 'SC_Deck': 1.3}


def export(bake=4096, save=2048, **params):
    """Rebuild both pieces at the origin, bake one atlas, build LODs, rig and
    write Art/Models/BlockWorkspace/BlockWorkspace.fbx + BlockScaffold.fbx."""
    import simulean_modular as KM
    _clear(('BlockWorkspace', 'BlockScaffold', 'WS_', 'SC_'))
    coll = K.collection(PREFIX)
    mats = make_materials()
    ws, socket = build_workspace(coll=coll, mats=mats, **params)
    sc = build_scaffold(coll=coll, mats=mats)
    pieces = [
        KM.Piece('BlockWorkspace', 'fixed', ws, levels=3,
                 lod={'WS_Pad': (1.0, 1.0), 'WS_Marks': (1.0, 1.0), 'WS_Stools': (0.5, 0.22)},
                 sockets={'BlockSocket': tuple(socket)}),
        KM.Piece('BlockScaffold', 'fixed', sc, levels=3,
                 lod={'SC_Tubes': (0.5, 0.25), 'SC_Deck': (1.0, 0.6)}),
    ]
    old_slot, KM.SLOT = KM.SLOT, 40.0                     # the pad is 16 x 20 m
    try:
        K.setup_cycles_gpu(bpy.context.scene, backends=('CUDA', 'HIP', 'ONEAPI', 'METAL'))  # OptiX bakes black
        KM.bake_kit(pieces, OUT_DIR, PREFIX, bake, save, weights=TEXEL_WEIGHTS)
    finally:
        KM.SLOT = old_slot
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    return pieces


def main():
    import argparse
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    K.reset_scene()
    export(args.bake, args.save)
    os.makedirs(BUILD_DIR, exist_ok=True)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))


if __name__ == '__main__':
    main()
