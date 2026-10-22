"""
SimuLean kit: single-girder overhead travelling crane (EOT, "grua puente") on
steel runway beams.  10 t class, modular: the runway and the girder tile, the
end trucks and the trolley/hoist keep their aspect ratio.

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/overhead_crane.py --
            [--bake 4096] [--save 2048] [--no-render] [--quick]

Outputs
    Assets/SimuLean.Unity/Art/Models/OverheadCrane/<Piece>.fbx  (one per piece)
    Assets/SimuLean.Unity/Art/Models/OverheadCrane/OverheadCrane_*.png  (one shared atlas)
    Tools/Blender/build/OverheadCrane.blend
    Tools/Blender/previews/crane_*.png

Pieces (Unity frame: +Y up; the tiling axis of strips is local +Z)
    Runway_Segment  segment, 2 m along Z.  A45 crane rail on an HEB400 beam; rail
                    top at local y = 0 (the origin), beam underside at -0.455.
    Runway_EndStop  cap, 0.52 m.  Beam end plate + welded rail stop at buffer
                    height (0.30 above rail top), hazard striped.
    Girder_Segment  segment, 2 m along Z.  Box girder 0.80 high with a wide
                    bottom running flange (the trolley wheels run on it); origin
                    at the girder top.  Festoon C-track on local +X.
    Girder_Logo     same as Girder_Segment with "SimuLean" and "SWL 10 t" on both
                    webs (UnityModularStrip puts it at the centre of the run).
    Girder_End      cap, 0.40 m.  End plate and the bolted connection plate that
                    sits on the end truck (underside 0.83 below the girder top).
    EndTruck        fixed.  Origin on the rail top under the truck centre, runs
                    along Z.  Nodes: Wheel_A (z +1.25) / Wheel_B (z -1.25), spin
                    about local X, r = 0.16.  Top plate top at y = 0.52.
    Trolley         fixed.  Origin at the girder bottom-flange underside, travels
                    along local X.  Nodes:
                      Drum          spin about local X (r = 0.16)
                      Ropes         localScale.y = drop (m); unit strands 0..-1
                      HookBlock     localPosition.y = -0.61 - drop
                        '- HookSocket   attach the carried load here
                      PendantCable  localScale.y = cable length (m)
                      Pendant       localPosition.y = PendantCable.y - length

Assembly (what UnityOverheadCrane builds; bridge frame origin on the rail top at
the left rail, X across the span S):
    EndTruck at x = +S/2 and x = -S/2 (the latter turned 180 deg about Y)
    Girder strip from x = -(S/2 + 0.20), y = 1.35, rotated so its Z runs along +X,
    length S + 0.40
    Trolley at y = 0.55 (girder underside), travel |x| <= S/2 - 0.55
"""

import os
import sys
import argparse
import time
from math import radians, pi, atan2, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
from mathutils import Vector, Matrix, Euler  # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
import simulean_modular as KM                # noqa: E402
from simulean_kit import V, Part, rrect, finish, boolean, cull_faces  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'OverheadCrane')
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'
PREFIX = 'OverheadCrane'

# ---------------------------------------------------------------- dimensions (authored frame, m)
PERIOD = 2.0                                  # segment length of every tiling piece
YELLOW = (0.80, 0.42, 0.015)
INK = (0.022, 0.022, 0.025)
# runway: A45 rail on HEB400, rail top at z = 0
RAIL_FOOT = -0.055
BEAM_H, BEAM_B, BEAM_TF, BEAM_TW, BEAM_R = 0.400, 0.300, 0.024, 0.0135, 0.027
BEAM_TOP, BEAM_BOT = RAIL_FOOT, RAIL_FOOT - BEAM_H
# girder: origin at the top
G_H, G_TOP_HB, G_TOP_T = 0.80, 0.20, 0.02
G_WEB_X, G_BOT_HB, G_BOT_T = 0.175, 0.25, 0.025
G_END, G_OVERHANG, CONN_T = 0.40, 0.20, 0.03
FEST_X, FEST_Z = 0.40, -0.062                 # festoon C-track centre / top
# end truck
WHEEL_R, WHEEL_Y, WHEEL_Z = 0.16, 1.25, 0.16
TRUCK_HX, TRUCK_HY, TRUCK_Z0, TRUCK_Z1 = 0.13, 1.45, 0.08, 0.50
TRUCK_TOP = 0.52
GIRDER_TOP = TRUCK_TOP + CONN_T + G_H         # 1.35 above the rail top
# trolley: origin at the girder bottom-flange underside
DRUM_Z, DRUM_R = -0.45, 0.16
ROPE_Z, ROPE_X = DRUM_Z - DRUM_R, 0.08        # rope leaves the drum at -0.61
HOOK_Z = ROPE_Z - 1.0                         # sheave centre at unit rope length
CAB = -1                                      # cabinet side (y sign): faces the girder festoon
PCAB = (-0.05, CAB * 0.32, -0.64)             # pendant cable exit (cabinet gland)
PEND = (PCAB[0], PCAB[1], PCAB[2] - 1.0)

MATS = {}


# =============================================================================
# Materials
# =============================================================================

def rail_steel(name):
    mat = M._new(name)
    nb = K.NB(mat)
    x, y, z = nb.xyz(nb.obj)
    top = nb.m('MULTIPLY', nb.mr(z, -0.0045, -0.0015), nb.up_mask(0.8, 0.95))
    scratch = nb.noise(1.0, 8, 0.6, aniso=(90, 1.5, 90))
    base = nb.mixc(nb.noise(3.0, 6, 0.6), (0.050, 0.047, 0.044), (0.085, 0.064, 0.048))
    base = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.05), 0.7), base, M.GRIME)
    shine = nb.mixc(scratch, (0.40, 0.40, 0.39), (0.62, 0.62, 0.60))
    col = nb.mixc(top, base, shine)
    r = nb.mixf(top, nb.mr(nb.noise(8, 4), 0.3, 0.7, 0.55, 0.7), nb.mr(scratch, 0.3, 0.7, 0.14, 0.3))
    metal = nb.mixf(top, 0.55, 1.0)
    return K.finalize_material(nb, col, r, metal, nb.bump(scratch, 0.15, 0.0004))


