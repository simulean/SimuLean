"""
SimuLean asset: electric counterbalance forklift (2.0 t class, 4-wheel, duplex mast)

Run (headless):
    blender -b -P Tools/Blender/assets/forklift.py -- [--paint yellow|orange|red|blue]
            [--bake 4096] [--save 2048] [--no-render] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/Forklift/Forklift.fbx   (+ 5 PNG maps)
    Tools/Blender/build/Forklift.blend                        (source, regenerable)
    Tools/Blender/previews/forklift_*.png                     (turntable renders)

Real-world reference: Linde E20 / Toyota 8FBE20 class.  Wheelbase 1.40 m,
overall width 1.08 m, lowered mast 2.29 m, overhead guard 2.14 m, forks
1150 mm, max fork height 3.2 m.

Animation hierarchy (Unity names; local rotations are identity at rest except
SteeringWheel, whose rest rotation is the column tilt).  Every name below is a
bare Transform; its meshes are children <name>_LOD0/_LOD1/_LOD2 with identity
local transforms, so animate the named node, never the _LODn mesh.
    Forklift                   root, on the ground under the drive-axle centre
    |- Body                    static chassis, counterweight, cab, guard, lights
    |- SteeringWheel           spin about local Y (column axis), Space.Self
    |- Wheel_FL / Wheel_FR     drive wheels: spin about local X, r = 0.2285 m
    |- Steer_RL / Steer_RR     steer about local Y (rear-wheel steering)
    |   '- Wheel_RL / Wheel_RR spin about local X, r = 0.19 m
    '- Mast                    tilt about local X: +6 deg forward .. -8 deg back
        '- MastInner           lift: localPosition.y = h / 2
            '- Carriage        lift: localPosition.y = h / 2   (h = fork height 0..3.2 m)
                |- Fork_L / Fork_R   fork spread: localPosition.x
                '- LoadSocket  parent the carried load here (top of forks)

LODs (see LOD_PLAN / build_lods): LOD0 ~22k tris / 11 renderers, LOD1 ~6k / 10
(steering wheel folded into Body), LOD2 ~1.8k / 4 (wheels folded into Body,
forks into Carriage: at that distance only lift and tilt stay visible).
"""

import os
import sys
import argparse
import time
from math import radians, pi, sin, cos, sqrt

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)

import bpy                                  # noqa: E402
from mathutils import Vector, Matrix, Euler  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import V, Part, rrect, circle, fillet, finish, boolean, cull_faces  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'Forklift')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

PAINTS = {                      # linear RGB
    'yellow': (0.80, 0.42, 0.015),
    'orange': (0.85, 0.20, 0.01),
    'red': (0.55, 0.03, 0.025),
    'blue': (0.03, 0.12, 0.35),
}

# ---------------------------------------------------------------- dimensions
FW_R, FW_HW, FW_X = 0.2285, 0.085, 0.455     # front drive wheel: radius, half width, centre x
RW_R, RW_HW, RW_X = 0.190, 0.060, 0.395      # rear steer wheel
WB = 1.40                                    # wheelbase (front axle at y=0, rear at y=WB)
HALF_W = 0.53                                # chassis half width
TUB_FRONT, TUB_REAR = -0.33, 1.21
FLOOR_Z, HOOD_Z = 0.60, 1.00
CW_REAR = 1.88
MAST_PIVOT = V((0.0, -0.37, 0.24))
FORK_X = 0.19                                # fork centre (EUR pallet, 800 mm side)
LIFT_MAX = 3.2

MATS = {}


# =============================================================================
# Materials
# =============================================================================

def build_materials(paint_rgb):
    logo, logo_aspect = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_logo.png'),
                                            256, FONT, bold_offset=0.016)
    code, code_aspect = K.render_text_image('SL 20E', os.path.join(BUILD_DIR, 'decal_code.png'),
                                            256, FONT, bold_offset=0.016)
    ink = (0.025, 0.025, 0.028)
    lw = 0.40
    side_center_y, side_center_z = 1.405, 0.735
    decals = [
        dict(image=logo, origin=(HALF_W, side_center_y, side_center_z), u=(0, 1, 0), v=(0, 0, 1),
             size=(lw, lw / logo_aspect), normal=(1, 0, 0), color=ink),
        dict(image=logo, origin=(-HALF_W, side_center_y, side_center_z), u=(0, -1, 0), v=(0, 0, 1),
             size=(lw, lw / logo_aspect), normal=(-1, 0, 0), color=ink),
        dict(image=code, origin=(0, CW_REAR, 0.66), u=(-1, 0, 0), v=(0, 0, 1),
             size=(0.24, 0.24 / code_aspect), normal=(0, 1, 0), color=ink),
    ]
    graphite = (0.028, 0.030, 0.033)
    mast_black = (0.020, 0.021, 0.023)
    MATS.update({
        'Paint': M.paint('Paint', paint_rgb, rough=0.34, wear=0.7, decals=decals),
        'Graphite': M.paint('Graphite', graphite, rough=0.48, wear=0.65),
        'MastPaint': M.paint('MastPaint', mast_black, rough=0.42, wear=1.1, dirt=0.8),
        'Hazard': M.paint('Hazard', None, rough=0.38, wear=1.3,
                          color_fn=M.hazard_fn(paint_rgb, width=0.045, axis=('x', 'z'))),
        'Rim': M.paint('Rim', (0.46, 0.46, 0.44), rough=0.38, wear=0.7, edge=0.004),
        'Fork': M.forged_steel('Fork'),
        'Chrome': M.chrome('Chrome'),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Chain': M.chain('Chain'),
        'Tread': M.tread_plate('Tread'),
        'TireF': M.tire('TireF', FW_R, lugs=30),
        'TireR': M.tire('TireR', RW_R, lugs=24),
        'Rubber': M.rubber('Rubber'),
        'FloorMat': M.rubber('FloorMat', rough=0.8, ribs=('y', 0.016)),
        'Plastic': M.plastic('Plastic'),
        'Vinyl': M.vinyl('Vinyl'),
        'LensW': M.lens('LensW', (1.0, 0.93, 0.80)),
        'LensR': M.lens('LensR', (1.0, 0.04, 0.02)),
        'LensA': M.lens('LensA', (1.0, 0.42, 0.0)),
        'LensB': M.lens('LensB', (0.10, 0.35, 1.0)),
        'Mirror': M.mirror('Mirror'),
    })
    return MATS


# =============================================================================
# Helpers
# =============================================================================

def mk(name, fn, bevel=0.006, seg=2, angle=40.0, cull=None, cutters=None, sharp=40.0):
    # 40 deg keeps the facets of 10+-sided cylinders unbevelled and smooth-shaded
    if bevel <= 0.008:
        seg = 1         # small chamfers: one segment + weighted normals reads the same
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        boolean(ob, [c() for c in cutters])
    if cull:
        cull_faces(ob, cull)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def cut_cyl_x(cy, cz, r, x0, x1, n=48):
    def f():
        p = Part('cutter')
        p.mi('Graphite')
        p.lathe([(x0, r), (x1, r)], n, 'Graphite', center=(0, cy, cz), axis='X')
        return p.build(MATS)
    return f


