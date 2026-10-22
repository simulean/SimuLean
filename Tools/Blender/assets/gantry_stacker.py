"""
SimuLean asset: GANTRY STACKER - a portal (gantry) pick-and-place robot that forms
stacks of flat items (boards, panels, sheets, cartons) at several positions under
its runway.  Four corner posts carry two runway girders; a bridge spanning the
posts travels along the runway, a trolley travels along the bridge and carries a
telescopic mast with a vacuum (suction-cup) head that moves up and down.  Low detail,
the visual level of the RailShuttle.  Library palette: steel-blue portal, safety-yellow
bridge, light-grey trolley.  Driven in Unity by GantryStackerView (docs/architecture.md
section 4.10): pick at a pick point, travel, place on the stack top.

Fitting (no non-uniform scaling of the chunky parts).  Every piece that has to follow the
span S (rail centre distance, along X) or the runway length L sits in its own node, so a
scene moves the posts and saddles and stretches only the girders:
    Leg_SW/SE/NW/NE   local position (-+S/2, 0, -+L/2)              unscaled
    Runway_W/E        local position (-+S/2, RUNWAY_Z, 0), scale z = L / L0
    EndBeam_S/N       local position (0, END_Z, -+L/2),    scale x = S / S0
    Bridge            local position (0, RAIL_TOP, b): travels along local Z (Unity), the runway
      Girder          scale x = S / S0 (the two bridge girders)
      Saddle_W/E      local position (-+S/2, 0, 0) (end trucks on the runway rails)
      Trolley         local position (t, TROLLEY_Z - RAIL_TOP, 0): travels along local X, the bridge
        Head          local position (0, CUP_Z - TROLLEY_Z, 0): moves along local up (Unity +Y)
(Unity numbers; S0 = 4.00, L0 = 12.00 are the authored values and can be read back from the
Runway_W/E and Leg_* local positions.)  Reach of the head (trolley / bridge centre) from a rail
centre: 0.55 m along the bridge, from a post centre: 1.05 m along the runway (end stop +
buffers + saddle half length).

Run (headless):
    blender -b --factory-startup -P Tools/Blender/assets/gantry_stacker.py --
            [--variant all|main] [--bake 2048] [--save 2048] [--no-render] [--quick] [--cpu] [--render-cpu]

    --variant all   (default) writes Art/Models/GantryStacker/ (FBX + texture set).
    --variant main  iteration only: baked into build/GantryStacker_main/, previews,
                    nothing written to Unity.
    --cpu           bake/render on the CPU instead of OptiX/CUDA.
    --render-cpu    bake on the GPU, render the previews on the CPU.

Outputs (--variant all)
    Assets/SimuLean.Unity/Art/Models/GantryStacker/GantryStacker.fbx
    Assets/SimuLean.Unity/Art/Models/GantryStacker/GantryStacker_*.png   one atlas
    Tools/Blender/build/GantryStacker.blend
    Tools/Blender/previews/gantry_stacker_*.png  (hero, side, end, head, top, lod1, lod2)

Frame (authored frame = Unity frame, as in the modular kits / rail_shuttle.py)
    authored +X = Unity +X = bridge span (W = -X, E = +X)
    authored +Y = Unity +Z = the runway (S = -Y, N = +Y)
    authored +Z = Unity +Y = up.   Metres.  Unity position of authored (x, y, z) is (x, z, y).
    Origin on the floor at the centre of the four posts.

Heights (fixed)
    posts 0.35 m square to 4.25 m; runway girders 0.30 x 0.55 m, top 4.80, rail top 4.87 (RAIL_TOP);
    bridge: two girders 0.20 x 0.44 m at y +-0.30, top 5.42 (TROLLEY_Z), end trucks 1.5 m long;
    trolley 0.75 x 0.95 m on the girders; outer mast 0.26 m square down to 2.95 m;
    head: suction frame 2.60 x 1.50 m (along x / y), cup lips at 2.30 m (CUP_Z) = above a
    1.2 m stack on a 0.55 m table.  Overall ~ 4.4 x 12.4 x 6.0 m.

Nodes (bare Transforms; meshes hang under them as <Node>_LODn at identity)
    Leg_SW (-2.00, 0, -6.00)  Leg_SE (+2.00, 0, -6.00)  Leg_NW (-2.00, 0, +6.00)  Leg_NE (+2.00, 0, +6.00)
                  Unity, pivot on the floor at the post centre; post, base plate, cap, end stop,
                  knee brace towards the runway centre, yellow/black band at the foot.
    Runway_W / Runway_E  (-+2.00, 4.25, 0): girder + rail along local Z, mesh z -L0/2..+L0/2.
    EndBeam_S / EndBeam_N  (0, 4.25, -+6.00): end tie between the post heads, mesh x -S0/2..+S0/2.
    Bridge        (0, 4.87, 0) = on the rail tops, runway centre.  Moves along local Z.  Mesh: the
                  bridge control box (centre, outside of the -Y girder).
      Girder      (0, 4.87, 0) relative = (0, 0, 0): the two bridge girders, mesh x -S0/2..+S0/2.
      Saddle_W/E  (-+2.00, 0, 0) relative: end trucks with wheels and buffers.
      Trolley     (0, 0.55, 0) relative (world 5.42 = girder top).  Moves along local X.  Carriage,
                  hoist motor, beacon, outer mast.
        Head      (0, -3.12, 0) relative (world 2.30 = cup lips).  Moves along local up: rest 0,
                  down to about -1.70 (cups on a bare 0.55 m table), up to +0.40.  Inner mast,
                  hub, suction frame, 15 cups (LOD0 only).

LODs: LOD1 decimates and drops the cups and the end stops' bolts; LOD2 keeps posts, girders,
bridge, trolley box and the head frame as low boxes.  Tri counts are printed by the run (KM.report).
"""