def wheel_steel(name):
    mat = M._new(name)
    nb = K.NB(mat)
    x, y, z = nb.xyz(nb.obj)
    ay = nb.m('SUBTRACT', nb.m('ABSOLUTE', y), WHEEL_Y)
    zz = nb.m('SUBTRACT', z, WHEEL_Z)
    rr = nb.m('SQRT', nb.m('ADD', nb.m('MULTIPLY', ay, ay), nb.m('MULTIPLY', zz, zz)))
    tread = nb.m('MULTIPLY', nb.mr(rr, WHEEL_R - 0.004, WHEEL_R - 0.0005),
                 nb.mr(nb.m('ABSOLUTE', x), 0.034, 0.029))
    base = nb.mixc(nb.noise(6.0, 6, 0.6), (0.055, 0.052, 0.05), (0.08, 0.075, 0.07))
    base = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.06), 0.8), base, M.GRIME)
    col = nb.mixc(tread, base, (0.52, 0.52, 0.50))
    return K.finalize_material(nb, col, nb.mixf(tread, 0.5, 0.22), nb.mixf(tread, 0.7, 1.0))


def drum_steel(name):
    """Grooved hoist drum; the two halves are wound with rope (opposite hands)."""
    mat = M._new(name)
    nb = K.NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    zz = nb.m('SUBTRACT', z, DRUM_Z)
    theta = nb.m('ARCTAN2', zz, y)
    ax = nb.m('ABSOLUTE', x)
    phase = nb.m('SUBTRACT', nb.m('DIVIDE', ax, 0.018), nb.m('DIVIDE', theta, 2 * pi))
    ridge = nb.m('ABSOLUTE', nb.m('SINE', nb.m('MULTIPLY', phase, pi)))
    wound = nb.m('MULTIPLY', nb.mr(ax, 0.07, 0.08), nb.mr(ax, 0.26, 0.25))
    galv = nb.mixc(nb.noise(40.0, 3, 0.5), (0.40, 0.41, 0.42), (0.52, 0.53, 0.54))
    rope = nb.mixc(ridge, (0.025, 0.025, 0.024), (0.10, 0.10, 0.095))
    col = nb.mixc(wound, galv, rope)
    col = nb.mixc(nb.m('MULTIPLY', nb.cavity(0.05), 0.6), col, M.GRIME)
    h = nb.mixf(wound, nb.m('MULTIPLY', ridge, 0.4), ridge)
    return K.finalize_material(nb, col, nb.mixf(wound, 0.38, 0.45), nb.mixf(wound, 1.0, 0.75),
                               nb.bump(h, 0.8, 0.003))


def wire_rope(name):
    """Longitudinal strands only: the rope mesh is stretched along its length."""
    mat = M._new(name)
    nb = K.NB(mat, period={})
    x, y, z = nb.xyz(nb.obj)
    xs = nb.m('SUBTRACT', nb.m('ABSOLUTE', x), ROPE_X)
    ang = nb.m('ARCTAN2', y, xs)
    ridge = nb.m('ABSOLUTE', nb.m('SINE', nb.m('MULTIPLY', ang, 3.0)))
    col = nb.mixc(ridge, (0.035, 0.035, 0.034), (0.13, 0.13, 0.125))
    return K.finalize_material(nb, col, 0.42, 0.8, nb.bump(ridge, 0.7, 0.002))


def build_materials():
    K.NB.default_period = {'y': PERIOD}
    logo, la = K.render_text_image('SimuLean', os.path.join(BUILD_DIR, 'decal_logo.png'),
                                   256, FONT, bold_offset=0.016)
    swl, sa = K.render_text_image('SWL 10 t', os.path.join(BUILD_DIR, 'decal_swl10.png'),
                                  256, FONT, bold_offset=0.014)
    ten, ta = K.render_text_image('10 t', os.path.join(BUILD_DIR, 'decal_10t.png'),
                                  256, FONT, bold_offset=0.016)
    girder_decals = []
    for sx in (1, -1):
        girder_decals += [
            dict(image=logo, origin=(sx * G_WEB_X, PERIOD / 2, -0.36), u=(0, sx, 0), v=(0, 0, 1),
                 size=(1.30, 1.30 / la), normal=(sx, 0, 0), color=INK),
            dict(image=swl, origin=(sx * G_WEB_X, PERIOD / 2, -0.60), u=(0, sx, 0), v=(0, 0, 1),
                 size=(0.09 * sa, 0.09), normal=(sx, 0, 0), color=INK),
        ]
    truck_decals = [dict(image=logo, origin=(-TRUCK_HX, 0.0, 0.29), u=(0, -1, 0), v=(0, 0, 1),
                         size=(0.62, 0.62 / la), normal=(-1, 0, 0), color=INK)]
    cab_decals = [dict(image=logo, origin=(-0.08, CAB * 0.40, -0.27), u=(-CAB, 0, 0), v=(0, 0, 1),
                       size=(0.26, 0.26 / la), normal=(0, CAB, 0), color=INK)]
    hook_decals = [dict(image=ten, origin=(0, s * 0.075, HOOK_Z + 0.062), u=(-s, 0, 0), v=(0, 0, 1),
                        size=(0.085, 0.085 / ta), normal=(0, s, 0), color=INK, depth=0.01)
                   for s in (1, -1)]
    MATS.update({
        'Yellow': M.paint('Yellow', YELLOW, rough=0.36, wear=0.55),
        'GirderLogo': M.paint('GirderLogo', YELLOW, rough=0.36, wear=0.55, decals=girder_decals),
        'TruckPaint': M.paint('TruckPaint', YELLOW, rough=0.36, wear=0.7, decals=truck_decals),
        'HookPaint': M.paint('HookPaint', YELLOW, rough=0.38, wear=1.2, decals=hook_decals),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=1.1,
                          color_fn=M.hazard_fn(YELLOW, width=0.05, axis=('x', 'z'))),
        'Runway': M.paint('Runway', (0.050, 0.055, 0.060), rough=0.48, wear=0.5, edge=0.005),
        'HoistPaint': M.paint('HoistPaint', (0.15, 0.16, 0.17), rough=0.42, wear=0.5),
        'Cabinet': M.paint('Cabinet', (0.50, 0.51, 0.50), rough=0.45, wear=0.3, decals=cab_decals),
        'RailSteel': rail_steel('RailSteel'),
        'WheelSteel': wheel_steel('WheelSteel'),
        'Drum': drum_steel('Drum'),
        'Rope': wire_rope('Rope'),
        'Galv': M.galvanised('Galv'),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'HookSteel': M.dark_steel('HookSteel', color=(0.045, 0.046, 0.05), rough=0.38, metal=0.9),
        'Buffer': M.rubber('Buffer', color=(0.55, 0.09, 0.02), rough=0.55),
        'Rubber': M.rubber('Rubber'),
        'Plastic': M.plastic('Plastic'),
        'Cable': M.plastic('Cable', color=(0.015, 0.015, 0.016), rough=0.5),
        'EStop': M.plastic('EStop', color=(0.60, 0.02, 0.015), rough=0.35),
    })
    return MATS