def cut_box(lo, hi):
    def f():
        p = Part('cutter')
        p.box_minmax(lo, hi, 'Graphite')
        return p.build(MATS)
    return f


DOWN = lambda c, n: n.z < -0.9                          # noqa: E731


def frame_matrix(origin, x_axis, y_axis):
    x = V(x_axis).normalized()
    y = V(y_axis).normalized()
    z = x.cross(y).normalized()
    m = Matrix((x, y, z)).transposed().to_4x4()
    m.translation = V(origin)
    return m


def box_in_frame(p, frame, center_local, size, mat):
    rot = frame.to_3x3()
    c = frame @ V(center_local)
    p.box(c, size, mat, rot=rot)


# =============================================================================
# Body (static)
# =============================================================================

def build_tub():
    outline = rrect(2 * HALF_W, TUB_REAR - TUB_FRONT, 0.02, 2, 0, (TUB_FRONT + TUB_REAR) / 2)

    def f(p):
        p.mi('Graphite')
        # graphite chassis skirt, painted side band, graphite deck lip
        rings = [[V((x, y, z)) for x, y in outline] for z in (0.13, 0.37, FLOOR_Z - 0.02, FLOOR_Z)]
        p.loft(rings, 'Graphite', band_mats=['Graphite', 'Paint', 'Graphite'], cap_mat='Graphite')
    cutters = [
        cut_cyl_x(0.0, FW_R, 0.285, 0.335, 0.7), cut_cyl_x(0.0, FW_R, 0.285, -0.7, -0.335),
        cut_cyl_x(WB, RW_R, 0.26, 0.20, 0.7), cut_cyl_x(WB, RW_R, 0.26, -0.7, -0.20),
        cut_box((0.44, 0.34, 0.36), (0.7, 0.64, 0.7)),            # entry step (left side)
    ]
    return mk('Tub', f, bevel=0.008, cutters=cutters)


def build_counterweight():
    def outline(inset, bulge=0.0):
        i = inset - bulge
        y0, y1 = 1.20 + max(i, 0.0), CW_REAR - i
        rr = max(0.28 - i, 0.01)
        return rrect(2 * (HALF_W - i), y1 - y0, 0, 8, 0, (y0 + y1) / 2,
                     radii=(rr, rr, 0.015, 0.015))

    def f(p):
        p.mi('Graphite')
        secs = [(0.12, outline(0.012)), (0.135, outline(0.0, 0.012)), (0.265, outline(0.0, 0.012)),
                (0.28, outline(0.0)), (0.29, outline(0.0)), (0.98, outline(0.0))]
        R = 0.12
        for k in range(1, 6):
            a = radians(90 * k / 5)
            secs.append((0.98 + R * sin(a), outline(R * (1 - cos(a)))))
        rings = [[V((x, y, z)) for x, y in pts] for z, pts in secs]
        bands = ['Graphite', 'Graphite', 'Graphite', 'Graphite'] + ['Paint'] * (len(rings) - 5)
        p.loft(rings, 'Paint', band_mats=bands)
    cutters = [
        cut_cyl_x(WB, RW_R, 0.26, 0.20, 0.7), cut_cyl_x(WB, RW_R, 0.26, -0.7, -0.20),
        cut_box((-0.075, 1.78, 0.15), (0.075, 2.0, 0.255)),          # tow-pin pocket
    ]
    return mk('Counterweight', f, bevel=0.007, cutters=cutters,
              cull=lambda c, n: n.z < -0.9 and c.z < 0.125)


def build_hood():
    prof = [(0.38, FLOOR_Z), (0.38, 0.962), (0.418, HOOD_Z), (1.245, HOOD_Z), (1.245, FLOOR_Z)]

    def f(p):
        p.extrude(prof, 'X', -0.50, 0.50, 'Paint')
    return mk('Hood', f, bevel=0.035, seg=4, angle=40, cull=DOWN)


def build_hood_vents():
    def f(p):
        for s in (1, -1):
            x0 = s * 0.50
            p.box((x0 + s * 0.002, 1.00, 0.80), (0.008, 0.29, 0.20), 'Plastic')
            for k in range(6):
                z = 0.72 + k * 0.03
                p.box((x0 + s * 0.008, 1.00, z), (0.006, 0.26, 0.016), 'Plastic',
                      rot=Euler((0, s * radians(35), 0)))
    return mk('HoodVents', f, bevel=0.002, seg=1)


def build_cowl():
    prof = [(-0.335, FLOOR_Z), (-0.335, 0.985), (-0.30, 1.045), (-0.22, 1.07), (-0.15, 1.065),
            (-0.06, 0.905), (-0.035, 0.86), (-0.03, FLOOR_Z)]

    def f(p):
        p.extrude(prof, 'X', -0.40, 0.40, 'Paint')
    return mk('Cowl', f, bevel=0.035, seg=4, angle=40, cull=DOWN)


SLOPE_A, SLOPE_B = V((0, -0.15, 1.065)), V((0, -0.06, 0.905))


def dash_frame():
    s = (SLOPE_B - SLOPE_A).normalized()          # down-slope
    up = -s
    n = V((1, 0, 0)).cross(up)                    # outward (towards operator)
    if n.y < 0:
        n = -n
    mid = (SLOPE_A + SLOPE_B) / 2 + V((0.22, 0, 0))     # left of the steering column
    return frame_matrix(mid, (1, 0, 0), up), n, up


def build_dash():
    fr, n, up = dash_frame()
    sw, sh = 0.15, 0.080
    screen_origin = fr @ V((sw / 2, -sh / 2, 0.019))
    MATS['Screen'] = M.screen('Screen', tuple(screen_origin), (-1, 0, 0), tuple(up), (sw, sh))

    def f(p):
        box_in_frame(p, fr, (0, 0, 0.006), (0.20, 0.115, 0.024), 'Plastic')
        box_in_frame(p, fr, (0, 0, 0.0175), (sw, sh, 0.004), 'Screen')
        # hydraulic control levers, right-hand side (-X)
        p.box((-0.30, -0.19, 1.078), (0.15, 0.085, 0.03), 'Plastic')
        for i, x in enumerate((-0.25, -0.30, -0.35)):
            a, b = V((x, -0.19, 1.09)), V((x, -0.15, 1.215))
            p.cylinder(a, b, 0.0065, 'DarkSteel', n=8)
            p.sphere(b + (b - a).normalized() * 0.012, 0.018, 'Plastic', u=8, v=6,
                     scale=(1, 1, 1.25))
            p.box((x, -0.19, 1.095), (0.03, 0.03, 0.012), 'Rubber')
        # key switch + horn
        p.cylinder((0.07, -0.25, 1.065), (0.07, -0.25, 1.08), 0.02, 'DarkSteel', n=12)
    return mk('Dash', f, bevel=0.003, seg=1)


COLUMN_A, COLUMN_B = V((0, -0.12, 1.05)), V((0, 0.05, 1.26))


