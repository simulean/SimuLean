"""
SimuLean kit: load units for intralogistics / automotive supply (pallets, small and large load
carriers, cartons, pallet containers and full pallet loads).  Generic, no brands or logos.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/warehouse_load_units.py --
            [--bake 4096] [--save 2048] [--quick] [--cpu] [--no-render] [--look] [--only A,B]

    --quick      1024 px bake, small previews (iteration)
    --look       no bake / export: render the LOD0 source materials in Cycles (fast look check)
    --only A,B   build only these pieces (with --look, or a test bake into build/WarehouseLoadUnits_test/)

Outputs (a kit: one FBX per piece, one shared texture set + one colour variant)
    Assets/SimuLean.Unity/Art/Models/WarehouseLoadUnits/<Piece>.fbx
    Assets/SimuLean.Unity/Art/Models/WarehouseLoadUnits/WarehouseLoadUnits_{Albedo,MetallicSmoothness,Normal,Occlusion,Emission}.png
    Assets/SimuLean.Unity/Art/Models/WarehouseLoadUnits/WarehouseLoadUnits_Blue_*.png   colour set: the small load
        carriers (and the pallet load of carriers) in blue instead of grey; same UVs, so the
        postprocessor's WarehouseLoadUnits_Blue.mat can be put on any piece's renderers
    Tools/Blender/build/WarehouseLoadUnits.blend, Tools/Blender/previews/loadunits_*.png

Frame (kit rules, simulean_modular): authored +X = Unity +X (width), authored +Y = Unity +Z
(LENGTH: the long side of every piece lies along local +Z in Unity), authored +Z = Unity +Y (up).
Pivot of every piece = bottom centre (floor contact, centre of the footprint).  Metres.

Built for thousands of instances: ONE renderer per LOD, one shared material (GPU instancing),
detail baked into the atlas.  LOD1 / LOD2 are hand-made proxies (simple boxes / open boxes)
whose maps are baked from LOD0 by selected-to-active (simulean_kit.transfer_to_proxies), so a
12-triangle LOD2 box still shows the plank gaps, ribs and handle openings.
Budgets: LOD0 <= ~1.5 k tris (pallets <= 1 k), LOD1 <= ~300, LOD2 <= ~40.

Pieces (W = X, L = Z in Unity, H = height; sockets are bare Transforms):
    EuroPallet                 1200 L x 800 W x 144 H.  EPAL-style block pallet: 3 bottom boards
                               (100/145/100 x 22 mm, along L), 9 blocks (100 or 145 W x 145 L x 78,
                               pressed wood chip), 3 stringer boards (22 mm, along L), 5 top deck
                               boards across (145/100/145/100/145 x 22 mm, gaps 141 mm), nails.
                               Fork entry 4-way, pocket height 78 mm.  TopSocket (0, 0.144, 0).
    IndustrialPallet           1200 L x 1000 W x 144 H, same construction, 7 top boards (gaps 61 mm).
                               TopSocket (0, 0.144, 0).
    SmallLoadCarrier_300x200x147, _400x300x147, _400x300x280, _600x400x280
                               KLT-type bins (name = L x W x H mm): stacking foot (8 mm, inset
                               8 mm), ribbed outer walls, rim band, open handle slots through the
                               short (+-Z) walls, label holder with a barcode card on both long
                               (+-X) walls, floor 14 mm.  Grey (WarehouseLoadUnits) / blue (WarehouseLoadUnits_Blue).
                               TopSocket (0, H, 0) = rim top (stack pitch H).
    CardboardCarton_300x200x200, _400x300x250, _600x400x300
                               brown corrugated cartons (L x W x H): top flaps meeting along L,
                               packing tape over the seam and 70 mm down both ends, white shipping
                               label on +X, glue flap at a corner.  TopSocket (0, H, 0).
    LargeLoadCarrier           1200 L x 1000 W x 975 H plastic GLT-type container: dark base
                               (3 skids, 9 feet, deck 170 mm), four grey walls (35 mm) with ribs,
                               corner posts and rim (fold-wall look), access flap on +X with two
                               latches, label holders.  ContentSocket (0, 0.17, 0), TopSocket (0, 0.975, 0).
    MetalMeshBoxPallet         1240 L x 835 W x 970 H galvanised wire-mesh box pallet: 4 corner
                               feet (120 mm), floor sheet at 170 mm, angle corner posts and top
                               frame, 50 mm wire mesh (real wires at LOD0), drop gate in the upper
                               half of the +X wall (divider rail at 570 mm, 2 latches), stacking
                               cones.  ContentSocket (0, 0.17, 0), TopSocket (0, 0.97, 0).
    PalletCollar               EuroPallet + 3 wooden collars (1200 x 800 x 195 mm, 20 mm boards,
                               200 mm pitch, galvanised corner hinges).  Height 0.744.
                               ContentSocket (0, 0.144, 0), TopSocket (0, 0.744, 0).
    PalletLoad_Cartons         EuroPallet + 4 layers x 8 cartons 400 x 300 x 250 (block 1200 x 800 x
                               1000), stretch-wrapped (film over the sides, folded 50 mm over the
                               top edge).  Height 1.144.  TopSocket (0, 1.144, 0).
    PalletLoad_SmallLoadCarriers  EuroPallet + 3 layers x 4 carriers 600 x 400 x 280 (2 x 2), a
                               plastic pallet lid (30 mm) and two green PET straps.  Height 1.014.
                               TopSocket (0, 1.014, 0).
A full pallet load is ONE piece (one mesh per LOD): simulations show a full pallet as one unit.
"""

import os
import sys
import shutil
import argparse
import time
from math import pi

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import numpy as np                          # noqa: E402
from mathutils import Matrix                # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
import simulean_modular as KM               # noqa: E402
from simulean_kit import V, Part, NB, rrect, finish, finalize_material  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'WarehouseLoadUnits')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
PREFIX = 'WarehouseLoadUnits'

PH = 0.144                                  # pallet height
GREY = (0.24, 0.255, 0.27)                  # small load carrier grey (linear)
BLUE = (0.010, 0.085, 0.30)                 # small load carrier blue (linear)
GAP = 0.0025                                # atlas island gap (UV units)

MATS = {}
WEIGHTS = {}                                # part name -> texel weight
VARIANT = {}                                # grey material name -> blue twin


# =============================================================================
# shader helpers
# =============================================================================

def wnoise(nb, vec):
    n = nb.node('ShaderNodeTexWhiteNoise', props={'noise_dimensions': '3D'}, Vector=vec)
    return n.outputs['Value']


def band(nb, v, c, half, soft=0.0006):
    """1 where |v - c| < half (soft edge)."""
    return nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', v, c)), half + soft, half)


def rect(nb, u, v, cu, cv, hu, hv, soft=0.0006):
    return nb.m('MULTIPLY', band(nb, u, cu, hu, soft), band(nb, v, cv, hv, soft))


def mul(nb, *xs):
    r = xs[0]
    for x in xs[1:]:
        r = nb.m('MULTIPLY', r, x)
    return r


def rrect_sdf(nb, u, v, hu, hv, r):
    """Signed distance to a rounded rectangle centred at 0 (half sizes hu, hv)."""
    qx = nb.m('SUBTRACT', nb.m('ABSOLUTE', u), hu - r)
    qy = nb.m('SUBTRACT', nb.m('ABSOLUTE', v), hv - r)
    out = nb.vm('LENGTH', nb.vec(nb.m('MAXIMUM', qx, 0.0), nb.m('MAXIMUM', qy, 0.0), 0.0))
    inside = nb.m('MINIMUM', nb.m('MAXIMUM', qx, qy), 0.0)
    return nb.m('SUBTRACT', nb.m('ADD', out, inside), r)


def paper_label(nb, col, rough, u, v, face, cu, cv, w, h, seed=0.0, height=None):
    """White printed label (text lines, a bold field, a barcode) centred at (cu, cv) of a
    face whose in-plane coordinates are u (right) / v (up).  Returns col, rough, mask, height."""
    lu = nb.m('ADD', nb.m('DIVIDE', nb.m('SUBTRACT', u, cu), w), 0.5)
    lv = nb.m('ADD', nb.m('DIVIDE', nb.m('SUBTRACT', v, cv), h), 0.5)
    inside = mul(nb, rect(nb, u, v, cu, cv, w / 2, h / 2, 0.0004), face)
    bars = nb.m('GREATER_THAN', wnoise(nb, nb.vec(nb.m('FLOOR', nb.m('MULTIPLY', lu, 56)), seed, 1.0)), 0.45)
    bars = mul(nb, bars, rect(nb, lu, lv, 0.5, 0.2, 0.40, 0.12, 0.002))
    row = nb.m('FLOOR', nb.m('MULTIPLY', lv, 10))
    line = band(nb, nb.m('FRACT', nb.m('MULTIPLY', lv, 10)), 0.5, 0.22, 0.05)
    words = nb.m('GREATER_THAN', wnoise(nb, nb.vec(nb.m('FLOOR', nb.m('MULTIPLY', lu, 9)), row, seed + 3)), 0.3)
    text = mul(nb, line, words, rect(nb, lu, lv, 0.5, 0.69, 0.42, 0.24, 0.002))
    bold = rect(nb, lu, lv, 0.24, 0.42, 0.16, 0.05, 0.002)
    frame = nb.m('SUBTRACT', 1.0, rect(nb, lu, lv, 0.5, 0.5, 0.47, 0.46, 0.002))
    ink = nb.m('MAXIMUM', nb.m('MAXIMUM', bars, nb.m('MULTIPLY', text, 0.85)), nb.m('MAXIMUM', bold, frame))
    paper = nb.mixc(nb.mr(nb.noise(30, 4), 0.3, 0.7), (0.78, 0.78, 0.74), (0.70, 0.69, 0.64))
    lab = nb.mixc(ink, paper, (0.025, 0.025, 0.028))
    col = nb.mixc(inside, col, lab)
    rough = nb.mixf(inside, rough, 0.5)
    return col, rough, inside


