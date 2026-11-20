"""
SimuLean asset: generic car body in the trim-assembly state (compact SUV / hatchback, no brand), for
an assembly-line model where dozens to a hundred bodies are on screen.  One folder, one atlas:

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/car_body.py --
            [--bake 4096] [--save 2048] [--quick] [--cpu] [--no-render] [--look]

Outputs  (Assets/SimuLean.Unity/Art/Models/CarBody/)
    CarBody.fbx      the painted body: doors on, side windows open (the cabin is a hollow painted
                     shell you can see into), tinted windscreen and rear window, dashboard, mirrors,
                     lights; NO wheels (empty arches) -- CarWheel is an option piece
    CarWheel.fbx     one wheel (tyre 215/60 R17, dark five-spoke rim), pivot = hub centre, axle along
                     local X, the outer face towards +X
    CarBody_{Albedo,MetallicSmoothness,Normal,Occlusion,Emission}.png   white paint (CarBody.mat)
    CarBody_Red_*.png, CarBody_Blue_*.png   paint colour sets (same UVs): CarBody_Red.mat / _Blue.mat
    Tools/Blender/build/CarBody.blend, Tools/Blender/previews/carbody_*.png

Frame (kit rules): authored +X = Unity +X (right-hand side), authored +Y = Unity +Z (FORWARD, the
nose), authored +Z = Unity +Y.  Pivot = the ground under the body centre as if it stood on its wheels
(ground clearance 0.19 m).  Metres.  An authored yaw Rz(t) is Unity Euler(0, -t, 0).

CarBody  4.30 L x 1.80 W x 1.63 H; wheelbase 2.65 m (axles at z = +1.30 / -1.35), track 1.56 m,
         sills at 0.19-0.31 m (jacking points y = +-0.75: where an AssemblySkid's pads carry it),
         cabin floor 0.40 m, belt line 1.00 m.  Left-hand drive.
    Sockets (bare Transforms):
    ConsoleSocket   (0, 0.40, 0.00): cabin floor between the front seats; a CentralConsole goes here
                    with identity rotation (its long side along the car)
    SteeringSocket  (-0.37, 0.97, 0.30): steering-wheel hub (driver side, left); the socket is tilted
                    25 deg so a SteeringWheel (axis along local Z) sits with its face towards the driver
    DoorSocket_L / DoorSocket_R  (-+0.91, 0.72, 0.30): outer skin at the front-door centre, local +Z
                    pointing OUT of the car (yaw +-90 deg): where a door-line worker / tool stands
    Wheel_FL / FR / RL / RR  (+-0.78, 0.345, +1.30 / -1.35): hub centres for the CarWheel option piece;
                    the left sockets are turned 180 deg about Y so CarWheel's outer face points out
LOD0 / LOD1 / LOD2 = the modelled body (<= 8 k) / hand-made proxies baked from it (<= 2 k / <= 300),
one renderer each.
"""

import os
import sys
import shutil
import argparse
import time
from math import pi, radians

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
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'CarBody')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
PREFIX = 'CarBody'
GAP = 0.0025

PAINTS = {'White': (0.78, 0.78, 0.76), 'Red': (0.42, 0.012, 0.010), 'Blue': (0.012, 0.055, 0.22)}
AXF, AXR, WR = 1.30, -1.35, 0.345
MATS = {}
WEIGHTS = {}

# body stations: (y, sill z, belt z, top z, half width, roof half width)
STATIONS = [(-2.15, 0.30, 0.60, 0.61, 0.80, 0.79), (-2.10, 0.22, 0.95, 1.50, 0.87, 0.66),
            (-1.98, 0.20, 0.98, 1.58, 0.89, 0.68), (-1.40, 0.19, 1.00, 1.62, 0.90, 0.70),
            (-0.40, 0.19, 1.00, 1.63, 0.90, 0.71), (0.15, 0.19, 1.00, 1.60, 0.90, 0.71),
            (0.50, 0.19, 0.99, 1.38, 0.90, 0.74), (0.85, 0.20, 0.98, 1.00, 0.89, 0.80),
            (1.40, 0.22, 0.94, 0.95, 0.88, 0.80), (1.90, 0.26, 0.88, 0.89, 0.86, 0.78),
            (2.08, 0.32, 0.80, 0.81, 0.82, 0.74), (2.15, 0.38, 0.66, 0.67, 0.76, 0.70)]