def build_column_and_pedals():
    def f(p):
        p.cylinder(COLUMN_A, COLUMN_B, 0.028, 'Plastic', n=12)
        ax = (COLUMN_B - COLUMN_A).normalized()
        p.sweep([COLUMN_A - ax * 0.03, COLUMN_A + ax * 0.07], rrect(0.10, 0.085, 0.03, 3),
                'Plastic', side=(1, 0, 0), scales=[1.0, 0.75])
        # pedals: brake (left, +X) accelerator (right, -X)
        for x, w in ((0.10, 0.085), (-0.12, 0.065)):
            rot = Euler((radians(-52), 0, 0))
            p.box((x, -0.02, 0.675), (w, 0.10, 0.014), 'Rubber', rot=rot)
            p.cylinder((x, -0.045, 0.66), (x, -0.075, 0.62), 0.008, 'DarkSteel', n=8)
    return mk('Column', f, bevel=0.003, seg=1)


def build_floor():
    def f(p):
        p.box((0, 0.17, FLOOR_Z + 0.004), (0.78, 0.40, 0.008), 'FloorMat')
        p.box((0.49, 0.49, 0.363), (0.09, 0.29, 0.006), 'Tread')
    return mk('Floor', f, bevel=0.002, seg=1, cull=DOWN)


def build_seat():
    def base(p):
        p.box((0, 0.69, 1.02), (0.50, 0.46, 0.04), 'Plastic')
        # backrest shell
        rot = Euler((radians(-12), 0, 0))
        p.box((0, 0.998, 1.300), (0.48, 0.03, 0.47), 'Plastic', rot=rot)
        # seat rails + slides
        for x in (-0.16, 0.16):
            p.box((x, 0.69, 1.004), (0.03, 0.40, 0.012), 'DarkSteel')
    seat_base = mk('SeatBase', base, bevel=0.012, seg=3, angle=40, cull=DOWN)

    def cushion(p):
        pillow(p, frame_matrix((0, 0.68, 1.04), (1, 0, 0), (0, 1, 0)), 0.46, 0.42, 0.085, 'Vinyl')
        t = radians(12)
        h = V((0, sin(t), cos(t)))
        fwd = V((0, -cos(t), sin(t)))
        org = V((0, 0.998, 1.30)) + fwd * 0.013
        pillow(p, frame_matrix(org, (1, 0, 0), h), 0.46, 0.46, 0.075, 'Vinyl', bolster=0.025)
    seat = mk('Seat', cushion, bevel=0.0, angle=40, sharp=50)
    return [seat_base, seat]


def pillow(p, frame, w, d, h, mat, r=0.06, bolster=0.0):
    """Upholstered cushion: rounded-rect slab with a puffed crown, local +Z up.
    bolster > 0 raises the two long sides (side support on the backrest)."""
    levels = [(0.0, 0.010, 0.0), (0.18, 0.0, 0.0), (0.62, 0.0, 0.0), (0.86, 0.22, 0.6),
              (0.96, 0.50, 0.9), (1.0, 0.85, 1.0)]
    rings = []
    for fz, inset, crown in levels:
        i = inset * r
        ring = []
        for x, y in rrect(w - 2 * i, d - 2 * i, max(r - i, 0.004), 5):
            z = fz * h
            if bolster:                       # side bolsters along local X extremes
                z += bolster * crown * min(1.0, (abs(x) / (w / 2)) ** 4 * 1.6)
            ring.append(frame @ V((x, y, z)))
        rings.append(ring)
    # quilted centre: extra ring pulled down gives a seam line
    p.loft(rings, mat)


def post_y(z):
    return -0.31 + (z - 0.58) * 0.21 / 1.52


def build_guard():
    def f(p):
        tube = rrect(0.05, 0.066, 0.012, 2)
        for x in (0.455, -0.455):
            hoop = fillet([V((x, -0.31, 0.58)), V((x, -0.10, 2.10)), V((x, 1.21, 2.10)),
                           V((x, 1.22, 0.96))], 0.12, 4)
            p.sweep(hoop, tube, 'Graphite', side=(1, 0, 0))
            # foot plates
            p.box((x, -0.31, FLOOR_Z + 0.008), (0.09, 0.12, 0.016), 'Graphite')
        bar = rrect(0.05, 0.05, 0.01, 1)
        for y in (0.0, 0.56, 1.12):
            p.sweep([V((-0.455, y, 2.10)), V((0.455, y, 2.10))], bar, 'Graphite', side=(0, 1, 0))
        slat = rrect(0.05, 0.014, 0.004, 1)
        for x in (-0.30, -0.15, 0.0, 0.15, 0.30):
            p.sweep([V((x, 0.0, 2.123)), V((x, 1.12, 2.123))], slat, 'Graphite', side=(1, 0, 0))
    return mk('Guard', f, bevel=0.004, seg=1)


def build_handles_mirror():
    def f(p):
        grip = circle(0.014, 8)
        x = 0.455
        z0, z1 = 1.05, 1.55
        path = fillet([V((x, post_y(z0) + 0.02, z0)), V((x, post_y(z0) + 0.085, z0 + 0.03)),
                       V((x, post_y(z1) + 0.085, z1 - 0.03)), V((x, post_y(z1) + 0.02, z1))], 0.03, 3)
        p.sweep(path, grip, 'Rubber')
        path = fillet([V((x, 1.19, 1.25)), V((x, 1.115, 1.28)), V((x, 1.115, 1.58)),
                       V((x, 1.19, 1.61))], 0.03, 3)
        p.sweep(path, grip, 'Rubber')
        p.cylinder((x, 1.105, 1.50), (x - 0.0, 1.085, 1.50), 0.012, 'LensR', n=10)   # horn button
        # rear-view mirror on the left front post
        zm = 1.80
        a = V((0.48, post_y(zm) + 0.01, zm))
        b = V((0.575, post_y(zm) - 0.005, zm + 0.04))
        p.cylinder(a, b, 0.008, 'DarkSteel', n=8)
        p.box((0.615, post_y(zm) - 0.005, zm + 0.05), (0.12, 0.028, 0.085), 'Plastic')
        p.box((0.615, post_y(zm) + 0.0105, zm + 0.05), (0.108, 0.004, 0.073), 'Mirror')
    return mk('Handles', f, bevel=0.003, seg=1)


def lamp(p, center, facing, w, h, depth, lens_mat, body='Plastic'):
    """Rounded lamp housing (depth along `facing` = +/-Y) with an inset lens."""
    c = V(center)
    f = V((0, facing, 0))
    p.sweep([c - f * depth / 2, c + f * depth / 2], rrect(w, h, min(w, h) * 0.22, 2), body,
            side=(1, 0, 0))
    p.box(c + f * (depth / 2 + 0.001), (w * 0.82, 0.006, h * 0.76), lens_mat)