def common_dirt(nb, col, cav=0.55, dust=0.3, edge_col=None, edge_amt=0.0):
    c = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.08), cav), col, M.GRIME)
    d = mul(nb, nb.up_mask(0.6, 0.95), nb.mr(nb.noise(5.0, 6, 0.6), 0.4, 0.8, 0.1, 1.0), dust)
    c = nb.mixc(d, c, M.DUST)
    if edge_col is not None:
        c = nb.mixc(nb.m('MULTIPLY', nb.edge_mask(0.004), edge_amt), c, edge_col)
    return c


# =============================================================================
# materials
# =============================================================================

def wood(name, axis, nails=None, tone=(0.47, 0.33, 0.17), grey=0.5, hinges=None, scale=1.0):
    """Sawn pine boards, grain along `axis`; per-board tone, knots, end grain, light
    weathering, cavity grime.  nails = (xs, ys, z_top): nail heads on the top faces.
    hinges = (W, L, z0s, h): galvanised corner hinge plates (pallet collars)."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    across = y if axis == 'x' else x
    along_n = nx if axis == 'x' else ny
    an = (2.0, 60.0, 60.0) if axis == 'x' else (60.0, 2.0, 60.0)
    grain = nb.noise(1.0 * scale, 8, 0.62, aniso=an, dist=0.5)
    fine = nb.noise(4.0, 4, 0.5, aniso=tuple(a * 3 for a in an))
    bid = nb.vec(nb.m('FLOOR', nb.m('MULTIPLY', across, 9.0)), nb.m('FLOOR', nb.m('MULTIPLY', z, 40.0)), 7.0)
    w1 = wnoise(nb, bid)
    base = nb.mixc(w1, tuple(c * 0.80 for c in tone), tuple(min(1, c * 1.15) for c in tone))
    w2 = wnoise(nb, nb.vec(nb.m('FLOOR', nb.m('MULTIPLY', across, 9.0)), nb.m('FLOOR', nb.m('MULTIPLY', z, 40.0)), 3.0))
    base = nb.mixc(nb.mr(w2, 0.75, 1.0, 0.0, 0.5), base, (tone[0] * 0.95, tone[1] * 0.75, tone[2] * 0.55))
    col = nb.mixc(nb.mr(grain, 0.42, 0.66, 0.0, 0.75), base, nb.multc(base, (0.62, 0.54, 0.45)))
    col = nb.mixc(nb.mr(fine, 0.5, 0.7, 0.0, 0.3), col, nb.multc(base, (0.82, 0.75, 0.66)))
    knot = nb.mr(nb.noise(5.0, 2, 0.5, aniso=tuple(a * 0.06 for a in an)), 0.70, 0.74)
    col = nb.mixc(nb.m('MULTIPLY', knot, 0.8), col, (0.12, 0.06, 0.025))
    endg = nb.mr(nb.m('ABSOLUTE', along_n), 0.6, 0.85)                      # end grain faces
    col = nb.mixc(nb.m('MULTIPLY', endg, 0.55), col, nb.multc(col, (0.55, 0.45, 0.38)))
    weather = nb.mr(nb.noise(1.6, 5, 0.6), 0.25, 0.75, grey * 0.4, grey)
    col = nb.mixc(weather, col, (0.24, 0.215, 0.18))
    col = nb.mixc(nb.m('MULTIPLY', nb.edge_mask(0.005), 0.45), col, (0.16, 0.12, 0.08))
    smudge = mul(nb, nb.mr(nb.noise(3.0, 6, 0.6), 0.55, 0.75), nb.mr(z, 0.11, 0.0), 0.7)
    col = nb.mixc(smudge, col, (0.05, 0.04, 0.03))
    stain = mul(nb, nb.mr(nb.noise(2.2, 6, 0.65, dist=0.6), 0.58, 0.68), 0.45)     # water / oil stains
    col = nb.mixc(stain, col, nb.multc(col, (0.55, 0.5, 0.45)))
    col = common_dirt(nb, col, cav=0.6, dust=0.25)
    rough = nb.mr(grain, 0.3, 0.7, 0.72, 0.9)
    metal = 0.0
    h = nb.m('ADD', nb.m('MULTIPLY', grain, 0.6), nb.m('MULTIPLY', fine, 0.4))
    if nails:
        xs, ys, ztop = nails
        dx = None
        for xi in xs:
            d = nb.m('ABSOLUTE', nb.m('SUBTRACT', x, xi))
            dx = d if dx is None else nb.m('MINIMUM', dx, d)
        dy = None
        for yi in ys:
            d = nb.m('ABSOLUTE', nb.m('SUBTRACT', y, yi))
            dy = d if dy is None else nb.m('MINIMUM', dy, d)
        r = nb.vm('LENGTH', nb.vec(dx, dy, 0.0))
        top = mul(nb, nb.mr(nz, 0.8, 0.95), nb.mr(z, ztop - 0.003, ztop - 0.001))
        head = mul(nb, nb.mr(r, 0.0042, 0.0034), top)
        ring = mul(nb, nb.mr(r, 0.0065, 0.0042), top)
        col = nb.mixc(nb.m('MULTIPLY', ring, 0.5), col, (0.06, 0.04, 0.025))
        col = nb.mixc(head, col, nb.mixc(nb.noise(200, 2), (0.10, 0.10, 0.10), (0.16, 0.08, 0.035)))
        rough = nb.mixf(head, rough, 0.55)
        metal = nb.m('MULTIPLY', head, 0.6)
        h = nb.m('SUBTRACT', h, nb.m('MULTIPLY', ring, 0.6))
    if hinges:
        W, L, z0s, hh = hinges
        corner = nb.m('MAXIMUM', mul(nb, nb.mr(nb.m('ABSOLUTE', y), L / 2 - 0.075, L / 2 - 0.073),
                                     nb.mr(nb.m('ABSOLUTE', nx), 0.7, 0.9)),
                      mul(nb, nb.mr(nb.m('ABSOLUTE', x), W / 2 - 0.075, W / 2 - 0.073),
                          nb.mr(nb.m('ABSOLUTE', ny), 0.7, 0.9)))
        zz = None
        for z0 in z0s:
            for zc in (z0 + 0.035, z0 + hh - 0.035):
                b = band(nb, z, zc, 0.022)
                zz = b if zz is None else nb.m('MAXIMUM', zz, b)
        plate = mul(nb, corner, zz)
        steel = nb.mixc(nb.noise(60, 3), (0.42, 0.43, 0.44), (0.30, 0.30, 0.30))
        col = nb.mixc(plate, col, steel)
        rough = nb.mixf(plate, rough, 0.4)
        metal = nb.m('MAXIMUM', metal, nb.m('MULTIPLY', plate, 0.9)) if not isinstance(metal, float) else \
            nb.m('MULTIPLY', plate, 0.9)
        h = nb.m('ADD', h, nb.m('MULTIPLY', plate, 0.8))
    nrm = nb.bump(h, 0.35, 0.0015)
    return finalize_material(nb, col, rough, metal, nrm)


def chip_block(name):
    """Pressed wood-chip pallet blocks: mottled chips, a burnt stamp on the outer middle blocks."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    chips = nb.noise(90.0, 3, 0.5, dist=0.8)
    big = nb.noise(25.0, 3, 0.5)
    col = nb.mixc(nb.mr(chips, 0.35, 0.65), (0.30, 0.20, 0.10), (0.50, 0.37, 0.20))
    col = nb.mixc(nb.mr(big, 0.6, 0.72, 0.0, 0.35), col, (0.20, 0.13, 0.06))
    stamp = mul(nb, nb.mr(nb.m('ABSOLUTE', nx), 0.8, 0.95), rect(nb, y, z, 0.0, 0.061, 0.045, 0.022, 0.002))
    lines = nb.m('GREATER_THAN', wnoise(nb, nb.vec(nb.m('FLOOR', nb.m('MULTIPLY', y, 90)),
                                                   nb.m('FLOOR', nb.m('MULTIPLY', z, 160)), 2.0)), 0.45)
    col = nb.mixc(mul(nb, stamp, nb.m('ADD', 0.25, nb.m('MULTIPLY', lines, 0.7))), col, (0.025, 0.012, 0.006))
    smudge = mul(nb, nb.mr(nb.noise(3.0, 6, 0.6), 0.5, 0.75), 0.6)
    col = nb.mixc(smudge, col, (0.07, 0.05, 0.035))
    col = common_dirt(nb, col, cav=0.6, dust=0.2)
    nrm = nb.bump(chips, 0.5, 0.0015)
    return finalize_material(nb, col, nb.mr(chips, 0.3, 0.7, 0.8, 0.92), 0.0, nrm)


