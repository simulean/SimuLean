"""
SimuLean kit: level-luffing portal crane ("grua cigüeña") for a shipyard model -
a double-link crane: the boom carries a jib at its head, and a tie from the
top of the A-frame to the jib's tail keeps the jib tip (and the hook) at an
almost constant height while the boom luffs in and out.

    portal     four legs on rail bogies (gauge 10.5 m, rails along Y), portal
               girders, slewing pedestal; travels along the rails
    slewing    turntable, deck with handrails, machinery house, counterweight,
               A-frame, blue cab, boom foot pedestal
    boom       24.0 m, hinged at F
    jib        5.0 m tail + 14.2 m nose, hinged at the boom head B
    tie        24.7 m, from the A-frame top A to the jib tail R
    hook       two rope falls from the jib tip and a hook block

Linkage (slewing frame, x = towards the jib, z = up, metres; found with a
numerical search for the flattest tip path over the luffing range):
    F = (2.0, 16.0)   A = (-6.0, 19.1)   boom 24.0   tail 5.0   tie 24.7   nose 14.2
    boom angle 38 .. 80 deg -> reach 35.1 .. 16.8 m, tip height 31.2 .. 32.1 m
    rest pose (as exported): boom at 56 deg, reach 29.0 m
Solve it with `linkage(theta)` (same maths for the Unity view).

Built live through the blender-ship MCP:

    import runpy; ns = runpy.run_path('<repo>/Tools/Blender/assets/level_luffing_crane.py')
    ns['build_scene']()                  # build + pose (boom angle, slew, hook drop)
    ns['pose'](theta=45, slew=30, drop=18)
    ns['export']()

Frame (Blender, before export): rails along Y, jib at rest along +X, up +Z;
pivot at the rail-top centre between the four bogies.
"""

import os
import sys
from math import pi, sin, cos, atan2, sqrt, hypot, radians, degrees

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
if KIT not in sys.path:
    sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
from mathutils import Vector                 # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
from simulean_kit import V, Part, circle     # noqa: E402

PREFIX = 'LevelLuffingCrane'
REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'LevelLuffingCrane')
BUILD_DIR = os.path.join(KIT, 'build')

# ---------------------------------------------------------------- colours (linear RGB)
WHITE = (0.70, 0.71, 0.69)
GREY = (0.20, 0.21, 0.215)
CAB_BLUE = (0.018, 0.085, 0.30)
YELLOW = (0.80, 0.42, 0.015)

# ---------------------------------------------------------------- layout (m)
GAUGE = 10.5             # rail centres, across X
BASE = 10.0              # leg spacing along the rails (Y)
PORTAL_Z = 11.0          # underside of the portal girders
SLEW_Z = 13.4            # top of the slewing pedestal = slew pivot height
DECK_Z = 14.2            # deck top
# linkage, slewing frame (x, z)
F = (2.0, 16.0)
A = (-6.0, 19.1)
L_BOOM, L_TAIL, L_TIE, L_NOSE = 24.0, 5.0, 24.7, 14.2
THETA_REST = 56.0
SHEAVE_R = 0.5           # rope leaves the tip sheave this far below the tip pin
UNIT_DROP = 1.0          # exported rope length (Ropes localScale.y = drop / 1 m)


def linkage(theta_deg):
    """Boom angle (deg from horizontal) -> boom head B, jib tail R, jib tip T
    as (x, z) in the slewing frame."""
    th = radians(theta_deg)
    B = (F[0] + L_BOOM * cos(th), F[1] + L_BOOM * sin(th))
    dx, dz = A[0] - B[0], A[1] - B[1]
    d = hypot(dx, dz)
    a = (L_TAIL ** 2 - L_TIE ** 2 + d * d) / (2 * d)
    h = sqrt(max(0.0, L_TAIL ** 2 - a * a))
    mx, mz = B[0] + a * dx / d, B[1] + a * dz / d
    R = (mx + h * dz / d, mz - h * dx / d)
    ux, uz = (B[0] - R[0]) / L_TAIL, (B[1] - R[1]) / L_TAIL
    T = (B[0] + L_NOSE * ux, B[1] + L_NOSE * uz)
    return B, R, T