def build_lights():
    def f(p):
        zl = 1.90
        for s in (1, -1):
            x = s * 0.515
            y = post_y(zl)
            p.box((s * 0.49, y, zl), (0.035, 0.03, 0.025), 'DarkSteel')
            lamp(p, (x, y - 0.035, zl), -1, 0.085, 0.065, 0.075, 'LensW')
            # tail lamps (counterweight rear face)
            p.box((s * 0.20, CW_REAR + 0.012, 0.90), (0.14, 0.03, 0.078), 'Plastic')
            p.box((s * 0.225, CW_REAR + 0.028, 0.90), (0.075, 0.006, 0.056), 'LensR')
            p.box((s * 0.158, CW_REAR + 0.028, 0.90), (0.038, 0.006, 0.056), 'LensA')
        # rear work lamp (right rear post)
        lamp(p, (-0.515, 1.27, 1.95), 1, 0.085, 0.065, 0.07, 'LensW')
        p.box((-0.49, 1.22, 1.95), (0.035, 0.04, 0.025), 'DarkSteel')
        # beacon
        p.cylinder((0, 1.12, 2.123), (0, 1.12, 2.16), 0.045, 'Plastic', n=12)
        p.lathe([(0.0, 0.040), (0.03, 0.040), (0.058, 0.032), (0.074, 0.018), (0.080, 0.004)],
                12, 'LensA', center=(0, 1.12, 2.16), axis='Z')
        # blue safety spot (projects onto the floor behind the truck)
        a, b = V((0, 1.15, 2.07)), V((0, 1.20, 2.03))
        p.cylinder(a, b, 0.032, 'Plastic', n=12)
        p.cylinder(b, b + (b - a).normalized() * 0.004, 0.026, 'LensB', n=12)
        p.box((0, 1.13, 2.09), (0.03, 0.05, 0.03), 'DarkSteel')
    return mk('Lights', f, bevel=0.003, seg=1)


def build_rear_details():
    def f(p):
        # tow pin
        p.cylinder((0, 1.84, 0.14), (0, 1.84, 0.30), 0.014, 'DarkSteel', n=10)
        p.cylinder((0, 1.84, 0.30), (0, 1.84, 0.315), 0.03, 'DarkSteel', n=12)
        # rear bumper rubber pads
        for s in (1, -1):
            p.box((s * 0.34, 1.82, 0.20), (0.10, 0.03, 0.08), 'Rubber',
                  rot=Euler((0, 0, s * radians(-30))))
    return mk('RearDetails', f, bevel=0.004, seg=1)


def build_axles_and_tilt():
    def f(p):
        # drive axle + gearbox
        p.cylinder((-0.37, 0, FW_R), (0.37, 0, FW_R), 0.085, 'DarkSteel', n=10)
        p.box((0, 0.02, 0.27), (0.34, 0.26, 0.24), 'DarkSteel')
        for s in (1, -1):
            p.cylinder((s * 0.355, 0, FW_R), (s * 0.372, 0, FW_R), 0.10, 'DarkSteel', n=10)
        # steer axle
        p.box((0, WB, RW_R + 0.02), (0.60, 0.12, 0.10), 'DarkSteel')
        # tilt cylinder barrels (rods live on the Mast)
        for s in (1, -1):
            p.cylinder((s * 0.47, -0.22, 0.50), (s * 0.47, -0.40, 0.50), 0.034, 'Graphite', n=10)
            p.cylinder((s * 0.47, -0.40, 0.50), (s * 0.47, -0.405, 0.50), 0.026, 'DarkSteel', n=10)
            # mast pivot brackets
            p.box((s * 0.39, -0.35, 0.24), (0.06, 0.05, 0.12), 'Graphite')
    return mk('Axles', f, bevel=0.004, seg=1)


def build_body_parts():
    """The body stays as separate part objects through unwrap and bake: the
    inside-AO edge-wear mask only sees its own object, so parts that touch or
    interpenetrate (hood in counterweight, dash in cowl) don't chip each other.
    They are joined into Body_LODn afterwards (see build_lods)."""
    return [build_tub(), build_counterweight(), build_hood(), build_hood_vents(), build_cowl(),
            build_dash(), build_column_and_pedals(), build_floor(), *build_seat(), build_guard(),
            build_handles_mirror(), build_lights(), build_rear_details(), build_axles_and_tilt()]


# =============================================================================
# Mast, carriage, forks
# =============================================================================

def channel(x_web_out, x_tip, y_front, y_back, t_flange, t_web):
    """C-channel profile (x,y) opening towards x_tip."""
    s = 1 if x_web_out > x_tip else -1
    xw = x_web_out - s * t_web
    return [(x_tip, y_back), (x_web_out, y_back), (x_web_out, y_front), (x_tip, y_front),
            (x_tip, y_front + t_flange), (xw, y_front + t_flange), (xw, y_back - t_flange),
            (x_tip, y_back - t_flange)]


def build_mast_outer():
    def f(p):
        for s in (1, -1):
            prof = channel(s * 0.42, s * 0.36, -0.525, -0.395, 0.014, 0.016)
            p.extrude(prof, 'Z', 0.06, 2.20, 'MastPaint')
            # lift cylinder barrel + gland
            p.cylinder((s * 0.39, -0.355, 0.12), (s * 0.39, -0.355, 2.05), 0.030, 'MastPaint', n=12)
            p.cylinder((s * 0.39, -0.355, 2.03), (s * 0.39, -0.355, 2.075), 0.034, 'DarkSteel', n=12)
            p.box((s * 0.39, -0.37, 0.10), (0.07, 0.07, 0.05), 'MastPaint')
            # tilt pivot boss + pin
            p.box((s * 0.39, -0.40, 0.24), (0.07, 0.03, 0.14), 'MastPaint')
            p.cylinder((s * 0.34, -0.37, 0.24), (s * 0.44, -0.37, 0.24), 0.028, 'DarkSteel', n=10)
            # tilt cylinder clevis + rod
            p.box((s * 0.46, -0.44, 0.50), (0.08, 0.04, 0.06), 'MastPaint')
            p.cylinder((s * 0.47, -0.34, 0.50), (s * 0.47, -0.435, 0.50), 0.02, 'Chrome', n=10)
        for z0, z1 in ((0.06, 0.16), (1.14, 1.22), (2.12, 2.20)):
            p.box_minmax((-0.42, -0.405, z0), (0.42, -0.385, z1), 'MastPaint')
    return mk('Mast', f, bevel=0.004, seg=2)


def build_mast_inner():
    def f(p):
        for s in (1, -1):
            prof = channel(s * 0.355, s * 0.295, -0.513, -0.407, 0.013, 0.015)
            p.extrude(prof, 'Z', 0.10, 2.215, 'MastPaint')
            p.cylinder((s * 0.39, -0.355, 0.25), (s * 0.39, -0.355, 2.22), 0.019, 'Chrome', n=10)
            # chain sheave + chain
            p.cylinder((s * 0.178, -0.455, 2.25), (s * 0.142, -0.455, 2.25), 0.046, 'DarkSteel', n=12)
            p.cylinder((s * 0.19, -0.455, 2.25), (s * 0.13, -0.455, 2.25), 0.012, 'DarkSteel', n=8)
            p.box_minmax((s * 0.143 if s > 0 else -0.177, -0.506, 0.43),
                         (s * 0.177 if s > 0 else -0.143, -0.491, 2.25), 'Chain')
        p.box_minmax((-0.42, -0.43, 2.215), (0.42, -0.33, 2.29), 'MastPaint')
        p.box_minmax((-0.34, -0.435, 0.10), (0.34, -0.41, 0.17), 'MastPaint')
        # sheave brackets
        for x in (0.20, 0.12, -0.12, -0.20):
            p.box((x, -0.46, 2.25), (0.012, 0.07, 0.07), 'MastPaint')
    return mk('MastInner', f, bevel=0.004, seg=2)