def klt_plastic(name, W, L, H, color, shell=False):
    """Polypropylene small load carrier: vertical ribs on the outer walls, a dark handle slot
    on both short (+-Y) walls, label holders with a barcode card on both long (+-X) walls,
    scuffs, dust and floor dirt.  Object space = the bin's own frame (built at the origin)."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    ax, ay = nb.m('ABSOLUTE', x), nb.m('ABSOLUTE', y)
    outx = mul(nb, nb.mr(ax, W / 2 - 0.007, W / 2 - 0.004), nb.mr(nb.m('ABSOLUTE', nx), 0.5, 0.8))
    outy = mul(nb, nb.mr(ay, L / 2 - 0.007, L / 2 - 0.004), nb.mr(nb.m('ABSOLUTE', ny), 0.5, 0.8))
    zone = mul(nb, nb.mr(z, 0.016, 0.022), nb.mr(z, H - 0.030, H - 0.034))
    pitch = 0.034

    def ribs(c):
        return nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', c, pitch)), 0.5)), 0.13, 0.09)
    lw, lh = min(0.15, L * 0.36), 0.054
    lz = H - 0.024 - 0.012 - lh / 2 - 0.012 if H < 0.2 else H * 0.62
    lab_reg = rect(nb, y, z, 0.0, lz, lw / 2 + 0.010, lh / 2 + 0.010, 0.002)
    hw, hh = min(0.11, W * 0.40) / 2, 0.015
    hz = H - 0.045
    hd = rrect_sdf(nb, x, nb.m('SUBTRACT', z, hz), hw, hh, 0.012)
    h_reg = nb.mr(hd, 0.016, 0.012)
    height = nb.m('ADD', mul(nb, outx, ribs(y), nb.m('SUBTRACT', 1.0, lab_reg)),
                  mul(nb, outy, ribs(x), nb.m('SUBTRACT', 1.0, h_reg)))
    height = nb.m('MULTIPLY', height, zone)
    if H > 0.2:                                                     # stiffening band on tall bins
        hb = band(nb, z, H * 0.36, 0.006, 0.002)
        height = nb.m('MAXIMUM', height, mul(nb, hb, nb.m('MAXIMUM', outx, outy)))
    ring = nb.m('SUBTRACT', lab_reg, rect(nb, y, z, 0.0, lz, lw / 2 + 0.003, lh / 2 + 0.003, 0.0015))
    height = nb.m('ADD', height, mul(nb, outx, ring, 0.9))
    lip = nb.m('SUBTRACT', nb.mr(hd, 0.008, 0.005), nb.mr(hd, 0.0015, 0.0))
    height = nb.m('ADD', height, mul(nb, outy, lip, 0.8))
    slot = mul(nb, nb.mr(hd, 0.0012, -0.0003), nb.mr(ay, L / 2 - 0.03, L / 2 - 0.02))
    height = nb.m('SUBTRACT', height, nb.m('MULTIPLY', slot, 0.5))

    tone = nb.mr(nb.noise(2.0, 3, 0.5), 0.3, 0.7, 0.93, 1.05)
    col = nb.multc(color, nb.vec(tone, tone, tone))
    scratch = mul(nb, nb.mr(nb.noise(1.0, 4, 0.6, aniso=(40, 40, 260)), 0.66, 0.72),
                  nb.mr(nb.noise(3.0, 3), 0.5, 0.7), nb.m('MAXIMUM', outx, outy))
    lighter = tuple(min(1.0, c * 1.5 + 0.05) for c in color)
    col = nb.mixc(nb.m('MULTIPLY', scratch, 0.35), col, lighter)
    col = nb.mixc(nb.m('MULTIPLY', nb.edge_mask(0.003), 0.5), col, lighter)
    floor = mul(nb, nb.mr(nz, 0.8, 0.95), nb.mr(z, 0.03, 0.02), nb.mr(nb.noise(4.0, 6, 0.6), 0.4, 0.75, 0.2, 0.8))
    col = nb.mixc(floor, col, (0.05, 0.045, 0.04))
    col = common_dirt(nb, col, cav=0.45, dust=0.22)
    rough = nb.m('ADD', 0.48, nb.mr(nb.noise(9.0, 4), 0.3, 0.7, -0.04, 0.05))
    rough = nb.mixf(scratch, rough, 0.62)
    face = nb.m('MULTIPLY', outx, 1.0)
    u = nb.m('MULTIPLY', y, nb.m('SIGN', x))
    col, rough, lab = paper_label(nb, col, rough, u, z, face, 0.0, lz, lw - 0.012, lh - 0.010, seed=W * 10 + H)
    col = nb.mixc(slot, col, (0.008, 0.008, 0.009))
    nrm = nb.bump(height, 0.9, 0.0022)
    nrm = nb.bump(nb.noise(700, 2), 0.06, 0.0002, nrm)
    return finalize_material(nb, col, rough, 0.0, nrm)


def carton(name, W, L, H):
    """Brown corrugated regular slotted carton: tape along L over the flap seam and down both
    ends, shipping label on +X, printed handling block on -X, glue flap, crushed edges."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    kraft = (0.30, 0.165, 0.075)
    tone = nb.mr(nb.noise(2.5, 4, 0.5), 0.3, 0.7, 0.92, 1.06)
    col = nb.multc(kraft, nb.vec(tone, tone, tone))
    flutes = nb.m('SINE', nb.m('MULTIPLY', nb.m('ADD', x, y), 2 * pi / 0.008))
    top = nb.mr(nz, 0.8, 0.95)
    endf = nb.mr(nb.m('ABSOLUTE', ny), 0.8, 0.95)
    sidef = nb.mr(nb.m('ABSOLUTE', nx), 0.8, 0.95)
    tape_w = 0.024
    tape = nb.m('MAXIMUM', mul(nb, top, band(nb, x, 0.0, tape_w, 0.0006)),
                mul(nb, endf, band(nb, x, 0.0, tape_w, 0.0006), nb.mr(z, H - 0.072, H - 0.070)))
    seam = mul(nb, top, band(nb, x, 0.0, 0.0012, 0.0006))
    crease = mul(nb, nb.m('MAXIMUM', endf, sidef), band(nb, z, H - 0.004, 0.0015, 0.001))
    glue = mul(nb, sidef, nb.mr(y, L / 2 - 0.035, L / 2 - 0.033), nb.m('GREATER_THAN', x, 0.0))
    col = nb.mixc(nb.m('MULTIPLY', glue, 0.35), col, nb.multc(kraft, (0.75, 0.7, 0.65)))
    col = nb.mixc(nb.m('MULTIPLY', nb.m('MAXIMUM', seam, crease), 0.6), col, (0.08, 0.045, 0.02))
    tcol = nb.mixc(nb.mr(nb.noise(1.0, 3, 0.5, aniso=(3, 40, 3)), 0.4, 0.7), (0.30, 0.17, 0.055), (0.38, 0.22, 0.07))
    col = nb.mixc(nb.m('MULTIPLY', tape, 0.92), col, tcol)
    # printed handling block (generic, no marks): two arrows + text bars on -X
    u2 = nb.m('MULTIPLY', y, -1.0)
    pr = mul(nb, nb.mr(nx, -0.8, -0.95), rect(nb, u2, z, -L * 0.22, H * 0.70, min(0.06, L * 0.12), 0.006, 0.0006))
    pr2 = mul(nb, nb.mr(nx, -0.8, -0.95), rect(nb, u2, z, -L * 0.22, H * 0.62, min(0.045, L * 0.09), 0.004, 0.0006))
    arr = None
    for k in (-1, 1):
        cx = L * 0.22 + k * 0.022
        shaft = rect(nb, u2, z, cx, H * 0.62, 0.004, 0.018, 0.0006)
        head = mul(nb, band(nb, z, H * 0.62 + 0.024, 0.008, 0.0006),
                   nb.m('LESS_THAN', nb.m('ABSOLUTE', nb.m('SUBTRACT', u2, cx)),
                        nb.m('SUBTRACT', H * 0.62 + 0.033, z)))
        a = nb.m('MAXIMUM', shaft, head)
        arr = a if arr is None else nb.m('MAXIMUM', arr, a)
    arr = mul(nb, arr, nb.mr(nx, -0.8, -0.95))
    ink = nb.m('MAXIMUM', nb.m('MAXIMUM', pr, pr2), arr)
    col = nb.mixc(nb.m('MULTIPLY', ink, 0.85), col, (0.03, 0.025, 0.022))
    rough = nb.mixf(tape, 0.88, 0.42)
    col, rough, lab = paper_label(nb, col, rough, y, z, nb.mr(nx, 0.8, 0.95), L * 0.18, H * 0.55,
                                  min(0.10, L * 0.32), min(0.07, H * 0.32), seed=L * 7 + H)
    crushed = nb.m('MULTIPLY', nb.edge_mask(0.006, 0.15, 0.5), nb.mr(nb.noise(20, 4), 0.4, 0.7))
    col = nb.mixc(nb.m('MULTIPLY', crushed, 0.5), col, (0.42, 0.29, 0.16))
    col = common_dirt(nb, col, cav=0.5, dust=0.35)
    h = nb.m('ADD', nb.m('MULTIPLY', flutes, 0.15), nb.m('MULTIPLY', nb.noise(3.0, 4), 0.6))
    h = nb.m('SUBTRACT', h, nb.m('MULTIPLY', nb.m('MAXIMUM', seam, crease), 0.8))
    h = nb.m('ADD', h, nb.m('MULTIPLY', tape, 0.3))
    nrm = nb.bump(h, 0.25, 0.002)
    nrm = nb.bump(nb.mr(nb.noise(1.0, 3, 0.5, aniso=(3, 25, 3)), 0.3, 0.7), nb.m('MULTIPLY', tape, 0.15), 0.001, nrm)
    return finalize_material(nb, col, rough, 0.0, nrm)


