"""
SimuLean asset: rail-guided transfer car (rail shuttle) with a powered roller-conveyor
deck, e.g. for a panel / board plant.  The car runs on a straight floor rail and
carries pallet-less board packages between line exits, packaging stations and
warehouses.  The deck is ONE lane, 6.0 m long (across the rail) x 2.3 m wide
(along the rail): packages lie lengthwise on it, one behind the other, and
transfer on/off SIDEWAYS (the roller axes are parallel to the rail, the conveying
direction is across it).  Two short packages fit (2 x 2.85 m + gap), a long one
(e.g. 3.66 m or 5.7 m) travels alone -- a length-based deck capacity rule.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/rail_shuttle.py --
            [--variant all|main] [--bake 4096] [--save 2048] [--no-render] [--quick] [--cpu]

    --variant all   (default) builds RailShuttle + Rail_1m into ONE shared atlas and
                    writes them to Art/Models/RailShuttle/.
    --variant main  iteration only: same pieces, baked into build/RailShuttle_main/
                    with previews, writing nothing to Unity.
    --cpu           bake/render on the CPU instead of OptiX/CUDA (same speed at 1024 px).

Outputs (--variant all)
    Assets/SimuLean.Unity/Art/Models/RailShuttle/RailShuttle.fbx       the car
    Assets/SimuLean.Unity/Art/Models/RailShuttle/Rail_1m.fbx           tiling rail section
    Assets/SimuLean.Unity/Art/Models/RailShuttle/RailShuttle_*.png     one shared atlas
    Tools/Blender/build/RailShuttle.blend
    Tools/Blender/previews/railshuttle_*.png

    RailShuttle.fbx is named after its folder, so the postprocessor builds
    RailShuttle.mat for it; Rail_1m.fbx is a kit piece and uses that same folder
    material.  (The former 8 m RailShuttle_Long variant was dropped 2026-09-26:
    every car uses the same 6 x 2.3 m single-lane deck.)

Frame (authored frame = Unity frame, as in the modular kits)
    authored +Y = Unity +Z = along the rail (travel)
    authored +X = Unity +X = across the rail = along the deck (package transfer direction)
    authored +Z = Unity +Y = up.   Metres.  Origin on the floor, centred on the car.
    Unity position of an authored point (x, y, z) is (x, z, y).

Car.  The cabinet (HMI, beacon) sits on the +Z end (along the rail).
    Chassis 6.20 (X) x 2.90 (Z) m (3.00 m over the bumpers); roller deck 6.00 x 2.30 m,
    one section, 23 rollers dia 80 mm (pitch 0.264 m), galvanised, axes along Z (the rail);
    roller top = 0.55 m above the floor.  Drive hood on the -Z edge of the deck.
    Nodes (bare Transforms; meshes hang under them as <Node>_LODn at identity):
        <root>        Body meshes <root>_LOD0/1/2.  The four flanged wheels (r = 0.16 m,
                      axles at z = +/-1.00, gauge 2.20 m) are enclosed by the skirt,
                      so they are part of the body (LOD0 only), not animated nodes.
        Beacon        amber beacon on the cabinet roof (LOD0/LOD1; body at LOD2):
                      toggle / pulse its renderer or add a light here
        Deck          (0, 0.55, 0): roller top plane, no mesh
          Slot_Center (0, 0.55, 0): bottom-centre of a single (long) package
          Slot_Front  (+1.55, 0.55, 0): bottom-centre of the +X package of a pair
          Slot_Rear   (-1.55, 0.55, 0): bottom-centre of the -X package of a pair
        (Front / Rear slots = centres of two 2.85 m packages 0.25 m apart.)  Packages
        are oriented lengthwise along local X; slide them along X to transfer off
        either side.  Views may place pairs by their real lengths instead.

Rail_1m (segment, tiles along local +Z over [0, 1] m)
    Two floor-embedded rails at gauge 2.20 m (x = +/-1.10) with steel cover plates
    and flangeways, plus a slotted bus-bar duct at x = 0 (the car's current
    collector runs in it).  Rail top at y = 0.012 (the wheels' contact height),
    cover plates 8 mm proud of the floor plane (y = 0) so they never z-fight with
    it; the wheel flanges dip 10 mm below the floor into the flangeways.
    Tile with UnityModularStrip / ModularKit.Strip (period 1 m, no caps).

LODs: LOD1 decimates the rollers and drops the wheels and small hardware; LOD2
replaces the roller bed by one baked striped plane and folds the beacon into the
body.  Tri counts are printed by the run (KM.report).
"""

import os
import sys
import argparse
import time
from math import pi, sqrt

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
import numpy as np                           # noqa: E402
from mathutils import Vector, Matrix         # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
import simulean_modular as KM                # noqa: E402
from simulean_kit import V, Part, rrect, finish, cull_faces  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'RailShuttle')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'
PREFIX = 'RailShuttle'

# ---------------------------------------------------------------- dimensions (authored frame, m)
GAUGE = 2.20
RX = GAUGE / 2                    # rail centre |x|
RAIL_TOP = 0.012                  # rail head top (wheel contact)
WHEEL_R = 0.16
WZ = RAIL_TOP + WHEEL_R           # wheel axle height
CH_Z0, CH_Z1 = 0.06, 0.37         # chassis skirt bottom / deck-plate top
ROLL_R, ROLL_TOP = 0.040, 0.55
ROLL_Z = ROLL_TOP - ROLL_R
FR_TOP = 0.53                     # conveyor side-frame top (2 cm under the roller top)
FR_F, FR_T = 0.05, 0.006          # side-frame flange width / sheet thickness
HOOD_W, HOOD_TOP = 0.16, 0.54     # drive hood band width / top (under the package)
GUIDE_TOP = 0.60
CAB_TOP = 1.20

YELLOW = (0.80, 0.42, 0.015)
BLUE = (0.010, 0.072, 0.20)       # RAL 5010-ish
GREY = (0.46, 0.48, 0.47)         # RAL 7035-ish
INK = (0.022, 0.022, 0.025)

# half_len: along the rail (Y); half_w / bay_w: across the rail = along the deck (X).
# One roller section = one lane (6.0 m deck length x 2.3 m deck width).
DECK_LEN, DECK_W = 6.00, 2.30
SLOT_X = 1.55                     # pair slots: two 2.85 m packages, 0.25 m apart
SPECS = {
    'main': dict(name='RailShuttle', tag='M', half_len=1.45, half_w=3.10, bay_w=DECK_LEN, n_roll=23,
                 sections=[(0.0, DECK_W)],
                 bays=[('Lane', [0])], drive={0: -1},
                 slots={'Slot_Center': 0.0, 'Slot_Front': SLOT_X, 'Slot_Rear': -SLOT_X},
                 wheel_y=1.00, code='RS 01'),
}

MATS = {}


# =============================================================================
# Decal images
# =============================================================================