def build_carriage():
    def f(p):
        p.box_minmax((-0.50, -0.605, 0.32), (0.50, -0.555, 0.44), 'Hazard')
        p.box_minmax((-0.48, -0.600, 0.04), (0.48, -0.560, 0.12), 'MastPaint')
        for s in (1, -1):
            lo, hi = sorted((s * 0.255, s * 0.285))
            p.box_minmax((lo, -0.60, 0.05), (hi, -0.44, 0.44), 'MastPaint')
            for z in (0.10, 0.38):
                p.cylinder((s * 0.285, -0.46, z), (s * 0.335, -0.46, z), 0.032, 'DarkSteel', n=10)
            p.box((s * 0.16, -0.505, 0.45), (0.05, 0.03, 0.05), 'DarkSteel')
            p.box((s * 0.10, -0.575, 0.24), (0.02, 0.05, 0.36), 'MastPaint')
        # load backrest
        tube = rrect(0.035, 0.035, 0.008, 2)
        frame = fillet([V((-0.48, -0.58, 0.43)), V((-0.48, -0.58, 1.40)), V((0.48, -0.58, 1.40)),
                        V((0.48, -0.58, 0.43))], 0.06, 4)
        p.sweep(frame, tube, 'MastPaint', side=(0, 1, 0))
        p.sweep([V((-0.48, -0.58, 0.92)), V((0.48, -0.58, 0.92))], rrect(0.02, 0.04, 0.005, 1),
                'MastPaint', side=(0, 1, 0))
        for x in (-0.30, -0.10, 0.10, 0.30):
            p.sweep([V((x, -0.58, 0.43)), V((x, -0.58, 1.40))], rrect(0.012, 0.035, 0.004, 1),
                    'MastPaint', side=(0, 1, 0))
    return mk('Carriage', f, bevel=0.004, seg=2)


def build_fork(name, cx):
    def f(p):
        yc = -0.6275
        path = [V((cx, yc, 0.47)), V((cx, yc, 0.0225))]
        path = fillet(path + [V((cx, -1.0, 0.0225))], 0.05, 3)
        path += [V((cx, y, 0.0225)) for y in (-1.30, -1.55, -1.64, -1.72, -1.78)]
        _, faces = p.sweep(path, rrect(0.10, 0.045, 0.004, 1), 'Fork', side=(1, 0, 0))
        verts = {v for fc in faces for v in fc.verts}
        for v in verts:
            if v.co.y < -1.58:
                t = min(1.0, (-1.58 - v.co.y) / 0.20)
                if v.co.z < 0.0225:
                    v.co.z += 0.019 * t * t
                v.co.x = cx + (v.co.x - cx) * (1 - 0.10 * t)
        p.box((cx, -0.593, 0.46), (0.10, 0.115, 0.04), 'Fork')           # top hook
        p.box((cx, -0.585, 0.07), (0.10, 0.04, 0.05), 'Fork')            # bottom hook
        p.cylinder((cx + 0.03, -0.625, 0.48), (cx + 0.03, -0.625, 0.505), 0.009, 'DarkSteel', n=8)
    return mk(name, f, bevel=0.004, seg=2)


# =============================================================================
# Wheels & steering wheel (built in local space)
# =============================================================================

def wheel_profile(R, hw, bead=0.112):
    """(t, r, mat) list from inner bead, over the tread, to the hub centre.
    t > 0 is the outboard side."""
    d = R - bead
    tire = [(-0.80, 0.0), (-0.95, 0.25), (-1.0, 0.50), (-0.975, 0.74), (-0.88, 0.91),
            (-0.72, 1.0), (0.72, 1.0), (0.88, 0.91), (0.975, 0.74), (1.0, 0.50),
            (0.95, 0.25), (0.80, 0.0)]
    pts = [(a * hw, bead + b * d, 'Tire') for a, b in tire]
    rim = [(0.82 * hw, bead - 0.001), (0.80 * hw, bead - 0.010), (0.60 * hw, bead - 0.016),
           (0.36 * hw, bead - 0.030), (0.30 * hw, 0.066), (0.44 * hw, 0.058),
           (0.50 * hw, 0.042), (0.53 * hw, 0.014)]
    pts += [(t, r, 'Rim') for t, r in rim]
    return pts


def build_wheel(name, R, hw, side, tire_mat, segments):
    """side = +1 (left, outboard +X) or -1 (right, outboard -X)."""
    def f(p):
        prof = wheel_profile(R, hw)
        tr = [(side * t, r) for t, r, _ in prof]
        mats = []
        for i in range(len(prof) - 1):
            a, b = prof[i][2], prof[i + 1][2]
            mats.append(tire_mat if (a == 'Tire' and b == 'Tire') else 'Rim')
        p.mi('Rim')
        p.lathe(tr, segments, 'Rim', axis='X', mats=mats, a0=pi / segments)
        # lug nuts
        t0 = side * 0.30 * hw
        for i in range(6):
            a = 2 * pi * i / 6
            c = V((t0, 0.074 * cos(a), 0.074 * sin(a)))
            p.cylinder(c, c + V((side * 0.014, 0, 0)), 0.0095, 'DarkSteel', n=6)
    ob = mk(name, f, bevel=0.003, seg=1, angle=40, sharp=50)
    return ob


def build_steering_wheel():
    def f(p):
        ring = [V((0.165 * cos(a), 0.165 * sin(a), 0)) for a in
                [2 * pi * i / 22 for i in range(22)]]
        p.sweep(ring, circle(0.0165, 8), 'Plastic', closed=True, side=(0, 0, 1))
        for k in range(3):
            a = radians(90 + 120 * k)
            d = V((cos(a), sin(a), 0))
            p.sweep([d * 0.03 + V((0, 0, -0.035)), d * 0.155], rrect(0.012, 0.034, 0.004, 1),
                    'Plastic', side=(0, 0, 1))
        p.cylinder((0, 0, -0.065), (0, 0, -0.012), 0.042, 'Plastic', n=12)
        p.cylinder((0, 0, -0.012), (0, 0, 0.0), 0.034, 'Rubber', n=12)
        a = radians(-60)
        c = V((0.165 * cos(a), 0.165 * sin(a), 0))
        p.cylinder(c, c + V((0, 0, 0.05)), 0.011, 'DarkSteel', n=8)
        p.sphere(c + V((0, 0, 0.058)), 0.02, 'Plastic', u=10, v=6, scale=(1, 1, 0.9))
    return mk('SteeringWheel', f, bevel=0.002, seg=1, angle=50, sharp=50)


# =============================================================================
# Assembly
# =============================================================================