def film_load(name, W, L, z0, cw, cl, ch, layers):
    """Stretch-wrapped carton block: carton seams, tapes and labels under a glossy film with
    haze bands and wrinkles; the film folds 50 mm over the top edge."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    gx = nb.m('DIVIDE', nb.m('ADD', x, W / 2), cw)
    gy = nb.m('DIVIDE', nb.m('ADD', y, L / 2), cl)
    gz = nb.m('DIVIDE', nb.m('SUBTRACT', z, z0), ch)
    fx, fy, fz = (nb.m('FRACT', g) for g in (gx, gy, gz))
    cell = nb.vec(nb.m('FLOOR', gx), nb.m('FLOOR', gy), nb.m('FLOOR', gz))
    top = nb.mr(nz, 0.8, 0.95)
    sidex = nb.mr(nb.m('ABSOLUTE', nx), 0.7, 0.9)
    sidey = nb.mr(nb.m('ABSOLUTE', ny), 0.7, 0.9)

    def seam(f, size):
        return nb.mr(nb.m('MINIMUM', f, nb.m('SUBTRACT', 1.0, f)), 0.006 / size, 0.002 / size)
    seams = nb.m('MAXIMUM', nb.m('MULTIPLY', seam(fz, ch), nb.m('SUBTRACT', 1.0, top)),
                 nb.m('MAXIMUM', nb.m('MULTIPLY', seam(fy, cl), nb.m('MAXIMUM', sidex, top)),
                      nb.m('MULTIPLY', seam(fx, cw), nb.m('MAXIMUM', sidey, top))))
    kraft = (0.30, 0.165, 0.075)
    t = nb.mr(wnoise(nb, cell), 0.0, 1.0, 0.88, 1.08)
    col = nb.multc(kraft, nb.vec(t, t, t))
    tape = nb.m('MAXIMUM', mul(nb, top, band(nb, nb.m('MULTIPLY', nb.m('SUBTRACT', fy, 0.5), cl), 0.0, 0.024)),
                mul(nb, sidex, band(nb, nb.m('MULTIPLY', nb.m('SUBTRACT', fy, 0.5), cl), 0.0, 0.024),
                    nb.mr(fz, 1.0 - 0.07 / ch, 1.0 - 0.065 / ch)))
    col = nb.mixc(tape, col, (0.33, 0.19, 0.06))
    col = nb.mixc(seams, col, (0.02, 0.012, 0.006))
    # labels: on the +-Y faces (carton 400 wide) and the +-X faces, some cartons only
    has = nb.m('GREATER_THAN', wnoise(nb, nb.vm('ADD', cell, (0.3, 0.7, 0.1))), 0.35)
    ux = nb.m('MULTIPLY', nb.m('SUBTRACT', fx, 0.5), cw)
    uy = nb.m('MULTIPLY', nb.m('SUBTRACT', fy, 0.5), cl)
    vz = nb.m('MULTIPLY', nb.m('SUBTRACT', fz, 0.5), ch)
    rough = 0.85
    col, rough, l1 = paper_label(nb, col, rough, ux, vz, nb.m('MULTIPLY', sidey, has), 0.08, 0.01, 0.10, 0.07, seed=5.0)
    col, rough, l2 = paper_label(nb, col, rough, uy, vz, nb.m('MULTIPLY', sidex, has), -0.03, 0.01, 0.10, 0.07, seed=9.0)
    # film: sides everywhere, top within 50 mm of the edge
    ztop = z0 + ch * layers
    rim = nb.m('MAXIMUM', nb.mr(nb.m('ABSOLUTE', x), W / 2 - 0.06, W / 2 - 0.045),
               nb.mr(nb.m('ABSOLUTE', y), L / 2 - 0.06, L / 2 - 0.045))
    film = nb.m('MAXIMUM', nb.m('SUBTRACT', 1.0, top), nb.m('MULTIPLY', rim, top))
    bands = nb.mr(nb.noise(1.0, 5, 0.6, aniso=(0.6, 0.6, 7.0)), 0.35, 0.75, 0.05, 0.28)
    thick = nb.m('MAXIMUM', nb.mr(z, z0 + 0.15, z0 + 0.02), nb.mr(z, ztop - 0.15, ztop - 0.01))
    haze = nb.m('MULTIPLY', film, nb.m('ADD', bands, nb.m('MULTIPLY', thick, 0.08)))
    col = nb.mixc(nb.m('MULTIPLY', haze, 0.7), col, (0.50, 0.52, 0.54))
    wr = nb.noise(1.0, 6, 0.7, aniso=(5.0, 5.0, 55.0))
    rough = nb.mixf(film, rough, nb.mr(wr, 0.3, 0.7, 0.10, 0.28))
    col = common_dirt(nb, col, cav=0.35, dust=0.2)
    h = nb.m('SUBTRACT', nb.m('MULTIPLY', wr, nb.m('MULTIPLY', film, 0.8)), nb.m('MULTIPLY', seams, 0.7))
    nrm = nb.bump(h, 0.3, 0.0015)
    return finalize_material(nb, col, rough, 0.0, nrm)


def glt_wall(name, W, L, H, z0):
    """Large-load-carrier walls: rib grid on the outer faces, access flap outline on +X,
    label holders on the short walls, fold line above the base, scuffs."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    ax, ay = nb.m('ABSOLUTE', x), nb.m('ABSOLUTE', y)
    outx = mul(nb, nb.mr(ax, W / 2 - 0.004, W / 2 - 0.001), nb.mr(nb.m('ABSOLUTE', nx), 0.6, 0.85))
    outy = mul(nb, nb.mr(ay, L / 2 - 0.004, L / 2 - 0.001), nb.mr(nb.m('ABSOLUTE', ny), 0.6, 0.85))
    rib = lambda c, p: nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', c, p)), 0.5)), 0.43, 0.46)
    grid = nb.m('MAXIMUM', nb.m('MAXIMUM', mul(nb, outx, rib(y, 0.10)), mul(nb, outy, rib(x, 0.10))),
                mul(nb, nb.m('MAXIMUM', outx, outy), rib(nb.m('SUBTRACT', z, z0), 0.125)))
    door = mul(nb, nb.mr(x, W / 2 - 0.003, W / 2 + 0.002), rect(nb, y, z, 0.0, 0.735, 0.30, 0.185, 0.002))
    door_edge = nb.m('SUBTRACT', door, mul(nb, nb.mr(x, W / 2 - 0.003, W / 2 + 0.002),
                                           rect(nb, y, z, 0.0, 0.735, 0.292, 0.177, 0.002)))
    grid = nb.m('MULTIPLY', grid, nb.m('SUBTRACT', 1.0, door))
    fold = mul(nb, nb.m('MAXIMUM', outx, outy), band(nb, z, z0 + 0.028, 0.003, 0.002))
    h = nb.m('SUBTRACT', nb.m('ADD', grid, nb.m('MULTIPLY', door, 0.4)), nb.m('MAXIMUM', door_edge, fold))
    col = nb.mixc(nb.mr(nb.noise(1.5, 3), 0.3, 0.7), (0.15, 0.16, 0.165), (0.20, 0.205, 0.21))
    col = nb.mixc(nb.m('MULTIPLY', nb.m('MAXIMUM', door_edge, fold), 0.8), col, (0.03, 0.03, 0.03))
    scratch = mul(nb, nb.mr(nb.noise(1.0, 4, 0.6, aniso=(4, 4, 140)), 0.6, 0.68), nb.mr(nb.noise(2.5, 3), 0.45, 0.65))
    col = nb.mixc(nb.m('MULTIPLY', scratch, 0.5), col, (0.36, 0.37, 0.38))
    col = nb.mixc(nb.m('MULTIPLY', nb.edge_mask(0.004), 0.5), col, (0.34, 0.35, 0.36))
    rough = nb.mixf(scratch, 0.55, 0.65)
    u = nb.m('MULTIPLY', x, nb.m('SIGN', y))
    col, rough, lab = paper_label(nb, col, rough, u, z, outy, 0.0, 0.80, 0.21, 0.13, seed=4.0)
    col = common_dirt(nb, col, cav=0.5, dust=0.3)
    nrm = nb.bump(h, 0.8, 0.004)
    nrm = nb.bump(nb.noise(500, 2), 0.06, 0.0003, nrm)
    return finalize_material(nb, col, rough, 0.0, nrm)


def build_materials():
    MATS.clear()
    epal_tops = top_boards(1.2, (0.145, 0.10, 0.145, 0.10, 0.145))
    ind_tops = top_boards(1.2, (0.145, 0.10, 0.10, 0.145, 0.10, 0.10, 0.145))

    def nails(W, tops):
        xs = [xc + d for xc in (-(W / 2 - 0.05), 0.0, W / 2 - 0.05) for d in (-0.035, 0.0, 0.035)]
        ys = [yc + d for yc, w in tops for d in ((-0.04, 0.04) if w > 0.12 else (0.0,))]
        return xs, ys, PH
    MATS.update({
        'EpalTop': wood('EpalTop', 'x', nails(0.8, epal_tops)),
        'IndTop': wood('IndTop', 'x', nails(1.0, ind_tops)),
        'Runner': wood('Runner', 'y'),
        'Block': chip_block('Block'),
        'CollarX': wood('CollarX', 'x', hinges=(0.8, 1.2, [PH + 0.2 * k for k in range(3)], 0.195),
                        tone=(0.52, 0.34, 0.16), grey=0.15),
        'CollarY': wood('CollarY', 'y', hinges=(0.8, 1.2, [PH + 0.2 * k for k in range(3)], 0.195),
                        tone=(0.52, 0.34, 0.16), grey=0.15),
        'GltWall': glt_wall('GltWall', 1.0, 1.2, 0.975, 0.17),
        'GltBase': M.plastic('GltBase', color=(0.028, 0.029, 0.031), rough=0.6, grain=0.12),
        'GltLatch': M.plastic('GltLatch', color=(0.012, 0.012, 0.013), rough=0.45),
        'Zinc': M.galvanised('Zinc', rough=0.45),
        'ZincFloor': M.galvanised('ZincFloor', rough=0.55),
        'Lid': M.plastic('Lid', color=(0.12, 0.125, 0.13), rough=0.55, grain=0.1),
        'Strap': M.plastic('Strap', color=(0.02, 0.20, 0.06), rough=0.35, grain=0.05),
        'Film': film_load('Film', 0.8, 1.2, PH, 0.4, 0.3, 0.25, 4),
    })
    for (L, W, H) in KLT_SIZES:
        n = klt_name(L, W, H)
        MATS[n] = klt_plastic(n, W / 1000, L / 1000, H / 1000, GREY)
    for (L, W, H) in CARTON_SIZES:
        n = f'CardboardCarton_{L}x{W}x{H}'
        MATS[n] = carton(n, W / 1000, L / 1000, H / 1000)


def build_variant_materials():
    """Blue twins of the small-load-carrier plastics (the WarehouseLoadUnits_Blue colour set)."""
    for (L, W, H) in KLT_SIZES:
        n = klt_name(L, W, H)
        VARIANT[n] = klt_plastic(n + '_Blue', W / 1000, L / 1000, H / 1000, BLUE)


# =============================================================================
# geometry helpers
# =============================================================================

KLT_SIZES = [(300, 200, 147), (400, 300, 147), (400, 300, 280), (600, 400, 280)]
CARTON_SIZES = [(300, 200, 200), (400, 300, 250), (600, 400, 300)]


def klt_name(L, W, H):
    return f'SmallLoadCarrier_{L}x{W}x{H}'


def top_boards(L, widths):
    """[(y centre, width)] of deck boards across a length L with equal gaps."""
    gap = (L - sum(widths)) / (len(widths) - 1)
    out, y = [], -L / 2
    for w in widths:
        out.append((y + w / 2, w))
        y += w + gap
    return out


