"""
SimuLean kit: assembly-line equipment for a trim / final-assembly area.  One FBX per piece, one shared
texture set AssemblyLineKit_*.png and material AssemblyLineKit.mat.  Generic, no marks.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/assembly_line.py --
            [--bake 4096] [--save 2048] [--quick] [--cpu] [--no-render] [--look]

Outputs  Assets/SimuLean.Unity/Art/Models/AssemblyLineKit/<Piece>.fbx (+ AssemblyLineKit_*.png)
         Tools/Blender/build/AssemblyLineKit.blend, Tools/Blender/previews/assyline_*.png

Frame (kit rules): authored +X = Unity +X, authored +Y = Unity +Z, authored +Z = Unity +Y.  Metres.
Floor pieces: pivot on the floor.  Hanging pieces (AndonBoard, ToolBalancer, LineLight): pivot = the
suspension point at the top (hang it from the ceiling / a beam at that point).

AssemblySkid        5.00 L x 2.20 W x 0.40 H body carrier for a slat / skid conveyor: perimeter frame
                    (0.12 m) with a chequer-plate deck, four support towers with rubber pads at the body
                    jacking points (x +-0.70, z +-0.75), yellow corner bumpers.
                    BodySocket (0, 0.21, 0): put a CarBody root here (its sills, 0.19 m above its own
                    pivot, rest on the pads at 0.40 m).
SlatConveyor_6m     tiling SEGMENT along local +Z over [0, 6] m: a 4.0 m wide floor-flush slat platform
                    (slats 0.15 m, top 5 mm above the floor so it never z-fights with it), yellow edge
                    angles at x = +-2.0.  Repeat end to end.
WagonParkingFrame   line-side parking frame for 2 wagons: 2 bays 1.00 W x 1.60 D between yellow floor
                    guide rails (flared entry at z = 0), rear stop bar and two bay-sign posts at z = 1.6.
                    Bay_0 / Bay_1 (-+0.5, 0, 0.8): wagon root, wagon front towards +Z (the stop).
WorkstationPost     aluminium post 2.40 m on a base plate, a 0.6 m arm with an LED work lamp (node Lamp,
                    emissive: toggle / tint its renderer), a three-colour stack light (node StackLight)
                    and an HMI screen at 1.45 m facing +Z.
AndonBoard          1.80 x 0.60 m LED board hanging 0.70 m below its pivot on two rods; face towards +Z.
ToolBalancer        0.6 m C-rail with trolley at the pivot, spring balancer, Cable (unit 1 m strand:
                    localScale.y = drop) and Tool (a pistol nutrunner: localPosition.y = -drop - 0.35)
                    with ToolSocket at its grip.
LineLight           1.50 m LED line light hanging 1.00 m below its pivot (emissive diffuser).
FloorTape_1m, FloorTape_Hazard_1m   tiling SEGMENTS along local +Z over [0, 1] m: 75 mm yellow tape /
                    100 mm yellow-black hazard tape, 1 mm thick, 2 mm above the floor (no z-fighting).
"""

import os
import sys
import argparse
import time
from math import pi

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
from mathutils import Matrix                # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import simulean_modular as KM               # noqa: E402
from simulean_kit import V, Part, NB, finish, finalize_material  # noqa: E402
import load_units as LU                     # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'AssemblyLineKit')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
PREFIX = 'AssemblyLineKit'
YELLOW = (0.80, 0.50, 0.02)
MATS = {}
WEIGHTS = {}
bx = LU.bx


# =============================================================================
# materials
# =============================================================================