def build_forklift(paint_rgb):
    build_materials(paint_rgb)
    parts = build_body_parts()
    mast = build_mast_outer()
    inner = build_mast_inner()
    carriage = build_carriage()
    fork_l = build_fork('Fork_L', FORK_X)
    fork_r = build_fork('Fork_R', -FORK_X)
    wheels = {
        'Wheel_FL': build_wheel('Wheel_FL', FW_R, FW_HW, 1, 'TireF', 20),
        'Wheel_FR': build_wheel('Wheel_FR', FW_R, FW_HW, -1, 'TireF', 20),
        'Wheel_RL': build_wheel('Wheel_RL', RW_R, RW_HW, 1, 'TireR', 18),
        'Wheel_RR': build_wheel('Wheel_RR', RW_R, RW_HW, -1, 'TireR', 18),
    }
    wheels['Wheel_FL'].location = (FW_X, 0, FW_R)
    wheels['Wheel_FR'].location = (-FW_X, 0, FW_R)
    wheels['Wheel_RL'].location = (RW_X, WB, RW_R)
    wheels['Wheel_RR'].location = (-RW_X, WB, RW_R)
    sw = build_steering_wheel()
    ax = (COLUMN_B - COLUMN_A).normalized()
    sw.location = COLUMN_B + ax * 0.055
    sw.rotation_mode = 'ZXY'                 # spin (local Z) first, then the column tilt
    sw.rotation_euler = Euler((-ax.angle(V((0, 0, 1))), 0, 0), 'ZXY')
    bpy.context.view_layer.update()
    movers = [mast, inner, carriage, fork_l, fork_r, *wheels.values(), sw]
    return parts, movers


# -----------------------------------------------------------------------------
# LODs.  Unity LODGroup thresholds (screen height) are set on import by
# SimuLeanModelPostprocessor: LOD0 > 25 %, LOD1 > 7 %, LOD2 > 1 %, culled below.
# Ratios are of the LOD0 part; None = the part is dropped at that level.
# -----------------------------------------------------------------------------
LOD_PLAN = {
    'Tub': (0.28, 0.10), 'Counterweight': (0.28, 0.10), 'Hood': (0.5, 0.35),
    'Cowl': (0.5, 0.35), 'HoodVents': (0.2, None), 'Dash': (0.2, None),
    'Column': (0.25, None), 'Floor': (1.0, 1.0), 'SeatBase': (0.35, 0.15), 'Seat': (0.3, 0.12),
    'Guard': (0.25, 0.08), 'Handles': (0.22, None), 'Lights': (0.22, 0.06),
    'RearDetails': (0.25, None), 'Axles': (0.2, None),
    'SteeringWheel': (0.3, None),
    'Wheel_FL': (0.28, 0.10), 'Wheel_FR': (0.28, 0.10), 'Wheel_RL': (0.28, 0.10),
    'Wheel_RR': (0.28, 0.10),
    'Mast': (0.3, 0.08), 'MastInner': (0.3, 0.08), 'Carriage': (0.28, 0.08),
    'Fork_L': (0.3, 0.12), 'Fork_R': (0.3, 0.12),
}
# relative texel density in the shared atlas (1.0 = default)
TEXEL_WEIGHTS = {
    'Counterweight': 1.5, 'Hood': 1.5, 'Cowl': 1.5, 'Tub': 1.3, 'Seat': 1.2, 'Dash': 1.2,
    'Floor': 0.7, 'Axles': 0.35, 'Mast': 0.6, 'MastInner': 0.6, 'Carriage': 0.8,
    'Fork_L': 0.55, 'Fork_R': 0.55, 'Wheel_FL': 0.8, 'Wheel_FR': 0.8, 'Wheel_RL': 0.8,
    'Wheel_RR': 0.8,
}
NOT_SYMMETRIC = {'Handles', 'Tub', 'Column', 'Dash', 'SteeringWheel', 'Fork_L', 'Fork_R',
                 'Wheel_FL', 'Wheel_FR', 'Wheel_RL', 'Wheel_RR'}
WHEELS = ('Wheel_FL', 'Wheel_FR', 'Wheel_RL', 'Wheel_RR')


def build_lods(parts, movers):
    """-> {node name: [mesh LOD0, LOD1, (LOD2)]}.  Fewer renderers further out:
    LOD1 folds the steering wheel into the body; LOD2 folds the wheels into the
    body and the forks into the carriage (the mast still lifts and tilts)."""
    def levels(ob):
        r1, r2 = LOD_PLAN[ob.name]
        sym = None if ob.name in NOT_SYMMETRIC else 'X'
        l1 = K.make_lod(ob, r1, ob.name + '_LOD1', symmetry=sym)
        l2 = K.make_lod(ob, r2, ob.name + '_LOD2', symmetry=sym) if r2 else None
        return l1, l2

    body = [levels(p) for p in parts]
    m = {o.name: o for o in movers}
    lv = {n: levels(o) for n, o in m.items()}
    for n, o in m.items():
        o.name = o.data.name = n + '_LOD0'
    lods = {
        'Body': [K.join(parts, 'Body_LOD0'),
                 K.join([l1 for l1, _ in body] + [lv['SteeringWheel'][0]], 'Body_LOD1'),
                 K.join([l2 for _, l2 in body] + [lv[w][1] for w in WHEELS], 'Body_LOD2')],
        'SteeringWheel': [m['SteeringWheel']],
        'Carriage': [m['Carriage'], lv['Carriage'][0],
                     K.join([lv['Carriage'][1], lv['Fork_L'][1], lv['Fork_R'][1]], 'Carriage_LOD2')],
    }
    for n in ('Mast', 'MastInner'):
        lods[n] = [m[n], *lv[n]]
    for n in WHEELS + ('Fork_L', 'Fork_R'):
        lods[n] = [m[n], lv[n][0]]
    return lods


NODE_PIVOTS = {
    'Body': (0.0, 0.0, 0.0), 'Mast': MAST_PIVOT, 'MastInner': MAST_PIVOT, 'Carriage': MAST_PIVOT,
    'Fork_L': (FORK_X, -0.605, 0.0), 'Fork_R': (-FORK_X, -0.605, 0.0),
    'Wheel_FL': (FW_X, 0.0, FW_R), 'Wheel_FR': (-FW_X, 0.0, FW_R),
    'Wheel_RL': (RW_X, WB, RW_R), 'Wheel_RR': (-RW_X, WB, RW_R),
    'Steer_RL': (RW_X, WB, RW_R), 'Steer_RR': (-RW_X, WB, RW_R),
}
NODE_PARENTS = {
    'Body': 'Forklift', 'SteeringWheel': 'Forklift', 'Wheel_FL': 'Forklift',
    'Wheel_FR': 'Forklift', 'Steer_RL': 'Forklift', 'Steer_RR': 'Forklift',
    'Wheel_RL': 'Steer_RL', 'Wheel_RR': 'Steer_RR', 'Mast': 'Forklift', 'MastInner': 'Mast',
    'Carriage': 'MastInner', 'Fork_L': 'Carriage', 'Fork_R': 'Carriage', 'LoadSocket': 'Carriage',
}