def text_image(text, fname):
    """Bold text decal (see K.render_text_image).  The written PNG is the final,
    dilated image, so an existing one is reused (delete it to re-render)."""
    path = os.path.join(BUILD_DIR, fname)
    if os.path.exists(path):
        img = bpy.data.images.load(path, check_existing=False)
        return img, img.size[0] / img.size[1]
    return K.render_text_image(text, path, 256, FONT, bold_offset=0.016)


def warning_images():
    """Warning triangle as two alpha masks (white on transparent): the filled
    triangle (painted yellow) and its border + exclamation mark (painted black)."""
    S, ss = 256, 4
    N = S * ss
    v = (np.arange(N) + 0.5) / N * 2 - 1
    X, Y = np.meshgrid(v, v)                               # row 0 = bottom (Blender order)
    P = [(0.0, 0.90), (-0.98, -0.80), (0.98, -0.80)]       # CCW
    d = np.full(X.shape, 1e9)
    for (ax, ay), (bx, by) in zip(P, P[1:] + P[:1]):
        nx, ny = -(by - ay), (bx - ax)
        ln = sqrt(nx * nx + ny * ny)
        d = np.minimum(d, ((X - ax) * nx + (Y - ay) * ny) / ln)
    fill = d > 0
    border = fill & (d < 0.12)
    w = 0.055 + 0.03 * np.clip((Y + 0.2) / 0.6, 0, 1)      # slightly tapered bar
    bar = (np.abs(X) < w) & (Y > -0.22) & (Y < 0.40)
    dot = X * X + (Y + 0.44) ** 2 < 0.085 ** 2
    mark = border | bar | dot
    out = []
    for name, mask in (('decal_warn_fill', fill), ('decal_warn_mark', mark)):
        a = mask.astype(np.float32).reshape(S, ss, S, ss).mean(axis=(1, 3))
        px = np.ones((S, S, 4), np.float32)
        px[..., 3] = a
        img = bpy.data.images.new(name, S, S, alpha=True)
        img.pixels.foreach_set(px.ravel())
        img.filepath_raw = os.path.join(BUILD_DIR, name + '.png')
        img.file_format = 'PNG'
        img.save()
        out.append(img)
    return out


# =============================================================================
# Materials
# =============================================================================

def rail_steel(name):
    mat = M._new(name)
    nb = K.NB(mat)
    x, y, z = nb.xyz(nb.obj)
    top = nb.m('MULTIPLY', nb.mr(z, RAIL_TOP - 0.0045, RAIL_TOP - 0.0015), nb.up_mask(0.8, 0.95))
    scratch = nb.noise(1.0, 8, 0.6, aniso=(90, 1.5, 90))
    base = nb.mixc(nb.noise(3.0, 6, 0.6), (0.050, 0.047, 0.044), (0.085, 0.064, 0.048))
    base = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.05), 0.7), base, M.GRIME)
    shine = nb.mixc(scratch, (0.40, 0.40, 0.39), (0.62, 0.62, 0.60))
    col = nb.mixc(top, base, shine)
    r = nb.mixf(top, nb.mr(nb.noise(8, 4), 0.3, 0.7, 0.55, 0.7), nb.mr(scratch, 0.3, 0.7, 0.14, 0.3))
    metal = nb.mixf(top, 0.55, 1.0)
    return K.finalize_material(nb, col, r, metal, nb.bump(scratch, 0.15, 0.0004))


def wheel_steel(name, wy):
    """Dark wheel steel with a polished tread / flange (axle along X at |y| = wy)."""
    mat = M._new(name)
    nb = K.NB(mat)
    x, y, z = nb.xyz(nb.obj)
    ay = nb.m('SUBTRACT', nb.m('ABSOLUTE', y), wy)
    zz = nb.m('SUBTRACT', z, WZ)
    rr = nb.m('SQRT', nb.m('ADD', nb.m('MULTIPLY', ay, ay), nb.m('MULTIPLY', zz, zz)))
    tread = nb.mr(rr, WHEEL_R - 0.004, WHEEL_R - 0.0005)
    base = nb.mixc(nb.noise(6.0, 6, 0.6), (0.055, 0.052, 0.05), (0.08, 0.075, 0.07))
    base = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.06), 0.8), base, M.GRIME)
    col = nb.mixc(tread, base, (0.52, 0.52, 0.50))
    return K.finalize_material(nb, col, nb.mixf(tread, 0.5, 0.22), nb.mixf(tread, 0.7, 1.0))


def roller_proxy(name, x0, pitch):
    """Far-LOD stand-in for a roller bed: galvanised cylinders faked in albedo
    and normal on a flat plane (rollers along Y, spaced `pitch` along X from x0)."""
    mat = M._new(name)
    nb = K.NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    u = nb.m('SUBTRACT', nb.m('FRACT', nb.m('ADD', nb.m('DIVIDE', nb.m('SUBTRACT', x, x0), pitch), 0.5)), 0.5)
    d = nb.m('MULTIPLY', nb.m('ABSOLUTE', u), pitch)
    roller = nb.mr(d, ROLL_R, ROLL_R - 0.004)
    q = nb.m('DIVIDE', d, ROLL_R)
    h = nb.m('SQRT', nb.m('MAXIMUM', nb.m('SUBTRACT', 1.0, nb.m('MULTIPLY', q, q)), 0.0))
    shade = nb.m('ADD', 0.35, nb.m('MULTIPLY', h, 0.65))
    galv = nb.multc((0.52, 0.53, 0.54), nb.vec(shade, shade, shade))
    col = nb.mixc(roller, (0.022, 0.022, 0.024), galv)
    return K.finalize_material(nb, col, nb.mixf(roller, 0.6, 0.38), roller, nb.bump(h, 1.0, 0.03))


def screen_frame(S):
    """HMI screen rectangle on the cabinet door (faces +Y)."""
    y0, y1 = cab_extent(S)
    sw, sh = 0.17, 0.11
    cx, cz = 0.12, 0.99
    yf = y1 + 0.006 + 0.012 + 0.002
    return (cx + sw / 2, yf, cz - sh / 2), (sw, sh), (cx, yf, cz)