def bx(p, x0, x1, y0, y1, z0, z1, mat):
    p.box_minmax((min(x0, x1), min(y0, y1), min(z0, z1)), (max(x0, x1), max(y0, y1), max(z0, z1)), mat)


def mk(name, fn, bevel=0.003, seg=1, weight=1.0, loc=None, cutters=None):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        cs = []
        for c in cutters:
            cp = Part(name + '_cut')
            c(cp)
            cs.append(cp.build({'Cut': MATS['Lid']}))
        K.boolean(ob, cs)
    finish(ob, bevel, seg, 40, 40)
    if loc:
        ob.location = loc
    WEIGHTS[ob.name] = weight
    return ob


def proxy(name, fn, weight):
    """A low LOD mesh (proxy): faceted, no bevel; its maps come from transfer_to_proxies."""
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, 0, 1, 40, 30)
    WEIGHTS[ob.name] = weight
    return ob


def ring(W, L, inset, z, r, seg):
    rr = max(r - inset, 0.0015) if inset >= 0 else r - inset
    return [V((u, v, z)) for u, v in rrect(W - 2 * inset, L - 2 * inset, rr, seg)]


def ring4(W, L, inset, z):
    a, b = W / 2 - inset, L / 2 - inset
    return [V((a, b, z)), V((-a, b, z)), V((-a, -b, z)), V((a, -b, z))]


W1, W2 = 0.30, 0.18                          # proxy texel weights (LOD1, LOD2)


class Unit:
    """One kit piece: LOD0 source parts, LOD1 / LOD2 proxies and their ray reach."""

    def __init__(self, name, parts, lod1, lod2, reach, sockets, pin=None, seen_through=False):
        self.piece = KM.Piece(name, 'fixed', {None: parts}, levels=3, sockets=sockets)
        self.name = name
        self.parts = parts
        self.proxies = [lod1, lod2]
        self.reach = reach
        self.pin = pin                      # (part, reference part): see pin_uvs
        self.seen_through = seen_through    # proxy misses read as the floor seen through a mesh


# =============================================================================
# pallets
# =============================================================================

def pallet_parts(tag, W, L, tops, top_mat, weight=0.8, light=False):
    cols = [(-(W / 2 - 0.05), 0.10), (0.0, 0.145), (W / 2 - 0.05, 0.10)]
    rows = [-(L / 2 - 0.0725), 0.0, L / 2 - 0.0725]
    lb = 0.0 if light else 0.003

    def bottom(p):
        for xc, w in cols:
            bx(p, xc - w / 2, xc + w / 2, -L / 2, L / 2, 0.0, 0.022, 'Runner')

    def blocks(p):
        for xc, w in cols:
            for yc in rows:
                bx(p, xc - w / 2, xc + w / 2, yc - 0.0725, yc + 0.0725, 0.022, 0.100, 'Block')

    def stringers(p):
        for xc, w in cols:
            bx(p, xc - w / 2, xc + w / 2, -L / 2, L / 2, 0.100, 0.122, 'Runner')

    def deck(p):
        for yc, w in tops:
            bx(p, -W / 2, W / 2, yc - w / 2, yc + w / 2, 0.122, PH, top_mat)
    return [mk(f'{tag}_Bottom', bottom, lb, weight=weight * 0.8), mk(f'{tag}_Blocks', blocks, lb, weight=weight),
            mk(f'{tag}_Stringers', stringers, lb, weight=weight * 0.8),
            mk(f'{tag}_Deck', deck, 0.003, weight=weight)]


def pallet_lod1(p, W, L):
    for xc, w in [(-(W / 2 - 0.05), 0.10), (0.0, 0.145), (W / 2 - 0.05, 0.10)]:
        bx(p, xc - w / 2, xc + w / 2, -L / 2, L / 2, 0.0, 0.022, 'Lid')
        for yc in (-(L / 2 - 0.0725), 0.0, L / 2 - 0.0725):
            bx(p, xc - w / 2, xc + w / 2, yc - 0.0725, yc + 0.0725, 0.022, 0.100, 'Lid')
    bx(p, -W / 2, W / 2, -L / 2, L / 2, 0.100, PH, 'Lid')


def build_pallet(name, W, L, widths, top_mat):
    tops = top_boards(L, widths)
    parts = pallet_parts(name, W, L, tops, top_mat)
    l1 = proxy(f'{name}_P1', lambda p: pallet_lod1(p, W, L), W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -W / 2, W / 2, -L / 2, L / 2, 0, PH, 'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.16, W * 0.7), {'TopSocket': (0, 0, PH)})


# =============================================================================
# small load carriers (KLT type)
# =============================================================================

def klt_profile(W, L, H, seg, r=0.018):
    """Closed cup solid: foot, wall (draft), rim band, rim top, inner wall, floor."""
    return [ring(W, L, 0.008, 0.0, r, seg), ring(W, L, 0.008, 0.008, r, seg),
            ring(W, L, 0.003, 0.010, r, seg), ring(W, L, 0.0028, 0.020, r, seg),
            ring(W, L, 0.0006, H - 0.026, r, seg), ring(W, L, -0.002, H - 0.024, r, seg),
            ring(W, L, -0.002, H - 0.003, r, seg), ring(W, L, 0.0005, H, r, seg),
            ring(W, L, 0.010, H, r, seg), ring(W, L, 0.0105, H - 0.012, r, seg),
            ring(W, L, 0.012, 0.014, r, seg)]


def klt_shell(p, W, L, H, mat):
    """Outside only (stacked in a pallet load): 4 bands of 8 points, open top and bottom."""
    rings = [ring(W, L, 0.006, 0.0, 0.018, 1), ring(W, L, 0.003, 0.010, 0.018, 1),
             ring(W, L, 0.0006, H - 0.026, 0.018, 1), ring(W, L, -0.002, H - 0.024, 0.018, 1),
             ring(W, L, -0.002, H, 0.018, 1)]
    p.loft(rings, mat, cap0=False, cap1=False)


def build_klt(L, W, H):
    name = klt_name(L, W, H)
    w, l, h = W / 1000, L / 1000, H / 1000
    hw, hh, hz = min(0.11, w * 0.40), 0.030, h - 0.045

    def body(p):
        p.loft(klt_profile(w, l, h, 3), name)

    def cutter(sign):
        def f(p):
            prof = rrect(hw, hh, 0.012, 3, 0.0, hz)
            a0, a1 = (l / 2 - 0.03, l / 2 + 0.02) if sign > 0 else (-l / 2 - 0.02, -l / 2 + 0.03)
            p.extrude(prof, 'Y', a0, a1, 'Cut')
        return f
    parts = [mk(name, body, 0.0015, 1, weight=1.5, cutters=[cutter(1), cutter(-1)])]

    def lod1(p):
        rings = [ring(w, l, 0.008, 0.0, 0.018, 1), ring(w, l, 0.002, 0.012, 0.018, 1),
                 ring(w, l, -0.001, h, 0.018, 1), ring(w, l, 0.0105, h, 0.018, 1),
                 ring(w, l, 0.012, 0.014, 0.018, 1)]
        p.loft(rings, 'Lid')
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -w / 2, w / 2, -l / 2, l / 2, 0, h, 'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.04, h + 0.05), {'TopSocket': (0, 0, h)})


# =============================================================================
# cartons
# =============================================================================

def build_carton(L, W, H):
    name = f'CardboardCarton_{L}x{W}x{H}'
    w, l, h = W / 1000, L / 1000, H / 1000

    def body(p):
        # slight bulge: rings at the bottom, middle (+3 mm) and top
        rings = [ring(w, l, 0.0, 0.0, 0.004, 2), ring(w, l, -0.0025, h * 0.5, 0.006, 2),
                 ring(w, l, 0.0, h, 0.004, 2)]
        p.loft(rings, name)
    parts = [mk(name, body, 0.003, 2, weight=1.2)]
    box = lambda p: bx(p, -w / 2 - 0.0025, w / 2 + 0.0025, -l / 2 - 0.0025, l / 2 + 0.0025, 0, h, 'Lid')  # noqa
    l1 = proxy(f'{name}_P1', box, W1)
    l2 = proxy(f'{name}_P2', box, W2)
    return Unit(name, parts, l1, l2, (0.04, 0.04), {'TopSocket': (0, 0, h)})


# =============================================================================
# large load carrier (GLT type)
# =============================================================================

def build_glt():
    name, W, L, H, Z0 = 'LargeLoadCarrier', 1.0, 1.2, 0.975, 0.17
    cols, rows = (-0.42, 0.0, 0.42), (-0.51, 0.0, 0.51)

    def base(p):
        for xc in cols:
            bx(p, xc - 0.08, xc + 0.08, -L / 2 + 0.01, L / 2 - 0.01, 0.0, 0.03, 'GltBase')
            for yc in rows:
                bx(p, xc - 0.08, xc + 0.08, yc - 0.09, yc + 0.09, 0.03, 0.13, 'GltBase')

    def deck(p):
        p.loft([ring(W, L, 0.002, 0.13, 0.03, 2), ring(W, L, 0.0, 0.14, 0.03, 2), ring(W, L, 0.0, Z0, 0.03, 2)],
               'GltBase')

    t = 0.035

    def walls(p):
        bx(p, W / 2 - t, W / 2, -L / 2 + 0.03, L / 2 - 0.03, Z0, H - 0.035, 'GltWall')
        bx(p, -W / 2, -W / 2 + t, -L / 2 + 0.03, L / 2 - 0.03, Z0, H - 0.035, 'GltWall')
        bx(p, -W / 2 + 0.03, W / 2 - 0.03, L / 2 - t, L / 2, Z0, H - 0.035, 'GltWall')
        bx(p, -W / 2 + 0.03, W / 2 - 0.03, -L / 2, -L / 2 + t, Z0, H - 0.035, 'GltWall')
        for zc in (0.43, 0.70):                                          # real stiffening ribs
            bx(p, W / 2 - 0.01, W / 2 + 0.008, -L / 2 + 0.06, -0.31, zc - 0.012, zc + 0.012, 'GltWall')
            bx(p, W / 2 - 0.01, W / 2 + 0.008, 0.31, L / 2 - 0.06, zc - 0.012, zc + 0.012, 'GltWall')
            bx(p, -W / 2 - 0.008, -W / 2 + 0.01, -L / 2 + 0.06, L / 2 - 0.06, zc - 0.012, zc + 0.012, 'GltWall')
            for s in (1, -1):
                bx(p, -W / 2 + 0.06, W / 2 - 0.06, s * (L / 2 - 0.01), s * (L / 2 + 0.008),
                   zc - 0.012, zc + 0.012, 'GltWall')
        bx(p, W / 2 - 0.004, W / 2 + 0.006, -0.30, 0.30, 0.55, 0.92, 'GltWall')     # access flap

    def frame(p):
        for sx in (1, -1):
            for sy in (1, -1):
                bx(p, sx * (W / 2 + 0.004), sx * (W / 2 - 0.06), sy * (L / 2 + 0.004), sy * (L / 2 - 0.06),
                   Z0, H - 0.035, 'GltBase')
        p.loft([ring(W, L, -0.006, H - 0.035, 0.02, 2), ring(W, L, -0.006, H, 0.02, 2),
                ring(W, L, 0.045, H, 0.02, 2), ring(W, L, 0.045, H - 0.035, 0.02, 2)], 'GltBase', cap0=False,
               cap1=False)
        p.loft([ring(W, L, -0.006, H - 0.035, 0.02, 2), ring(W, L, 0.045, H - 0.035, 0.02, 2)], 'GltBase',
               cap0=False, cap1=False)
        for yc in (-0.22, 0.22):                                         # flap latches
            bx(p, W / 2 + 0.006, W / 2 + 0.022, yc - 0.035, yc + 0.035, 0.87, 0.91, 'GltLatch')
    parts = [mk(f'{name}_Base', base, 0.0, weight=0.5), mk(f'{name}_Deck', deck, 0.004, weight=0.6),
             mk(f'{name}_Walls', walls, 0.004, weight=0.7), mk(f'{name}_Frame', frame, 0.004, weight=0.7)]

    def lod1(p):
        for xc in cols:
            bx(p, xc - 0.08, xc + 0.08, -L / 2 + 0.01, L / 2 - 0.01, 0.0, 0.03, 'Lid')
            for yc in rows:
                bx(p, xc - 0.08, xc + 0.08, yc - 0.09, yc + 0.09, 0.03, 0.13, 'Lid')
        p.loft([ring(W, L, 0, 0.13, 0.03, 1), ring(W, L, -0.006, H, 0.03, 1), ring(W, L, t, H, 0.03, 1),
                ring(W, L, t, Z0, 0.03, 1)], 'Lid')
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -W / 2, W / 2, -L / 2, L / 2, 0, H, 'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.06, 0.9),
                {'ContentSocket': (0, 0, Z0), 'TopSocket': (0, 0, H)})