def rig(lods, steering_matrix):
    """Every animated name is an empty (a Unity Transform); the meshes hang under
    it as <name>_LODn with identity local transforms."""
    nodes = {'Forklift': K.empty('Forklift', (0, 0, 0), size=0.5, display='ARROWS')}
    for name, pivot in NODE_PIVOTS.items():
        nodes[name] = K.empty(name, pivot, size=0.12)
    sw = nodes['SteeringWheel'] = K.empty('SteeringWheel', (0, 0, 0), size=0.12)
    sw.rotation_mode = 'ZXY'                 # spin (local Z) first, then the column tilt
    sw.matrix_world = steering_matrix
    nodes['LoadSocket'] = K.empty('LoadSocket', (0.0, -1.20, 0.045), size=0.25, display='CUBE')
    bpy.context.view_layer.update()
    for name, meshes in lods.items():
        for mesh in meshes:
            K.bake_into(mesh, nodes[name])
    bpy.context.view_layer.update()
    for child, parent in NODE_PARENTS.items():
        K.parent_keep_local(nodes[child], nodes[parent])
    bpy.context.view_layer.update()
    return nodes['Forklift']


def show_lod(objs, level):
    for o in objs:
        if o.type == 'MESH' and '_LOD' in o.name:
            o.hide_render = int(o.name.rsplit('_LOD', 1)[1]) != level


def set_pose(objs, lift=0.0, tilt=0.0, steer=0.0, spin=0.0):
    """lift: fork height (m); tilt: degrees (+ forward); steer: rear-wheel angle deg."""
    o = {ob.name: ob for ob in objs}
    o['Mast'].rotation_euler = (radians(tilt), 0, 0)
    o['MastInner'].location = (0, 0, lift / 2)
    o['Carriage'].location = (0, 0, lift / 2)
    o['Steer_RL'].rotation_euler = (0, 0, radians(steer))
    o['Steer_RR'].rotation_euler = (0, 0, radians(steer))
    for w in ('Wheel_FL', 'Wheel_FR', 'Wheel_RL', 'Wheel_RR'):
        o[w].rotation_euler = (spin, 0, 0)
    o['SteeringWheel'].rotation_euler.z = radians(-steer * 4)
    bpy.context.view_layer.update()


# =============================================================================
# Preview studio (render-only props, never exported)
# =============================================================================

def studio(scene):
    props = K.collection('PreviewProps')
    # floor
    p = Part('Floor_Studio')
    p.box((0, 0, -0.05), (80, 80, 0.1), 'Concrete')
    mat = bpy.data.materials.new('Concrete')
    nb = K.NB(mat)
    x, y, z = nb.xyz(nb.obj)
    base = nb.mixc(nb.noise(0.8, 8, 0.6), (0.20, 0.195, 0.185), (0.26, 0.25, 0.235))
    stains = nb.mr(nb.noise(0.35, 6, 0.7), 0.55, 0.72)
    base = nb.mixc(nb.m('MULTIPLY', stains, 0.55), base, (0.07, 0.065, 0.06))
    tyre = nb.mr(nb.noise(1.0, 4, 0.5, vec=nb.scaled(0.4, 6, 1)), 0.6, 0.75)
    base = nb.mixc(nb.m('MULTIPLY', tyre, 0.25), base, (0.05, 0.05, 0.05))
    lane = nb.m('MULTIPLY', nb.m('LESS_THAN', nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('ABSOLUTE', x), 1.75)), 0.05),
                nb.mr(nb.noise(30, 4), 0.3, 0.45))
    base = nb.mixc(lane, base, (0.75, 0.50, 0.02))
    rough = nb.mixf(stains, nb.mr(nb.noise(4, 6), 0.3, 0.7, 0.55, 0.8), 0.35)
    nrm = nb.bump(nb.noise(60, 8, 0.7), 0.15, 0.002)
    K.finalize_material(nb, base, rough, 0.0, nrm)
    floor = p.build({'Concrete': mat}, coll=props)

    world = bpy.data.worlds.new('Studio')
    scene.world = world
    world.use_nodes = True
    bg = world.node_tree.nodes['Background']
    bg.inputs['Color'].default_value = (0.052, 0.046, 0.043, 1)
    bg.inputs['Strength'].default_value = 0.55

    def area(name, loc, target, size, power, color=(1, 1, 1)):
        ld = bpy.data.lights.new(name, 'AREA')
        ld.size = size
        ld.energy = power
        ld.color = color
        ob = bpy.data.objects.new(name, ld)
        props.objects.link(ob)
        ob.location = loc
        d = V(target) - V(loc)
        ob.rotation_euler = d.to_track_quat('-Z', 'Y').to_euler()
        return ob
    area('Key', (4.5, -4.0, 6.5), (0, 0.3, 0.8), 4.0, 1500, (1.0, 0.95, 0.88))
    area('Fill', (-6.0, -2.5, 3.0), (0, 0.3, 1.0), 6.0, 380, (0.82, 0.88, 1.0))
    area('Rim', (-2.0, 6.5, 4.5), (0, 0.3, 1.2), 3.0, 1800, (1.0, 0.95, 0.9))
    return props


def scale_figure(props, loc=(1.35, -0.9, 0), rot=0.4):
    """1.78 m clay mannequin for scale (like a blockout reference figure)."""
    mat = bpy.data.materials.new('Clay')
    nb = K.NB(mat)
    K.finalize_material(nb, (0.16, 0.16, 0.165), 0.55, 0.0, nb.bevel_normal(0.01))
    p = Part('ScaleFigure')
    for s in (1, -1):
        p.box((s * 0.085, 0, 0.44), (0.12, 0.14, 0.86), 'Clay')          # legs
        p.box((s * 0.25, 0, 1.13), (0.09, 0.11, 0.62), 'Clay')           # arms
    p.box((0, 0, 1.18), (0.40, 0.21, 0.64), 'Clay')                      # torso
    p.box((0, 0, 1.54), (0.10, 0.10, 0.08), 'Clay')                      # neck
    p.box((0, 0, 1.675), (0.19, 0.21, 0.21), 'Clay')                     # head
    ob = p.build({'Clay': mat}, coll=props)
    finish(ob, 0.02, 3, 40)
    ob.location = loc
    ob.rotation_euler = (0, 0, rot)
    return ob