def build_materials(variants):
    K.NB.default_period = {}
    logo, la = text_image('SimuLean', 'decal_logo.png')
    warn_fill, warn_mark = warning_images()

    def warn(origin, u, normal, w):
        return [dict(image=warn_fill, origin=origin, u=u, v=(0, 0, 1), size=(w, w), normal=normal,
                     color=YELLOW, opacity=1.0, depth=0.03),
                dict(image=warn_mark, origin=origin, u=u, v=(0, 0, 1), size=(w, w), normal=normal,
                     color=INK, opacity=1.0, depth=0.03)]

    for v in variants:
        S = SPECS[v]
        T, hl, hw = S['tag'], S['half_len'], S['half_w']
        code, ca = text_image(S['code'], f'decal_code_{T}.png')
        zc = (CH_Z0 + CH_Z1 - 0.045) / 2
        dec = []
        for s in (1, -1):
            u = (0, s, 0)
            dec.append(dict(image=logo, origin=(s * hw, 0.0, zc), u=u, v=(0, 0, 1),
                            size=(0.66, 0.66 / la), normal=(s, 0, 0), color=(0.85, 0.86, 0.86)))
            for e in (1, -1):
                dec += warn((s * hw, e * (hl - 0.32), zc), u, (s, 0, 0), 0.15)
        MATS['Chassis' + T] = M.paint('Chassis' + T, BLUE, rough=0.38, wear=0.6, decals=dec)
        y0, y1 = cab_extent(S)
        yd = y1 + 0.006
        cdec = [dict(image=code, origin=(0.0, yd, 0.58), u=(-1, 0, 0), v=(0, 0, 1),
                     size=(0.30, 0.30 / ca), normal=(0, 1, 0), color=INK, depth=0.02)]
        cdec += warn((0.0, yd, 0.76), (-1, 0, 0), (0, 1, 0), 0.13)
        MATS['Cabinet' + T] = M.paint('Cabinet' + T, GREY, rough=0.42, wear=0.35, decals=cdec)
        origin, size, _ = screen_frame(S)
        MATS['Screen' + T] = M.screen('Screen' + T, origin, (-1, 0, 0), (0, 0, 1), size)
        MATS['Wheel' + T] = wheel_steel('Wheel' + T, S['wheel_y'])
        bw = S['bay_w']
        MATS['Proxy' + T] = roller_proxy('Proxy' + T, -(bw / 2 - 0.10), (bw - 0.20) / (S['n_roll'] - 1))

    MATS.update({
        'DeckPlate': M.paint('DeckPlate', (0.030, 0.031, 0.033), rough=0.55, wear=0.8, dirt=1.4),
        'Frame': M.paint('Frame', GREY, rough=0.40, wear=0.6, edge=0.003),
        'Yellow': M.paint('Yellow', YELLOW, rough=0.36, wear=0.8, edge=0.004),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=1.2,
                          color_fn=M.hazard_fn(YELLOW, width=0.05, axis=('x', 'z'))),
        'HazardTop': M.paint('HazardTop', None, rough=0.45, wear=1.4, dirt=1.3,
                             color_fn=M.hazard_fn(YELLOW, width=0.05, axis=('x', 'y'))),
        'Galv': M.galvanised('Galv', rough=0.36),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Tread': M.tread_plate('Tread'),
        'Rubber': M.rubber('Rubber'),
        'Plastic': M.plastic('Plastic'),
        'SensorBody': M.plastic('SensorBody', color=(0.30, 0.31, 0.32), rough=0.45),
        'ScanWin': M.glass('ScanWin', tint=(0.012, 0.012, 0.014)),
        'EStop': M.plastic('EStop', color=(0.60, 0.02, 0.015), rough=0.35),
        'Collector': M.plastic('Collector', color=(0.55, 0.08, 0.02), rough=0.45),
        'LensA': M.lens('LensA', (1.0, 0.42, 0.0)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'LensG': M.lens('LensG', (0.10, 1.0, 0.25)),
        'LensW': M.lens('LensW', (1.0, 0.95, 0.85)),
    })
    # the rail tiles along Y with a 1 m period: its noise must repeat exactly
    K.NB.default_period = {'y': 1.0}
    MATS.update({
        'RailSteel': rail_steel('RailSteel'),
        'RPlate': M.paint('RPlate', (0.055, 0.056, 0.060), rough=0.5, wear=1.4, dirt=1.5, edge=0.004),
        'RGroove': M.dark_steel('RGroove', color=(0.012, 0.012, 0.013), rough=0.7, metal=0.3),
    })
    K.NB.default_period = {}
    return MATS


# =============================================================================
# Helpers
# =============================================================================