# =============================================================================
# Helpers
# =============================================================================

def mk(name, fn, bevel=0.004, seg=1, angle=40.0, cull=None, cutters=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cutters:
        boolean(ob, [c() for c in cutters])
    if cull:
        cull_faces(ob, cull)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def ends(axis, *vals, eps=1e-4):
    """Cull predicate: faces lying in the planes axis = vals (open tile joints)."""
    i = 'xyz'.index(axis)
    return lambda c, n: abs(n[i]) > 0.99 and any(abs(c[i] - v) < eps for v in vals)


def bx(p, x0, x1, y0, y1, z0, z1, mat):
    p.box_minmax((min(x0, x1), min(y0, y1), min(z0, z1)), (max(x0, x1), max(y0, y1), max(z0, z1)), mat)


def cut_box(lo, hi):
    def f():
        p = Part('cutter')
        p.box_minmax(lo, hi, 'DarkSteel')
        return p.build(MATS)
    return f


def hexbolt(p, c, axis, h, r=0.012, washer=True, mat='DarkSteel'):
    """Hex head (or nut) standing on a surface at c, pointing along +/-axis."""
    c, d = V(c), V(axis).normalized()
    if washer:
        p.cylinder(c, c + d * 0.003, r * 1.3, mat, n=10)
    p.cylinder(c + d * 0.003, c + d * h, r, mat, n=6)


def i_profile(h, b, tf, tw, r, top, seg=3):
    """Rolled I-section outline (u = across the flanges, v = height), top at v = top."""
    t, bt = top, top - h
    pts = [(-b / 2, t), (b / 2, t), (b / 2, t - tf), (tw / 2, t - tf), (tw / 2, bt + tf),
           (b / 2, bt + tf), (b / 2, bt), (-b / 2, bt), (-b / 2, bt + tf), (-tw / 2, bt + tf),
           (-tw / 2, t - tf), (-b / 2, t - tf)]
    rad = [0, 0, 0, r, r, 0, 0, 0, 0, r, r, 0]
    return K.fillet_closed(pts, rad, seg)


def rail_profile():
    """A45 crane rail (head 45, foot 125, height 55 mm), top at 0."""
    pts = [(-0.0225, -0.001), (0.0, 0.0), (0.0225, -0.001), (0.0225, -0.020), (0.012, -0.025),
           (0.012, -0.041), (0.0625, -0.047), (0.0625, -0.055), (-0.0625, -0.055),
           (-0.0625, -0.047), (-0.012, -0.041), (-0.012, -0.025), (-0.0225, -0.020)]
    rad = [0.005, 0, 0.005, 0.003, 0.004, 0.006, 0.002, 0, 0, 0.002, 0.006, 0.004, 0.003]
    return K.fillet_closed(pts, rad, 3)


def girder_profile():
    t, w, bt = G_TOP_T, G_WEB_X, -G_H + G_BOT_T
    pts = [(-G_TOP_HB, 0), (G_TOP_HB, 0), (G_TOP_HB, -t), (w, -t), (w, bt), (G_BOT_HB, bt),
           (G_BOT_HB, -G_H), (-G_BOT_HB, -G_H), (-G_BOT_HB, bt), (-w, bt), (-w, -t), (-G_TOP_HB, -t)]
    rad = [0, 0, 0, 0.007, 0.007, 0, 0, 0, 0, 0.007, 0.007, 0]
    return K.fillet_closed(pts, rad, 2)


def stiffener(p, sx, y, t=0.010):
    x0 = BEAM_TW / 2
    top, bot = BEAM_TOP - BEAM_TF, BEAM_BOT + BEAM_TF
    prof = [(x0 + 0.02, top), (0.14, top), (0.14, bot), (x0 + 0.02, bot), (x0, bot + 0.02), (x0, top - 0.02)]
    p.extrude([(sx * u, v) for u, v in prof], 'Y', y - t / 2, y + t / 2, 'Runway')


def rail_clips(p, ys):
    for y in ys:
        for sx in (1, -1):
            bx(p, sx * 0.058, sx * 0.118, y - 0.04, y + 0.04, RAIL_FOOT, RAIL_FOOT + 0.012, 'DarkSteel')
            hexbolt(p, (sx * 0.095, y, RAIL_FOOT + 0.012), (0, 0, 1), 0.011, 0.012)
            p.cylinder((sx * 0.095, y, RAIL_FOOT + 0.021), (sx * 0.095, y, RAIL_FOOT + 0.029), 0.0065,
                       'DarkSteel', n=6)


def c_track(p, y0, y1):
    """Festoon C-rail, open downwards."""
    w, h, t, slot = 0.040, 0.035, 0.003, 0.018
    pts = [(-w / 2, 0), (w / 2, 0), (w / 2, -h), (slot / 2, -h), (slot / 2, -h + t), (w / 2 - t, -h + t),
           (w / 2 - t, -t), (-w / 2 + t, -t), (-w / 2 + t, -h + t), (-slot / 2, -h + t), (-slot / 2, -h),
           (-w / 2, -h)]
    p.extrude([(FEST_X + u, FEST_Z + v) for u, v in pts], 'Y', y0, y1, 'Galv')


def festoon_arm(p, y):
    bx(p, G_WEB_X, FEST_X + 0.025, y - 0.02, y + 0.02, FEST_Z, FEST_Z + 0.008, 'Galv')
    bx(p, G_WEB_X, G_WEB_X + 0.008, y - 0.04, y + 0.04, FEST_Z - 0.03, FEST_Z + 0.03, 'Galv')


# =============================================================================
# Runway
# =============================================================================

def build_runway_segment():
    L = PERIOD
    beam = mk('RW_Beam', lambda p: p.extrude(i_profile(BEAM_H, BEAM_B, BEAM_TF, BEAM_TW, BEAM_R, BEAM_TOP),
                                             'Y', 0.0, L, 'Runway'),
              bevel=0.0, cull=ends('y', 0.0, L))
    rail = mk('RW_Rail', lambda p: p.extrude(rail_profile(), 'Y', 0.0, L, 'RailSteel'),
              bevel=0.0, cull=ends('y', 0.0, L))
    stiff = mk('RW_Stiff', lambda p: [stiffener(p, sx, L / 2) for sx in (1, -1)], bevel=0.0)
    clips = mk('RW_Clips', lambda p: rail_clips(p, (0.25, 0.75, 1.25, 1.75)), bevel=0.0)
    return KM.Piece('Runway_Segment', 'segment', [beam, rail, stiff, clips], levels=2,
                    lod={'RW_Clips': (None,)},
                    ghosts=[('Runway_Segment', (0, L, 0)), ('Runway_Segment', (0, -L, 0))])


def build_runway_end():
    L = -0.50

    def body(p):
        p.extrude(i_profile(BEAM_H, BEAM_B, BEAM_TF, BEAM_TW, BEAM_R, BEAM_TOP), 'Y', L, 0.0, 'Runway')
    beam = mk('RWE_Beam', body, bevel=0.0, cull=ends('y', 0.0, L))

    def plates(p):
        bx(p, -0.165, 0.165, L - 0.016, L + 0.001, BEAM_BOT - 0.01, BEAM_TOP, 'Runway')
        for sx in (1, -1):
            stiffener(p, sx, -0.31, 0.012)
    plate = mk('RWE_Plates', plates, bevel=0.003)
    rail = mk('RWE_Rail', lambda p: p.extrude(rail_profile(), 'Y', -0.28, 0.0, 'RailSteel'),
              bevel=0.0, cull=ends('y', 0.0))
    stop = mk('RWE_Stop', lambda p: bx(p, -0.15, 0.15, -0.32, -0.30, BEAM_TOP, 0.42, 'Hazard'),
              bevel=0.004)

    def gussets(p):
        prof = [(-0.32, BEAM_TOP), (-0.32, 0.40), (-0.36, 0.40), (-0.49, 0.0), (-0.49, BEAM_TOP)]
        for x in (-0.12, 0.12):
            p.extrude(prof, 'X', x - 0.006, x + 0.006, 'Runway')
    gus = mk('RWE_Gussets', gussets, bevel=0.002)
    clips = mk('RWE_Clips', lambda p: rail_clips(p, (-0.18,)), bevel=0.0)
    return KM.Piece('Runway_EndStop', 'cap', [beam, plate, rail, stop, gus, clips], levels=2,
                    lod={'RWE_Clips': (None,)},
                    ghosts=[('Runway_Segment', (0, 0, 0))])


# =============================================================================
# Girder
# =============================================================================

def build_girder(name, mat):
    L = PERIOD
    tag = 'GL' if mat == 'GirderLogo' else 'GS'
    body = mk(f'{tag}_Body', lambda p: p.extrude(girder_profile(), 'Y', 0.0, L, mat),
              bevel=0.004, cull=ends('y', 0.0, L))

    def festoon(p):
        c_track(p, 0.0, L)
        festoon_arm(p, 0.2)
    fest = mk(f'{tag}_Festoon', festoon, bevel=0.0, cull=ends('y', 0.0, L))
    return KM.Piece(name, 'segment', [body, fest], levels=1,
                    ghosts=[('Girder_Segment', (0, L, 0)), ('Girder_Segment', (0, -L, 0))])


def build_girder_end():
    L = -G_END
    body = mk('GE_Body', lambda p: p.extrude(girder_profile(), 'Y', L, 0.0, 'Yellow'),
              bevel=0.004, cull=ends('y', 0.0, L))

    def plates(p):
        bx(p, -0.26, 0.26, L - 0.015, L + 0.001, -G_H, 0.0, 'Yellow')                    # end plate
        bx(p, -0.35, 0.35, -0.38, -0.02, -G_H - CONN_T, -G_H, 'Yellow')                  # connection plate
    plate = mk('GE_Plates', plates, bevel=0.004)

    def gussets(p):
        for sx in (1, -1):
            prof = [(sx * G_WEB_X, -G_H), (sx * 0.34, -G_H), (sx * 0.34, -G_H + 0.03), (sx * G_WEB_X, -G_H + 0.18)]
            for y in (-0.28, -0.12):
                p.extrude(prof, 'Y', y - 0.006, y + 0.006, 'Yellow')
    gus = mk('GE_Gussets', gussets, bevel=0.002)

    def bolts(p):
        for sx in (1, -1):
            for y in (-0.355, -0.20, -0.045):
                hexbolt(p, (sx * 0.30, y, -G_H), (0, 0, 1), 0.012, 0.013)
    bol = mk('GE_Bolts', bolts, bevel=0.0)

    def festoon(p):
        c_track(p, -0.36, 0.0)
        festoon_arm(p, -0.25)
        bx(p, FEST_X - 0.03, FEST_X + 0.03, -0.37, -0.36, FEST_Z - 0.05, FEST_Z + 0.005, 'Galv')   # end stop
    fest = mk('GE_Festoon', festoon, bevel=0.0, cull=ends('y', 0.0))
    box = mk('GE_FeedBox', lambda p: bx(p, G_WEB_X, G_WEB_X + 0.09, -0.36, -0.20, -0.36, -0.16, 'Cabinet'),
             bevel=0.006)
    return KM.Piece('Girder_End', 'cap', [body, plate, gus, bol, fest, box], levels=2,
                    lod={'GE_Bolts': (None,)},
                    ghosts=[('Girder_Segment', (0, 0, 0))])


# =============================================================================
# End truck
# =============================================================================

def build_end_truck():
    hx, hy = TRUCK_HX, TRUCK_HY
    cutters = [cut_box((-0.072, s * WHEEL_Y - 0.21, -0.05), (0.072, s * WHEEL_Y + 0.21, 0.36)) for s in (1, -1)]
    box = mk('ET_Box', lambda p: p.extrude(rrect(2 * hx, TRUCK_Z1 - TRUCK_Z0, 0.014, 2, 0,
                                                  (TRUCK_Z0 + TRUCK_Z1) / 2), 'Y', -hy, hy, 'TruckPaint'),
             bevel=0.004, cutters=cutters)

    def plates(p):
        for s in (1, -1):
            bx(p, -0.14, 0.14, s * hy, s * (hy + 0.015), 0.07, 0.51, 'TruckPaint')          # end plates
            for sx in (1, -1):                                                            # bearing flanges
                bx(p, sx * hx, sx * (hx + 0.012), s * WHEEL_Y - 0.09, s * WHEEL_Y + 0.09, 0.07, 0.25, 'TruckPaint')
        bx(p, -0.18, 0.18, -0.40, 0.40, TRUCK_Z1, TRUCK_TOP, 'TruckPaint')                  # top plate
    plate = mk('ET_Plates', plates, bevel=0.004)

    def gussets(p):
        for sx in (1, -1):
            prof = [(sx * hx, TRUCK_Z1), (sx * 0.175, TRUCK_Z1), (sx * hx, TRUCK_Z1 - 0.07)]
            for y in (-0.36, -0.12, 0.12, 0.36):
                p.extrude(prof, 'Y', y - 0.005, y + 0.005, 'TruckPaint')
    gus = mk('ET_Gussets', gussets, bevel=0.0)

    def hardware(p):
        for s in (1, -1):
            for sx in (1, -1):
                if not (sx == 1 and s == 1):                                              # gearbox side
                    p.cylinder((sx * (hx + 0.012), s * WHEEL_Y, WHEEL_Z),
                               (sx * (hx + 0.045), s * WHEEL_Y, WHEEL_Z), 0.055, 'DarkSteel', n=16)
                for dy in (-0.065, 0.065) if not (sx == 1 and s == 1) else ():
                    for dz in (-0.065, 0.065):
                        hexbolt(p, (sx * (hx + 0.012), s * WHEEL_Y + dy, WHEEL_Z + dz), (sx, 0, 0), 0.009,
                                0.009, washer=False)
            for sx in (1, -1):                                                            # top plate nuts
                for y in (-0.30, 0.30):
                    hexbolt(p, (sx * 0.155, y, TRUCK_Z1), (0, 0, -1), 0.012, 0.013)
            bx(p, -0.045, 0.045, s * (hy + 0.015), s * (hy + 0.025), 0.006, 0.09, 'DarkSteel')  # rail sweeper
    hw = mk('ET_Hardware', hardware, bevel=0.0)

    def drive(p):
        bx(p, hx + 0.012, 0.29, WHEEL_Y - 0.17, WHEEL_Y + 0.15, 0.05, 0.31, 'HoistPaint')    # shaft gearbox
        p.lathe([(1.08, 0.080), (1.06, 0.080), (1.06, 0.074), (0.80, 0.074), (0.80, 0.068),
                 (0.74, 0.068), (0.74, 0.078), (0.665, 0.078), (0.655, 0.05)], 20, 'HoistPaint',
                center=(0.225, 0.0, 0.215), axis='Y')
        bx(p, 0.19, 0.26, 0.86, 0.98, 0.28, 0.33, 'HoistPaint')                           # terminal box
        p.cylinder((0.225, 0.92, 0.33), (0.225, 0.92, 0.35), 0.012, 'Plastic', n=8)
    drv = mk('ET_Drive', drive, bevel=0.008, seg=2)

    def buffers(p):
        for s in (1, -1):
            y0 = s * (hy + 0.015)
            prof = [(0.0, 0.07), (s * 0.085, 0.07), (s * 0.10, 0.056), (s * 0.10, 0.02)]
            p.lathe([(y0 + t, r) for t, r in prof], 20, 'Buffer', center=(0, 0, 0.30), axis='Y')
            bx(p, -0.075, 0.075, y0, y0 + s * 0.012, 0.225, 0.375, 'DarkSteel')
    buf = mk('ET_Buffers', buffers, bevel=0.004)

    wheels = {}
    for node, s in (('Wheel_A', 1), ('Wheel_B', -1)):
        prof = [(-0.13, 0.035), (-0.055, 0.035), (-0.055, 0.075), (-0.048, 0.075), (-0.048, 0.18),
                (-0.040, 0.18), (-0.034, 0.162), (-0.030, WHEEL_R), (0.030, WHEEL_R), (0.034, 0.162),
                (0.040, 0.18), (0.048, 0.18), (0.048, 0.075), (0.055, 0.075), (0.055, 0.035), (0.13, 0.035)]
        wheels[node] = mk(f'ET_{node}', lambda p, prof=prof, s=s: p.lathe(
            prof, 24, 'WheelSteel', center=(0, s * WHEEL_Y, WHEEL_Z), axis='X'), bevel=0.0)
    body = [box, plate, gus, hw, drv, buf]
    return KM.Piece('EndTruck', 'fixed',
                    {None: body, 'Wheel_A': [wheels['Wheel_A']], 'Wheel_B': [wheels['Wheel_B']]},
                    levels=3,
                    lod={'ET_Box': (0.6, 0.25), 'ET_Plates': (1.0, 0.5), 'ET_Gussets': (1.0, None),
                         'ET_Hardware': (None, None), 'ET_Drive': (0.35, 0.12), 'ET_Buffers': (0.4, None),
                         'ET_Wheel_A': (0.3, None), 'ET_Wheel_B': (0.3, None)},
                    pivots={'Wheel_A': (0, WHEEL_Y, WHEEL_Z), 'Wheel_B': (0, -WHEEL_Y, WHEEL_Z)})


# =============================================================================
# Trolley + hoist
# =============================================================================

def build_trolley():
    def frame(p):
        for s in (1, -1):
            p.extrude(rrect(0.74, 0.31, 0.03, 3, 0, 0.055), 'Y', s * 0.285, s * 0.30, 'Yellow')
            bx(p, s * 0.27, s * 0.33, -0.285, 0.285, -0.10, -0.03, 'Yellow')                # cross beams
    fr = mk('TR_Frame', frame, bevel=0.004)

    def hoist_frame(p):
        for s in (1, -1):
            p.extrude(rrect(0.44, 0.58, 0.05, 3, 0, -0.38), 'X', s * 0.30, s * 0.32, 'HoistPaint')
    hf = mk('TR_HoistFrame', hoist_frame, bevel=0.004)

    def wheels(p):
        for x in (-0.25, 0.25):
            for s in (1, -1):
                prof = [(0.185, 0.06), (0.185, 0.07), (0.25, 0.07), (0.25, 0.082), (0.262, 0.082),
                        (0.262, 0.022), (0.30, 0.022)]
                p.lathe([(s * t, r) for t, r in prof], 16, 'DarkSteel', center=(x, 0, 0.095), axis='Y')
                if not (x > 0 and s > 0):
                    p.cylinder((x, s * 0.30, 0.095), (x, s * 0.325, 0.095), 0.04, 'DarkSteel', n=12)
    wh = mk('TR_Wheels', wheels, bevel=0.0)

    def gear(p):
        bx(p, -0.47, -0.32, -0.19, 0.19, -0.64, -0.22, 'HoistPaint')
        bx(p, 0.17, 0.33, 0.30, 0.37, 0.02, 0.17, 'HoistPaint')                              # travel gearbox
    gb = mk('TR_Gear', gear, bevel=0.018, seg=2)

    def motors(p):
        p.lathe([(0.32, 0.11), (0.345, 0.11), (0.345, 0.098), (0.62, 0.098), (0.62, 0.09), (0.68, 0.09),
                 (0.68, 0.102), (0.76, 0.102), (0.767, 0.06)], 24, 'HoistPaint',
                center=(0, 0, DRUM_Z), axis='X')
        for i in range(12):
            a = 2 * pi * (i + 0.5) / 12
            if abs(a - pi / 2) < 0.4:
                continue                                                                  # terminal box
            p.box((0.48, 0.104 * cos(a), DRUM_Z + 0.104 * sin(a)),
                  (0.25, 0.018, 0.004), 'HoistPaint', rot=Euler((a, 0, 0)))
        bx(p, 0.42, 0.56, -0.07, 0.07, DRUM_Z + 0.09, DRUM_Z + 0.16, 'HoistPaint')
        p.lathe([(0.37, 0.055), (0.52, 0.055), (0.52, 0.05), (0.56, 0.05), (0.565, 0.03)], 16, 'HoistPaint',
                center=(0.25, 0, 0.095), axis='Y')                                          # travel motor
        p.lathe([(-0.47, 0.12), (-0.49, 0.12), (-0.49, 0.10), (-0.50, 0.06)], 20, 'HoistPaint',
                center=(0, 0, DRUM_Z), axis='X')                                            # gearbox cover
    mo = mk('TR_Motors', motors, bevel=0.006)

    def cabinet(p):
        bx(p, -0.30, 0.14, CAB * 0.235, CAB * 0.40, -0.62, -0.15, 'Cabinet')
    cab = mk('TR_Cabinet', cabinet, bevel=0.01, seg=2)

    def small(p):
        for x in (-0.22, 0.06):
            bx(p, x - 0.02, x + 0.02, CAB * 0.28, CAB * 0.31, -0.155, -0.095, 'HoistPaint')   # hangers
        p.cylinder((PCAB[0], PCAB[1], -0.62), PCAB, 0.013, 'Plastic', n=10)                  # gland
        p.cylinder((0.08, CAB * 0.32, -0.62), (0.08, CAB * 0.32, -0.635), 0.010, 'Plastic', n=10)
        for x in (-0.37, 0.37):
            for s in (1, -1):
                sx = 1 if x > 0 else -1
                p.cylinder((x, s * 0.2925, 0.10), (x + sx * 0.05, s * 0.2925, 0.10), 0.028, 'Rubber', n=12)
    sm = mk('TR_Small', small, bevel=0.0)

    drum = mk('TR_Drum', lambda p: p.lathe(
        [(-0.30, 0.03), (-0.30, 0.178), (-0.285, 0.178), (-0.285, DRUM_R), (0.285, DRUM_R),
         (0.285, 0.178), (0.30, 0.178), (0.30, 0.03)], 32, 'Drum', center=(0, 0, DRUM_Z), axis='X'),
        bevel=0.0)

    def ropes(p):
        for sx in (1, -1):
            p.cylinder((sx * ROPE_X, 0, ROPE_Z), (sx * ROPE_X, 0, ROPE_Z - 1.0), 0.008, 'Rope', n=8, cap=False)
    rope = mk('TR_Ropes', ropes, bevel=0.0)

    hb = build_hook_block()

    def pcable(p):
        p.cylinder(PCAB, (PCAB[0], PCAB[1], PCAB[2] - 1.0), 0.0055, 'Cable', n=6, cap=False)
    pc = mk('TR_PendantCable', pcable, bevel=0.0)
    pend = build_pendant()

    body = [fr, hf, wh, gb, mo, cab, sm]
    return KM.Piece('Trolley', 'fixed',
                    {None: body, 'Drum': [drum], 'Ropes': [rope], 'HookBlock': hb,
                     'PendantCable': [pc], 'Pendant': pend},
                    levels=3,
                    lod={'TR_Frame': (0.5, 0.2), 'TR_HoistFrame': (0.5, 0.25), 'TR_Wheels': (0.3, None),
                         'TR_Gear': (0.3, 0.1), 'TR_Motors': (0.3, 0.08), 'TR_Cabinet': (0.3, 0.1),
                         'TR_Small': (None, None), 'TR_Drum': (0.5, None),
                         'HB_Plates': (0.4, 0.2), 'HB_Sheave': (0.4, None), 'HB_Hook': (0.4, 0.2),
                         'HB_Cross': (0.4, 0.2), 'TR_PendantCable': (1.0, None),
                         'PD_Body': (0.4, None), 'PD_Buttons': (None, None)},
                    pivots={'Drum': (0, 0, DRUM_Z), 'Ropes': (0, 0, ROPE_Z), 'HookBlock': (0, 0, HOOK_Z),
                            'PendantCable': PCAB, 'Pendant': PEND},
                    parents={'HookSocket': 'HookBlock'},
                    sockets={'HookSocket': (-0.055, 0, HOOK_Z - 0.36)})


def build_hook_block():
    Z = HOOK_Z

    def plates(p):
        for s in (1, -1):
            p.extrude(rrect(0.26, 0.23, 0.05, 4, 0, Z - 0.005), 'Y', s * 0.06, s * 0.075, 'HookPaint')
    pl = mk('HB_Plates', plates, bevel=0.004, seg=2)

    def sheave(p):
        p.lathe([(-0.03, 0.05), (-0.03, 0.09), (-0.02, 0.09), (-0.008, 0.078), (0.008, 0.078), (0.02, 0.09),
                 (0.03, 0.09), (0.03, 0.05)], 24, 'DarkSteel', center=(0, 0, Z), axis='Y')
        for s in (1, -1):
            p.cylinder((0, s * 0.075, Z), (0, s * 0.086, Z), 0.022, 'DarkSteel', n=12)
    sh = mk('HB_Sheave', sheave, bevel=0.0)
    cross = mk('HB_Cross', lambda p: bx(p, -0.11, 0.11, -0.075, 0.075, Z - 0.165, Z - 0.115, 'Hazard'),
               bevel=0.008, seg=2)

    def hook(p):
        p.cylinder((0, 0, Z - 0.165), (0, 0, Z - 0.195), 0.036, 'DarkSteel', n=16)           # thrust housing
        c, R = V((-0.055, 0, Z - 0.33)), 0.055
        path = [V((0, 0, Z - 0.19)), V((0, 0, Z - 0.26)), V((0, 0, Z - 0.30))]
        n = 14
        for i in range(n + 1):
            a = radians(-220.0 * i / n)
            path.append(c + V((R * cos(a), 0, R * sin(a))))
        m = len(path)
        scales = []
        for i in range(m):
            if i < 3:
                scales.append((0.9, 0.9))
            else:
                u = (i - 3) / (m - 4)
                k = 1.0 + 0.25 * u / 0.4 if u < 0.4 else 1.25 - 0.7 * ((u - 0.4) / 0.6) ** 1.4
                scales.append((k * 0.95, k))
        section = [(0.018 * cos(t), 0.022 * sin(t))
                   for t in [2 * pi * j / 12 for j in range(12)]]
        p.sweep(path, section, 'HookSteel', side=(0, 1, 0), scales=scales)
        a0, a1 = V((-0.004, 0, Z - 0.285)), V((-0.092, 0, Z - 0.298))
        d = a1 - a0
        p.box((a0 + a1) / 2, (d.length, 0.012, 0.004), 'DarkSteel', rot=Euler((0, atan2(-d.z, d.x), 0)))
    hk = mk('HB_Hook', hook, bevel=0.0)
    return [pl, sh, cross, hk]


def build_pendant():
    x, y, z = PEND

    def body(p):
        p.cylinder((x, y, z), (x, y, z - 0.05), 0.012, 'Plastic', n=10)
        p.extrude(rrect(0.085, 0.07, 0.012, 3, x, y), 'Z', z - 0.34, z - 0.07, 'Yellow')
        bx(p, x - 0.047, x + 0.047, y - 0.039, y + 0.039, z - 0.07, z - 0.05, 'Plastic')
        bx(p, x - 0.047, x + 0.047, y - 0.039, y + 0.039, z - 0.36, z - 0.34, 'Plastic')
    bd = mk('PD_Body', body, bevel=0.004)

    def buttons(p):
        yf, d = y + CAB * 0.035, CAB
        p.cylinder((x, yf - d * 0.001, z - 0.098), (x, yf + d * 0.012, z - 0.098), 0.013, 'Plastic', n=12)
        p.cylinder((x, yf + d * 0.012, z - 0.098), (x, yf + d * 0.024, z - 0.098), 0.019, 'EStop', n=16)
        for zz in (-0.15, -0.20, -0.25, -0.30):
            for dx in (-0.018, 0.018):
                p.cylinder((x + dx, yf - d * 0.001, z + zz), (x + dx, yf + d * 0.008, z + zz), 0.011,
                           'Plastic', n=12)
    bt = mk('PD_Buttons', buttons, bevel=0.0)
    return [bd, bt]


# =============================================================================
# Kit assembly
# =============================================================================

# The kit shares one atlas and the 2 m girder webs would dominate it: big flat
# painted areas get little density, the narrow crane rail a lot (its flanks are
# only 20 mm tall and must stay wider than a few texels).
TEXEL_WEIGHTS = {
    'RW_Beam': 0.6, 'RW_Rail': 2.6, 'RW_Clips': 0.9, 'RW_Stiff': 0.5,
    'RWE_Rail': 2.6, 'RWE_Beam': 0.6,
    'GS_Body': 0.5, 'GL_Body': 0.7, 'GE_Body': 0.6, 'GS_Festoon': 0.6, 'GL_Festoon': 0.6,
    'ET_Box': 1.1, 'ET_Drive': 1.3, 'TR_Frame': 1.2, 'TR_Motors': 1.3, 'TR_Cabinet': 1.4,
    'HB_Plates': 1.6, 'HB_Hook': 1.5, 'HB_Cross': 1.5, 'PD_Body': 1.8, 'PD_Buttons': 1.8,
    'TR_Ropes': 0.4, 'TR_PendantCable': 0.3,
}


def build_kit():
    return [build_runway_segment(), build_runway_end(),
            build_girder('Girder_Segment', 'Yellow'), build_girder('Girder_Logo', 'GirderLogo'),
            build_girder_end(), build_end_truck(), build_trolley()]


# =============================================================================
# Preview
# =============================================================================

def preview_columns(coll, xs, ys, h):
    """Render-only stand-ins for the building kit columns + runway brackets."""
    p = Part('PreviewColumns')
    prof = i_profile(0.30, 0.30, 0.019, 0.011, 0.027, 0.15)
    for x in xs:
        sx = 1 if x > 0 else -1
        for y in ys:
            p.extrude([(x + v, y + u) for u, v in prof], 'Z', 0.0, h + BEAM_BOT, 'Runway')
            bx(p, x - sx * 0.15, x - sx * 0.62, y - 0.15, y + 0.15, h + BEAM_BOT - 0.02, h + BEAM_BOT, 'Runway')
            bx(p, x - sx * 0.15, x - sx * 0.60, y - 0.006, y + 0.006, h + BEAM_BOT - 0.40, h + BEAM_BOT - 0.02,
               'Runway')
            bx(p, x - 0.25, x + 0.25, y - 0.25, y + 0.25, 0.0, 0.025, 'Runway')
    ob = p.build(MATS, coll=coll)
    finish(ob, 0.004, 1, 40)
    return ob


def assemble(coll, pcs, S=12.0, RL=24.0, H=6.0, yb=-3.0, xt=2.0, drop=3.2, level=0):
    """Preview mirror of UnityOverheadCrane.  Returns nothing; adds copies."""
    T, Rz = Matrix.Translation, lambda a: Matrix.Rotation(a, 4, 'Z')
    cap = KM.extent(pcs['Runway_EndStop'], 'y')
    gcap = KM.extent(pcs['Girder_End'], 'y')
    for sx in (1, -1):
        KM.strip(coll, RL, pcs['Runway_Segment'], cap=pcs['Runway_EndStop'], axis='y', level=level,
                 base=T((sx * S / 2, -RL / 2, H)), seg_len=PERIOD, cap_len=cap)
    bridge = T((0, yb, H))
    for sx in (1, -1):
        KM.place(pcs['EndTruck'], bridge @ T((sx * S / 2, 0, 0)) @ Rz(0 if sx > 0 else pi), coll, level)
    GL = S + 2 * G_OVERHANG
    KM.strip(coll, GL, pcs['Girder_Segment'], cap=pcs['Girder_End'], center=pcs['Girder_Logo'], axis='y',
             level=level, base=bridge @ T((-GL / 2, 0, GIRDER_TOP)) @ Rz(-pi / 2), seg_len=PERIOD, cap_len=gcap)
    top = H + GIRDER_TOP - G_H
    cable = top + PCAB[2] - 0.36 - 1.1
    pose = {'Ropes': Matrix.Diagonal((1, 1, drop, 1)), 'HookBlock': T((0, 0, -(drop - 1.0))),
            'PendantCable': Matrix.Diagonal((1, 1, cable, 1)), 'Pendant': T((0, 0, -(cable - 1.0)))}
    KM.place(pcs['Trolley'], bridge @ T((xt, 0, GIRDER_TOP - G_H)), coll, level, node_pose=pose)


def preview_renders(scene, pieces, quick=False):
    pcs = {pc.name: pc for pc in pieces}
    KM.hide_kit(pieces)
    props = KM.studio(scene, k=4.0, floor=120)
    spp = 32 if quick else 160
    res = (800, 500) if quick else (1600, 1000)
    S, RL, H = 12.0, 24.0, 6.0
    out = lambda n: os.path.join(PREVIEW_DIR, f'crane_{n}.png')        # noqa: E731

    crane = K.collection('PreviewCrane')
    assemble(crane, pcs, S, RL, H)
    preview_columns(crane, (S / 2 + 0.45, -S / 2 - 0.45), (-11.7, -6.0, 0.0, 6.0, 11.7), H)
    KM.scale_figure(crane, (1.4, -4.6, 0), 0.5)
    hero = ((-10.0, -19.5, 2.4), (0.8, -2.5, 5.0), 24)
    KM.render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Truck', (3.1, -6.6, 5.5), (6.0, -3.0, 6.7), 30), out('truck'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Trolley', (0.1, -6.4, 5.2), (2.0, -3.0, 6.0), 32), out('trolley'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Hook', (1.0, -4.9, 2.3), (2.0, -3.0, 2.5), 45), out('hook'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Stop', (4.4, -9.4, 7.1), (6.0, -11.7, 6.1), 32), out('stop'), res, spp)

    for lvl in (1, 2):
        c = K.collection(f'PreviewCrane_LOD{lvl}')
        crane.hide_render = True
        assemble(c, pcs, S, RL, H, level=lvl)
        KM.render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (800, 500), spp)
        c.hide_render = True
    crane.hide_render = False
    return props


# =============================================================================
# Main
# =============================================================================

def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=4096)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()

    scene = K.reset_scene()
    K.setup_cycles_gpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    build_materials()
    pieces = build_kit()
    KM.bake_kit(pieces, OUT_DIR, PREFIX, args.bake, args.save, weights=TEXEL_WEIGHTS)
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
        preview_renders(scene, pieces, args.quick)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[crane] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