def pallet_load(props, socket):
    wood = bpy.data.materials.new('Wood')
    nb = K.NB(wood)
    grain = nb.noise(1.0, 8, 0.7, vec=nb.scaled(3, 3, 60))
    col = nb.mixc(grain, (0.36, 0.23, 0.12), (0.52, 0.36, 0.20))
    K.finalize_material(nb, col, 0.7, 0.0, nb.bump(grain, 0.3, 0.002, nb.bevel_normal(0.004)))
    card = bpy.data.materials.new('Cardboard')
    nb = K.NB(card)
    x, y, z = nb.xyz(nb.obj)
    tape = nb.m('LESS_THAN', nb.m('ABSOLUTE', nb.m('SUBTRACT', nb.m('FRACT', nb.m('DIVIDE', nb.m('ADD', x, 0.19), 0.38)), 0.5)), 0.05)
    col = nb.mixc(nb.noise(8, 6), (0.40, 0.25, 0.12), (0.46, 0.30, 0.15))
    col = nb.mixc(nb.m('MULTIPLY', tape, nb.up_mask(0.9, 0.99)), col, (0.55, 0.42, 0.25))
    K.finalize_material(nb, col, 0.75, 0.0, nb.bevel_normal(0.006))
    mats = {'Wood': wood, 'Cardboard': card}
    p = Part('Pallet')
    for x in (-0.35, 0.0, 0.35):                             # bottom boards
        p.box((x, 0, 0.011), (0.10 if x else 0.145, 1.2, 0.022), 'Wood')
    for x in (-0.35, 0.0, 0.35):
        for y in (-0.5, 0.0, 0.5):
            p.box((x, y, 0.061), (0.10 if x else 0.145, 0.145 if y else 0.1, 0.078), 'Wood')
    for y in (-0.55, 0.0, 0.55):                             # stringer boards
        p.box((0, y, 0.111), (0.8, 0.10 if y else 0.145, 0.022), 'Wood')
    for x in (-0.3275, -0.1625, 0.0, 0.1625, 0.3275):        # top deck
        p.box((x, 0, 0.133), (0.145 if x in (-0.3275, 0.0, 0.3275) else 0.10, 1.2, 0.022), 'Wood')
    for layer in range(2):
        for ix in (-0.2, 0.2):
            for iy in (-0.3, 0.3):
                p.box((ix, iy, 0.144 + 0.175 + layer * 0.355), (0.385, 0.585, 0.35), 'Cardboard')
    ob = p.build(mats, coll=props)
    finish(ob, 0.004, 1, 40)
    ob.parent = socket
    ob.matrix_parent_inverse.identity()
    ob.location = (0, -0.06, -0.10)
    return ob


def camera(scene, name, loc, target, lens=55, ortho=None):
    cd = bpy.data.cameras.new(name)
    cd.lens = lens
    cd.sensor_width = 36
    if ortho:
        cd.type = 'ORTHO'
        cd.ortho_scale = ortho
    ob = bpy.data.objects.new(name, cd)
    scene.collection.objects.link(ob)
    ob.location = loc
    ob.rotation_euler = (V(target) - V(loc)).to_track_quat('-Z', 'Y').to_euler()
    return ob


def render(scene, cam, path, res=(1600, 1000), samples=128):
    scene.camera = cam
    scene.render.resolution_x, scene.render.resolution_y = res
    scene.render.resolution_percentage = 100
    scene.cycles.samples = samples
    scene.cycles.use_denoising = True
    scene.render.image_settings.file_format = 'PNG'
    scene.render.image_settings.color_mode = 'RGB'
    scene.render.filepath = path
    bpy.ops.render.render(write_still=True)
    print(f"[forklift] render -> {path}")


def preview_renders(scene, root, quick=False):
    objs = [root] + list(root.children_recursive)
    props = studio(scene)
    fig = scale_figure(props, loc=(1.05, 2.75, 0), rot=-0.5)
    socket = next(o for o in objs if o.name == 'LoadSocket')
    load = pallet_load(props, socket)
    try:
        scene.view_settings.view_transform = 'AgX'
        scene.view_settings.look = 'AgX - Punchy'
    except TypeError:
        pass
    spp = 32 if quick else 160
    res = (800, 500) if quick else (1600, 1000)
    os.makedirs(PREVIEW_DIR, exist_ok=True)

    load.hide_render = True
    show_lod(objs, 0)
    set_pose(objs, 0.0, 0.0, 0.0)
    render(scene, camera(scene, 'Cam_Hero', (4.3, -5.0, 1.55), (0.05, 0.15, 0.95), 50),
           os.path.join(PREVIEW_DIR, 'forklift_hero.png'), res, spp)
    fig.hide_render = True
    render(scene, camera(scene, 'Cam_Rear', (-4.2, 5.4, 2.1), (0, 0.55, 0.85), 50),
           os.path.join(PREVIEW_DIR, 'forklift_rear.png'), res, spp)
    load.hide_render = False
    set_pose(objs, 1.45, -4.0, 22.0)
    render(scene, camera(scene, 'Cam_Action', (-4.4, -4.6, 2.3), (0, -0.35, 1.35), 50),
           os.path.join(PREVIEW_DIR, 'forklift_action.png'), res, spp)
    load.hide_render = True
    set_pose(objs, 0.0, 0.0, 0.0)
    render(scene, camera(scene, 'Cam_Cab', (1.55, -1.25, 2.35), (0.0, 0.45, 1.05), 40),
           os.path.join(PREVIEW_DIR, 'forklift_cab.png'), res, spp)
    fig.hide_render = False
    render(scene, camera(scene, 'Cam_Side', (9.0, 0.3, 1.15), (0.0, 0.3, 1.15), ortho=4.4),
           os.path.join(PREVIEW_DIR, 'forklift_side.png'), res, spp)
    fig.hide_render = True
    cam = camera(scene, 'Cam_LOD', (4.3, -5.0, 1.55), (0.05, 0.15, 0.95), 50)
    for lvl in (1, 2):
        show_lod(objs, lvl)
        render(scene, cam, os.path.join(PREVIEW_DIR, f'forklift_lod{lvl}.png'), (800, 500), spp)
    show_lod(objs, 0)
    return props


# =============================================================================
# Main
# =============================================================================

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--paint', default='yellow', choices=sorted(PAINTS))
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true', help='small bake + fast renders')
    ap.add_argument('--cpu', action='store_true', help='bake / render on the CPU')
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

    parts, movers = build_forklift(PAINTS[args.paint])
    steering_matrix = movers[-1].matrix_world.copy()
    everything = parts + movers

    K.atlas_unwrap(everything, margin=0.0025, weights=TEXEL_WEIGHTS)
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.6      # AO bake reach
    except AttributeError:
        pass
    suffix = '' if args.paint == 'yellow' else '_' + args.paint.capitalize()
    paths = K.bake_texture_set(everything, OUT_DIR, 'Forklift' + suffix, args.bake, args.save)
    runtime = K.make_runtime_material('Forklift' + suffix, paths)
    K.assign_single_material(everything, runtime)

    lods = build_lods(parts, movers)
    print('[forklift] triangles per LOD (renderers):')
    totals = []
    for lvl in range(3):
        obs = [ls[lvl] for ls in lods.values() if len(ls) > lvl]
        totals.append(sum(K.tri_count(o) for o in obs))
        print(f'    LOD{lvl}  {totals[-1]:>7}  ({len(obs)})  ' +
              ', '.join(f'{o.name}={K.tri_count(o)}' for o in obs))
    total = totals[0]

    root = rig(lods, steering_matrix)
    if args.paint == 'yellow':
        K.export_fbx(root, os.path.join(OUT_DIR, 'Forklift.fbx'))
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'Forklift{suffix}.blend'))
    if not args.no_render:
        preview_renders(scene, root, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'Forklift{suffix}.blend'))
    print(f'[forklift] done in {time.time() - t0:.1f}s  (tris={total})')


if __name__ == '__main__':
    main()