def mk(name, fn, bevel=0.004, seg=1, angle=40.0, cull=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cull:
        cull_faces(ob, cull)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def bx(p, x0, x1, y0, y1, z0, z1, mat):
    p.box_minmax((min(x0, x1), min(y0, y1), min(z0, z1)), (max(x0, x1), max(y0, y1), max(z0, z1)), mat)


def hexbolt(p, c, axis, h, r=0.010, mat='DarkSteel'):
    c, d = V(c), V(axis).normalized()
    p.cylinder(c, c + d * h, r, mat, n=6)


DOWN = lambda c, n: n.z < -0.9                      # noqa: E731


def ends(axis, *vals, eps=1e-4):
    i = 'xyz'.index(axis)
    return lambda c, n: abs(n[i]) > 0.99 and any(abs(c[i] - v) < eps for v in vals)


def sec_ext(S, i):
    yc, L = S['sections'][i]
    return yc - L / 2, yc + L / 2


def bay_ext(S, idx):
    return min(sec_ext(S, i)[0] for i in idx), max(sec_ext(S, i)[1] for i in idx)


def conveyor_ext(S):
    ys = [e for i in range(len(S['sections'])) for e in sec_ext(S, i)]
    return min(ys), max(ys)


def cab_extent(S):
    y0 = conveyor_ext(S)[1] + 0.02
    return y0, min(S['half_len'] - 0.02, y0 + 0.18)


def roller_xs(S):
    bw, n = S['bay_w'], S['n_roll']
    pitch = (bw - 0.20) / (n - 1)
    return [-(bw / 2 - 0.10) + k * pitch for k in range(n)]


def roller_span(S, i):
    ys, ye = sec_ext(S, i)
    y0, y1 = ys + FR_F + 0.006, ye - FR_F - 0.006
    side = S['drive'].get(i, 0)
    if side > 0:
        y1 = ye - FR_F - HOOD_W - 0.006
    elif side < 0:
        y0 = ys + FR_F + HOOD_W + 0.006
    return y0, y1


def wheel_nodes(S):
    wy = S['wheel_y']
    return {f"Wheel_{'F' if sy > 0 else 'R'}{'R' if sx > 0 else 'L'}": (sx * RX, sy * wy, WZ)
            for sy in (1, -1) for sx in (-1, 1)}


# =============================================================================
# Car
# =============================================================================

def build_car(S):
    T, hl, hw, bw = S['tag'], S['half_len'], S['half_w'], S['bay_w']
    chassis = 'Chassis' + T
    ylo, yhi = conveyor_ext(S)
    nsec = len(S['sections'])

    # ---- chassis skirt: closed box, painted sides, dark deck plate on top
    def skirt(p):
        def ring(z, inset):
            return [V((x, y, z)) for x, y in rrect(2 * (hw - inset), 2 * (hl - inset), max(0.03 - inset, 0.002), 2)]
        levels = [(CH_Z0, 0.012), (CH_Z0 + 0.012, 0.0), (CH_Z1 - 0.050, 0.0), (CH_Z1 - 0.047, 0.006),
                  (CH_Z1 - 0.040, 0.006), (CH_Z1 - 0.037, 0.0), (CH_Z1, 0.0)]
        p.loft([ring(z, i) for z, i in levels], chassis, cap_mat='DeckPlate')
    sk = mk(f'{T}_Skirt', skirt, bevel=0.0, cull=DOWN)

    # ---- bolted access covers on both long sides
    zc = (CH_Z0 + CH_Z1 - 0.045) / 2

    def panels(p):
        for s in (1, -1):
            for i in range(nsec):
                yc, L = S['sections'][i]
                bx(p, s * (hw - 0.02), s * (hw + 0.006), yc - min(1.1, 0.5 * L) / 2, yc + min(1.1, 0.5 * L) / 2,
                   zc - 0.075, zc + 0.075, chassis)
    pn = mk(f'{T}_Panels', panels, bevel=0.003)

    # ---- conveyor side frames (C-channels along X at both ends of every section)
    def frames(p):
        for i in range(nsec):
            ys, ye = sec_ext(S, i)
            for e, d in ((ys, 1), (ye, -1)):
                prof = [(e, CH_Z1), (e + d * FR_F, CH_Z1), (e + d * FR_F, CH_Z1 + FR_T), (e + d * FR_T, CH_Z1 + FR_T),
                        (e + d * FR_T, FR_TOP - FR_T), (e + d * FR_F, FR_TOP - FR_T), (e + d * FR_F, FR_TOP),
                        (e, FR_TOP)]
                p.extrude(prof, 'X', -bw / 2, bw / 2, 'Frame')
            for s in (1, -1):                                  # end cross bars (stops mount here)
                bx(p, s * (bw / 2 - 0.06), s * bw / 2, ys + FR_F, ye - FR_F, CH_Z1, CH_Z1 + 0.08, 'Frame')
    fr = mk(f'{T}_Frames', frames, bevel=0.003, cull=DOWN)

    # ---- rollers: axes along Y (parallel to the rail), spaced across X
    def rollers(p):
        for i in range(nsec):
            y0, y1 = roller_span(S, i)
            for x in roller_xs(S):
                p.lathe([(y0, 0.033), (y0 + 0.005, ROLL_R), (y1 - 0.005, ROLL_R), (y1, 0.033)], 12, 'Galv',
                        center=(x, 0, ROLL_Z), axis='Y')
                for a, b in ((y0 - 0.006, y0), (y1, y1 + 0.006)):         # axle stubs into the frames
                    p.lathe([(a, 0.011), (b, 0.011)], 6, 'DarkSteel', center=(x, 0, ROLL_Z), axis='Y')
    ro = mk(f'{T}_Rollers', rollers, bevel=0.0)

    # ---- drive hoods: chain + gear motor under a yellow cover at the drive end
    def hoods(p):
        for i, side in S['drive'].items():
            ys, ye = sec_ext(S, i)
            a, b = (ye - FR_F - HOOD_W, ye - FR_F) if side > 0 else (ys + FR_F, ys + FR_F + HOOD_W)
            prof = K.fillet_closed([(a, CH_Z1), (b, CH_Z1), (b, HOOD_TOP), (a, HOOD_TOP)], [0, 0, 0.035, 0.035], 3)
            p.extrude(prof, 'X', -(bw / 2 - 0.065), bw / 2 - 0.065, 'Yellow')
            for x in (-0.9, -0.3, 0.3, 0.9):                   # vent grilles on top
                for k in range(4):
                    yy = (a + b) / 2 + (k - 1.5) * 0.024
                    bx(p, x - 0.14, x + 0.14, yy - 0.007, yy + 0.007, HOOD_TOP - 0.001, HOOD_TOP + 0.002, 'Plastic')
            # motor terminal box on the bay side of the hood
            f = a if side > 0 else b
            dd = -1 if side > 0 else 1
            bx(p, 0.35, 0.55, f, f + dd * 0.04, CH_Z1 + 0.03, CH_Z1 + 0.13, 'Plastic')
    ho = mk(f'{T}_Hoods', hoods, bevel=0.003, cull=DOWN)

    # ---- low guide rails with lead-in flares at both X ends
    def guides(p):
        for _, idx in S['bays']:
            b0, b1 = bay_ext(S, idx)
            for yg, out in ((b0 + 0.025, -1), (b1 - 0.025, 1)):
                zc_ = (FR_TOP + GUIDE_TOP) / 2
                path = [V((-bw / 2 + 0.02, yg + out * 0.022, zc_)), V((-bw / 2 + 0.24, yg, zc_)),
                        V((bw / 2 - 0.24, yg, zc_)), V((bw / 2 - 0.02, yg + out * 0.022, zc_))]
                p.sweep(path, rrect(GUIDE_TOP - FR_TOP, 0.008, 0.002, 1), 'Galv', side=(0, 0, 1))
    gd = mk(f'{T}_Guides', guides, bevel=0.0)

    # ---- end stops at the bay X ends + package-presence photocells on the frames
    def stops(p):
        for _, idx in S['bays']:
            b0, b1 = bay_ext(S, idx)
            yc = (b0 + b1) / 2
            for s in (1, -1):
                for dy in (-0.55, 0.55):
                    bx(p, s * (bw / 2 - 0.058), s * (bw / 2 - 0.004), yc + dy - 0.07, yc + dy + 0.07,
                       CH_Z1 + 0.08, CH_Z1 + 0.10, 'DarkSteel')
                    bx(p, s * (bw / 2 - 0.050), s * (bw / 2 - 0.010), yc + dy - 0.06, yc + dy + 0.06,
                       CH_Z1 + 0.10, ROLL_TOP + 0.035, 'Yellow')
                # photocell (b0 side, looks +Y) and reflector (b1 side)
                x = s * (bw / 2 - 0.17)
                bx(p, x - 0.016, x + 0.016, b0 + 0.030, b0 + 0.048, FR_TOP, FR_TOP + 0.055, 'SensorBody')
                bx(p, x - 0.009, x + 0.009, b0 + 0.048, b0 + 0.050, FR_TOP + 0.022, FR_TOP + 0.042, 'LensR')
                bx(p, x - 0.02, x + 0.02, b1 - 0.034, b1 - 0.030, FR_TOP + 0.005, FR_TOP + 0.045, 'LensR')
    st = mk(f'{T}_Stops', stops, bevel=0.003)

    # ---- end zones: tread plates, hazard edge strips along the transfer sides
    def endzones(p):
        bx(p, -(hw - 0.02), hw - 0.02, yhi + 0.004, hl - 0.015, CH_Z1, CH_Z1 + 0.006, 'Tread')
        bx(p, -(hw - 0.02), hw - 0.02, -(hl - 0.015), ylo - 0.004, CH_Z1, CH_Z1 + 0.006, 'Tread')
        for s in (1, -1):
            bx(p, s * (bw / 2 + 0.006), s * (hw - 0.006), ylo, yhi, CH_Z1 - 0.02, CH_Z1 + 0.004, 'HazardTop')
    ez = mk(f'{T}_Ends', endzones, bevel=0.002)

    # ---- striped bumpers with rubber buffers at both ends
    def bumpers(p):
        for e in (1, -1):
            bx(p, -1.60, 1.60, e * hl, e * (hl + 0.05), 0.13, 0.29, 'Hazard')
            for x in (-1.25, 1.25):
                y0 = e * (hl + 0.05)
                prof = [(0.0, 0.08), (0.012, 0.08), (0.012, 0.068), (0.070, 0.058), (0.080, 0.045), (0.083, 0.02)]
                p.lathe([(y0 + e * t, r) for t, r in prof], 20, 'Rubber', center=(x, 0, 0.21), axis='Y')
    bu = mk(f'{T}_Bumpers', bumpers, bevel=0.010, seg=2)

    # ---- safety laser scanners (diagonal corners) + photocells (other corners)
    def sensors(p):
        for sx, e in ((1, 1), (-1, -1)):
            x = sx * (hw - 0.10)
            bx(p, x - 0.012 * 6, x + 0.012 * 6, e * hl, e * (hl + 0.012), 0.10, 0.30, 'DarkSteel')   # bracket
            bx(p, x - 0.07, x + 0.07, e * (hl + 0.012), e * (hl + 0.115), 0.115, 0.235, 'Yellow')
            p.lathe([(0.235, 0.058), (0.285, 0.058)], 18, 'ScanWin', center=(x, e * (hl + 0.075), 0), axis='Z')
            p.lathe([(0.285, 0.062), (0.300, 0.062), (0.305, 0.05)], 18, 'Yellow',
                    center=(x, e * (hl + 0.075), 0), axis='Z')
        for sx, e in ((-1, 1), (1, -1)):
            x = sx * (hw - 0.10)
            bx(p, x - 0.03, x + 0.03, e * hl, e * (hl + 0.01), 0.17, 0.27, 'DarkSteel')
            bx(p, x - 0.025, x + 0.025, e * (hl + 0.01), e * (hl + 0.06), 0.185, 0.255, 'SensorBody')
            p.cylinder((x, e * (hl + 0.06), 0.22), (x, e * (hl + 0.064), 0.22), 0.012, 'LensR', n=10)
    se = mk(f'{T}_Sensors', sensors, bevel=0.004)

    # ---- electrical cabinet on the front (+Y) end
    y0, y1 = cab_extent(S)
    cab = 'Cabinet' + T

    def cabinet(p):
        bx(p, -0.52, 0.52, y0 + 0.01, y1 - 0.01, CH_Z1 + 0.006, CH_Z1 + 0.06, 'DarkSteel')      # plinth
        bx(p, -0.55, 0.55, y0, y1, CH_Z1 + 0.06, CAB_TOP - 0.02, cab)
        bx(p, -0.57, 0.57, y0 - 0.015, y1 + 0.02, CAB_TOP - 0.02, CAB_TOP, cab)                  # roof
        bx(p, -0.50, 0.50, y1 - 0.02, y1 + 0.006, CH_Z1 + 0.10, CAB_TOP - 0.06, cab)              # door
    cb = mk(f'{T}_Cabinet', cabinet, bevel=0.008, seg=2)

    _, (sw, sh), (scx, syf, scz) = screen_frame(S)

    def cab_details(p):
        yd = y1 + 0.006
        for z in (0.55, 1.02):                                                               # hinges (left = +X)
            p.cylinder((0.505, yd + 0.008, z - 0.05), (0.505, yd + 0.008, z + 0.05), 0.008, 'DarkSteel', n=8)
        bx(p, -0.46, -0.44, yd, yd + 0.02, 0.62, 0.78, 'DarkSteel')                           # handle
        bx(p, -0.47, -0.43, yd, yd + 0.006, 0.60, 0.80, 'Plastic')
        bx(p, scx - sw / 2 - 0.02, scx + sw / 2 + 0.02, yd, yd + 0.012, scz - sh / 2 - 0.02, scz + sh / 2 + 0.03,
           'Plastic')                                                                        # HMI bezel
        bx(p, scx - sw / 2, scx + sw / 2, yd + 0.012, syf, scz - sh / 2, scz + sh / 2, 'Screen' + T)
        for x, z, m in ((-0.14, 1.02, 'LensG'), (-0.14, 0.95, 'LensW'), (-0.22, 1.02, 'Plastic'),
                        (-0.22, 0.95, 'Plastic')):
            p.cylinder((x, yd, z), (x, yd + 0.012, z), 0.022, 'Plastic', n=12)
            p.cylinder((x, yd + 0.012, z), (x, yd + 0.022, z), 0.016, m, n=12)
        p.cylinder((-0.33, yd, 0.985), (-0.33, yd + 0.008, 0.985), 0.045, 'Yellow', n=16)      # e-stop
        p.cylinder((-0.33, yd + 0.008, 0.985), (-0.33, yd + 0.030, 0.985), 0.018, 'Plastic', n=12)
        p.lathe([(0.030, 0.035), (0.045, 0.033), (0.052, 0.02), (0.054, 0.0)], 16, 'EStop',
                center=(-0.33, yd, 0.985), axis='Y', cap0=True, cap1=True)
        # main isolator on the right (-X) side face
        bx(p, -0.556, -0.55, (y0 + y1) / 2 - 0.06, (y0 + y1) / 2 + 0.06, 0.92, 1.04, 'Yellow')
        p.cylinder((-0.556, (y0 + y1) / 2, 0.98), (-0.585, (y0 + y1) / 2, 0.98), 0.03, 'EStop', n=12)
        bx(p, -0.60, -0.585, (y0 + y1) / 2 - 0.012, (y0 + y1) / 2 + 0.012, 0.93, 1.03, 'EStop')
        # louvres on the left (+X) side face
        for k in range(5):
            z = 0.50 + k * 0.035
            bx(p, 0.55, 0.558, y0 + 0.03, y1 - 0.03, z, z + 0.014, 'Plastic')
        # horn on the roof, cable glands under the plinth side
        p.cylinder((-0.34, (y0 + y1) / 2, CAB_TOP), (-0.34, (y0 + y1) / 2, CAB_TOP + 0.05), 0.035, 'Plastic', n=12)
        for x in (-0.3, -0.2, 0.2):
            p.cylinder((x, y0 + 0.02, CH_Z1 + 0.06), (x, y0 + 0.02, CH_Z1 + 0.03), 0.012, 'Plastic', n=8)
    cd = mk(f'{T}_CabDetails', cab_details, bevel=0.002)

    # ---- under the chassis: wheel blocks, current collector
    def under(p):
        for _, (wx, wy, _) in wheel_nodes(S).items():
            for s in (1, -1):
                bx(p, wx + s * 0.058, wx + s * 0.075, wy - 0.27, wy + 0.27, 0.035, CH_Z0 + 0.03, 'DarkSteel')
                bx(p, wx - 0.075, wx + 0.075, wy + s * 0.15, wy + s * 0.27, 0.035, CH_Z0 + 0.03, 'DarkSteel')
            for s in (1, -1):
                p.cylinder((wx + s * 0.075, wy, WZ), (wx + s * 0.09, wy, WZ), 0.05, 'DarkSteel', n=12)
        yc = -(hl - 0.65)
        bx(p, -0.06, 0.06, yc - 0.09, yc + 0.09, CH_Z0 - 0.012, CH_Z0 + 0.01, 'DarkSteel')
        bx(p, -0.014, 0.014, yc - 0.014, yc + 0.014, 0.04, CH_Z0 - 0.012, 'DarkSteel')
        bx(p, -0.03, 0.03, yc - 0.13, yc + 0.13, 0.014, 0.04, 'Collector')
        p.cylinder((0.02, yc, 0.04), (0.02, yc, CH_Z0), 0.008, 'Plastic', n=6)
    un = mk(f'{T}_Under', under, bevel=0.003)

    # ---- small hardware (dropped at LOD1)
    def hardware(p):
        for s in (1, -1):
            for i in range(nsec):
                yc, L = S['sections'][i]
                hl_ = min(1.1, 0.5 * L) / 2 - 0.03
                for dy in (-hl_, hl_):
                    for dz in (-0.05, 0.05):
                        hexbolt(p, (s * (hw + 0.006), yc + dy, zc + dz), (s, 0, 0), 0.004, 0.008)
        for _, idx in S['bays']:
            b0, b1 = bay_ext(S, idx)
            for yy in (b0 + 0.025, b1 - 0.025):
                for x in (-bw / 2 + 0.15, -bw / 4, 0.0, bw / 4, bw / 2 - 0.15):
                    hexbolt(p, (x, yy + 0.008, FR_TOP + 0.03), (0, 1, 0), 0.006, 0.009)
    hwr = mk(f'{T}_Hardware', hardware, bevel=0.0)

    # ---- wheels (nodes): flanged, flange on the gauge (inner) side
    wheels = {}
    for name, (wx, wy, wz) in wheel_nodes(S).items():
        s = 1 if wx > 0 else -1                     # inner direction is -s
        prof_u = [(-0.075, 0.035), (-0.075, 0.065), (-0.045, 0.078), (-0.045, 0.150), (-0.040, WHEEL_R),
                  (0.028, WHEEL_R), (0.032, 0.168), (0.036, 0.182), (0.048, 0.182), (0.052, 0.170),
                  (0.052, 0.080), (0.068, 0.068), (0.068, 0.035)]
        prof = [(-s * u, r) for u, r in prof_u]

        def wfn(p, prof=prof, wx=wx, wy=wy):
            p.lathe(prof, 18, 'Wheel' + T, center=(wx, wy, WZ), axis='X')
        wheels[name] = mk(f'{T}_{name}', wfn, bevel=0.0, sharp=50)

    # ---- beacon (node)
    bxy = (0.34, (y0 + y1) / 2)

    def beacon(p):
        p.lathe([(CAB_TOP, 0.055), (CAB_TOP + 0.035, 0.055), (CAB_TOP + 0.04, 0.048)], 16, 'Plastic',
                center=(*bxy, 0), axis='Z')
        p.lathe([(CAB_TOP + 0.04, 0.045), (CAB_TOP + 0.10, 0.045), (CAB_TOP + 0.13, 0.036),
                 (CAB_TOP + 0.148, 0.018), (CAB_TOP + 0.152, 0.003)], 16, 'LensA', center=(*bxy, 0), axis='Z')
    bc = mk(f'{T}_Beacon', beacon, bevel=0.0, sharp=50)

    # The skirt encloses the wheels (only the bottom 5 cm show), so they are
    # part of the body: separate spinning nodes would cost 4 draw calls for
    # nothing visible.
    body = [sk, pn, fr, ro, ho, gd, st, ez, bu, se, cb, cd, un, hwr, *wheels.values()]
    nodes = {None: body, 'Beacon': [bc]}
    pivots = {'Beacon': (*bxy, CAB_TOP)}
    sockets = {'Deck': (0.0, 0.0, ROLL_TOP)}
    yc = sum(conveyor_ext(S)) / 2
    for name, x in S['slots'].items():
        sockets[name] = (x, yc, ROLL_TOP)
    pc = KM.Piece(S['name'], 'fixed', nodes, levels=3, pivots=pivots, sockets=sockets,
                  parents={n: 'Deck' for n in S['slots']})
    pc.spec = S
    return pc


def build_proxies(S):
    """Flat roller-bed planes for LOD2 (baked in the atlas, used only at LOD2)."""
    T, bw = S['tag'], S['bay_w']
    out = []
    for i in range(len(S['sections'])):
        y0, y1 = roller_span(S, i)
        x0, x1 = -(bw / 2 - 0.06), bw / 2 - 0.06
        z = ROLL_Z + 0.025

        def fn(p, y0=y0, y1=y1, x0=x0, x1=x1, z=z):
            p.add([V((x0, y0, z)), V((x1, y0, z)), V((x1, y1, z)), V((x0, y1, z))], [(0, 1, 2, 3)], 'Proxy' + T,
                  recalc=False)
        out.append(mk(f'{T}_Proxy{i}', fn, bevel=0.0))
    return out


# -----------------------------------------------------------------------------
# LODs (custom: wheels fold into the body at LOD1, rollers become proxies at LOD2)
# ratios of the LOD0 part per extra level; None = dropped
# -----------------------------------------------------------------------------
LOD_PLAN = {
    'Skirt': (1.0, 0.6), 'Panels': (1.0, None), 'Frames': (0.3, 0.12), 'Rollers': (0.3, None),
    'Hoods': (0.35, 0.12), 'Guides': (0.4, 0.25), 'Stops': (0.45, None), 'Ends': (1.0, 1.0),
    'Bumpers': (0.3, 0.1), 'Sensors': (0.3, 0.12), 'Cabinet': (0.5, 0.3), 'CabDetails': (0.25, None),
    'Under': (0.3, None), 'Hardware': (None, None),
}
WHEEL_LOD = (None, None)          # hidden behind the skirt beyond close range
BEACON_LOD = (0.6, 0.3)


def car_lods(pc, proxies):
    T = pc.spec['tag']
    body = pc.nodes[None]
    lv = {1: [], 2: []}
    for p in body:
        key = p.name.split('_', 1)[1]
        plan = WHEEL_LOD if key.startswith('Wheel_') else LOD_PLAN[key]
        for lvl in (1, 2):
            r = plan[lvl - 1]
            if r is not None:
                lv[lvl].append(K.make_lod(p, r, f'{p.name}_L{lvl}'))
    bsrc = pc.nodes['Beacon'][0]
    b1 = K.make_lod(bsrc, BEACON_LOD[0], 'Beacon_LOD1', sharp=50)
    lv[2].append(K.make_lod(bsrc, BEACON_LOD[1], f'{T}_Beacon_L2', sharp=50))
    lv[2] += [K.make_lod(p, 1.0, f'{p.name}_L2') for p in proxies]
    pc.meshes = {None: [K.join(body, f'{pc.name}_LOD0'), K.join(lv[1], f'{pc.name}_LOD1'),
                        K.join(lv[2], f'{pc.name}_LOD2')]}
    bsrc.name = bsrc.data.name = 'Beacon_LOD0'
    pc.meshes['Beacon'] = [bsrc, b1]


# =============================================================================
# Rail_1m (tiling segment along Y, [0, 1])
# =============================================================================

def build_rail():
    L = 1.0
    open_ends = ends('y', 0.0, L)

    def rails(p):
        for s in (1, -1):
            c = s * RX
            prof = K.fillet_closed([(c - 0.025, -0.004), (c + 0.025, -0.004), (c + 0.025, RAIL_TOP),
                                    (c - 0.025, RAIL_TOP)], [0, 0, 0.004, 0.004], 2)
            p.extrude(prof, 'Y', 0.0, L, 'RailSteel')
    ra = mk('R_Rails', rails, bevel=0.0, cull=open_ends)

    def plate(p, x0, x1):
        prof = K.fillet_closed([(x0, -0.004), (x1, -0.004), (x1, 0.008), (x0, 0.008)], [0, 0, 0.003, 0.003], 1)
        p.extrude(prof, 'Y', 0.0, L, 'RPlate')

    def plates(p):
        for s in (1, -1):
            c = s * RX
            plate(p, *sorted((c + s * 0.027, c + s * 0.17)))          # outer cover plate
            plate(p, *sorted((c - s * 0.070, c - s * 0.21)))          # inner plate (flangeway 45 mm)
        plate(p, -0.11, -0.012)                                      # bus-bar duct covers
        plate(p, 0.012, 0.11)
    pl = mk('R_Plates', plates, bevel=0.0, cull=open_ends)

    def grooves(p):
        for s in (1, -1):
            c = s * RX
            bx(p, c - s * 0.070, c - s * 0.025, 0.0, L, -0.004, 0.001, 'RGroove')
            bx(p, c + s * 0.025, c + s * 0.027, 0.0, L, -0.004, 0.001, 'RGroove')
        bx(p, -0.012, 0.012, 0.0, L, -0.004, 0.001, 'RGroove')
    gr = mk('R_Groove', grooves, bevel=0.0, cull=open_ends)

    def bolts(p):
        for y in (0.25, 0.75):
            for s in (1, -1):
                c = s * RX
                for x in (c + s * 0.12, c - s * 0.16):
                    p.cylinder((x, y, 0.008), (x, y, 0.010), 0.011, 'RPlate', n=10)
            for x in (-0.07, 0.07):
                p.cylinder((x, y, 0.008), (x, y, 0.010), 0.009, 'RPlate', n=10)
    bo = mk('R_Bolts', bolts, bevel=0.0)
    return KM.Piece('Rail_1m', 'segment', [ra, pl, gr, bo], levels=2, lod={'R_Bolts': (None,)},
                    ghosts=[('Rail_1m', (0, L, 0)), ('Rail_1m', (0, -L, 0))])


# relative texel density in the shared atlas
TEXEL_WEIGHTS = {
    'Skirt': 0.55, 'Panels': 0.6, 'Frames': 0.75, 'Rollers': 0.55, 'Hoods': 0.8, 'Guides': 0.9,
    'Stops': 1.2, 'Ends': 0.6, 'Bumpers': 1.1, 'Sensors': 1.6, 'Cabinet': 1.1, 'CabDetails': 1.8,
    'Under': 0.35, 'Hardware': 0.6, 'Beacon': 1.6, 'Wheel': 0.45, 'Proxy': 0.3,
    'R_Rails': 2.4, 'R_Plates': 0.9, 'R_Groove': 0.3, 'R_Bolts': 1.0,
}


def texel_weights(objs):
    w = {}
    for o in objs:
        if o.name in TEXEL_WEIGHTS:
            w[o.name] = TEXEL_WEIGHTS[o.name]
            continue
        key = o.name.split('_', 1)[1] if '_' in o.name else o.name
        for k, v in TEXEL_WEIGHTS.items():
            if key.startswith(k):
                w[o.name] = v
                break
    return w


# =============================================================================
# Preview (render-only props, never exported)
# =============================================================================

def preview_materials():
    mats = {}
    m = bpy.data.materials.new('BoardStack')
    nb = K.NB(m, period={})
    x, y, z = nb.xyz(nb.obj)
    _, _, nz = nb.xyz(nb.geo.outputs['Normal'])
    side = nb.mr(nb.m('ABSOLUTE', nz), 0.6, 0.4)
    f = nb.m('FRACT', nb.m('DIVIDE', z, 0.019))
    face = nb.m('LESS_THAN', f, 0.14)
    core = nb.mixc(nb.noise(120, 4, 0.7), (0.40, 0.29, 0.17), (0.55, 0.42, 0.26))
    sidecol = nb.mixc(face, core, (0.74, 0.74, 0.72))
    col = nb.mixc(side, (0.76, 0.76, 0.74), sidecol)
    K.finalize_material(nb, col, nb.mixf(side, 0.3, 0.7), 0.0,
                        nb.bump(nb.m('MULTIPLY', face, side), 0.3, 0.001))
    mats['BoardStack'] = m
    m = bpy.data.materials.new('PkgTop')
    nb = K.NB(m, period={})
    K.finalize_material(nb, nb.mixc(nb.noise(10, 5), (0.30, 0.22, 0.13), (0.36, 0.27, 0.16)), 0.8, 0.0)
    mats['PkgTop'] = m
    m = bpy.data.materials.new('Strap')
    nb = K.NB(m, period={})
    K.finalize_material(nb, (0.02, 0.02, 0.022), 0.4, 0.0)
    mats['Strap'] = m
    return mats


def package(coll, mats, base, h, name, length=2.85, width=2.10):
    """Pallet-less board package, length (X, along the deck) x width (Y) x h,
    bottom-centre at base; straps across the boards."""
    x, y, z = base
    p = Part(name)
    p.box((x, y, z + h / 2), (length, width, h), 'BoardStack')
    n = max(2, round(length / 1.4))
    for k in range(n):
        xx = x - length / 2 + length * (k + 0.5) / n
        p.box((xx, y, z + h + 0.001), (0.025, width + 0.02, 0.002), 'Strap')
        for s in (1, -1):
            p.box((xx, y + s * (width / 2 + 0.0015), z + h / 2), (0.025, 0.002, h), 'Strap')
    ob = p.build(mats, coll=coll)
    finish(ob, 0.003, 1, 40)
    return ob


def preview_renders(scene, cars, rail, sockets_auth, quick=False, full_spp=160):
    KM.hide_kit(cars + [rail])
    props = KM.studio(scene, k=2.5, floor=140)
    spp = 32 if quick else full_spp
    res = (800, 500) if quick else (1600, 1000)
    out = lambda n: os.path.join(PREVIEW_DIR, f'railshuttle_{n}.png')      # noqa: E731
    pm = preview_materials()
    I = Matrix.Identity(4)

    track = K.collection('PreviewTrack')
    KM.strip(track, 30.0, rail, axis='y', base=Matrix.Translation((0, -15.0, 0)), seg_len=1.0)
    fig = KM.scale_figure(track, (3.7, -2.4, 0), 0.4)

    def stage(pc, loads, level=0, tag=''):
        c = K.collection(f'Preview_{pc.name}_L{level}{tag}')
        KM.place(pc, I, c, level)
        for sock, (h, ln) in loads.items():
            package(c, pm, sockets_auth[pc.name][sock], h, f'Pkg_{pc.name}_{sock}_{level}{tag}', ln)
        return c

    def only(c):
        for o in bpy.data.collections:
            if o.name.startswith('Preview_'):
                o.hide_render = o is not c

    by = {pc.name: pc for pc in cars}
    if 'RailShuttle' in by:
        pc = by['RailShuttle']
        pair = {'Slot_Front': (0.75, 2.85), 'Slot_Rear': (0.55, 2.85)}
        c = stage(pc, pair)
        only(c)
        hero = ((8.6, -8.4, 4.0), (0.0, 0.0, 0.5), 32)
        KM.render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
        fig.hide_render = True
        KM.render(scene, KM.camera(scene, 'Cam_Deck', (3.6, -3.6, 2.2), (0.8, -0.4, 0.45), 30), out('deck'), res, spp)
        KM.render(scene, KM.camera(scene, 'Cam_End', (2.6, 6.8, 1.8), (0.1, 1.2, 0.6), 36), out('end'), res, spp)
        KM.render(scene, KM.camera(scene, 'Cam_Low', (4.4, -4.6, 0.42), (1.6, -1.4, 0.18), 30), out('low'), res, spp)
        KM.render(scene, KM.camera(scene, 'Cam_Top', (0.0, 0.0, 20.0), (0.0, 0.0, 0.0), ortho=7.2), out('top'),
                  res, spp)
        KM.render(scene, KM.camera(scene, 'Cam_Side', (0.0, -14.0, 0.8), (0.0, 0.0, 0.8), ortho=7.2), out('side'),
                  res, spp)
        KM.render(scene, KM.camera(scene, 'Cam_Rail', (1.9, -8.6, 0.55), (1.0, -10.6, 0.0), 40), out('rail'),
                  (800, 500), spp)
        for lvl in (1, 2):
            only(stage(pc, pair, lvl))
            KM.render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (800, 500), spp)
        only(stage(pc, {'Slot_Center': (0.65, 5.70)}, 0, '_single'))
        KM.render(scene, KM.camera(scene, 'Cam_Single', *hero), out('single'), res, spp)
    return props