CABIN = [(-1.90, 0.40, 0.98, 1.52, 0.82, 0.62), (-1.40, 0.40, 1.00, 1.56, 0.83, 0.64),
         (0.15, 0.40, 1.00, 1.54, 0.83, 0.65), (0.50, 0.40, 0.98, 1.30, 0.83, 0.67),
         (0.72, 0.42, 0.95, 1.02, 0.82, 0.72)]


def section(y, zb, belt, top, w, wr, full=True):
    h = max(top - belt, 0.0)
    f = min(1.0, h / 0.5)
    if full:
        right = [(w - 0.10, zb), (w - 0.02, zb + 0.08), (w, zb + 0.25), (w + 0.012, (zb + belt) / 2 + 0.05),
                 (w - 0.005, belt - 0.02), (w - 0.03, belt), ((w - 0.03 + wr) / 2, belt + 0.5 * h),
                 (wr, top - 0.10 * f), (wr - 0.05 * f, top - 0.025 * f), (wr - 0.17 * f - 0.01, top)]
    else:
        right = [(w - 0.08, zb), (w, zb + 0.15), (w, belt - 0.02), (wr, top - 0.10 * f), (wr - 0.12 * f - 0.01, top)]
    left = [(-x, z) for x, z in reversed(right)]
    return [V((x, y, z)) for x, z in right + left]


# =============================================================================
# materials
# =============================================================================