# =============================================================================
# wire-mesh box pallet
# =============================================================================

def angle_profile(a=0.04, t=0.004):
    return [(0, 0), (a, 0), (a, t), (t, t), (t, a), (0, a)]


def build_meshbox():
    name, W, L, H, ZF, ZT = 'MetalMeshBoxPallet', 0.835, 1.24, 0.97, 0.17, 0.93
    r, pitch = 0.0024, 0.05

    def frame(p):
        for sx in (1, -1):
            for sy in (1, -1):
                x0, y0 = sx * (W / 2 - 0.05), sy * (L / 2 - 0.05)
                p.loft([[V((x0 + u, y0 + v, 0.0)) for u, v in rrect(0.07, 0.08, 0.008, 1)],
                        [V((x0 + u, y0 + v, 0.12)) for u, v in rrect(0.09, 0.10, 0.008, 1)]], 'Zinc')
                prof = [(sx * (W / 2 - u), sy * (L / 2 - v)) for u, v in angle_profile(0.045, 0.005)]
                if sx * sy < 0:
                    prof.reverse()
                p.extrude(prof, 'Z', 0.12, H - 0.02, 'Zinc')
                xc, yc = sx * (W / 2 - 0.0225), sy * (L / 2 - 0.0225)
                p.loft([[V((xc + u, yc + v, H - 0.02)) for u, v in rrect(0.03, 0.03, 0.004, 1)],
                        [V((xc + u, yc + v, H)) for u, v in rrect(0.016, 0.016, 0.004, 1)]], 'Zinc')
        for s in (1, -1):                                                 # floor frame
            bx(p, s * W / 2, s * (W / 2 - 0.04), -L / 2 + 0.045, L / 2 - 0.045, 0.12, ZF, 'Zinc')
            bx(p, -W / 2 + 0.045, W / 2 - 0.045, s * L / 2, s * (L / 2 - 0.04), 0.12, ZF, 'Zinc')
            bx(p, s * W / 2, s * (W / 2 - 0.035), -L / 2 + 0.045, L / 2 - 0.045, ZT, H - 0.02, 'Zinc')
            bx(p, -W / 2 + 0.045, W / 2 - 0.045, s * L / 2, s * (L / 2 - 0.035), ZT, H - 0.02, 'Zinc')
        bx(p, W / 2, W / 2 - 0.03, -L / 2 + 0.045, L / 2 - 0.045, 0.555, 0.585, 'Zinc')   # gate divider
        for yc in (-L / 2 + 0.06, L / 2 - 0.06):                                          # gate sides
            bx(p, W / 2, W / 2 - 0.02, yc - 0.012, yc + 0.012, 0.585, ZT, 'Zinc')
        for yc in (-0.45, 0.45):                                                          # latches
            bx(p, W / 2 + 0.002, W / 2 + 0.018, yc - 0.03, yc + 0.03, ZT - 0.03, ZT + 0.01, 'Zinc')
        bx(p, -0.18, 0.18, -L / 2 - 0.003, -L / 2 + 0.001, 0.80, 0.90, 'Zinc')          # sign plate

    def floor(p):
        bx(p, -W / 2 + 0.035, W / 2 - 0.035, -L / 2 + 0.035, L / 2 - 0.035, ZF - 0.006, ZF, 'ZincFloor')

    def mesh(p):
        sec = K.circle(r, 3)
        xw = W / 2 - 0.012
        for s in (1, -1):
            n = int((L - 0.12) / pitch)
            for i in range(n + 1):
                yc = -n * pitch / 2 + i * pitch
                p.sweep([(s * xw, yc, ZF), (s * xw, yc, ZT)], sec, 'Zinc', cap=False)
            for zc in np.arange(ZF + 0.03, ZT, pitch):
                p.sweep([(s * (xw - 2 * r), -L / 2 + 0.03, zc), (s * (xw - 2 * r), L / 2 - 0.03, zc)], sec, 'Zinc',
                        cap=False)
            yw = L / 2 - 0.012
            n = int((W - 0.12) / pitch)
            for i in range(n + 1):
                xc = -n * pitch / 2 + i * pitch
                p.sweep([(xc, s * yw, ZF), (xc, s * yw, ZT)], sec, 'Zinc', cap=False)
            for zc in np.arange(ZF + 0.03, ZT, pitch):
                p.sweep([(-W / 2 + 0.03, s * (yw - 2 * r), zc), (W / 2 - 0.03, s * (yw - 2 * r), zc)], sec,
                        'Zinc', cap=False)
    parts = [mk(f'{name}_Frame', frame, 0.0, weight=0.8), mk(f'{name}_Floor', floor, 0.0, weight=0.5),
             mk(f'{name}_Mesh', mesh, 0.0, weight=0.22)]
    pin = (parts[2], parts[0])

    def lod1(p):
        for sx in (1, -1):
            for sy in (1, -1):
                x0, y0 = sx * (W / 2 - 0.05), sy * (L / 2 - 0.05)
                bx(p, x0 - 0.045, x0 + 0.045, y0 - 0.05, y0 + 0.05, 0, 0.12, 'Lid')
                bx(p, sx * W / 2, sx * (W / 2 - 0.045), sy * L / 2, sy * (L / 2 - 0.045), 0.12, H - 0.02, 'Lid')
        bx(p, -W / 2, W / 2, -L / 2, L / 2, 0.12, ZF, 'Lid')
        for s in (1, -1):
            bx(p, s * W / 2, s * (W / 2 - 0.035), -L / 2 + 0.045, L / 2 - 0.045, ZT, H - 0.02, 'Lid')
            bx(p, -W / 2 + 0.045, W / 2 - 0.045, s * L / 2, s * (L / 2 - 0.035), ZT, H - 0.02, 'Lid')
            for xo, sgn in ((W / 2 - 0.006, 1), (W / 2 - 0.018, -1)):     # mesh walls: outer + inner face
                v = [V((s * xo, -L / 2 + 0.045, ZF)), V((s * xo, L / 2 - 0.045, ZF)),
                     V((s * xo, L / 2 - 0.045, ZT)), V((s * xo, -L / 2 + 0.045, ZT))]
                f = [(0, 1, 2, 3)] if s * sgn > 0 else [(3, 2, 1, 0)]
                p.add(v, f, 'Lid', recalc=False)
            for yo, sgn in ((L / 2 - 0.006, 1), (L / 2 - 0.018, -1)):
                v = [V((-W / 2 + 0.045, s * yo, ZF)), V((W / 2 - 0.045, s * yo, ZF)),
                     V((W / 2 - 0.045, s * yo, ZT)), V((-W / 2 + 0.045, s * yo, ZT))]
                f = [(3, 2, 1, 0)] if s * sgn > 0 else [(0, 1, 2, 3)]
                p.add(v, f, 'Lid', recalc=False)
    def lod2(p):                              # open box: the floor stays visible from above
        p.loft([ring4(W, L, 0.0, 0.0), ring4(W, L, 0.0, H), ring4(W, L, 0.04, H), ring4(W, L, 0.04, ZF)], 'Lid')
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lod2, W2)
    return Unit(name, parts, l1, l2, (0.035, 0.06),
                {'ContentSocket': (0, 0, ZF), 'TopSocket': (0, 0, H)}, pin=pin, seen_through=True)


# =============================================================================
# pallet collar stack and pallet loads
# =============================================================================

EPAL = (0.145, 0.10, 0.145, 0.10, 0.145)