import os
import sys
import argparse
import math
import time

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
from mathutils import Vector, Matrix         # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
import simulean_modular as KM                # noqa: E402
from simulean_kit import V, Part, finish, cull_faces  # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
NAME = 'GantryStacker'
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', NAME)
BUILD_DIR = os.path.join(KIT, 'build')
PREVIEW_DIR = os.path.join(KIT, 'previews')

# ---------------------------------------------------------------- plan dimensions (authored frame, m)
# Authored reference size (a scene may move the posts / stretch the girders, see the docstring).
DIMS = dict(
    span=4.00,          # rail centre distance (bridge span, x)
    length=12.00,       # runway length (y), post centre to post centre
    post=0.35,          # post section
    head_x=2.60, head_y=1.50,   # suction frame
    cups=(5, 3),        # cups along x, rows along y
)
D = DIMS
S0, L0 = D['span'], D['length']
XW, YN = S0 / 2, L0 / 2

# ---------------------------------------------------------------- vertical sizes (fixed)
POST_TOP = 4.25
RUNWAY_Z = POST_TOP                  # runway girder bottom (node pivot)
RW_TOP = 4.80
RAIL_TOP = 4.87                      # Bridge pivot
GIRDER_Y, GIRDER_W = 0.30, 0.20      # bridge girders at y +-GIRDER_Y
TROLLEY_Z = 5.42                     # girder top = trolley pivot
MAST_LOW = 2.95                      # outer mast bottom
CUP_Z = 2.30                         # cup lips at rest (Head pivot)
SADDLE_HL = 0.75                     # end-truck half length (along y)

BLUE = (0.010, 0.072, 0.20)          # library palette (rail_shuttle.py): RAL 5010-ish steel blue
YELLOW = (0.80, 0.42, 0.015)         # safety yellow, as the overhead crane
LIGHT = (0.46, 0.48, 0.47)           # RAL 7035-ish light grey

MATS = {}


# =============================================================================
# Materials
# =============================================================================

def build_materials():
    K.NB.default_period = {}
    MATS.update({
        'Struct': M.paint('Struct', BLUE, rough=0.40, wear=0.45, dirt=1.2, edge=0.012),
        'Accent': M.paint('Accent', YELLOW, rough=0.36, wear=0.5, edge=0.010),
        'Light': M.paint('Light', LIGHT, rough=0.40, wear=0.45, edge=0.010),
        'Yellow': M.paint('Yellow', YELLOW, rough=0.38, wear=0.7, edge=0.006),
        'Hazard': M.paint('Hazard', None, rough=0.4, wear=1.0,
                          color_fn=M.hazard_fn(YELLOW, width=0.07, axis=('x', 'z'))),
        'HazardY': M.paint('HazardY', None, rough=0.4, wear=1.0,
                           color_fn=M.hazard_fn(YELLOW, width=0.07, axis=('y', 'z'))),
        'Grey': M.paint('Grey', (0.40, 0.42, 0.42), rough=0.42, wear=0.4),
        'DarkSteel': M.dark_steel('DarkSteel'),
        'Steel': M.dark_steel('Steel', color=(0.30, 0.31, 0.32), rough=0.30, metal=0.9),
        'Chrome': M.chrome('Chrome'),
        'Rubber': M.rubber('Rubber'),
    })
    return MATS