def body_paint(name, color):
    """Clear-coated paint with every non-paint zone of the shell as a mask: tinted windscreen and
    rear window, black cladding / bumper lowers / B-pillar / window surrounds, panel gaps, lights,
    grille, door handles; the cabin inside is painted body colour, its floor dark sealer."""
    mat = M._new(name)
    nb = NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    nx, ny, nz = nb.xyz(nb.geo.outputs['Normal'])
    ax = nb.m('ABSOLUTE', x)
    band, rect, mul = LU.band, LU.rect, LU.mul
    tone = nb.mr(nb.noise(1.5, 3), 0.3, 0.7, 0.97, 1.02)
    col = nb.multc(color, nb.vec(tone, tone, tone))
    rough = 0.12
    metal = 0.0
    black = (0.012, 0.012, 0.013)
    outer = nb.mr(ax, 0.70, 0.74)                                    # the outer side skin
    clad = nb.m('MAXIMUM', mul(nb, outer, nb.mr(z, 0.36, 0.33)),     # lower cladding / sill
                mul(nb, nb.mr(nb.m('ABSOLUTE', ny), 0.6, 0.8), nb.mr(z, 0.44, 0.41),
                    nb.m('MAXIMUM', nb.mr(y, 2.0, 2.05), nb.mr(y, -2.0, -2.05))))
    arch = None
    for ya in (AXF, AXR):
        d = nb.vm('LENGTH', nb.vec(nb.m('SUBTRACT', y, ya), nb.m('SUBTRACT', z, 0.36), 0.0))
        a = mul(nb, nb.mr(d, 0.47, 0.45), outer)
        arch = a if arch is None else nb.m('MAXIMUM', arch, a)
    bpil = mul(nb, nb.mr(z, 1.02, 1.04), band(nb, y, -0.05, 0.05, 0.004), outer)
    surround = mul(nb, outer, band(nb, z, 1.025, 0.012, 0.003), nb.mr(y, 0.75, 0.70), nb.mr(y, -1.85, -1.80))
    trim = nb.m('MAXIMUM', nb.m('MAXIMUM', clad, arch), nb.m('MAXIMUM', bpil, surround))
    # glass: windscreen (on the slope) and rear window
    ws = mul(nb, nb.mr(y, 0.18, 0.22), nb.mr(y, 0.83, 0.79), nb.mr(z, 1.03, 1.05), nb.mr(ax, 0.68, 0.64))
    rw = mul(nb, nb.mr(y, -1.99, -2.02), nb.mr(z, 1.08, 1.10), nb.mr(z, 1.50, 1.47), nb.mr(ax, 0.66, 0.62))
    glass = nb.m('MAXIMUM', ws, rw)
    # lights and grille
    head = mul(nb, nb.mr(y, 1.98, 2.02), band(nb, z, 0.76, 0.05, 0.01), band(nb, ax, 0.63, 0.15, 0.01))
    tail = mul(nb, nb.mr(y, -2.04, -2.08), band(nb, z, 1.05, 0.07, 0.01), band(nb, ax, 0.72, 0.10, 0.01))
    grille = mul(nb, nb.mr(y, 2.08, 2.11), band(nb, z, 0.52, 0.10, 0.01), nb.mr(ax, 0.50, 0.46))
    slats = nb.mr(nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', z, 0.03)), 0.5)), 0.32, 0.22)
    # panel gaps: door shut lines, hood, tailgate
    gap_y = None
    for yg in (0.80, -0.05, -0.97):
        g = band(nb, y, yg, 0.0015, 0.0012)
        gap_y = g if gap_y is None else nb.m('MAXIMUM', gap_y, g)
    gaps = mul(nb, gap_y, outer, nb.mr(z, 0.33, 0.36), nb.mr(z, 1.56, 1.52))
    gaps = nb.m('MAXIMUM', gaps, mul(nb, outer, band(nb, z, 0.33, 0.0015, 0.0012), nb.mr(y, 0.80, 0.78),
                                    nb.mr(y, -0.97, -0.95)))
    hood = mul(nb, nb.mr(nz, 0.5, 0.8), band(nb, y, 0.86, 0.0015, 0.0012))
    hood = nb.m('MAXIMUM', hood, mul(nb, band(nb, ax, 0.80, 0.0015, 0.0012), nb.mr(y, 0.86, 0.88), nb.mr(z, 0.85, 0.9)))
    tg = mul(nb, nb.mr(y, -2.0, -2.05), band(nb, z, 0.66, 0.0015, 0.0012))
    gaps = nb.m('MAXIMUM', gaps, nb.m('MAXIMUM', hood, tg))
    handles = None
    for yh in (0.05, -0.85):
        hd = mul(nb, outer, band(nb, y, yh, 0.07, 0.003), band(nb, z, 0.93, 0.012, 0.003))
        handles = hd if handles is None else nb.m('MAXIMUM', handles, hd)
    # inside of the cabin (faces whose normal points inwards) and its floor
    inside = nb.mr(nb.m('ADD', nb.m('MULTIPLY', nx, x), nb.m('MULTIPLY', ny, nb.m('SUBTRACT', y, -0.6))), -0.05, -0.15)
    floor = mul(nb, nb.mr(nz, 0.7, 0.9), nb.mr(z, 0.45, 0.42), nb.mr(ax, 0.84, 0.80))
    col = nb.mixc(nb.m('MULTIPLY', inside, 0.25), col, nb.multc(col, (0.8, 0.8, 0.8)))
    col = nb.mixc(floor, col, (0.035, 0.036, 0.04))
    col = nb.mixc(trim, col, black)
    col = nb.mixc(glass, col, (0.010, 0.012, 0.015))
    col = nb.mixc(grille, col, nb.mixc(slats, (0.004, 0.004, 0.004), (0.03, 0.03, 0.032)))
    bezel = mul(nb, nb.mr(y, 1.97, 2.0), band(nb, z, 0.76, 0.07, 0.01), band(nb, ax, 0.63, 0.17, 0.01))
    col = nb.mixc(bezel, col, black)
    col = nb.mixc(head, col, (0.80, 0.82, 0.85))
    col = nb.mixc(tail, col, (0.35, 0.01, 0.008))
    col = nb.mixc(handles, col, nb.multc(color, (0.9, 0.9, 0.9)))
    col = nb.mixc(nb.m('MULTIPLY', gaps, 0.95), col, (0.004, 0.004, 0.004))
    col = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.06), 0.35), col, (0.02, 0.02, 0.02))
    rough = nb.mixf(trim, 0.12, 0.6)
    rough = nb.mixf(floor, rough, 0.8)
    rough = nb.mixf(glass, rough, 0.04)
    rough = nb.mixf(nb.m('MAXIMUM', head, tail), rough, 0.08)
    emit = nb.mixc(head, (0, 0, 0), (0.45, 0.46, 0.48))
    h = nb.m('SUBTRACT', nb.m('MULTIPLY', nb.noise(300, 3, 0.5), 0.05), nb.m('MULTIPLY', gaps, 1.0))
    h = nb.m('ADD', h, nb.m('MULTIPLY', nb.m('MULTIPLY', trim, nb.noise(400, 2)), 0.2))
    h = nb.m('SUBTRACT', h, nb.m('MULTIPLY', nb.m('MULTIPLY', grille, slats), 0.8))
    h = nb.m('ADD', h, nb.m('MULTIPLY', handles, 0.6))
    nrm = nb.bump(h, 0.5, 0.0015)
    return finalize_material(nb, col, rough, metal, nrm, emit=emit)