def P3(xz, y=0.0):
    return V((xz[0], y, xz[1]))


# =============================================================================
# helpers
# =============================================================================

def beam(p, a, b, w, h, mat, w1=None, h1=None, side=(0, 1, 0)):
    """Box member from a to b, w across `side`, h in the other direction;
    optionally tapering to w1 x h1 at b."""
    sec = [(-0.5, -0.5), (0.5, -0.5), (0.5, 0.5), (-0.5, 0.5)]
    w1 = w if w1 is None else w1
    h1 = h if h1 is None else h1
    p.sweep([V(a), V(b)], sec, mat, side=side, scales=[(w, h), (w1, h1)])


def tube(p, a, b, r, mat, n=8):
    p.cylinder(V(a), V(b), r, mat, n=n)


def rail_run(p, pts, mat, r=0.024, post=1.1):
    """Handrail along a polyline at deck level: posts + top and knee rails."""
    pts = [V(q) for q in pts]
    for q in pts:
        tube(p, q, q + V((0, 0, post)), r, mat)
    for a, b in zip(pts, pts[1:]):
        tube(p, a + V((0, 0, post)), b + V((0, 0, post)), r, mat)
        tube(p, a + V((0, 0, post / 2)), b + V((0, 0, post / 2)), r * 0.8, mat)


def pin(p, c, y, r, mat):
    tube(p, (c[0], -y, c[1]), (c[0], y, c[1]), r, mat, n=12)


# =============================================================================
# portal (static under the root; the root travels)
# =============================================================================

