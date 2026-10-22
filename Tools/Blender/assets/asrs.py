"""
SimuLean kit: automated storage and retrieval system (AS/RS) for unit loads (EUR pallets) -- pallet racking
and a single-mast stacker crane in the aisle.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/asrs.py -- [--bake 2048] [--save 1024]
            [--quick] [--cpu]

Outputs (a kit: one FBX per piece, one shared texture set / material ASRS.mat)
    Assets/SimuLean.Unity/Art/Models/ASRS/Rack_Bay.fbx      segment, 1.4 m along the aisle: one pallet
                                                           column, 5 levels, single deep (an upright frame at 0)
    Assets/SimuLean.Unity/Art/Models/ASRS/Rack_End.fbx      cap: the upright frame closing a rack row
    Assets/SimuLean.Unity/Art/Models/ASRS/Aisle_Rail.fbx    segment, 1.4 m: the floor rail of the aisle
    Assets/SimuLean.Unity/Art/Models/ASRS/StackerCrane.fbx  single-mast stacker crane (8.2 m)
    Tools/Blender/build/ASRS.blend

Frame (kit rules, simulean_modular): authored +X = Unity +X, authored +Y = Unity +Z (along the aisle: racks
and rail tile along it, period 1.4 m), authored +Z = Unity +Y.  Library palette: steel-blue uprights,
orange beams, yellow crane.  Generic, no marks.

Layout: the aisle runs along Unity Z through x = 0; a rack row's aisle face is at its local x = 0 and its
depth goes to +x (1.1 m frame, a 1.2 m pallet overhangs 50 mm on each side); for the row on the other
side of the aisle, turn it 180 deg about Y.  Aisle width (rack face to rack face) 1.6 m: place the rows
at x = +-0.8.  Beam tops (pallet seats) at y = 0.60, 2.05, 3.50, 4.95, 6.40.

Nodes (Unity names, local positions in metres):
    Rack_Bay       Slot_1 .. Slot_5   pallet seats, centre of the position: (0.55, beam top, 0.70)
    StackerCrane   root on the floor at the mast's centre; the view moves the root along the aisle (Unity Z)
    |- Carriage    lifting carriage, in front of the mast (its pallet centre at Unity z = -0.95):
    |   |          localPosition.y = seat height - 0.60 (0 .. 5.80 for the seats 0.60 .. 6.40)
    |   '- Forks   telescopic forks: localPosition.x = 0 at rest, +-1.35 into the rack on either side
    |       '- LoadSocket   fork top (0, 0.60, -0.95) at rest, where a carried pallet goes
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
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'ASRS')
BUILD_DIR = os.path.join(KIT, 'build')
PREFIX = 'ASRS'

BAY = 1.4                                   # along the aisle (one EUR pallet, 0.8 wide, + clearances)
DEPTH = 1.1                                 # upright frame depth
UP_H = 7.7                                  # upright height
LEVELS = (0.60, 2.05, 3.50, 4.95, 6.40)     # beam tops
UP_W, UP_D = 0.09, 0.07                     # upright section (along the aisle, across)
BEAM_H, BEAM_T = 0.12, 0.05
MAST_H = 8.2
CAR_Z0 = 0.60                               # carriage (fork top) at rest = the lowest seat
CAR_Y = -0.95                               # carriage centre along the aisle (in front of the mast)

MATS = {}


def build_materials():
    K.NB.default_period = {'y': BAY}
    MATS.update({
        'Upright': M.paint('Upright', (0.035, 0.11, 0.26), rough=0.4, wear=0.25),
        'Beam': M.paint('Beam', (0.85, 0.20, 0.01), rough=0.38, wear=0.3),
        'Crane': M.paint('Crane', (0.80, 0.42, 0.015), rough=0.38, wear=0.3),
        'Dark': M.dark_steel('Dark'),
        'Rail': M.dark_steel('Rail', color=(0.22, 0.22, 0.23), rough=0.3, metal=0.95),
        'Galv': M.galvanised('Galv'),
        'Black': M.plastic('Black', color=(0.02, 0.02, 0.022), rough=0.5),
        'Cabinet': M.paint('Cabinet', (0.45, 0.46, 0.47), rough=0.45, wear=0.2),
        'LensG': M.lens('LensG', (0.10, 0.85, 0.30)),
    })


def mk(name, fn, bevel=0.002):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, 1, 40, 40)
    return ob


# =============================================================================
# Racking
# =============================================================================

def upright_frame(p, y):
    """Two uprights (aisle face and back) at y."""
    for x0 in (0.0, DEPTH - UP_D):
        p.box_minmax((x0, y - UP_W / 2, 0.0), (x0 + UP_D, y + UP_W / 2, UP_H), 'Upright')
        p.box_minmax((x0 - 0.02, y - 0.08, 0.0), (x0 + UP_D + 0.02, y + 0.08, 0.008), 'Galv')   # base plate


def bracing(p, y):
    """Horizontal and diagonal bracing between the two uprights at y (dropped at the far LOD)."""
    z, k = 0.25, 0
    while z < UP_H - 0.3:
        p.box_minmax((UP_D, y - 0.015, z - 0.015), (DEPTH - UP_D, y + 0.015, z + 0.015), 'Upright')
        z1 = min(z + 1.2, UP_H - 0.25)
        a, b = (UP_D, z), (DEPTH - UP_D, z1)
        if k % 2:
            a, b = (DEPTH - UP_D, z), (UP_D, z1)
        p.cylinder((a[0], y, a[1]), (b[0], y, b[1]), 0.014, 'Upright', n=6)
        z, k = z1, k + 1
    p.box_minmax((UP_D, y - 0.015, UP_H - 0.25), (DEPTH - UP_D, y + 0.015, UP_H - 0.22), 'Upright')


def build_rack_bay():
    frame = mk('RB_Frame', lambda p: upright_frame(p, 0.0))
    brace = mk('RB_Brace', lambda p: bracing(p, 0.0))

    def beams(p):
        for zt in LEVELS:
            for x0 in (0.0, DEPTH - 0.05):
                p.box_minmax((x0, UP_W / 2, zt - BEAM_H), (x0 + BEAM_T, BAY - UP_W / 2, zt), 'Beam')

    def supports(p):
        for zt in LEVELS:
            for yy in (0.25, BAY - 0.25):                                                    # pallet supports
                p.box_minmax((BEAM_T, yy - 0.03, zt - 0.03), (DEPTH - 0.05, yy + 0.03, zt), 'Galv')
    bm = mk('RB_Beams', beams)
    sup = mk('RB_Supports', supports)
    sockets = {f'Slot_{i + 1}': (DEPTH / 2, BAY / 2, zt) for i, zt in enumerate(LEVELS)}
    return KM.Piece('Rack_Bay', 'segment', [frame, brace, bm, sup], levels=2,
                    lod={'RB_Frame': (1.0,), 'RB_Brace': (None,), 'RB_Beams': (1.0,), 'RB_Supports': (None,)},
                    ghosts=[('Rack_Bay', (0, BAY, 0)), ('Rack_Bay', (0, -BAY, 0))], sockets=sockets)


def build_rack_end():
    frame = mk('RE_Frame', lambda p: upright_frame(p, 0.0))
    brace = mk('RE_Brace', lambda p: bracing(p, 0.0))
    return KM.Piece('Rack_End', 'cap', [frame, brace], levels=2, lod={'RE_Frame': (1.0,), 'RE_Brace': (None,)},
                    ghosts=[('Rack_Bay', (0, -BAY, 0))])


def build_rail():
    def f(p):
        p.box_minmax((-0.10, 0.0, 0.0), (0.10, BAY, 0.01), 'Galv')                           # sole plate
        p.box_minmax((-0.03, 0.0, 0.01), (0.03, BAY, 0.06), 'Rail')                          # rail
        p.box_minmax((-0.035, 0.0, 0.06), (0.035, BAY, 0.075), 'Rail')                       # head
        for yy in (0.35, 1.05):
            p.box((0.0, yy, 0.03), (0.16, 0.05, 0.04), 'Dark')                               # clamps
    rail = mk('AR_Rail', f, bevel=0.0)
    return KM.Piece('Aisle_Rail', 'segment', [rail], levels=2, lod={'AR_Rail': (1.0,)},
                    ghosts=[('Aisle_Rail', (0, BAY, 0)), ('Aisle_Rail', (0, -BAY, 0))])


# =============================================================================
# Stacker crane
# =============================================================================

def build_crane():
    def base(p):
        p.box_minmax((-0.20, -1.6, 0.09), (0.20, 1.5, 0.32), 'Crane')                        # bottom beam
        for yy in (-1.25, 1.25):                                                              # travel wheels
            p.lathe([(-0.04, 0.0), (-0.04, 0.12), (0.04, 0.12), (0.04, 0.0)], 20, 'Dark',
                    center=(0.0, yy, 0.17), axis='X', mats=['Dark', 'Dark', 'Dark'])
            p.box_minmax((-0.21, yy - 0.18, 0.08), (0.21, yy + 0.18, 0.30), 'Crane')
        p.box_minmax((-0.32, 0.55, 0.32), (0.32, 1.35, 2.15), 'Cabinet')                     # control cabinet
        p.box((0.0, 0.55, 2.0), (0.10, 0.006, 0.06), 'LensG')
        p.box_minmax((-0.25, -0.22, 0.32), (0.25, 0.22, MAST_H), 'Crane')                    # mast
        p.box_minmax((-0.30, -0.6, MAST_H), (0.30, 0.6, MAST_H + 0.18), 'Crane')             # top carriage
        for yy in (-0.45, 0.45):                                                              # guide rollers
            for sx in (1, -1):
                p.cylinder((sx * 0.08, yy, MAST_H + 0.18), (sx * 0.08, yy, MAST_H + 0.30), 0.04, 'Dark', n=12)
        p.box_minmax((-0.05, -0.04, MAST_H + 0.30), (0.05, 0.04, MAST_H + 0.32), 'Dark')     # top guide rail
        for z in [0.6 + 0.3 * k for k in range(int((MAST_H - 1.0) / 0.3))]:                  # ladder rungs
            p.box_minmax((0.25, -0.24, z), (0.29, -0.20, z + 0.02), 'Galv')
        p.box_minmax((0.25, -0.24, 0.5), (0.29, -0.22, MAST_H - 0.2), 'Galv')
        p.box_minmax((0.25, -0.06, 0.5), (0.29, -0.04, MAST_H - 0.2), 'Galv')

    def carriage(p):
        z, y0, y1 = CAR_Z0, CAR_Y - 0.62, -0.22
        p.box_minmax((-0.30, y1 - 0.14, z - 0.22), (0.30, y1, z + 0.75), 'Crane')            # guide frame on the mast
        for sx in (1, -1):
            p.cylinder((sx * 0.28, y1 + 0.03, z), (sx * 0.28, y1 + 0.03, z + 0.5), 0.035, 'Dark', n=10)  # guide rollers
        p.box_minmax((-0.62, y0, z - 0.22), (0.62, y1 - 0.14, z - 0.10), 'Dark')             # platform
        for sx in (1, -1):
            p.box_minmax((sx * 0.60 - 0.03, y0, z - 0.10), (sx * 0.60 + 0.03, y1 - 0.14, z + 0.25), 'Crane')  # side guards

    def forks(p):
        z = CAR_Z0
        for yy in (CAR_Y - 0.32, CAR_Y + 0.32):
            p.box_minmax((-0.58, yy - 0.08, z - 0.10), (0.58, yy + 0.08, z - 0.03), 'Galv')  # lower stage
            p.box_minmax((-0.60, yy - 0.06, z - 0.03), (0.60, yy + 0.06, z), 'Rail')         # top stage
    base_ob = mk('SC_Base', base)
    car = mk('SC_Carriage', carriage)
    frk = mk('SC_Forks', forks)
    return KM.Piece('StackerCrane', 'fixed', {None: [base_ob], 'Carriage': [car], 'Forks': [frk]}, levels=3,
                    lod={'SC_Base': (0.5, 0.2), 'SC_Carriage': (0.5, 0.25), 'SC_Forks': (1.0, 0.5)},
                    pivots={'Carriage': (0.0, 0.0, 0.0), 'Forks': (0.0, 0.0, 0.0)},
                    parents={'Forks': 'Carriage', 'LoadSocket': 'Forks'},
                    sockets={'LoadSocket': (0.0, CAR_Y, CAR_Z0)})


def build_kit():
    build_materials()
    return [build_rack_bay(), build_rack_end(), build_rail(), build_crane()]


def build_preview():
    """Preview (no bake): two rack rows of 4 bays + end frames, the rail and the crane in the aisle."""
    from mathutils import Matrix
    from math import pi
    pieces = build_kit()
    by = {pc.name: pc for pc in pieces}
    out = []

    def place(name, M_):
        pc = by[name]
        for o in pc.parts:
            c = o.copy()
            bpy.context.scene.collection.objects.link(c)
            c.matrix_world = M_ @ o.matrix_world
            out.append(c)
    for side, M0 in ((1, Matrix.Translation((0.8, 0, 0))),
                     (-1, Matrix.Translation((-0.8, 0, 0)) @ Matrix.Rotation(pi, 4, 'Z'))):
        for i in range(4):                       # the turned row's bay i spans local -1.4 .. 0: shift one bay
            off = Matrix.Translation((0, i * BAY if side > 0 else -(i + 1) * BAY, 0))
            place('Rack_Bay', M0 @ off)
        place('Rack_End', M0 @ Matrix.Translation((0, 4 * BAY if side > 0 else 0.0, 0)))
    for i in range(4):
        place('Aisle_Rail', Matrix.Translation((0, i * BAY, 0)))
    place('StackerCrane', Matrix.Translation((0, 2.4, 0)))
    for pc in pieces:
        for o in pc.parts:
            o.hide_render = True
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
    KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, ao_distance=0.4)
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{PREFIX}.blend'))
    print(f'[asrs] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