def build_materials():
    MATS.clear()
    MATS.update({
        'Paint': body_paint('Paint', PAINTS['White']),
        'Black': M.plastic('Black', color=(0.014, 0.014, 0.015), rough=0.5),
        'Dash': M.plastic('Dash', color=(0.025, 0.025, 0.027), rough=0.6, grain=0.12),
        'Tyre': M.tire('Tyre', WR - 0.003, lugs=48, chevron=3.0),
        'Rim': M.dark_steel('Rim', color=(0.10, 0.10, 0.105), rough=0.3, metal=0.9),
        'Disc': M.dark_steel('Disc', color=(0.30, 0.30, 0.30), rough=0.45, metal=0.9),
        'Proxy': M.plastic('Proxy'),
    })


def mk(name, fn, bevel=0.004, seg=1, weight=1.0, cutters=None):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        cs = []
        for c in cutters:
            cp = Part(name + '_cut')
            c(cp)
            cs.append(cp.build({'Cut': MATS['Proxy']}))
        K.boolean(ob, cs)
    finish(ob, bevel, seg, 35, 35)
    WEIGHTS[ob.name] = weight
    return ob


def proxy(name, fn, weight):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    finish(ob, 0, 1, 40, 35)
    WEIGHTS[ob.name] = weight
    return ob


# =============================================================================
# body
# =============================================================================

def build_body():
    def shell(p):
        p.loft([section(*s) for s in STATIONS], 'Paint')

    def cabin(p):
        p.loft([section(*s) for s in CABIN], 'Cut')

    def arches(p):
        for ya in (AXF, AXR):
            for s in (1, -1):
                p.cylinder((s * 0.56, ya, 0.36), (s * 1.0, ya, 0.36), 0.40, 'Cut', n=28)

    def windows(p):
        polys = [[(0.0, 1.045), (0.66, 1.045), (0.13, 1.545), (0.0, 1.545)],
                 [(-0.10, 1.045), (-0.85, 1.045), (-0.85, 1.555), (-0.10, 1.555)],
                 [(-0.95, 1.045), (-1.76, 1.07), (-1.80, 1.47), (-0.95, 1.555)]]
        for poly in polys:
            for s in (1, -1):
                p.extrude(poly, 'X', s * 0.55, s * 1.05, 'Cut')
        # trim state: windscreen and rear window not fitted yet (open, so the cabin is lit and visible)
        p.extrude([(0.25, 1.47), (0.76, 1.02), (1.2, 1.02), (1.2, 2.0), (0.25, 2.0)], 'X', -0.62, 0.62, 'Cut')
        p.extrude([(-2.4, 1.09), (-1.95, 1.09), (-1.95, 1.48), (-2.4, 1.48)], 'X', -0.60, 0.60, 'Cut')

    def parts(p):
        bx = LU.bx
        bx(p, -0.80, 0.80, 0.42, 0.74, 0.62, 0.98, 'Dash')                       # dashboard
        bx(p, -0.55, -0.20, 0.40, 0.46, 0.92, 1.03, 'Dash')                       # instrument hood
        p.cylinder((-0.37, 0.44, 0.88), (-0.37, 0.36, 0.95), 0.03, 'Dash', n=10)   # column stub
        bx(p, -0.12, 0.12, 0.40, 0.72, 0.40, 0.62, 'Dash')                       # centre stack
        for s in (1, -1):                                                         # mirrors
            p.box((s * 1.0, 0.66, 1.08), (0.20, 0.08, 0.12), 'Black')
            p.cylinder((s * 0.88, 0.66, 1.04), (s * 0.92, 0.66, 1.06), 0.02, 'Black', n=8)
        for s in (1, -1):                                                         # seat rails
            bx(p, s * 0.25, s * 0.55, -0.45, 0.25, 0.40, 0.43, 'Black')
        for ya in (AXF, AXR):                                                     # wheelhouse tubs
            for s in (1, -1):
                p.cylinder((s * 0.50, ya, 0.36), (s * 0.80, ya, 0.36), 0.42, 'Black', n=20)

    body = mk('CB_Shell', shell, 0.006, 2, weight=1.0, cutters=[cabin, arches, windows])
    extra = mk('CB_Parts', parts, 0.006, 1, weight=0.6)

    def lod1(p):
        p.loft([section(*s) for s in STATIONS], 'Proxy')
        for s in (1, -1):
            p.box((s * 1.0, 0.66, 1.08), (0.20, 0.08, 0.12), 'Proxy')

    def lod2(p):
        st = [STATIONS[i] for i in (0, 1, 2, 4, 6, 7, 9, 11)]
        p.loft([section(*s, full=False) for s in st], 'Proxy')
    l1 = proxy('CB_P1', lod1, 0.45)
    l2 = proxy('CB_P2', lod2, 0.25)
    x = 0.91
    sockets = {'ConsoleSocket': (0, 0.0, 0.40), 'SteeringSocket': (-0.37, 0.30, 0.97),
               'DoorSocket_L': (-x, 0.30, 0.72), 'DoorSocket_R': (x, 0.30, 0.72),
               'Wheel_FL': (-0.78, AXF, WR), 'Wheel_FR': (0.78, AXF, WR),
               'Wheel_RL': (-0.78, AXR, WR), 'Wheel_RR': (0.78, AXR, WR)}
    u = LU.Unit('CarBody', [body, extra], l1, l2, (0.15, 0.30), sockets)
    u.rot = {'SteeringSocket': (radians(-25), 0, 0), 'DoorSocket_L': (0, 0, pi / 2),
             'DoorSocket_R': (0, 0, -pi / 2), 'Wheel_FL': (0, 0, pi), 'Wheel_RL': (0, 0, pi)}
    return u