def build_portal(mats, coll):
    steel, bog, det = Part('PT_Steel'), Part('PT_Bogies'), Part('PT_Detail')
    feet, tops = [], []
    for sx in (1, -1):
        for sy in (1, -1):
            foot = V((sx * GAUGE / 2, sy * BASE / 2, 1.85))
            top = V((sx * 3.0, sy * 3.0, PORTAL_Z))
            feet.append(foot); tops.append(top)
            beam(steel, foot, top, 1.0, 1.0, 'white', 1.5, 1.5, side=(1, 0, 0))
            # bogie: frame, equaliser, 4 wheels, buffers
            cx, cy = sx * GAUGE / 2, sy * BASE / 2
            bog.box_minmax((cx - 0.38, cy - 2.1, 0.55), (cx + 0.38, cy + 2.1, 1.30), 'grey')
            bog.box_minmax((cx - 0.30, cy - 1.0, 1.30), (cx + 0.30, cy + 1.0, 1.85), 'grey')
            for wy in (-1.6, -0.55, 0.55, 1.6):
                tube(bog, (cx - 0.12, cy + wy, 0.40), (cx + 0.12, cy + wy, 0.40), 0.40, 'dark', n=16)
            yb = cy + sy * 2.1
            bog.box_minmax((cx - 0.30, min(yb, yb + sy * 0.12), 0.65),
                           (cx + 0.30, max(yb, yb + sy * 0.12), 1.20), 'hazard')
            tube(bog, (cx, yb + sy * 0.12, 0.92), (cx, yb + sy * 0.55, 0.92), 0.17, 'dark', n=12)
    # portal girders (top square) and the slewing pedestal
    z0, z1 = PORTAL_Z, PORTAL_Z + 1.4
    for s in (1, -1):
        steel.box_minmax((-3.8, s * 3.0 - 0.6, z0), (3.8, s * 3.0 + 0.6, z1), 'white')
        steel.box_minmax((s * 3.0 - 0.6, -2.4, z0), (s * 3.0 + 0.6, 2.4, z1), 'white')
    steel.box_minmax((-2.4, -0.5, z0 + 0.3), (2.4, 0.5, z1), 'white')          # cross beams
    steel.box_minmax((-0.5, -2.4, z0 + 0.3), (0.5, 2.4, z1), 'white')
    steel.lathe([(z1, 0.0), (z1, 2.7), (SLEW_Z - 0.2, 2.7), (SLEW_Z - 0.2, 2.95), (SLEW_Z, 2.95),
                 (SLEW_Z, 0.0)], 32, 'grey', axis='Z', cap0=False, cap1=False)
    # side bracing along the rails (each side), at mid height
    zb = 6.2
    for sx in (1, -1):
        k = (zb - 1.85) / (PORTAL_Z - 1.85)
        x = sx * (GAUGE / 2 + (3.0 - GAUGE / 2) * k)
        y = BASE / 2 + (3.0 - BASE / 2) * k
        beam(steel, (x, -y, zb), (x, y, zb), 0.7, 0.9, 'white', side=(1, 0, 0))
    # access ladder up the (+x, -y) leg, outside face
    f, t = feet[1], tops[1]
    for k in range(int((PORTAL_Z - 2.2) / 0.3)):
        z = 2.2 + k * 0.3
        s = (z - f.z) / (t.z - f.z)
        q = f.lerp(t, s) + V((0.78, 0, 0))
        tube(det, q + V((0, -0.22, 0)), q + V((0, 0.22, 0)), 0.016, 'yellow', n=6)
    for dy in (-0.24, 0.24):
        tube(det, f + V((0.78, dy, 0.3)), t + V((0.78, dy, 0)), 0.022, 'yellow', n=6)
    # walkway handrail round the portal top (the ladder lands there)
    e = 3.75
    rail_run(det, [(e, -e, z1), (e, e, z1), (-e, e, z1), (-e, -e, z1), (e - 1.0, -e, z1)], 'yellow')
    # power-cable reel hanging under the +x side bracing
    k = (zb - 1.85) / (PORTAL_Z - 1.85)
    xr = GAUGE / 2 + (3.0 - GAUGE / 2) * k + 0.75
    bog.lathe([(-0.35, 0.0), (-0.35, 1.3), (-0.28, 1.3), (-0.28, 0.8), (0.28, 0.8), (0.28, 1.3),
               (0.35, 1.3), (0.35, 0.0)], 24, 'dark', center=(xr, 0, 4.4), axis='X', cap0=False,
              cap1=False)
    for dy in (-0.45, 0.45):
        bog.box_minmax((xr - 0.75, dy - 0.08, 4.3), (xr - 0.35, dy + 0.08, zb - 0.4), 'grey')
    tube(bog, (xr - 0.6, -0.45, 4.4), (xr + 0.4, 0.45, 4.4), 0.08, 'grey', n=8)
    tube(bog, (xr, 0.0, 3.1), (xr, 0.0, 0.0), 0.035, 'rope', n=6)
    obs = []
    for part, bevel in ((steel, 0.02), (bog, 0.01), (det, 0.0)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    return obs


# =============================================================================
# slewing superstructure (node Slew)
# =============================================================================

def build_slewing(mats, coll):
    s, house, det, cab = Part('SL_Steel'), Part('SL_House'), Part('SL_Detail'), Part('SL_Cab')
    z = DECK_Z
    s.lathe([(SLEW_Z, 0.0), (SLEW_Z, 2.9), (SLEW_Z + 0.4, 2.9), (SLEW_Z + 0.4, 0.0)], 32, 'grey',
            axis='Z', cap0=False, cap1=True)
    s.box_minmax((-11.6, -3.5, SLEW_Z + 0.4), (3.3, 3.5, z), 'grey')                 # deck
    # machinery house, roof, door, louvres
    house.box_minmax((-10.4, -2.8, z), (-1.4, 2.8, 17.6), 'white')
    house.box_minmax((-10.6, -3.0, 17.6), (-1.2, 3.0, 17.8), 'grey')
    house.box_minmax((-3.4, -2.83, z), (-2.4, -2.79, z + 2.1), 'grey')
    for k in range(4):
        house.box_minmax((-9.6 + k * 1.6, 2.79, 16.2), (-8.6 + k * 1.6, 2.83, 17.0), 'grey')
    s.box_minmax((-11.6, -3.0, z), (-10.4, 3.0, 17.3), 'counter')                      # counterweight
    # A-frame: a struts pair on each side, apex cross beam at A
    ax, az = A
    for sy in (1, -1):
        y = sy * 3.15
        beam(s, (-2.6, y, z), (ax, y, az), 0.45, 0.45, 'white', side=(0, 1, 0))
        beam(s, (-9.4, y, z), (ax, y, az), 0.45, 0.45, 'white', side=(0, 1, 0))
        s.box_minmax((ax - 0.5, y - 0.3, az - 0.5), (ax + 0.5, y + 0.3, az + 0.5), 'white')
    s.box_minmax((ax - 0.35, -3.15, az + 0.25), (ax + 0.35, 3.15, az + 0.75), 'white')
    pin(s, A, 3.3, 0.16, 'dark')
    det.lathe([(az + 0.75, 0.0), (az + 0.75, 0.10), (az + 0.85, 0.10), (az + 0.85, 0.12),
               (az + 1.05, 0.10), (az + 1.12, 0.0)], 12, 'lamp', center=(ax, 0, 0), axis='Z',
              cap0=False, cap1=False)
    # boom foot pedestal (two cheeks) with the hinge pin at F
    fx, fz = F
    for sy in (1, -1):
        s.box_minmax((fx - 0.9, sy * 1.0 - 0.2, z), (fx + 0.6, sy * 1.0 + 0.2, fz + 0.5), 'white')
    pin(s, F, 1.35, 0.18, 'dark')
    # operator's cab on the front left, blue, glazed front and side
    x0, x1, y0, y1, c0, c1 = -1.0, 1.4, 1.75, 3.45, z, z + 2.5
    cab.box_minmax((x0, y0, c0), (x1, y1, c1), 'blue')
    cab.box_minmax((x0 - 0.1, y0 - 0.1, c1), (x1 + 0.15, y1 + 0.1, c1 + 0.15), 'grey')
    cab.box_minmax((x1, y0 + 0.15, c0 + 1.0), (x1 + 0.02, y1 - 0.15, c1 - 0.2), 'glass')
    cab.box_minmax((x0 + 0.2, y1, c0 + 1.0), (x1 - 0.2, y1 + 0.02, c1 - 0.2), 'glass')
    # deck handrails (sides and rear)
    rail_run(det, [(3.2, 3.45, z), (-1.1, 3.45, z)], 'yellow')
    rail_run(det, [(3.2, -3.45, z), (-1.2, -3.45, z), (-11.5, -3.45, z), (-11.5, 3.45, z),
                   (-10.5, 3.45, z)], 'yellow')
    obs = []
    for part, bevel in ((s, 0.01), (house, 0.01), (det, 0.0), (cab, 0.01)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    return obs


# =============================================================================
# boom, jib, tie, ropes, hook (rest pose)
# =============================================================================

def build_boom(mats, coll):
    B, R, T = linkage(THETA_REST)
    p = Part('BM_Boom')
    beam(p, P3(F), P3(B), 1.8, 1.2, 'white', 1.1, 0.9)
    d = (P3(B) - P3(F)).normalized()
    for sy in (1, -1):                                          # head fork cheeks
        c = P3(B) - d * 0.6
        p.box_minmax((c.x - 0.7, sy * 0.62 - 0.06, c.z - 0.55), (c.x + 0.9, sy * 0.62 + 0.06, c.z + 0.55),
                     'white')
    pin(p, B, 0.75, 0.15, 'dark')
    det = Part('BM_Detail')
    f3, b3 = P3(F), P3(B)
    L = (b3 - f3).length
    up = V((-d.z, 0.0, d.x))                                   # boom top normal
    for k in range(1, 8):                                       # diaphragm bands
        u = k / 8.0
        w, h = 1.8 + (1.1 - 1.8) * u, 1.2 + (0.9 - 1.2) * u
        c = f3.lerp(b3, u)
        beam(det, c - d * 0.08, c + d * 0.08, w + 0.06, h + 0.06, 'white')
    s0, s1 = 1.5, L - 2.0                                       # walkway on the +y side
    for a, b2 in ((s0, s1),):
        a3, b3w = f3 + d * a, f3 + d * b2
        u0 = a / L
        y0 = (1.8 + (1.1 - 1.8) * u0) / 2
        det.sweep([a3 + V((0, y0 + 0.35, 0)), b3w + V((0, 0.55 + 0.35, 0))],
                  [(-0.35, -0.03), (0.35, -0.03), (0.35, 0.03), (-0.35, 0.03)], 'grating',
                  side=(0, 1, 0))
        n = int((b2 - a) / 1.8)
        for i in range(n + 1):
            q = a3.lerp(b3w, i / n)
            yy = y0 + (0.55 - y0) * (i / n) + 0.68
            tube(det, q + V((0, yy, 0)), q + V((0, yy, 0)) + up * 1.0, 0.022, 'yellow', n=6)
        for hh in (0.5, 1.0):
            tube(det, a3 + V((0, y0 + 0.68, 0)) + up * hh, b3w + V((0, 0.55 + 0.68, 0)) + up * hh,
                 0.02, 'yellow', n=6)
    obs = []
    for part, bevel in ((p, 0.01), (det, 0.0)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    return obs


def build_jib(mats, coll):
    B, R, T = linkage(THETA_REST)
    p = Part('JB_Jib')
    beam(p, P3(R), P3(B), 0.8, 0.9, 'white', 1.0, 1.3)
    beam(p, P3(B), P3(T), 1.0, 1.3, 'jib', 0.6, 0.7)
    for sy in (1, -1):                                          # tip sheave cheeks
        p.box_minmax((T[0] - 0.75, sy * 0.36 - 0.04, T[1] - 0.8), (T[0] + 0.75, sy * 0.36 + 0.04, T[1] + 0.6),
                     'white')
    p.lathe([(-0.30, 0.0), (-0.30, SHEAVE_R), (0.30, SHEAVE_R), (0.30, 0.0)], 20, 'dark',
            center=(T[0], 0, T[1]), axis='Y', cap0=False, cap1=False)
    pin(p, R, 0.5, 0.13, 'dark')
    det = Part('JB_Detail')
    r3, b3, t3 = P3(R), P3(B), P3(T)
    dj = (t3 - r3).normalized()
    up = V((-dj.z, 0.0, dj.x))
    for u in (0.25, 0.5, 0.75):                                 # nose diaphragm bands
        c = b3.lerp(t3, u)
        w, h = 1.0 + (0.6 - 1.0) * u, 1.3 + (0.7 - 1.3) * u
        beam(det, c - dj * 0.07, c + dj * 0.07, w + 0.05, h + 0.05, 'white')
    # tail guide sheave and the two hoist-rope falls running to the tip sheave
    det.lathe([(-0.2, 0.0), (-0.2, 0.42), (0.2, 0.42), (0.2, 0.0)], 16, 'dark',
              center=tuple(r3 + up * 0.75), axis='Y', cap0=False, cap1=False)
    for y in (-0.15, 0.15):
        a = r3 + up * 1.17 + V((0, y, 0))
        b = t3 + up * (SHEAVE_R + 0.02) + V((0, y, 0))
        tube(det, a, b, 0.022, 'rope', n=6)
    obs = []
    for part, bevel in ((p, 0.008), (det, 0.0)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    return obs


def build_tie(mats, coll):
    B, R, T = linkage(THETA_REST)
    p = Part('TI_Tie')
    beam(p, P3(A), P3(R), 0.4, 0.55, 'white')
    ob = p.build(mats, coll)
    K.finish(ob, bevel=0.008, segments=1, sharp=40.0)
    return [ob]


def tip_point():
    B, R, T = linkage(THETA_REST)
    return V((T[0], 0.0, T[1] - SHEAVE_R))


def build_ropes(mats, coll):
    t = tip_point()
    p = Part('RP_Ropes')
    for y in (-0.15, 0.15):
        tube(p, t + V((0, y, 0)), t + V((0, y, -UNIT_DROP)), 0.022, 'rope', n=6)
    ob = p.build(mats, coll)
    K.finish(ob, bevel=0, sharp=40.0)
    return [ob]


def build_hook(mats, coll):
    top = tip_point() - V((0, 0, UNIT_DROP))
    p = Part('HK_Block')
    x, z = top.x, top.z
    for sy in (1, -1):                                          # block side plates
        p.box_minmax((x - 0.45, sy * 0.24 - 0.04, z - 1.0), (x + 0.45, sy * 0.24 + 0.04, z - 0.05), 'hazard')
    p.lathe([(-0.20, 0.0), (-0.20, 0.36), (0.20, 0.36), (0.20, 0.0)], 16, 'dark',
            center=(x, 0, z - 0.42), axis='Y', cap0=False, cap1=False)
    p.box_minmax((x - 0.25, -0.2, z - 1.15), (x + 0.25, 0.2, z - 1.0), 'dark')     # crosshead
    hook = Part('HK_Hook')
    tube(hook, (x, 0, z - 1.15), (x, 0, z - 1.55), 0.07, 'hook', n=12)
    c, rr = V((x - 0.22, 0, z - 1.85)), 0.22
    path = [V((x, 0, z - 1.5)), V((x, 0, z - 1.65))]
    n = 14
    for i in range(n + 1):
        a = radians(-220.0 * i / n)
        path.append(c + V((rr * cos(a), 0, rr * sin(a))))
    m = len(path)
    scales = []
    for i in range(m):
        u = i / (m - 1)
        k = 1.0 + 0.35 * min(u / 0.45, 1.0) - (0.75 * ((u - 0.45) / 0.55) ** 1.3 if u > 0.45 else 0.0)
        scales.append((k * 0.95, k))
    sec = [(0.065 * cos(t), 0.08 * sin(t)) for t in [2 * pi * j / 12 for j in range(12)]]
    hook.sweep(path, sec, 'hook', side=(0, 1, 0), scales=scales)
    obs = []
    for part, bevel in ((p, 0.008), (hook, 0.0)):
        ob = part.build(mats, coll)
        K.finish(ob, bevel=bevel, segments=1, sharp=40.0)
        obs.append(ob)
    socket = V((c.x, 0, c.z - rr + 0.05))                      # in the hook's mouth
    return obs, socket


# =============================================================================
# materials, rig, pose
# =============================================================================

def jib_decals():
    img, aspect = K.render_text_image('SWL 40 t', os.path.join(BUILD_DIR, 'decal_swl40.png'),
                                      256, 'C:/Windows/Fonts/bahnschrift.ttf', bold_offset=0.016)
    B, R, T = linkage(THETA_REST)
    d = (P3(T) - P3(B)).normalized()
    up = V((-d.z, 0.0, d.x))
    c = P3(B).lerp(P3(T), 0.45)
    h = 0.62
    out = []
    for sy in (1, -1):
        out.append(dict(image=img, origin=tuple(c + V((0, sy * 0.45, 0))), u=tuple(d * -sy), v=tuple(up),
                        size=(h * aspect, h), normal=(0, sy, 0), color=(0.02, 0.02, 0.025)))
    return out


def make_materials():
    os.makedirs(BUILD_DIR, exist_ok=True)
    return {
        'white': M.paint('LL_White', WHITE, rough=0.38, wear=0.25, dirt=0.6),
        'jib': M.paint('LL_JibWhite', WHITE, rough=0.38, wear=0.25, dirt=0.6, decals=jib_decals()),
        'grating': M.paint('LL_Grating', GREY, rough=0.5, wear=0.9, dirt=0.8),
        'grey': M.paint('LL_Grey', GREY, rough=0.45, wear=0.5, dirt=0.8),
        'blue': M.paint('LL_CabBlue', CAB_BLUE, rough=0.35, wear=0.3, dirt=0.5),
        'yellow': M.paint('LL_Rail', YELLOW, rough=0.4, wear=0.6, dirt=0.5),
        'hazard': M.paint('LL_Hazard', YELLOW, rough=0.4, wear=0.7, dirt=0.6,
                          color_fn=M.hazard_fn(YELLOW, width=0.12)),
        'dark': M.dark_steel('LL_Dark'),
        'rope': M.dark_steel('LL_Rope', color=(0.06, 0.06, 0.06), rough=0.5, metal=0.8),
        'hook': M.forged_steel('LL_Hook'),
        'counter': M.concrete('LL_Counter', color=(0.12, 0.12, 0.115)),
        'glass': M.glass('LL_Glass'),
        'lamp': M.lens('LL_Lamp', (0.9, 0.05, 0.02), emit=3.0),
    }


def node_layout():
    """Node -> (pivot, parent) in the authored frame (rest pose)."""
    B, R, T = linkage(THETA_REST)
    tip = tip_point()
    return {
        'Slew': (V((0, 0, SLEW_Z)), None),
        'Boom': (P3(F), 'Slew'),
        'Jib': (P3(B), 'Boom'),
        'Tie': (P3(A), 'Slew'),
        'Ropes': (tip, 'Slew'),
        'HookBlock': (tip - V((0, 0, UNIT_DROP)), 'Slew'),
    }


def build_parts(mats, coll):
    hook, socket = build_hook(mats, coll)
    B, R, T = linkage(THETA_REST)
    nodes = {None: build_portal(mats, coll), 'Slew': build_slewing(mats, coll),
             'Boom': build_boom(mats, coll), 'Jib': build_jib(mats, coll), 'Tie': build_tie(mats, coll),
             'Ropes': build_ropes(mats, coll), 'HookBlock': hook}
    sockets = {'JibTip': (tip_point(), 'Jib'), 'TieEnd': (P3(R), 'Tie'), 'HookSocket': (socket, 'HookBlock')}
    return nodes, sockets


RIG_NODES = ('Slew', 'Boom', 'Jib', 'Tie', 'Ropes', 'HookBlock', 'JibTip', 'TieEnd', 'HookSocket')


def _clear():
    """Remove the preview and any previous export rig (its node names would
    otherwise make the next rig 'Slew.001' and so on in the FBX)."""
    for ob in [o for o in bpy.data.objects
               if o.name.startswith((PREFIX, 'PT_', 'SL_', 'BM_', 'JB_', 'TI_', 'RP_', 'HK_', 'LL_'))
               or o.name.split('.')[0].split('_LOD')[0] in RIG_NODES]:
        bpy.data.objects.remove(ob, do_unlink=True)


def build_scene(theta=THETA_REST, slew=0.0, drop=14.0):
    """Preview rig in Blender (empties named LL_<Node>) and pose it."""
    _clear()
    coll = K.collection(PREFIX)
    mats = make_materials()
    nodes, sockets = build_parts(mats, coll)
    root = K.empty(PREFIX, (0, 0, 0), coll, size=2.0, display='ARROWS')
    empties = {None: root}
    for name, (piv, parent) in node_layout().items():
        empties[name] = K.empty(f'LL_{name}', piv, coll, size=0.8)
    bpy.context.view_layer.update()                 # matrix_world of the new empties
    for name, (piv, parent) in node_layout().items():
        K.parent_keep_local(empties[name], empties[parent])
    bpy.context.view_layer.update()
    for name, obs in nodes.items():
        for ob in obs:
            if name == 'Ropes':
                K.bake_into(ob, empties[name])      # mesh in node space: Ropes is scaled
            else:
                K.parent_keep_local(ob, empties[name])   # object space kept for the shaders
    socks = {name: K.empty(f'LL_{name}', loc, coll, size=0.4, display='CUBE')
             for name, (loc, parent) in sockets.items()}
    bpy.context.view_layer.update()
    for name, (loc, parent) in sockets.items():
        K.parent_keep_local(socks[name], empties[parent])
    tris = sum(K.tri_count(o) for obs in nodes.values() for o in obs)
    print(f'[LevelLuffingCrane] {tris} tris')
    pose(theta, slew, drop)
    return root


def pose(theta=THETA_REST, slew=0.0, drop=14.0):
    """Pose the preview rig: boom angle (deg), slew (deg), hook drop below the
    tip sheave (m).  The Unity view does the same with the same linkage."""
    ob = bpy.data.objects
    B0, R0, T0 = linkage(THETA_REST)
    B, R, T = linkage(theta)
    a_jib0 = atan2(B0[1] - R0[1], B0[0] - R0[0])
    a_jib = atan2(B[1] - R[1], B[0] - R[0])
    a_tie0 = atan2(R0[1] - A[1], R0[0] - A[0])
    a_tie = atan2(R[1] - A[1], R[0] - A[0])
    d_boom = radians(theta - THETA_REST)
    # Blender: +rotation about Y turns +X towards -Z, so a raise is negative
    ob['LL_Slew'].rotation_euler = (0, 0, radians(slew))
    ob['LL_Boom'].rotation_euler = (0, -d_boom, 0)
    ob['LL_Jib'].rotation_euler = (0, -((a_jib - a_jib0) - d_boom), 0)
    ob['LL_Tie'].rotation_euler = (0, -(a_tie - a_tie0), 0)
    tip = V((T[0], 0, T[1] - SHEAVE_R - SLEW_Z))
    ob['LL_Ropes'].location = tip
    ob['LL_Ropes'].scale = (1, 1, drop / UNIT_DROP)
    ob['LL_HookBlock'].location = tip - V((0, 0, drop))
    bpy.context.view_layer.update()
    print(f'[pose] boom {theta:.0f} deg: reach {T[0]:.1f} m, tip {T[1]:.2f} m, hook {T[1] - SHEAVE_R - drop:.1f} m')


# =============================================================================
# Export
# =============================================================================

TEXEL_WEIGHTS = {'PT_Steel': 0.45, 'PT_Bogies': 1.0, 'PT_Detail': 0.8, 'SL_Steel': 0.6, 'SL_House': 0.6,
                 'SL_Detail': 0.8, 'SL_Cab': 1.6, 'BM_Boom': 0.45, 'BM_Detail': 0.7, 'JB_Jib': 0.7,
                 'JB_Detail': 0.8, 'TI_Tie': 0.4, 'RP_Ropes': 0.5, 'HK_Block': 2.0, 'HK_Hook': 2.0}
LOD = {'PT_Steel': (0.7, 0.4), 'PT_Bogies': (0.5, 0.25), 'PT_Detail': (1.0, None),
       'SL_Steel': (0.7, 0.4), 'SL_House': (1.0, 1.0), 'SL_Detail': (1.0, None), 'SL_Cab': (1.0, 0.6),
       'BM_Boom': (0.8, 0.5), 'BM_Detail': (1.0, None), 'JB_Jib': (0.8, 0.5), 'JB_Detail': (1.0, None),
       'TI_Tie': (1.0, 1.0), 'RP_Ropes': (1.0, 1.0), 'HK_Block': (0.6, 0.3), 'HK_Hook': (0.5, 0.25)}


def export(bake=4096, save=2048):
    """Rebuild at the rest pose, bake one atlas, LODs, rig, write
    Art/Models/LevelLuffingCrane/LevelLuffingCrane.fbx."""
    import simulean_modular as KM
    _clear()
    coll = K.collection(PREFIX)
    mats = make_materials()
    nodes, sockets = build_parts(mats, coll)
    layout = node_layout()
    pc = KM.Piece(PREFIX, 'fixed', nodes, levels=3, lod=LOD,
                  pivots={n: tuple(piv) for n, (piv, par) in layout.items()},
                  parents={**{n: par for n, (piv, par) in layout.items() if par},
                           **{n: par for n, (loc, par) in sockets.items()}},
                  sockets={n: tuple(loc) for n, (loc, par) in sockets.items()})
    K.setup_cycles_gpu(bpy.context.scene, backends=('CUDA', 'HIP', 'ONEAPI', 'METAL'))  # OptiX bakes black
    KM.bake_kit([pc], OUT_DIR, PREFIX, bake, save, weights=TEXEL_WEIGHTS, ao_distance=1.0)
    KM.build_lods(pc)
    KM.report([pc])
    KM.rotate_to_export([pc])
    KM.rig(pc)
    KM.export_kit([pc], OUT_DIR)
    return pc


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