def slats(name, pitch=0.15):
    """Floor-flush slat platform: steel slats with a worn anti-slip texture, dark joints;
    periodic along y (the tiling axis, 6 m)."""
    mat = M._new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    f = nb.m('FRACT', nb.m('DIVIDE', y, pitch))
    joint = nb.mr(nb.m('MINIMUM', f, nb.m('SUBTRACT', 1.0, f)), 0.05, 0.02)
    sid = nb.m('FLOOR', nb.m('DIVIDE', y, pitch))
    tone = nb.mr(LU.wnoise(nb, nb.vec(nb.m('MODULO', sid, 40.0), 1.0, 2.0)), 0.0, 1.0, 0.85, 1.1)
    base = nb.multc((0.13, 0.135, 0.14), nb.vec(tone, tone, tone))
    ribs = nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', x, 0.012)), 0.5)), 0.3, 0.15)
    wear = nb.mr(nb.noise(2.5, 6, 0.6), 0.45, 0.75)
    walk = nb.m('MAXIMUM', LU.band(nb, x, 1.35, 0.35, 0.2), LU.band(nb, x, -1.35, 0.35, 0.2))
    col = nb.mixc(nb.m('MULTIPLY', ribs, 0.4), base, (0.18, 0.18, 0.18))
    col = nb.mixc(nb.m('MULTIPLY', wear, nb.m('ADD', 0.3, nb.m('MULTIPLY', walk, 0.5))), col, (0.30, 0.30, 0.29))
    col = nb.mixc(joint, col, (0.005, 0.005, 0.005))
    col = LU.common_dirt(nb, col, cav=0.4, dust=0.25)
    rough = nb.mixf(wear, 0.7, 0.5)
    h = nb.m('SUBTRACT', nb.m('MULTIPLY', ribs, 0.3), nb.m('MULTIPLY', joint, 1.0))
    nrm = nb.bump(h, 0.6, 0.003)
    return finalize_material(nb, col, rough, nb.m('MULTIPLY', wear, 0.15), nrm)   # painted, worn to metal


def tape(name, hazard=False):
    mat = M._new(name)
    nb = NB(mat)
    x, y, z = nb.xyz(nb.obj)
    if hazard:
        s = nb.m('LESS_THAN', nb.m('FRACT', nb.m('DIVIDE', nb.m('ADD', x, y), 0.10)), 0.5)
        col = nb.mixc(s, (0.015, 0.015, 0.016), YELLOW)
    else:
        col = YELLOW
    scuff = nb.mr(nb.noise(6.0, 6, 0.6), 0.55, 0.75, 0.0, 0.6)
    col = nb.mixc(scuff, col, (0.12, 0.11, 0.09))
    return finalize_material(nb, col, nb.mixf(scuff, 0.45, 0.7), 0.0, nb.bump(nb.noise(200, 2), 0.1, 0.0005))