# =============================================================================
# Helpers
# =============================================================================

def mk(name, fn, bevel=0.01, seg=1, angle=40.0, cull=None, sharp=40.0):
    p = Part(name)
    fn(p)
    ob = p.build(MATS)
    if cull:
        cull_faces(ob, cull)
    finish(ob, bevel, seg, angle, sharp)
    return ob


def bx(p, x0, x1, y0, y1, z0, z1, mat):
    p.box_minmax((min(x0, x1), min(y0, y1), min(z0, z1)), (max(x0, x1), max(y0, y1), max(z0, z1)), mat)


def tube(p, a, b, r, mat, n=8, cap=True):
    p.cylinder(V(a), V(b), r, mat, n=n, cap=cap)


FLOOR = lambda c, n: n.z < -0.9 and c.z < 0.02         # noqa: E731


# =============================================================================
# Stacker
# =============================================================================

def build_stacker():
    P = D['post'] / 2
    parts = {}

    # ---- four corner legs: post, base plate, cap, knee brace inwards along y, hazard foot band
    def leg(sx, sy):
        x, y = sx * XW, sy * YN

        def fn(p):
            bx(p, x - 0.30, x + 0.30, y - 0.30, y + 0.30, 0.0, 0.03, 'DarkSteel')          # base plate
            for dx in (-0.22, 0.22):                                                   # anchor bolts
                for dy in (-0.22, 0.22):
                    p.lathe([(0.03, 0.025), (0.07, 0.025)], 6, 'Steel', center=(x + dx, y + dy, 0), axis='Z')
            bx(p, x - P, x + P, y - P, y + P, 0.03, 1.00, 'Hazard')   # foot band
            bx(p, x - P, x + P, y - P, y + P, 1.00, POST_TOP - 0.02, 'Struct')               # post
            bx(p, x - P - 0.03, x + P + 0.03, y - P - 0.03, y + P + 0.03, POST_TOP - 0.02, POST_TOP,
               'DarkSteel')                                                                 # cap plate
            # knee brace in the y-z plane, towards the runway centre (-sy)
            a = V((x, y - sy * P, 3.25))
            b = V((x, y - sy * (P + 1.00), RUNWAY_Z))
            p.sweep([a, b], [(-0.07, -0.05), (0.07, -0.05), (0.07, 0.05), (-0.07, 0.05)], 'Struct',
                    side=(1, 0, 0))
            # runway end stop (buffer block on the rail end) and a rubber buffer
            bx(p, x - 0.13, x + 0.13, y - 0.10, y + 0.10, RW_TOP, RAIL_TOP + 0.25, 'Accent')
            p.lathe([(0.0, 0.07), (0.10, 0.07)], 8, 'Rubber',
                    center=(x, y - sy * 0.10 - (0.10 if sy > 0 else 0.0), RAIL_TOP + 0.12), axis='Y')
        return fn
    legs = {}
    for tag, sx, sy in (('SW', -1, -1), ('SE', 1, -1), ('NW', -1, 1), ('NE', 1, 1)):
        legs['Leg_' + tag] = mk('G_Leg' + tag, leg(sx, sy), bevel=0.008, cull=FLOOR)

    # ---- runway girders (box girder + rail) along y, one per side
    def runway(sx):
        x = sx * XW

        def fn(p):
            e = YN + P + 0.05
            bx(p, x - 0.15, x + 0.15, -e, e, RUNWAY_Z, RW_TOP - 0.03, 'Struct')                # box girder
            bx(p, x - 0.17, x + 0.17, -e, e, RW_TOP - 0.03, RW_TOP, 'Struct')                  # top flange
            bx(p, x - 0.035, x + 0.035, -e + 0.05, e - 0.05, RW_TOP, RAIL_TOP, 'Steel')      # rail
        return fn
    runways = {'Runway_W': mk('G_RunwayW', runway(-1), bevel=0.01),
               'Runway_E': mk('G_RunwayE', runway(1), bevel=0.01)}

    # ---- end ties between the post heads (across x)
    def endbeam(sy):
        y = sy * YN

        def fn(p):
            bx(p, -XW, XW, y - 0.12, y + 0.12, POST_TOP, POST_TOP + 0.40, 'Struct')
        return fn
    ends = {'EndBeam_S': mk('G_EndS', endbeam(-1), bevel=0.01),
            'EndBeam_N': mk('G_EndN', endbeam(1), bevel=0.01)}

    # ---- bridge: two girders spanning x (node Girder) ...
    def girder(p):
        for y in (GIRDER_Y, -GIRDER_Y):
            bx(p, -XW, XW, y - GIRDER_W / 2, y + GIRDER_W / 2, RAIL_TOP + 0.11, TROLLEY_Z - 0.02, 'Accent')
            bx(p, -XW, XW, y - GIRDER_W / 2 - 0.02, y + GIRDER_W / 2 + 0.02, TROLLEY_Z - 0.02, TROLLEY_Z, 'Accent')
            bx(p, -XW + 0.25, XW - 0.25, y - 0.02, y + 0.02, TROLLEY_Z, TROLLEY_Z + 0.04, 'Steel')   # trolley rail
    girder_ob = mk('G_Girder', girder, bevel=0.01)

    # ... end trucks on the runway rails (Saddle_W / Saddle_E)
    def saddle(sx):
        x = sx * XW

        def fn(p):
            bx(p, x - 0.17, x + 0.17, -SADDLE_HL, SADDLE_HL, RAIL_TOP + 0.06, RAIL_TOP + 0.40, 'Accent')
            for y in (-SADDLE_HL + 0.22, SADDLE_HL - 0.22):                        # wheels
                p.lathe([(-0.06, 0.12), (0.06, 0.12)], 12, 'DarkSteel', center=(x, y, RAIL_TOP + 0.10), axis='X')
            for sy in (-1, 1):                                                       # rubber buffers
                p.lathe([(0.0, 0.06), (0.10, 0.06)], 8, 'Rubber',
                        center=(x, sy * SADDLE_HL + (0.0 if sy > 0 else -0.10), RAIL_TOP + 0.22), axis='Y')
            bx(p, x - 0.12, x + 0.12, -0.42, -0.12, RAIL_TOP + 0.40, RAIL_TOP + 0.62, 'Grey')   # travel drive
            p.lathe([(-0.12, 0.09), (0.10, 0.09)], 10, 'Struct',
                    center=(x, -0.27, RAIL_TOP + 0.51), axis='X')
        return fn
    saddles = {'Saddle_W': mk('G_SaddleW', saddle(-1), bevel=0.008),
               'Saddle_E': mk('G_SaddleE', saddle(1), bevel=0.008)}

    # ... bridge control box (node Bridge), on the outside of the -y girder at mid span
    def bridge_box(p):
        y = -GIRDER_Y - GIRDER_W / 2 - 0.02
        bx(p, -0.30, 0.30, y - 0.22, y, RAIL_TOP + 0.12, RAIL_TOP + 0.52, 'Grey')
        bx(p, -0.26, 0.26, y - 0.23, y - 0.22, RAIL_TOP + 0.16, RAIL_TOP + 0.48, 'DarkSteel')    # door seam
    bridge_ob = mk('G_Bridge', bridge_box, bevel=0.008)

    # ---- trolley: carriage on the girders, hoist motor, beacon, outer mast
    def trolley(p):
        z0 = TROLLEY_Z + 0.04
        bx(p, -0.375, 0.375, -0.475, 0.475, z0, z0 + 0.26, 'Light')                        # carriage
        for x in (-0.30, 0.30):                                                          # wheels on the rails
            for y in (GIRDER_Y, -GIRDER_Y):
                p.lathe([(-0.04, 0.07), (0.04, 0.07)], 10, 'DarkSteel', center=(x, y, z0 + 0.03), axis='X')
        p.lathe([(-0.30, 0.15), (0.12, 0.16), (0.18, 0.11), (0.28, 0.11)], 12, 'Struct',
                center=(0.05, 0.0, z0 + 0.45), axis='Y')                                 # hoist motor + gear
        bx(p, -0.30, -0.10, -0.40, 0.40, z0 + 0.26, z0 + 0.62, 'Struct')                 # gearbox
        p.lathe([(z0 + 0.26, 0.05), (z0 + 0.36, 0.05), (z0 + 0.38, 0.03)], 10, 'Yellow',
                center=(0.28, 0.36, 0), axis='Z')                                        # beacon
        bx(p, -0.13, 0.13, -0.13, 0.13, MAST_LOW, z0, 'Grey')                           # outer mast
        bx(p, -0.16, 0.16, -0.16, 0.16, MAST_LOW, MAST_LOW + 0.10, 'DarkSteel')         # guide collar
    trolley_ob = mk('G_Trolley', trolley, bevel=0.008)

    # ---- head: inner mast, hub, suction frame, hazard edges, cups
    HX, HY = D['head_x'] / 2, D['head_y'] / 2
    nx, ny = D['cups']
    rows = [-HY + 0.10 + (2 * HY - 0.20) * j / (ny - 1) for j in range(ny)]
    bars_x = [-HX + 0.08 + (2 * HX - 0.16) * i / 3 for i in range(4)]
    zf0, zf1 = CUP_Z + 0.12, CUP_Z + 0.22

    def head(p):
        bx(p, -0.09, 0.09, -0.09, 0.09, zf1 + 0.12, MAST_LOW + 2.10, 'Chrome')           # inner mast
        bx(p, -0.32, 0.32, -0.26, 0.26, zf1, zf1 + 0.12, 'DarkSteel')                   # hub
        for y in rows:                                                                   # long bars (x)
            bx(p, -HX, HX, y - 0.05, y + 0.05, zf0, zf1, 'DarkSteel')
        ey = rows[-1] + 0.05                                                             # outer bar faces
        for x in bars_x:                                                                 # cross bars (y)
            bx(p, x - 0.04, x + 0.04, -ey, ey, zf1, zf1 + 0.06, 'DarkSteel')
        for s in (-1, 1):                                                                # hazard edge strips
            bx(p, -HX, HX, s * ey, s * (ey + 0.012), zf0, zf1, 'Hazard')
            bx(p, s * HX, s * (HX + 0.012), -ey - 0.012, ey + 0.012, zf0, zf1, 'HazardY')
        for k, y in enumerate((-0.16, 0.16)):                                            # vacuum manifolds
            tube(p, (-HX + 0.25, y, zf1 + 0.17), (HX - 0.25, y, zf1 + 0.17), 0.045, 'Grey', n=8)
    head_ob = mk('G_Head', head, bevel=0.006)

    def cups(p):
        for i in range(nx):
            x = -HX + 0.20 + (2 * HX - 0.40) * i / (nx - 1)
            for y in rows:
                p.lathe([(zf0, 0.025), (CUP_Z + 0.07, 0.025), (CUP_Z + 0.03, 0.06), (CUP_Z, 0.075)], 8, 'Rubber',
                        center=(x, y, 0), axis='Z')
    cups_ob = mk('G_Cups', cups, bevel=0.0, sharp=50)

    nodes = dict(legs)
    nodes.update(runways)
    nodes.update(ends)
    nodes.update({'Bridge': [bridge_ob], 'Girder': [girder_ob], 'Trolley': [trolley_ob],
                  'Head': [head_ob, cups_ob]})
    nodes.update(saddles)
    nodes = {k: (v if isinstance(v, list) else [v]) for k, v in nodes.items()}
    pivots = {
        'Leg_SW': (-XW, -YN, 0.0), 'Leg_SE': (XW, -YN, 0.0), 'Leg_NW': (-XW, YN, 0.0), 'Leg_NE': (XW, YN, 0.0),
        'Runway_W': (-XW, 0.0, RUNWAY_Z), 'Runway_E': (XW, 0.0, RUNWAY_Z),
        'EndBeam_S': (0.0, -YN, POST_TOP), 'EndBeam_N': (0.0, YN, POST_TOP),
        'Bridge': (0.0, 0.0, RAIL_TOP), 'Girder': (0.0, 0.0, RAIL_TOP),
        'Saddle_W': (-XW, 0.0, RAIL_TOP), 'Saddle_E': (XW, 0.0, RAIL_TOP),
        'Trolley': (0.0, 0.0, TROLLEY_Z), 'Head': (0.0, 0.0, CUP_Z),
    }
    parents = {'Girder': 'Bridge', 'Saddle_W': 'Bridge', 'Saddle_E': 'Bridge', 'Trolley': 'Bridge',
               'Head': 'Trolley'}
    lod = {p.name: LOD_PLAN[p.name] for ps in nodes.values() for p in ps}
    return KM.Piece(NAME, 'fixed', nodes, levels=3, lod=lod, pivots=pivots, parents=parents)