def build_collar():
    name, W, L, n = 'PalletCollar', 0.8, 1.2, 3
    tops = top_boards(L, EPAL)
    parts = pallet_parts(name, W, L, tops, 'EpalTop', weight=0.6, light=True)

    def boards(p):
        for k in range(n):
            z0 = PH + 0.2 * k
            for s in (1, -1):
                bx(p, s * W / 2, s * (W / 2 - 0.02), -L / 2, L / 2, z0, z0 + 0.195, 'CollarY')
                bx(p, -W / 2 + 0.02, W / 2 - 0.02, s * L / 2, s * (L / 2 - 0.02), z0, z0 + 0.195, 'CollarX')
    parts.append(mk(f'{name}_Collars', boards, 0.003, weight=0.65))
    top = PH + 0.2 * n - 0.005

    def lod1(p):
        pallet_lod1(p, W, L)
        p.loft([ring4(W, L, 0.0, PH), ring4(W, L, 0.0, top), ring4(W, L, 0.02, top), ring4(W, L, 0.02, PH)],
               'Lid', cap0=False, cap1=False)
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -W / 2, W / 2, -L / 2, L / 2, 0, top, 'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.16, top), {'ContentSocket': (0, 0, PH), 'TopSocket': (0, 0, top + 0.005)})


def build_load_cartons():
    name, W, L = 'PalletLoad_Cartons', 0.8, 1.2
    ch, layers = 0.25, 4
    top = PH + ch * layers
    parts = pallet_parts(name, W, L, top_boards(L, EPAL), 'EpalTop', weight=0.55)

    def block(p):
        rings = [ring(W, L, 0.004, PH, 0.02, 2)]
        for k in range(layers):
            z0 = PH + ch * k
            rings.append(ring(W, L, -0.003, z0 + ch * 0.5, 0.022, 2))
            if k < layers - 1:
                rings.append(ring(W, L, 0.001, z0 + ch, 0.02, 2))
        rings += [ring(W, L, 0.0, top - 0.012, 0.02, 2), ring(W, L, 0.012, top, 0.02, 2)]
        p.loft(rings, 'Film')
    parts.append(mk(f'{name}_Block', block, 0.0, weight=0.75))

    def lod1(p):
        pallet_lod1(p, W, L)
        bx(p, -W / 2 - 0.004, W / 2 + 0.004, -L / 2 - 0.004, L / 2 + 0.004, PH, top, 'Lid')
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -W / 2 - 0.004, W / 2 + 0.004, -L / 2 - 0.004, L / 2 + 0.004, 0, top,
                                          'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.16, 0.5), {'TopSocket': (0, 0, top)})


def build_load_klt():
    name, W, L = 'PalletLoad_SmallLoadCarriers', 0.8, 1.2
    kw, kl, kh, layers = 0.4, 0.6, 0.28, 3
    kmat = klt_name(600, 400, 280)
    ztop = PH + kh * layers
    lid_top = ztop + 0.03
    parts = pallet_parts(name, W, L, top_boards(L, EPAL), 'EpalTop', weight=0.55, light=True)
    i = 0
    for k in range(layers):
        for xc in (-0.2, 0.2):
            for yc in (-0.3, 0.3):
                parts.append(mk(f'{name}_Bin{i}', lambda p: klt_shell(p, kw, kl, kh, kmat), 0.0, weight=0.75,
                                loc=(xc, yc, PH + kh * k)))
                i += 1

    def lid(p):
        p.loft([ring(W, L, -0.006, ztop - 0.012, 0.01, 1), ring(W, L, -0.006, lid_top, 0.01, 1)], 'Lid',
               cap0=False)

    def straps(p):
        for yc in (-0.3, 0.3):
            xo = W / 2 + 0.008
            bx(p, xo, xo + 0.0015, yc - 0.008, yc + 0.008, 0.10, lid_top + 0.0015, 'Strap')
            bx(p, -xo, -xo - 0.0015, yc - 0.008, yc + 0.008, 0.10, lid_top + 0.0015, 'Strap')
            bx(p, -xo - 0.0015, xo + 0.0015, yc - 0.008, yc + 0.008, lid_top, lid_top + 0.0015, 'Strap')
    parts += [mk(f'{name}_Lid', lid, 0.003, weight=0.6), mk(f'{name}_Straps', straps, 0.0, weight=0.5)]

    def lod1(p):
        pallet_lod1(p, W, L)
        bx(p, -W / 2 - 0.002, W / 2 + 0.002, -L / 2 - 0.002, L / 2 + 0.002, PH, ztop - 0.012, 'Lid')
        bx(p, -W / 2 - 0.0095, W / 2 + 0.0095, -L / 2 - 0.006, L / 2 + 0.006, ztop - 0.012, lid_top + 0.0015, 'Lid')
    l1 = proxy(f'{name}_P1', lod1, W1)
    l2 = proxy(f'{name}_P2', lambda p: bx(p, -W / 2 - 0.0095, W / 2 + 0.0095, -L / 2 - 0.006, L / 2 + 0.006, 0,
                                          lid_top + 0.0015, 'Lid'), W2)
    return Unit(name, parts, l1, l2, (0.16, 0.5), {'TopSocket': (0, 0, lid_top)})


# =============================================================================
# kit
# =============================================================================

BUILDERS = {
    'EuroPallet': lambda: build_pallet('EuroPallet', 0.8, 1.2, EPAL, 'EpalTop'),
    'IndustrialPallet': lambda: build_pallet('IndustrialPallet', 1.0, 1.2,
                                             (0.145, 0.10, 0.10, 0.145, 0.10, 0.10, 0.145), 'IndTop'),
    **{klt_name(*s): (lambda s=s: build_klt(*s)) for s in KLT_SIZES},
    **{f'CardboardCarton_{L}x{W}x{H}': (lambda s=(L, W, H): build_carton(*s)) for (L, W, H) in CARTON_SIZES},
    'LargeLoadCarrier': build_glt,
    'MetalMeshBoxPallet': build_meshbox,
    'PalletCollar': build_collar,
    'PalletLoad_Cartons': build_load_cartons,
    'PalletLoad_SmallLoadCarriers': build_load_klt,
}


def build_kit(only=None):
    build_materials()
    return [fn() for n, fn in BUILDERS.items() if not only or n in only]


GRID = 3.0