def andon(name):
    """Dark LED board face: a row of large amber digits, a status bar (green / red) and text rows."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    face = nb.mr(y, 0.035, 0.04)
    cell = nb.m('FRACT', nb.m('DIVIDE', nb.m('ADD', x, 0.9), 0.14))
    cidx = nb.m('FLOOR', nb.m('DIVIDE', nb.m('ADD', x, 0.9), 0.14))
    vz = nb.m('DIVIDE', nb.m('ADD', z, 0.74), 0.22)                     # digit rows (board centre z = -0.70)
    seg_h = nb.m('MAXIMUM', LU.band(nb, vz, 0.15, 0.04, 0.01), nb.m('MAXIMUM', LU.band(nb, vz, 0.5, 0.04, 0.01),
                                                                         LU.band(nb, vz, 0.85, 0.04, 0.01)))
    seg_h = nb.m('MULTIPLY', seg_h, LU.band(nb, cell, 0.5, 0.25, 0.03))
    seg_v = nb.m('MULTIPLY', nb.m('MAXIMUM', LU.band(nb, cell, 0.22, 0.04, 0.02), LU.band(nb, cell, 0.78, 0.04, 0.02)),
                 nb.m('MULTIPLY', nb.mr(vz, 0.12, 0.18), nb.mr(vz, 0.88, 0.82)))
    on = nb.m('GREATER_THAN', LU.wnoise(nb, nb.vec(cidx, nb.m('FLOOR', nb.m('MULTIPLY', vz, 2.0)), 3.0)), 0.25)
    dig = LU.mul(nb, nb.m('MAXIMUM', seg_h, nb.m('MULTIPLY', seg_v, on)), face, nb.mr(vz, -0.02, 0.05),
                 nb.mr(vz, 1.02, 0.95), nb.mr(x, -0.85, -0.82), nb.mr(x, 0.85, 0.82))
    bar = LU.mul(nb, face, LU.band(nb, z, -0.92, 0.03, 0.005), nb.mr(x, -0.85, -0.82), nb.mr(x, 0.85, 0.82))
    green = nb.m('LESS_THAN', x, 0.35)
    em = nb.mixc(dig, (0, 0, 0), (1.0, 0.45, 0.02))
    em = nb.mixc(bar, em, nb.mixc(green, (0.9, 0.05, 0.02), (0.1, 0.9, 0.2)))
    col = nb.mixc(face, (0.03, 0.03, 0.032), (0.006, 0.006, 0.007))
    return finalize_material(nb, col, nb.mixf(face, 0.5, 0.2), 0.0, None, emit=em)


def build_materials():
    MATS.clear()
    NB.default_period = {'y': 6.0}
    MATS['Slats'] = slats('Slats')
    MATS['EdgeYellow'] = M.paint('EdgeYellow', YELLOW, rough=0.4, wear=1.4)
    MATS['PitDark'] = M.plastic('PitDark', color=(0.01, 0.01, 0.01), rough=0.8)
    NB.default_period = {'y': 1.0}
    MATS['Tape'] = tape('Tape')
    MATS['TapeHazard'] = tape('TapeHazard', hazard=True)
    NB.default_period = {}
    MATS.update({
        'Frame': M.paint('Frame', (0.06, 0.09, 0.14), rough=0.45, wear=0.9),
        'Yellow': M.paint('Yellow', YELLOW, rough=0.4, wear=1.1),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=1.2, color_fn=M.hazard_fn(YELLOW, width=0.05, axis=('x', 'z'))),
        'Deck': M.tread_plate('Deck'),
        'Rubber': M.rubber('Rubber'),
        'Alu': M.dark_steel('Alu', color=(0.55, 0.56, 0.58), rough=0.35, metal=0.9),
        'Steel': M.dark_steel('Steel'),
        'Zinc': M.galvanised('Zinc'),
        'Black': M.plastic('Black', color=(0.018, 0.018, 0.02), rough=0.45),
        'Grey': M.plastic('Grey', color=(0.25, 0.26, 0.27), rough=0.5),
        'LensW': M.lens('LensW', (1.0, 0.96, 0.88)),
        'LensG': M.lens('LensG', (0.10, 0.90, 0.25)),
        'LensA': M.lens('LensA', (1.0, 0.55, 0.02)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'Screen': M.screen('Screen', (-0.13, 0.045, 1.38), (1, 0, 0), (0, 0, 1), (0.26, 0.16)),
        'Andon': andon('Andon'),
        'Sign': M.paint('Sign', (0.75, 0.75, 0.72), rough=0.4, wear=0.3),
    })


def mk(name, fn, bevel=0.003, seg=1, weight=1.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, bevel, seg, 40, 40)
    WEIGHTS[ob.name] = weight
    return ob


# =============================================================================
# pieces
# =============================================================================

def build_skid():
    L, W = 5.0, 2.2

    def frame(p):
        for s in (1, -1):
            bx(p, s * W / 2, s * (W / 2 - 0.10), -L / 2, L / 2, 0.0, 0.12, 'Frame')
            bx(p, -W / 2 + 0.10, W / 2 - 0.10, s * L / 2, s * (L / 2 - 0.10), 0.0, 0.12, 'Frame')
        for yy in (-1.5, -0.75, 0.0, 0.75, 1.5):
            bx(p, -W / 2 + 0.10, W / 2 - 0.10, yy - 0.04, yy + 0.04, 0.02, 0.12, 'Frame')
        for s in (1, -1):                                                         # runners
            bx(p, s * 0.50, s * 0.62, -L / 2 + 0.05, L / 2 - 0.05, -0.0, 0.02, 'Steel')

    def deck(p):
        bx(p, -W / 2 + 0.005, W / 2 - 0.005, -L / 2 + 0.005, L / 2 - 0.005, 0.12, 0.128, 'Deck')

    def towers(p):
        for sx in (1, -1):
            for sy in (1, -1):
                x0, y0 = sx * 0.70, sy * 0.75
                bx(p, x0 - 0.10, x0 + 0.10, y0 - 0.10, y0 + 0.10, 0.128, 0.14, 'Frame')
                bx(p, x0 - 0.05, x0 + 0.05, y0 - 0.05, y0 + 0.05, 0.14, 0.37, 'Frame')
                bx(p, x0 - 0.07, x0 + 0.07, y0 - 0.09, y0 + 0.09, 0.37, 0.40, 'Rubber')
                p.cylinder((x0, y0 + 0.06, 0.40), (x0, y0 + 0.06, 0.43), 0.012, 'Zinc', n=8)
        for sx in (1, -1):
            for sy in (1, -1):
                bx(p, sx * (W / 2 + 0.03), sx * (W / 2 - 0.12), sy * (L / 2 + 0.03), sy * (L / 2 - 0.12), 0.0,
                   0.14, 'Hazard')
    parts = [mk('SK_Frame', frame, 0.004, weight=0.6), mk('SK_Deck', deck, 0.002, weight=0.35),
             mk('SK_Towers', towers, 0.0, weight=0.8)]
    return KM.Piece('AssemblySkid', 'fixed', parts, levels=3,
                    lod={'SK_Frame': (0.4, 0.15), 'SK_Deck': (1.0, 1.0), 'SK_Towers': (0.4, 0.1)},
                    sockets={'BodySocket': (0, 0, 0.21)})


def build_slat_conveyor():
    Wd, P = 4.0, 6.0

    def plate(p):
        bx(p, -Wd / 2 + 0.06, Wd / 2 - 0.06, 0.0, P, -0.025, 0.005, 'Slats')     # 5 mm proud: no z-fight

    def edges(p):
        for s in (1, -1):
            bx(p, s * (Wd / 2 - 0.06), s * (Wd / 2 + 0.02), 0.0, P, -0.025, 0.009, 'EdgeYellow')
            bx(p, s * (Wd / 2 - 0.065), s * (Wd / 2 - 0.06), 0.0, P, -0.045, 0.005, 'PitDark')
    a = mk('SC_Plate', plate, 0.0, weight=0.25)
    b = mk('SC_Edges', edges, 0.002, weight=0.5)
    for o in (a, b):
        K.cull_open_ends(o, 'y', 0.0, P)
    return KM.Piece('SlatConveyor_6m', 'segment', [a, b], levels=1,
                    ghosts=[('SlatConveyor_6m', (0, P, 0)), ('SlatConveyor_6m', (0, -P, 0))])


def build_parking():
    def rails(p):
        for x in (-1.0, 0.0, 1.0):
            bx(p, x - 0.04, x + 0.04, 0.25, 1.55, 0.0, 0.10, 'Yellow')
            for s in ((1, -1) if abs(x) < 0.5 else ((-1,) if x > 0 else (1,))):     # flared entry guides
                p.sweep([(x + s * 0.03, 0.26, 0.05), (x + s * 0.17, 0.0, 0.05)],
                        [(-0.05, -0.01), (0.05, -0.01), (0.05, 0.01), (-0.05, 0.01)], 'Yellow', side=(0, 0, 1))
        bx(p, -1.05, 1.05, 1.55, 1.63, 0.0, 0.25, 'Hazard')                      # rear stop bar
        for x in (-1.0, 1.0):
            bx(p, x - 0.04, x + 0.04, 1.55, 1.63, 0.0, 1.30, 'Frame')            # sign posts
        bx(p, -1.0, 1.0, 1.57, 1.61, 1.10, 1.32, 'Frame')
        for x in (-0.5, 0.5):
            bx(p, x - 0.30, x + 0.30, 1.555, 1.57, 1.13, 1.29, 'Sign')
        for x in (-1.0, 1.0):
            bx(p, x - 0.10, x + 0.10, 1.50, 1.68, 0.0, 0.01, 'Steel')
    parts = [mk('PK_Rails', rails, 0.003, weight=0.6)]
    return KM.Piece('WagonParkingFrame', 'fixed', parts, levels=3, lod={'PK_Rails': (0.45, 0.2)},
                    sockets={'Bay_0': (-0.5, 0.8, 0.0), 'Bay_1': (0.5, 0.8, 0.0)})


def build_post():
    def post(p):
        bx(p, -0.2, 0.2, -0.2, 0.2, 0.0, 0.015, 'Steel')
        p.extrude(K.rrect(0.08, 0.08, 0.006, 2), 'Z', 0.015, 2.40, 'Alu')
        bx(p, -0.03, 0.03, 0.04, 0.62, 2.30, 2.36, 'Alu')                          # arm
        bx(p, -0.13, 0.13, 0.04, 0.05, 1.30, 1.46, 'Black')                         # HMI housing
        bx(p, -0.15, 0.15, 0.03, 0.04, 1.28, 1.48, 'Black')
        p.box((0, 0.045, 1.38), (0.26, 0.004, 0.16), 'Screen')
        for s in (1, -1):
            p.cylinder((s * 0.12, 0.0, 0.015), (s * 0.12, 0.0, 0.03), 0.012, 'Zinc', n=8)

    def lamp(p):
        bx(p, -0.30, 0.30, 0.50, 0.64, 2.24, 2.30, 'Grey')
        bx(p, -0.28, 0.28, 0.52, 0.62, 2.235, 2.24, 'LensW')

    def stack(p):
        z = 2.40
        p.cylinder((0, 0, z), (0, 0, z + 0.02), 0.035, 'Black', n=14)
        for i, m in enumerate(('LensG', 'LensA', 'LensR')):
            p.cylinder((0, 0, z + 0.02 + i * 0.06), (0, 0, z + 0.075 + i * 0.06), 0.03, m, n=14)
        p.cylinder((0, 0, z + 0.20), (0, 0, z + 0.22), 0.032, 'Black', n=14)
    parts = [mk('WP_Post', post, 0.003, weight=0.8)]
    lp = mk('WP_Lamp', lamp, 0.003, weight=0.6)
    st = mk('WP_Stack', stack, 0.002, weight=0.6)
    return KM.Piece('WorkstationPost', 'fixed', {None: parts, 'Lamp': [lp], 'StackLight': [st]}, levels=3,
                    lod={'WP_Post': (0.45, 0.2), 'WP_Lamp': (1.0, 0.5), 'WP_Stack': (0.5, 0.25)},
                    pivots={'Lamp': (0, 0.57, 2.24), 'StackLight': (0, 0, 2.40)})


def build_andon():
    def board(p):
        bx(p, -0.90, 0.90, -0.04, 0.04, -1.00, -0.40, 'Black')
        bx(p, -0.86, 0.86, 0.04, 0.042, -0.97, -0.43, 'Andon')
        for s in (1, -1):
            p.cylinder((s * 0.7, 0, 0.0), (s * 0.7, 0, -0.40), 0.008, 'Zinc', n=6)
            bx(p, s * 0.7 - 0.05, s * 0.7 + 0.05, -0.05, 0.05, -0.005, 0.0, 'Steel')
    parts = [mk('AB_Board', board, 0.003, weight=1.2)]
    return KM.Piece('AndonBoard', 'fixed', parts, levels=3, lod={'AB_Board': (0.5, 0.3)})


def build_balancer():
    def rail(p):
        prof = [(-0.03, 0.0), (0.03, 0.0), (0.03, -0.05), (0.012, -0.05), (0.012, -0.04), (0.022, -0.04),
                (0.022, -0.01), (-0.022, -0.01), (-0.022, -0.04), (-0.012, -0.04), (-0.012, -0.05), (-0.03, -0.05)]
        p.extrude(prof, 'Y', -0.3, 0.3, 'Zinc')
        bx(p, -0.05, 0.05, -0.06, 0.06, -0.10, -0.045, 'Steel')                     # trolley
        p.cylinder((0, 0, -0.10), (0, 0, -0.14), 0.006, 'Steel', n=6)
        p.lathe([(-0.14, 0.0), (-0.15, 0.045), (-0.27, 0.05), (-0.28, 0.02), (-0.29, 0.0)], 14, 'Yellow',
                axis='Z')                                                              # balancer body

    def cable(p):
        p.cylinder((0, 0, -0.29), (0, 0, -1.29), 0.003, 'Steel', n=5)

    def tool(p):
        z0 = -1.29
        p.cylinder((0, 0, z0), (0, 0, z0 - 0.03), 0.012, 'Steel', n=6)
        p.cylinder((0, -0.10, z0 - 0.06), (0, 0.16, z0 - 0.06), 0.03, 'Grey', n=12)     # motor body
        p.cylinder((0, 0.16, z0 - 0.06), (0, 0.22, z0 - 0.06), 0.012, 'Steel', n=8)     # spindle
        bx(p, -0.02, 0.02, -0.07, -0.02, z0 - 0.22, z0 - 0.06, 'Black')                 # grip
        bx(p, -0.012, 0.012, -0.02, 0.0, z0 - 0.11, z0 - 0.09, 'Yellow')                # trigger
    parts = [mk('TB_Rail', rail, 0.002, weight=0.6)]
    cb = mk('TB_Cable', cable, 0.0, weight=0.2)
    tl = mk('TB_Tool', tool, 0.002, weight=0.8)
    return KM.Piece('ToolBalancer', 'fixed', {None: parts, 'Cable': [cb], 'Tool': [tl]}, levels=3,
                    lod={'TB_Rail': (0.5, 0.3), 'TB_Cable': (1.0, 1.0), 'TB_Tool': (0.5, 0.25)},
                    pivots={'Cable': (0, 0, -0.29), 'Tool': (0, 0, -1.29)},
                    parents={'ToolSocket': 'Tool'}, sockets={'ToolSocket': (0, -0.045, -1.29 - 0.14)})


def build_line_light():
    def light(p):
        bx(p, -0.75, 0.75, -0.075, 0.075, -1.06, -1.0, 'Grey')
        bx(p, -0.73, 0.73, -0.06, 0.06, -1.065, -1.06, 'LensW')
        for s in (1, -1):
            p.cylinder((s * 0.6, 0, 0.0), (s * 0.6, 0, -1.0), 0.0015, 'Steel', n=4)
    parts = [mk('LL_Light', light, 0.003, weight=0.6)]
    return KM.Piece('LineLight', 'fixed', parts, levels=3, lod={'LL_Light': (0.6, 0.4)})


def build_tapes():
    out = []
    for name, mat, w in (('FloorTape_1m', 'Tape', 0.075), ('FloorTape_Hazard_1m', 'TapeHazard', 0.10)):
        ob = mk(f'FT_{name}', lambda p, w=w, mat=mat: bx(p, -w / 2, w / 2, 0.0, 1.0, 0.002, 0.003, mat), 0.0,
                weight=0.5)                                                   # 2-3 mm above the floor
        K.cull_open_ends(ob, 'y', 0.0, 1.0)
        out.append(KM.Piece(name, 'segment', [ob], levels=1, ghosts=[(name, (0, 1, 0)), (name, (0, -1, 0))]))
    return out


def build_kit():
    build_materials()
    return [build_skid(), build_slat_conveyor(), build_parking(), build_post(), build_andon(),
            build_balancer(), build_line_light()] + build_tapes()


# =============================================================================
# previews
# =============================================================================

def preview_renders(pieces, quick):
    scene = bpy.context.scene
    pcs = {pc.name: pc for pc in pieces}
    KM.hide_kit(pieces)
    KM.studio(scene, k=3.0, floor=150)
    spp = 32 if quick else 128
    res = (1000, 625) if quick else (1800, 1100)
    out = lambda n: os.path.join(PREVIEW_DIR, f'assyline_{n}.png')      # noqa: E731
    T, Rz = Matrix.Translation, lambda a: Matrix.Rotation(a, 4, 'Z')    # noqa: E731
    R180 = Matrix.Rotation(pi, 4, 'Z')
    extra = {}
    for blend, names in ((os.path.join(BUILD_DIR, 'CarBody.blend'), ('CarBody_LOD0',)),
                         (os.path.join(BUILD_DIR, 'TuggerTrain.blend'), ('ConsoleWagon__ConsoleWagon_LOD0',
                                                                         'ToteTrolley__ToteTrolley_LOD0'))):
        if os.path.exists(blend):
            with bpy.data.libraries.load(blend, link=False) as (src, dst):
                dst.objects = [n for n in src.objects if n in names]
            for o in dst.objects:
                extra[o.name] = o

    def put_extra(coll, key, M_):
        o = extra.get(key)
        if o is None:
            return
        c = o.copy()
        coll.objects.link(c)
        c.parent = None
        c.hide_render = False
        c.matrix_world = M_ @ R180

    def line(coll, level=0):
        for k in range(4):                                                   # 24 m of slat conveyor
            KM.place(pcs['SlatConveyor_6m'], T((0, -12 + 6 * k, 0)), coll, level)
        for k in range(4):
            yy = -9.0 + 6.0 * k
            KM.place(pcs['AssemblySkid'], T((0, yy, 0)), coll, level)
            put_extra(coll, 'CarBody_LOD0', T((0, yy, 0.21)))
        for k in range(24):
            KM.place(pcs['FloorTape_Hazard_1m'], T((-2.6, -12 + k, 0)), coll, level)
            KM.place(pcs['FloorTape_1m'], T((-4.4, -12 + k, 0)), coll, level)
        KM.place(pcs['WagonParkingFrame'], T((-3.5, -1.0, 0)) @ Rz(pi / 2), coll, level)
        put_extra(coll, 'ConsoleWagon__ConsoleWagon_LOD0', T((-3.5, -1.5, 0)) @ Rz(pi / 2) @ T((0, 0, 0)))
        put_extra(coll, 'ToteTrolley__ToteTrolley_LOD0', T((-3.5, -0.5, 0)) @ Rz(pi / 2))
        for yy in (-6.0, 0.0, 6.0):
            KM.place(pcs['WorkstationPost'], T((-2.4, yy, 0)) @ Rz(-pi / 2), coll, level)
            KM.place(pcs['ToolBalancer'], T((1.6, yy, 3.2)), coll, level,
                     node_pose={'Cable': Matrix.Diagonal((1, 1, 1.3, 1)), 'Tool': T((0, 0, -0.3))})
            KM.place(pcs['LineLight'], T((0.0, yy + 3.0, 4.2)), coll, level)
        KM.place(pcs['AndonBoard'], T((0.0, 9.0, 4.0)) @ Rz(pi), coll, level)
    coll = K.collection('PreviewLine')
    line(coll)
    KM.scale_figure(coll, (-1.6, 1.0, 0), 1.4)
    hero = ((7.5, -10.5, 4.2), (-0.5, 0.5, 0.8), 28)
    LU._render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    LU._render(scene, KM.camera(scene, 'Cam_Station', (2.4, -2.4, 2.2), (-1.8, 1.0, 1.2), 32), out('station'),
               res, spp)
    LU._render(scene, KM.camera(scene, 'Cam_Skid', (3.6, -12.0, 1.5), (0.0, -9.0, 0.3), 32), out('skid'), res, spp)
    LU._render(scene, KM.camera(scene, 'Cam_Andon', (2.5, 3.0, 3.0), (0.0, 9.0, 3.4), 35), out('andon'), res, spp)
    coll.hide_render = True
    for lvl in (1, 2):
        c = K.collection(f'PreviewLine_LOD{lvl}')
        line(c, lvl)
        LU._render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (1000, 625), spp)
        c.hide_render = True


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--look', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene, backends=('CUDA', 'OPTIX', 'HIP', 'ONEAPI', 'METAL'))
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    pieces = build_kit()
    if args.look:
        KM.studio(scene, k=2.0)
        x = 0.0
        for pc in pieces:
            for o in pc.parts:
                o.location.x += x
            x += 3.0 if pc.name not in ('AssemblySkid', 'SlatConveyor_6m') else 5.5
        KM.render(scene, KM.camera(scene, 'Cam_Look', (x / 2, -14.0, 5.0), (x / 2, 0.0, 0.8), 40),
                  os.path.join(PREVIEW_DIR, 'assyline_look.png'), (1800, 900), 24 if args.quick else 96)
        return
    try:
        KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, weights=WEIGHTS, ao_distance=0.4)
    except RuntimeError as ex:                     # GPU fault: every part is built at the origin
        print(f'[assyline] GPU bake failed ({str(ex).splitlines()[0]}); retrying on the CPU')
        for o in list(bpy.data.objects):
            if o.name.endswith(tuple(f'_ghost{i}' for i in range(4))):
                bpy.data.objects.remove(o)
        for pc in pieces:
            for o in pc.parts:
                o.location = (0, 0, 0)
        from rail_shuttle import use_cpu
        use_cpu(scene)
        KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, weights=WEIGHTS, ao_distance=0.4)
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        KM.rig(pc)
    KM.export_kit(pieces, OUT_DIR)
    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(pieces, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[assyline] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