# -----------------------------------------------------------------------------
# LODs: ratios of the LOD0 part per extra level (LOD1, LOD2); None = dropped
# -----------------------------------------------------------------------------
LOD_PLAN = {
    'G_LegSW': (0.45, 0.12), 'G_LegSE': (0.45, 0.12), 'G_LegNW': (0.45, 0.12), 'G_LegNE': (0.45, 0.12),
    'G_RunwayW': (0.6, 0.35), 'G_RunwayE': (0.6, 0.35), 'G_EndS': (0.6, 0.4), 'G_EndN': (0.6, 0.4),
    'G_Girder': (0.6, 0.6), 'G_SaddleW': (0.35, 0.12), 'G_SaddleE': (0.35, 0.12), 'G_Bridge': (0.5, None),
    'G_Trolley': (0.35, 0.15), 'G_Head': (0.45, 0.3), 'G_Cups': (None, None),
}

TEXEL_WEIGHTS = {
    'G_LegSW': 0.7, 'G_LegSE': 0.7, 'G_LegNW': 0.7, 'G_LegNE': 0.7,
    'G_RunwayW': 0.35, 'G_RunwayE': 0.35, 'G_EndS': 0.5, 'G_EndN': 0.5,
    'G_Girder': 0.45, 'G_SaddleW': 0.9, 'G_SaddleE': 0.9, 'G_Bridge': 1.0,
    'G_Trolley': 1.1, 'G_Head': 1.0, 'G_Cups': 1.4,
}