def layout(units):
    """Bake-time spacing (pieces never shade each other); returns offsets."""
    offs = []
    for i, u in enumerate(units):
        d = V(((i % 5) * GRID, (i // 5) * GRID, 0))
        for o in u.parts + u.proxies:
            o.location = o.location + d
        offs.append(d)
    return offs


def pin_uvs(part, ref):
    """Every UV of `part` onto one texel in the middle of `ref`'s largest face.  Used for the
    136 mesh wires (4.8 mm): as islands of their own they cost a large share of the atlas and,
    a texel or two wide, baked dark; pinned they read as clean zinc and cost nothing."""
    big = max(ref.data.polygons, key=lambda f: f.area)
    uv = ref.data.uv_layers.active.data
    c = sum((V(uv[i].uv) for i in big.loop_indices), V((0, 0))) / len(big.loop_indices)
    if not part.data.uv_layers:
        part.data.uv_layers.new(name='UVMap')
    lay = part.data.uv_layers.active.data
    lay.foreach_set('uv', [c.x, c.y] * len(lay))


def gpu_or_cpu(fn, *args, **kw):
    """Run a bake step; if the GPU faults (CUDA / OptiX 'illegal address' on long 4096 px bakes,
    seen on the RTX 5090 workstation), switch the scene to the CPU and run it again."""
    try:
        return fn(*args, **kw)
    except RuntimeError as ex:
        print(f'[loadunits] GPU bake failed ({str(ex).splitlines()[0]}); retrying on the CPU')
        from rail_shuttle import use_cpu
        use_cpu(bpy.context.scene)
        return fn(*args, **kw)


def bake(units, out_dir, bake_size, save_size, variant=True):
    parts = [p for u in units for p in u.parts]
    proxies = [p for u in units for p in u.proxies]
    pinned = [u.pin for u in units if u.pin]
    K.atlas_unwrap([p for p in parts + proxies if p not in [a for a, _ in pinned]], margin=GAP, weights=WEIGHTS)
    for a, ref in pinned:
        pin_uvs(a, ref)
    offs = layout(units)
    scene = bpy.context.scene
    scene.world = scene.world or bpy.data.worlds.new('BakeWorld')
    scene.world.light_settings.distance = 0.25
    for p in proxies:
        p.hide_render = True
    margin = max(2, int(GAP * bake_size / 2))
    paths = gpu_or_cpu(K.bake_texture_set, parts, out_dir, PREFIX, bake_size, save_size, margin=margin)
    maps = [(paths['albedo'], 'rgb', None)]
    blue = None
    if variant:
        build_variant_materials()
        for o in parts:
            for i, m in enumerate(o.data.materials):
                if m and m.name in VARIANT:
                    o.data.materials[i] = VARIANT[m.name]
        mats = sorted({s.material for o in parts for s in o.material_slots if s.material}, key=lambda m: m.name)
        img = K.new_image(PREFIX + '_bake_blue', bake_size)
        gpu_or_cpu(K.bake_channel, parts, mats, 'albedo', img, samples=48, margin=margin)
        blue = os.path.join(out_dir, f'{PREFIX}_Blue_Albedo.png')
        K.write_png(blue, K.downsample(K.image_array(img), bake_size // save_size), 3)
        maps.append((blue, 'rgb', None))
    for p in proxies:
        p.hide_render = False
    jobs, see = [], []
    for u in units:
        for prx, reach in zip(u.proxies, u.reach):
            (see if u.seen_through else jobs).append((prx, u.parts, reach))
    # texels a proxy ray does not find anything for (an open top, a gap in a mesh wall) read
    # as the dim inside of the load unit
    dim = (0.025, 0.023, 0.022, 1.0)
    maps_all = [(p, k, dim) for p, k, _ in maps]
    maps_all += [(paths['metallic'], 'rgba', (0, 0, 0, 0.25)), (paths['normal'], 'normal', (0.5, 0.5, 1.0, 1)),
                 (paths['occlusion'], 'rgb', (0.45, 0.45, 0.45, 1))]
    pm = max(1, int(GAP * save_size / 2))
    if jobs:
        gpu_or_cpu(K.transfer_to_proxies, jobs, maps_all, margin=pm)
    if see:                                 # through a wire mesh: the grey floor, not a dark inside
        through = {dim: (0.30, 0.30, 0.30, 1.0), (0.45, 0.45, 0.45, 1): (0.85, 0.85, 0.85, 1.0)}
        gpu_or_cpu(K.transfer_to_proxies, see, [(p, k, through.get(f, f)) for p, k, f in maps_all], margin=pm,
                   scale={paths['metallic']: (0.25, 0.25, 0.25, 0.7)})    # wire + gap averaged: not chrome
    if blue:
        for m in ('MetallicSmoothness', 'Normal', 'Occlusion', 'Emission'):
            shutil.copyfile(os.path.join(out_dir, f'{PREFIX}_{m}.png'), os.path.join(out_dir, f'{PREFIX}_Blue_{m}.png'))
    for d, u in zip(offs, units):
        for o in u.parts + u.proxies:
            o.location = o.location - d
    # bake object offsets (bins in a load) into the meshes: rotate_to_export turns mesh data
    for o in parts + proxies:
        if o.location.length > 0:
            o.data.transform(Matrix.Translation(o.location))
            o.location = (0, 0, 0)
    mat = K.make_runtime_material(PREFIX, paths)
    K.assign_single_material(parts + proxies, mat)
    return paths, blue


def assemble_lods(units):
    for u in units:
        lod0 = K.join(u.parts, f'{u.name}_LOD0')
        meshes = [lod0]
        for i, p in enumerate(u.proxies, 1):
            p.name = p.data.name = f'{u.name}_LOD{i}'
            meshes.append(p)
        u.piece.meshes[None] = meshes


# =============================================================================
# previews
# =============================================================================

def look_renders(units, quick):
    """--look: Cycles renders of the LOD0 source materials (no bake)."""
    scene = bpy.context.scene
    KM.studio(scene, k=1.0)
    x = 0.0
    for u in units:
        size = max(o.dimensions.x for o in u.parts)
        x += size / 2 + 0.25
        for o in u.parts:
            o.location.x += x
        x += size / 2
    mid = x / 2
    spp = 24 if quick else 96
    KM.render(scene, KM.camera(scene, 'Cam_Look', (mid + 0.3, -max(3.0, x * 0.75), 1.6), (mid, 0.0, 0.25), 40),
              os.path.join(PREVIEW_DIR, 'loadunits_look.png'), (1600, 800), spp)
    for u in units:
        c = V(u.parts[0].location) + V((0, 0, 0))
        h = max(o.dimensions.z for o in u.parts)
        s = max(max(o.dimensions.x for o in u.parts), h)
        KM.render(scene, KM.camera(scene, f'Cam_{u.name}', (c.x + s * 1.1, c.y - s * 1.5, h + s * 0.7),
                                   (c.x, c.y, h * 0.45), 45),
                  os.path.join(PREVIEW_DIR, f'loadunits_look_{u.name}.png'), (900, 650), spp)


def _render(scene, cam, path, res, spp):
    """KM.render, retried on the CPU if the GPU faults (seen once with OptiX on a busy GPU)."""
    try:
        KM.render(scene, cam, path, res, spp)
    except RuntimeError as ex:
        print(f'[loadunits] GPU render failed ({ex}); retrying on the CPU')
        from rail_shuttle import use_cpu
        use_cpu(scene)
        KM.render(scene, cam, path, res, spp)


def preview_renders(units, blue_path, paths, quick):
    scene = bpy.context.scene
    for u in units:
        for o in [u.piece.root] + list(u.piece.root.children_recursive):
            o.hide_render = True
    KM.studio(scene, k=1.4, floor=120)
    spp = 32 if quick else 128
    res = (1000, 625) if quick else (1800, 1100)
    out = lambda n: os.path.join(PREVIEW_DIR, f'loadunits_{n}.png')        # noqa: E731
    by = {u.name: u for u in units}
    T = Matrix.Translation
    Rz = lambda a: Matrix.Rotation(a, 4, 'Z')                              # noqa: E731
    blue = None
    if blue_path:
        bp = dict(paths)
        bp['albedo'] = blue_path
        blue = K.make_runtime_material(PREFIX + '_BluePreview', bp)

    def put(coll, name, M_, level=0, mat=None):
        if name not in by:
            return []
        obs = KM.place(by[name].piece, M_, coll, level)
        if mat:
            for o in obs:
                o.data = o.data.copy()
                o.data.materials[0] = mat
        return obs

    def scene_layout(coll, level=0):
        """A tidy line-up: back row containers, middle row pallet loads, front small items."""
        put(coll, 'MetalMeshBoxPallet', T((-3.0, 1.2, 0)), level)
        put(coll, 'LargeLoadCarrier', T((-1.6, 1.2, 0)), level)
        put(coll, 'PalletCollar', T((-0.3, 1.2, 0)), level)
        put(coll, 'PalletLoad_Cartons', T((0.8, 1.2, 0)), level)
        put(coll, 'PalletLoad_SmallLoadCarriers', T((1.9, 1.2, 0)), level)
        put(coll, 'PalletLoad_SmallLoadCarriers', T((3.0, 1.2, 0)), level, blue)
        put(coll, 'EuroPallet', T((-2.6, -0.4, 0)) @ Rz(pi / 2), level)
        put(coll, 'EuroPallet', T((-2.6, -0.4, PH)) @ Rz(pi / 2 + 0.03), level)
        put(coll, 'IndustrialPallet', T((-1.2, -0.4, 0)) @ Rz(pi / 2), level)
        x = -0.3
        for s in KLT_SIZES:
            n = klt_name(*s)
            put(coll, n, T((x, -0.55, 0)), level)
            put(coll, n, T((x, -0.55 - s[0] / 1000 - 0.08, 0)), level, blue)
            x += s[1] / 1000 + 0.12
        put(coll, klt_name(400, 300, 147), T((0.0, -0.55, 0.147)), level, blue)
        x = 1.5
        for (L, W, H) in CARTON_SIZES:
            put(coll, f'CardboardCarton_{L}x{W}x{H}', T((x, -0.6, 0)) @ Rz(0.15), level)
            x += W / 1000 + 0.25
        put(coll, 'CardboardCarton_300x200x200', T((1.55, -0.62, 0.30)) @ Rz(-0.3), level)
    coll = K.collection('PreviewLine')
    scene_layout(coll)
    KM.scale_figure(coll, (4.2, 0.0, 0), 2.6)
    hero = ((1.0, -7.6, 3.1), (0.1, 0.2, 0.45), 32)
    _render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Small', (1.6, -3.2, 1.2), (0.7, -0.7, 0.15), 40), out('small'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Loads', (2.6, -1.7, 1.9), (1.5, 1.2, 0.6), 40), out('loads'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Cont', (-3.4, -2.2, 2.0), (-1.9, 1.0, 0.5), 40), out('containers'), res, spp)
    _render(scene, KM.camera(scene, 'Cam_Pallet', (-1.0, -2.4, 0.9), (-2.0, -0.4, 0.1), 40), out('pallets'), res, spp)
    for lvl in (1, 2):
        c = K.collection(f'PreviewLine_LOD{lvl}')
        coll.hide_render = True
        scene_layout(c, lvl)
        _render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (1000, 625), spp)
        _render(scene, KM.camera(scene, f'Cam_LODc{lvl}', (1.6, -3.2, 1.2), (0.7, -0.7, 0.15), 40),
                  out(f'lod{lvl}_close'), (1000, 625), spp)
        c.hide_render = True
    coll.hide_render = False
    # mass: a block-stacked storage area (instancing scenario)
    coll.hide_render = True
    mass = K.collection('PreviewMass')
    names = [n for n in ('PalletLoad_Cartons', 'PalletLoad_SmallLoadCarriers', 'MetalMeshBoxPallet',
                         'LargeLoadCarrier', 'PalletCollar') if n in by]
    if names:
        k = 0
        for i in range(8):
            for j in range(6):
                n = names[(i * 7 + j * 3) % len(names)]
                lvl = 0 if j < 2 else 1 if j < 4 else 2
                put(mass, n, T((i * 0.9 - 3.2, j * 1.35 - 1.0, 0)) @ Rz(pi / 2 * 0), lvl,
                    blue if (n == 'PalletLoad_SmallLoadCarriers' and (i + j) % 2) else None)
                k += 1
        _render(scene, KM.camera(scene, 'Cam_Mass', (-5.6, -5.0, 4.2), (-0.2, 1.6, 0.4), 30), out('mass'), res, spp)
    mass.hide_render = True
    coll.hide_render = False


# =============================================================================
# main
# =============================================================================

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--look', action='store_true')
    ap.add_argument('--only', default='')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    only = [s for s in args.only.split(',') if s]
    t0 = time.time()
    scene = K.reset_scene()
    K.setup_cycles_gpu(scene, backends=('CUDA', 'OPTIX', 'HIP', 'ONEAPI', 'METAL'))   # OptiX faulted on long bakes
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    units = build_kit(only)
    if args.look:
        for u in units:
            for p in u.proxies:
                p.hide_render = True
        look_renders(units, args.quick)
        print(f'[loadunits] look done in {time.time() - t0:.1f}s')
        return
    out_dir = os.path.join(BUILD_DIR, 'WarehouseLoadUnits_test') if only else OUT_DIR
    paths, blue = bake(units, out_dir, args.bake, args.save)
    assemble_lods(units)
    pieces = [u.piece for u in units]
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        root = KM.rig(pc)
        if not only:
            K.export_fbx(root, os.path.join(out_dir, f'{pc.name}.fbx'))
        for o in root.children_recursive:
            if o.type == 'EMPTY':
                o.name = f'{o.name}_{pc.name}'
    blend = os.path.join(BUILD_DIR, f'{PREFIX}{"_test" if only else ""}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(units, blue, paths, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[loadunits] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