def build_wheel():
    def wheel(p):
        r, h = WR, 0.108
        pts = [(-h * 0.2, 0.0), (-h * 0.2, 0.05), (h * 0.55, 0.06), (h * 0.62, 0.20), (h * 0.8, 0.215),
               (h * 0.95, 0.225), (h, 0.24), (h, 0.30), (h * 0.85, r - 0.006), (0.0, r), (-h * 0.85, r - 0.006),
               (-h, 0.30), (-h, 0.24), (-h * 0.8, 0.215), (-h * 0.5, 0.20), (-h * 0.4, 0.0)]
        mats = ['Rim', 'Rim', 'Rim', 'Rim', 'Rim', 'Tyre', 'Tyre', 'Tyre', 'Tyre', 'Tyre', 'Tyre', 'Tyre', 'Rim',
                'Rim', 'Rim']
        p.lathe(pts, 32, 'Rim', axis='X', mats=mats, cap0=False, cap1=False)
        for k in range(5):                                            # spokes
            a = 2 * pi * k / 5
            d = V((0, cos_(a), sin_(a)))
            p.sweep([V((h * 0.55, 0, 0)) + d * 0.06, V((h * 0.62, 0, 0)) + d * 0.205],
                    K.rrect(0.03, 0.045, 0.008, 1), 'Rim', side=(1, 0, 0))
        p.lathe([(-0.02, 0.0), (-0.02, 0.15), (0.02, 0.15), (0.02, 0.0)], 20, 'Disc', axis='X', cap0=False,
                cap1=False)                                            # brake disc behind the spokes
    parts = [mk('CarWheel', wheel, 0.0, weight=0.8)]

    def lod1(p):
        p.lathe([(-0.108, 0.22), (-0.108, 0.30), (-0.09, WR), (0.09, WR), (0.108, 0.30), (0.108, 0.22)], 14,
                'Proxy', axis='X')

    def lod2(p):
        p.lathe([(-0.108, WR * 0.95), (0.108, WR * 0.95)], 8, 'Proxy', axis='X')
    l1 = proxy('CW_P1', lod1, 0.35)
    l2 = proxy('CW_P2', lod2, 0.2)
    return LU.Unit('CarWheel', parts, l1, l2, (0.25, 0.25), {})


def cos_(a):
    from math import cos
    return cos(a)


def sin_(a):
    from math import sin
    return sin(a)


# =============================================================================
# bake, LODs, export
# =============================================================================