# =============================================================================
# Main
# =============================================================================

def rename_hierarchy(root, suffix):
    """Free the Unity names (Wheel_FL, Deck, ...) for the next car in this file."""
    for o in [root] + list(root.children_recursive):
        o.name = o.name + suffix
        if o.data is not None:
            o.data.name = o.name


def use_cpu(scene):
    """Cycles on the CPU only: no CUDA/OptiX device enabled, CPU denoiser."""
    scene.render.engine = 'CYCLES'
    prefs = bpy.context.preferences.addons['cycles'].preferences
    try:
        prefs.compute_device_type = 'NONE'
    except TypeError:
        pass
    scene.cycles.device = 'CPU'
    if hasattr(scene.cycles, 'denoising_use_gpu'):
        scene.cycles.denoising_use_gpu = False
    print('[railshuttle] Cycles device: CPU')


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--variant', default='all', choices=('all', 'main'))
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--device', default='gpu', choices=('gpu', 'cpu'),
                    help='Cycles device: gpu = OptiX/CUDA via the kit (default), cpu = CPU only')
    ap.add_argument('--cpu', dest='device', action='store_const', const='cpu',
                    help='shorthand for --device cpu (fallback if the GPU bake faults)')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    variants = ['main']
    export = args.variant == 'all'
    out_dir = OUT_DIR if export else os.path.join(BUILD_DIR, f'RailShuttle_{args.variant}')
    t0 = time.time()

    scene = K.reset_scene()
    if args.device == 'gpu':
        K.setup_cycles_gpu(scene)
    else:
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    build_materials(variants)
    cars = [build_car(SPECS[v]) for v in variants]
    proxies = {pc.name: build_proxies(pc.spec) for pc in cars}
    rail = build_rail()
    proxy_pc = KM.Piece('_Proxies', 'fixed', [p for ps in proxies.values() for p in ps], levels=1)
    everything = cars + [rail, proxy_pc]
    KM.bake_kit(everything, out_dir, PREFIX, args.bake, args.save,
                weights=texel_weights([p for pc in everything for p in pc.parts]))
    t_bake = time.time() - t0

    sockets_auth = {pc.name: dict(pc.sockets) for pc in cars}
    KM.build_lods(rail)
    KM.rotate_to_export([rail])
    KM.rig(rail)
    if export:
        K.export_fbx(rail.root, os.path.join(out_dir, 'Rail_1m.fbx'))
    for pc in cars:
        car_lods(pc, proxies[pc.name])
        KM.report([pc])
        KM.rotate_to_export([pc])
        KM.rig(pc)
        if export:
            K.export_fbx(pc.root, os.path.join(out_dir, f'{pc.name}.fbx'))
        print(f'[railshuttle] {pc.name} sockets (Unity x, y, z): ' + ', '.join(
            f'{n}=({x:.3f}, {z:.3f}, {y:.3f})' for n, (x, y, z) in sockets_auth[pc.name].items()))
        if len(cars) > 1:
            rename_hierarchy(pc.root, '.' + pc.spec['tag'])
    KM.report([rail])
    K.remove_objects([p for ps in proxies.values() for p in ps])

    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend' if export else f'{PREFIX}_{args.variant}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(scene, cars, rail, sockets_auth, args.quick, 160 if args.device == 'gpu' else 64)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[railshuttle] done in {time.time() - t0:.1f}s (bake phase {t_bake:.1f}s)')


if __name__ == '__main__':
    main()