# =============================================================================
# Preview (render-only props, never exported)
# =============================================================================

def preview_renders(scene, pc, quick=False, full_spp=128):
    KM.hide_kit([pc])
    KM.studio(scene, k=6.0, floor=300)
    spp = 32 if quick else full_spp
    res = (800, 500) if quick else (1600, 1000)
    out = lambda n: os.path.join(PREVIEW_DIR, f'gantry_stacker_{n}.png')      # noqa: E731
    I = Matrix.Identity(4)

    board = bpy.data.materials.new('PreviewBoard')
    nb = K.NB(board, period={})
    K.finalize_material(nb, nb.mixc(nb.noise(30, 4), (0.55, 0.42, 0.26), (0.62, 0.50, 0.32)), 0.7, 0.0)
    frame = bpy.data.materials.new('PreviewFrame')
    nb = K.NB(frame, period={})
    K.finalize_material(nb, (0.12, 0.20, 0.34), 0.5, 0.3)
    roll = bpy.data.materials.new('PreviewRoll')
    nb = K.NB(roll, period={})
    K.finalize_material(nb, (0.55, 0.56, 0.57), 0.35, 0.9)
    pm = {'B': board, 'F': frame, 'R': roll}

    def props(c):
        """Stacking table at y 0 under the head and four stacking positions on roller tables."""
        p = Part('Props')
        top = 0.55
        for yc, w in ((0.0, 2.4), (5.3, 2.4), (2.7, 2.4), (-2.7, 2.4), (-5.3, 2.4)):
            x0, x1 = (-1.8, 1.3) if yc == 0.0 else (-2.6, 3.4)
            for s in (-1, 1):
                p.box(((x0 + x1) / 2, yc + s * (w / 2 + 0.04), top - 0.10), (x1 - x0, 0.08, 0.16), 'F')
                for xl in (x0 + 0.1, x1 - 0.1):
                    p.box((xl, yc + s * (w / 2 + 0.04), (top - 0.18) / 2), (0.07, 0.07, top - 0.18), 'F')
            n = int((x1 - x0) / 0.25)
            for k in range(n):
                p.lathe([(-w / 2, 0.045), (w / 2, 0.045)], 6, 'R', center=(x0 + 0.125 + k * 0.25, yc, top - 0.045),
                        axis='Y')
        for (xc, yc, h) in ((-0.25, 0.0, 0.62), (0.4, 5.3, 1.1), (0.4, -2.7, 0.9)):     # stacks
            p.box((xc, yc, top + h / 2), (2.85, 2.10, h), 'B')
        p.build(pm, coll=c)

    def stage(level, tag='', pose=None):
        c = K.collection(f'Preview_L{level}{tag}')
        KM.place(pc, I, c, level, node_pose=pose)
        props(c)
        KM.scale_figure(c, (3.4, -7.8, 0), 0.6)
        return c

    def only(c):
        for o in bpy.data.collections:
            if o.name.startswith('Preview_'):
                o.hide_render = o is not c

    c = stage(0)
    only(c)
    hero = ((15.0, -17.0, 9.5), (0.0, -1.5, 2.6), 32)
    KM.render(scene, KM.camera(scene, 'Cam_Hero', *hero), out('hero'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Side', (-40.0, 0.0, 3.0), (0.0, 0.0, 3.0), ortho=25.0), out('side'),
              res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_End', (0.0, -40.0, 3.0), (0.0, 0.0, 3.0), ortho=9.0), out('end'), res, spp)
    KM.render(scene, KM.camera(scene, 'Cam_Top', (0.0, 0.0, 60.0), (0.0, 0.0, 0.0), ortho=25.0), out('top'),
              res, spp)
    # head lowered onto the stack being formed (Head -0.85 m)
    c = stage(0, 'dip', {'Head': Matrix.Translation((0.0, 0.0, -0.85))})
    only(c)
    KM.render(scene, KM.camera(scene, 'Cam_Head', (4.6, -5.4, 3.6), (-0.1, 0.0, 2.0), 32), out('head'), res, spp)
    for lvl in (1, 2):
        only(stage(lvl))
        KM.render(scene, KM.camera(scene, f'Cam_LOD{lvl}', *hero), out(f'lod{lvl}'), (800, 500), spp)


# =============================================================================
# Main
# =============================================================================

def use_cpu(scene):
    scene.render.engine = 'CYCLES'
    prefs = bpy.context.preferences.addons['cycles'].preferences
    try:
        prefs.compute_device_type = 'NONE'
    except TypeError:
        pass
    scene.cycles.device = 'CPU'
    print('[gantry_stacker] Cycles device: CPU')


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--variant', default='all', choices=('all', 'main'))
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--no-render', action='store_true')
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--render-cpu', action='store_true',
                    help='bake on the GPU, render the previews on the CPU (if OptiX faults while rendering)')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    export = args.variant == 'all'
    out_dir = OUT_DIR if export else os.path.join(BUILD_DIR, f'{NAME}_{args.variant}')
    t0 = time.time()

    scene = K.reset_scene()
    if args.cpu:
        use_cpu(scene)
    else:
        K.setup_cycles_gpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    os.makedirs(out_dir, exist_ok=True)
    build_materials()
    pc = build_stacker()
    KM.bake_kit([pc], out_dir, NAME, args.bake, args.save,
                weights={p.name: TEXEL_WEIGHTS.get(p.name, 1.0) for p in pc.parts})
    t_bake = time.time() - t0

    KM.build_lods(pc)
    KM.report([pc])
    KM.rotate_to_export([pc])
    KM.rig(pc)
    if export:
        K.export_fbx(pc.root, os.path.join(out_dir, f'{NAME}.fbx'))
    blend = os.path.join(BUILD_DIR, f'{NAME}.blend' if export else f'{NAME}_{args.variant}.blend')
    bpy.ops.wm.save_as_mainfile(filepath=blend)
    if not args.no_render:
        if args.render_cpu and not args.cpu:
            use_cpu(scene)
        cpu = args.cpu or args.render_cpu
        preview_renders(scene, pc, args.quick, 48 if cpu else 128)
        bpy.ops.wm.save_as_mainfile(filepath=blend)
    print(f'[gantry_stacker] done in {time.time() - t0:.1f}s (bake phase {t_bake:.1f}s)')


if __name__ == '__main__':
    main()