def bake(units, bake_size, save_size):
    parts = [p for u in units for p in u.parts]
    proxies = [p for u in units for p in u.proxies]
    K.atlas_unwrap(parts + proxies, margin=GAP, weights=WEIGHTS)
    offs = []
    for i, u in enumerate(units):
        d = V((i * 6.0, 0, 0))
        for o in u.parts + u.proxies:
            o.location = o.location + d
        offs.append(d)
    scene = bpy.context.scene
    scene.world = scene.world or bpy.data.worlds.new('BakeWorld')
    scene.world.light_settings.distance = 0.4
    for p in proxies:
        p.hide_render = True
    margin = max(2, int(GAP * bake_size / 2))
    paths = LU.gpu_or_cpu(K.bake_texture_set, parts, OUT_DIR, PREFIX, bake_size, save_size, margin=margin)
    variants = {}
    for cname, rgb in PAINTS.items():
        if cname == 'White':
            continue
        m = body_paint(f'Paint_{cname}', rgb)
        for o in parts:
            for i, mm in enumerate(o.data.materials):
                if mm and mm.name.startswith('Paint'):
                    o.data.materials[i] = m
        mats = sorted({s.material for o in parts for s in o.material_slots if s.material}, key=lambda q: q.name)
        img = K.new_image(f'{PREFIX}_bake_{cname}', bake_size)
        LU.gpu_or_cpu(K.bake_channel, parts, mats, 'albedo', img, samples=48, margin=margin)
        path = os.path.join(OUT_DIR, f'{PREFIX}_{cname}_Albedo.png')
        K.write_png(path, K.downsample(K.image_array(img), bake_size // save_size), 3)
        variants[cname] = path
    for p in proxies:
        p.hide_render = False
    jobs = [(prx, u.parts, reach) for u in units for prx, reach in zip(u.proxies, u.reach)]
    dim = (0.012, 0.013, 0.016, 1.0)                     # unseen cabin through an open window
    maps = [(paths['albedo'], 'rgb', dim)] + [(p, 'rgb', dim) for p in variants.values()]
    maps += [(paths['metallic'], 'rgba', (0, 0, 0, 0.9)), (paths['normal'], 'normal', (0.5, 0.5, 1.0, 1)),
             (paths['occlusion'], 'rgb', (0.4, 0.4, 0.4, 1)), (paths['emission'], 'rgb', (0, 0, 0, 1))]
    LU.gpu_or_cpu(K.transfer_to_proxies, jobs, maps, margin=max(1, int(GAP * save_size / 2)))
    for cname in variants:
        for mname in ('MetallicSmoothness', 'Normal', 'Occlusion', 'Emission'):
            shutil.copyfile(os.path.join(OUT_DIR, f'{PREFIX}_{mname}.png'),
                            os.path.join(OUT_DIR, f'{PREFIX}_{cname}_{mname}.png'))
    for d, u in zip(offs, units):
        for o in u.parts + u.proxies:
            o.location = o.location - d
    mat = K.make_runtime_material(PREFIX, paths)
    K.assign_single_material(parts + proxies, mat)
    return paths, variants


def assemble(units):
    for u in units:
        lod0 = K.join(u.parts, f'{u.name}_LOD0')
        for i, p in enumerate(u.proxies, 1):
            p.name = p.data.name = f'{u.name}_LOD{i}'
        u.piece.meshes[None] = [lod0] + u.proxies


def export(units):
    for u in units:
        root = KM.rig(u.piece)
        for name, e in getattr(u, 'rot', {}).items():
            next(o for o in root.children_recursive if o.name == name).rotation_euler = e
        bpy.context.view_layer.update()
        K.export_fbx(root, os.path.join(OUT_DIR, f'{u.name}.fbx'))
        for o in root.children_recursive:
            if o.type == 'EMPTY':
                o.name = f'{o.name}_{u.name}'


# =============================================================================
# previews
# =============================================================================

def preview_renders(units, paths, variants, quick):
    scene = bpy.context.scene
    by = {u.name: u for u in units}
    for u in units:
        for o in [u.piece.root] + list(u.piece.root.children_recursive):
            o.hide_render = True
    KM.studio(scene, k=2.0, floor=150)
    spp = 32 if quick else 128
    res = (1000, 625) if quick else (1800, 1100)
    out = lambda n: os.path.join(PREVIEW_DIR, f'carbody_{n}.png')       # noqa: E731
    T, Rz = Matrix.Translation, lambda a: Matrix.Rotation(a, 4, 'Z')   # noqa: E731
    mats = {'White': None}
    for cname, p in variants.items():
        pp = dict(paths)
        pp['albedo'] = p
        mats[cname] = K.make_runtime_material(f'CarBody_{cname}_Preview', pp)
    tt = os.path.join(BUILD_DIR, 'TuggerTrain.blend')
    extras = {}
    if os.path.exists(tt):
        with bpy.data.libraries.load(tt, link=False) as (src, dst):
            dst.objects = [n for n in src.objects if n in ('CentralConsole__CentralConsole_LOD0',
                                                             'SteeringWheel__SteeringWheel_LOD0')]
        for o in dst.objects:
            extras[o.name.split('__')[0]] = o
    R180 = Matrix.Rotation(pi, 4, 'Z')
    cb = by['CarBody']
    socket = lambda n: V(cb.piece.sockets_auth[n])                     # noqa: E731

    def car(coll, M_, level=0, colour='White', wheels=False, fitted=True):
        obs = KM.place(cb.piece, M_, coll, level)
        if mats[colour]:
            for o in obs:
                o.data = o.data.copy()
                o.data.materials[0] = mats[colour]
        if wheels:
            for n in ('Wheel_FL', 'Wheel_FR', 'Wheel_RL', 'Wheel_RR'):
                rz = pi if n.endswith('L') else 0.0
                KM.place(by['CarWheel'].piece, M_ @ T(socket(n)) @ Rz(rz), coll, level)
        if fitted and level == 0:
            for key, sock, rot in (('CentralConsole', 'ConsoleSocket', Matrix.Identity(4)),
                                   ('SteeringWheel', 'SteeringSocket', Matrix.Rotation(radians(-25), 4, 'X'))):
                if key in extras:
                    c = extras[key].copy()
                    coll.objects.link(c)
                    c.parent = None
                    c.hide_render = False
                    c.matrix_world = M_ @ T(socket(sock)) @ rot @ R180
    coll = K.collection('PreviewCars')
    car(coll, T((0, 0, 0)) @ Rz(0.5), colour='White', fitted=True)
    car(coll, T((3.0, 2.5, 0)) @ Rz(0.5), colour='Red', wheels=True)
    car(coll, T((-3.0, -2.2, 0)) @ Rz(0.5), colour='Blue')
    KM.scale_figure(coll, (2.6, -1.0, 0), 2.4)
    hero = ((5.5, 8.5, 3.2), (0.0, 0.3, 0.7), 32)
    LU._render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    LU._render(scene, KM.camera(scene, 'Cam_Inside', (-0.6, 3.1, 2.1), (-0.2, 0.0, 0.6), 35), out('inside'), res, spp)
    LU._render(scene, KM.camera(scene, 'Cam_Front', (-2.0, 6.0, 1.4), (0.0, 0.5, 0.7), 35), out('front'), res, spp)
    coll.hide_render = True
    for lvl in (1, 2):
        c = K.collection(f'PreviewCars_LOD{lvl}')
        car(c, T((0, 0, 0)) @ Rz(0.5), lvl, 'White')
        car(c, T((3.0, 2.5, 0)) @ Rz(0.5), lvl, 'Red', wheels=True)
        car(c, T((-3.0, -2.2, 0)) @ Rz(0.5), lvl, 'Blue')
        LU._render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (1000, 625), spp)
        c.hide_render = True


def look(units, quick):
    scene = bpy.context.scene
    KM.studio(scene, k=2.0)
    for u in units:
        for p in u.proxies:
            p.hide_render = True
    for o in units[1].parts:
        o.location = (2.0, 1.0, WR)
    spp = 24 if quick else 96
    KM.render(scene, KM.camera(scene, 'Cam_L1', (5.5, -6.0, 2.6), (0.3, 0.0, 0.8), 32),
              os.path.join(PREVIEW_DIR, 'carbody_look.png'), (1600, 1000), spp)
    KM.render(scene, KM.camera(scene, 'Cam_L2', (-4.5, 5.5, 2.0), (0.0, 0.3, 0.8), 32),
              os.path.join(PREVIEW_DIR, 'carbody_look_front.png'), (1600, 1000), spp)


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
    build_materials()
    units = [build_body(), build_wheel()]
    if args.look:
        look(units, args.quick)
        return
    paths, variants = bake(units, args.bake, args.save)
    assemble(units)
    for u in units:
        u.piece.sockets_auth = dict(u.piece.sockets)
    pieces = [u.piece for u in units]
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    export(units)
    blend = os.path.join(BUILD_DIR, f'{PREFIX}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        preview_renders(units, paths, variants, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[carbody] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
