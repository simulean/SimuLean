"""
SimuLean kit: prefabricated hull block ("bloque") for a shipyard model.  Two
variants share one parametric builder:

    curved   bilge / side block: bottom shell turning into the side shell
             through the bilge radius, double bottom (floors + tank top +
             hopper), web frames, longitudinals, light-grey deck.  Open at
             both butts (x = +-L/2) and on the inboard side (y = -B/2) so the
             internal structure reads from outside.
    flat     double-bottom panel: bottom shell, floors with lightening holes,
             side girders and longitudinals, margin plates on both sides,
             open on top (before the tank top goes on).

No block number is painted by default: the number is the item's label, shown by
ItemLabelView in Unity, so one model fits every block.  build_block(block_id='B204') still
paints one on the +Y side (and on the deck of the curved block) for stills.

Built live through the blender-ship MCP (see Tools/Blender/README.md):

    import runpy; ns = runpy.run_path('<repo>/Tools/Blender/assets/ship_block.py')
    ns['build_scene']()                       # both blocks, side by side
    ns['build_scene'](variants=('curved',), curved=dict(length=12, breadth=16))

Frame (Blender, before export): block length along X, breadth along Y (side
shell at +Y), up +Z.  Pivot at the base centre: the bottom shell's outer face
is z = 0, x/y centred.  Roots ShipBlockCurved / ShipBlockFlat, nodes:
    <Root>_Lug_1..4   centres of the pad-eye holes (hook attach points)
    <Root>_LiftPoint  6 m above the lugs, where the crane hook sits
"""

import os
import sys
from math import pi, sin, cos, atan2, sqrt

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
if KIT not in sys.path:
    sys.path.insert(0, KIT)

import bpy                                   # noqa: E402
from mathutils import Vector                 # noqa: E402
import simulean_kit as K                     # noqa: E402
import simulean_materials as M               # noqa: E402
from simulean_kit import V, Part             # noqa: E402

PREFIX = 'ShipBlock'
BUILD_DIR = os.path.join(KIT, 'build')
FONT = 'C:/Windows/Fonts/bahnschrift.ttf'

# ---------------------------------------------------------------- colours (linear RGB)
RED_OXIDE = (0.150, 0.042, 0.028)      # shop primer: brown-red, a little metallic
RED_INNER = (0.140, 0.044, 0.030)
MARK_WHITE = (0.75, 0.75, 0.72)
INK = (0.022, 0.022, 0.025)
DECK_GREY = (0.40, 0.40, 0.385)

# ---------------------------------------------------------------- scantlings (m, visual)
T_SHELL = 0.030          # shell / deck / tank-top plate (exaggerated a little to read)
T_WEB = 0.022            # floors and web frames
FRAME_SPACING = 2.4      # web frames / floors
LONG_SPACING = 0.80      # longitudinals
LONG_H, LONG_F, LONG_TW, LONG_TF = 0.24, 0.09, 0.014, 0.016


# =============================================================================
# 2D helpers (profiles in the (y, z) plane)
# =============================================================================

def _seg_normal(a, b):
    dy, dz = b[0] - a[0], b[1] - a[1]
    n = sqrt(dy * dy + dz * dz)
    return (-dz / n, dy / n)          # left normal


def offset_polyline(pts, t):
    """Offset an open polyline by t along its left normal (mitred joints)."""
    out = []
    for i, p in enumerate(pts):
        if i == 0:
            n = _seg_normal(pts[0], pts[1])
            k = 1.0
        elif i == len(pts) - 1:
            n = _seg_normal(pts[-2], pts[-1])
            k = 1.0
        else:
            n0, n1 = _seg_normal(pts[i - 1], p), _seg_normal(p, pts[i + 1])
            ny, nz = n0[0] + n1[0], n0[1] + n1[1]
            ln = sqrt(ny * ny + nz * nz)
            n = (ny / ln, nz / ln)
            k = 1.0 / max(0.3, n[0] * n0[0] + n[1] * n0[1])
        out.append((p[0] + n[0] * t * k, p[1] + n[1] * t * k))
    return out


def arc(cy, cz, r, a0, a1, n):
    return [(cy + r * cos(a0 + (a1 - a0) * i / n), cz + r * sin(a0 + (a1 - a0) * i / n))
            for i in range(n + 1)]


def dedupe(pts, eps=1e-6):
    out = []
    for p in pts:
        if not out or abs(p[0] - out[-1][0]) > eps or abs(p[1] - out[-1][1]) > eps:
            out.append(p)
    return out


def clip_polyline_z(pts, zmax):
    """Keep the start of a polyline up to height zmax (inserting the crossing)."""
    out = [pts[0]]
    for a, b in zip(pts, pts[1:]):
        if b[1] <= zmax:
            out.append(b)
            continue
        if a[1] < zmax:
            s = (zmax - a[1]) / (b[1] - a[1])
            out.append((a[0] + (b[0] - a[0]) * s, zmax))
        break
    return out


# =============================================================================
# 3D builders on a Part
# =============================================================================

def plate_strip(p, pts, t, x0, x1, mat_out, mat_in):
    """A plate of thickness t following the open (y, z) polyline `pts`,
    thickened towards its left normal, running x0..x1.  mat_out on the
    polyline's face, mat_in on the offset face and the cut edges."""
    a = pts
    b = offset_polyline(pts, t)
    n = len(a)
    verts = ([V((x0, y, z)) for y, z in a] + [V((x1, y, z)) for y, z in a] +
             [V((x0, y, z)) for y, z in b] + [V((x1, y, z)) for y, z in b])
    A0, A1, B0, B1 = 0, n, 2 * n, 3 * n
    faces, mats = [], []
    for i in range(n - 1):
        faces += [(A0 + i, A0 + i + 1, A1 + i + 1, A1 + i),
                  (B0 + i, B1 + i, B1 + i + 1, B0 + i + 1),
                  (A0 + i, B0 + i, B0 + i + 1, A0 + i + 1),
                  (A1 + i, A1 + i + 1, B1 + i + 1, B1 + i)]
        mats += [mat_out, mat_in, mat_in, mat_in]
    faces += [(A0, A1, B1, B0), (A0 + n - 1, B0 + n - 1, B1 + n - 1, A1 + n - 1)]
    mats += [mat_in, mat_in]
    p.add(verts, faces, mats)


def web_plate(p, profile, x, t, mat):
    """A flat plate (closed (y, z) polygon) of thickness t centred on x."""
    p.extrude(profile, 'X', x - t / 2, x + t / 2, mat)


def longitudinal(p, base, normal, x0, x1, mat, h=LONG_H, f=LONG_F, tw=LONG_TW, tf=LONG_TF):
    """Angle stiffener welded at `base` (y, z), web along `normal`, running x0..x1."""
    ny, nz = normal
    sy, sz = nz, -ny                       # flange side
    by, bz = base

    def P(u, v):                            # u along s, v along n
        return (by + sy * u + ny * v, bz + sz * u + nz * v)
    prof = [P(-tw / 2, 0), P(-tw / 2, h), P(f, h), P(f, h - tf), P(tw / 2, h - tf), P(tw / 2, 0)]
    p.extrude(prof, 'X', x0, x1, mat)


def lug(p, cx, cy, z0, mat, w=0.76, h=0.46, r=0.38, hole=0.13, th=0.06, n=20):
    """Pad-eye lifting lug in the XZ plane (hole axis Y) standing on z0.
    Returns the hole centre."""
    hc = (cx, z0 + h)                        # hole centre (x, z)
    outer, inner = [], []
    for i in range(n):
        a = pi / 2 + 2 * pi * i / n          # start at the top
        dx, dz = cos(a), sin(a)
        if dz >= 0:
            rad = r
        else:                                # rectangle below the hole centre
            ts = []
            if abs(dx) > 1e-6:
                ts.append((w / 2) / abs(dx))
            ts.append(h / -dz)
            rad = min(ts)
        outer.append((hc[0] + dx * rad, hc[1] + dz * rad))
        inner.append((hc[0] + dx * hole, hc[1] + dz * hole))
    y0, y1 = cy - th / 2, cy + th / 2
    verts = ([V((x, y0, z)) for x, z in outer] + [V((x, y1, z)) for x, z in outer] +
             [V((x, y0, z)) for x, z in inner] + [V((x, y1, z)) for x, z in inner])
    O0, O1, I0, I1 = 0, n, 2 * n, 3 * n
    faces = []
    for i in range(n):
        j = (i + 1) % n
        faces += [(O0 + i, O0 + j, I0 + j, I0 + i), (O1 + i, I1 + i, I1 + j, O1 + j),
                  (O0 + i, O1 + i, O1 + j, O0 + j), (I0 + i, I0 + j, I1 + j, I1 + i)]
    p.add(verts, faces, mat)
    # doubler pad under the lug
    p.box((cx, cy, z0 + 0.015), (w + 0.24, 0.40, 0.030), mat)
    return V((hc[0], cy, hc[1]))


# =============================================================================
# Shared structure
# =============================================================================

def frame_stations(L):
    nfr = int((L - 0.5) // FRAME_SPACING)
    x0 = -L / 2
    return [x0 + (L - (nfr - 1) * FRAME_SPACING) / 2 + i * FRAME_SPACING for i in range(nfr)]


def girder_positions(yi, y_end):
    """Side girders every 3.2 m, on longitudinal positions (they replace one)."""
    gs = [yi + LONG_SPACING / 2 + LONG_SPACING * k for k in range(3, 40, 4)]
    return [g for g in gs if g < y_end - 0.5]


def cut_lightening_holes(pw, mats, coll, y0, y1, zc, girders, x0, x1):
    """Build the floors part and cut one row of oval holes (along X) into it."""
    cut = Part(PREFIX + '_Cut')
    oval = [(0.42 * cos(2 * pi * i / 16), 0.30 * sin(2 * pi * i / 16)) for i in range(16)]
    y = y0 + 1.0
    while y < y1 - 0.6:
        if not any(abs(y - g) < 0.65 for g in girders):
            cut.extrude([(y + a, zc + b) for a, b in oval], 'X', x0 - 1, x1 + 1, 'red_in')
        y += 1.6
    webs = pw.build(mats, coll)
    if cut.bm.faces:
        K.boolean(webs, cut.build(mats, coll))
    else:
        cut.bm.free()
    return webs


# =============================================================================
# Curved (bilge / side) block
# =============================================================================

def build_curved(length=12.0, breadth=16.0, depth=5.0, bilge=2.5, db_height=1.6,
                 hopper_height=3.2, hopper_breadth=3.6, name='ShipBlockCurved', coll=None,
                 mats=None):
    L, B, D, R = length, breadth, depth, bilge
    t = T_SHELL
    yi, yo = -B / 2, B / 2
    x0, x1 = -L / 2, L / 2
    p = Part(name + '_Hull')
    pw = Part(name + '_Webs')               # floors: built apart to cut lightening holes

    # ---- shell: bottom -> bilge -> side (outer face on the polyline)
    shell = dedupe([(yi, 0.0)] + arc(yo - R, R, R, -pi / 2, 0.0, 10) + [(yo, D - t)])
    plate_strip(p, shell, t, x0, x1, 'red', 'red_in')

    # ---- deck (top face grey), from the side inwards
    plate_strip(p, [(yo, D), (yi, D)], t, x0, x1, 'deck', 'red_in')

    # ---- tank top + hopper (top face on the polyline)
    y_h = yo - hopper_breadth
    tank = [(yi, db_height + t), (y_h, db_height + t), (yo - t, hopper_height)]
    plate_strip(p, list(reversed(tank)), t, x0, x1, 'red_in', 'red_in')

    def hopper_z(y):
        if y <= y_h:
            return db_height
        return db_height + (hopper_height - db_height) * (y - y_h) / (yo - t - y_h)

    # ---- transverse members at every web-frame station
    stations = frame_stations(L)
    inner_shell = offset_polyline(shell, t)
    floor_low = clip_polyline_z(inner_shell, hopper_height - 0.02)
    floor = dedupe(floor_low + [(y_h, db_height), (yi, db_height)])
    hb, ws, br = 0.70, 0.75, 0.9             # deck beam depth, side web width, bracket
    wing = [(yo - t, hopper_height), (yo - t, D - t), (yi, D - t), (yi, D - t - hb),
            (yo - t - ws - br, D - t - hb), (yo - t - ws, D - t - hb - br),
            (yo - t - ws, hopper_z(yo - t - ws) + t)]
    for x in stations:
        web_plate(pw, floor, x, T_WEB, 'red_in')
        web_plate(p, wing, x, T_WEB, 'red_in')
        # face flats on the free edges of the wing web
        p.box_minmax((x - 0.09, yi, D - t - hb - 0.018), (x + 0.09, yo - t - ws - br, D - t - hb),
                     'red_in')
        p.box_minmax((x - 0.09, yo - t - ws - 0.018, hopper_z(yo - t - ws) + t),
                     (x + 0.09, yo - t - ws, D - t - hb - br), 'red_in')

    # ---- side girders in the double bottom (they replace a longitudinal)
    girders = girder_positions(yi, y_h)
    for g in girders:
        p.box_minmax((x0, g - T_WEB / 2, t), (x1, g + T_WEB / 2, db_height), 'red_in')

    def near_girder(y, d=0.3):
        return any(abs(y - g) < d for g in girders)

    # ---- longitudinals
    lx0, lx1 = x0 + 0.02, x1 - 0.02
    y = yi + LONG_SPACING / 2
    while y < yo - R - 0.2:                          # bottom shell (in the double bottom)
        if not near_girder(y):
            longitudinal(p, (y, t), (0, 1), lx0, lx1, 'red_in')
        yt = y + LONG_SPACING / 2
        if yt < y_h - 0.2 and not near_girder(yt):   # under the tank top
            longitudinal(p, (yt, db_height), (0, -1), lx0, lx1, 'red_in')
        y += LONG_SPACING
    for a in (-pi / 2 + pi / 6, -pi / 2 + pi / 3):   # bilge
        ny, nz = -cos(a), -sin(a)
        by, bz = yo - R + (R - t) * cos(a), R + (R - t) * sin(a)
        longitudinal(p, (by, bz), (ny, nz), lx0, lx1, 'red_in')
    z = hopper_height + LONG_SPACING                 # side shell above the hopper
    while z < D - hb - 0.3:
        longitudinal(p, (yo - t, z), (-1, 0), lx0, lx1, 'red_in')
        z += LONG_SPACING
    y = yi + LONG_SPACING / 2                        # under the deck
    while y < yo - ws - 0.3:
        longitudinal(p, (y, D - t), (0, -1), lx0, lx1, 'red_in')
        y += LONG_SPACING

    # ---- lifting lugs on the deck (over web frames, both sides of the centre)
    lug_x = (stations[0], stations[-1])
    lug_y = (yi + B * 0.25, yo - B * 0.25)
    holes = []
    for lx in lug_x:
        for ly in lug_y:
            holes.append(lug(p, lx, ly, D, 'lug'))

    webs = cut_lightening_holes(pw, mats, coll, yi, y_h, db_height / 2, girders, x0, x1)
    ob = K.join([p.build(mats, coll), webs], name + '_Hull')
    K.finish(ob, bevel=0, sharp=35.0)
    return ob, holes


def number_image(block_id):
    return K.render_text_image(block_id, os.path.join(BUILD_DIR, f'decal_{block_id}.png'),
                               256, FONT, bold_offset=0.02)


def curved_decals(block_id, length=12.0, breadth=16.0, depth=5.0, **_):
    if not block_id:
        return {}
    img, aspect = number_image(block_id)
    h = min(1.2, depth * 0.22)
    side = dict(image=img, origin=(0.0, breadth / 2, depth * 0.62), u=(-1, 0, 0), v=(0, 0, 1),
                size=(h * aspect, h), normal=(0, 1, 0), color=MARK_WHITE)
    deck = dict(image=img, origin=(0.0, breadth * 0.08, depth), u=(1, 0, 0), v=(0, 1, 0),
                size=(1.0 * aspect, 1.0), normal=(0, 0, 1), color=INK)
    return {'red': [side], 'deck': [deck]}


# =============================================================================
# Flat double-bottom panel
# =============================================================================

def build_flat(length=12.0, breadth=16.0, depth=1.6, name='ShipBlockFlat', coll=None, mats=None):
    L, B, H = length, breadth, depth
    t = T_SHELL
    yi, yo = -B / 2, B / 2
    x0, x1 = -L / 2, L / 2
    p = Part(name + '_Hull')
    pw = Part(name + '_Webs')

    # bottom shell (outer face down) and the margin plates closing both sides
    plate_strip(p, [(yi, 0.0), (yo, 0.0)], t, x0, x1, 'red', 'red_in')
    plate_strip(p, [(yo, t), (yo, H)], t, x0, x1, 'red', 'red_in')
    plate_strip(p, [(yi, H), (yi, t)], t, x0, x1, 'red', 'red_in')

    stations = frame_stations(L)
    girders = girder_positions(yi, yo - 0.4)
    floor = [(yi + t, t), (yo - t, t), (yo - t, H), (yi + t, H)]
    for x in stations:
        web_plate(pw, floor, x, T_WEB, 'red_in')
    for g in girders:
        p.box_minmax((x0, g - T_WEB / 2, t), (x1, g + T_WEB / 2, H), 'red_in')
    lx0, lx1 = x0 + 0.02, x1 - 0.02
    y = yi + LONG_SPACING / 2
    while y < yo - 0.2:
        if not any(abs(y - g) < 0.3 for g in girders):
            longitudinal(p, (y, t), (0, 1), lx0, lx1, 'red_in')
        y += LONG_SPACING

    # lugs on the top edge of the outermost girders, over the end floors
    holes = []
    for lx in (stations[0], stations[-1]):
        for g in (girders[0], girders[-1]):
            holes.append(lug(p, lx, g, H, 'lug'))

    webs = cut_lightening_holes(pw, mats, coll, yi, yo, (t + LONG_H + H) / 2, girders, x0, x1)
    ob = K.join([p.build(mats, coll), webs], name + '_Hull')
    K.finish(ob, bevel=0, sharp=35.0)
    return ob, holes


def flat_decals(block_id, length=12.0, breadth=16.0, depth=1.6, **_):
    if not block_id:
        return {}
    img, aspect = number_image(block_id)
    h = min(1.0, depth * 0.5)
    side = dict(image=img, origin=(0.0, breadth / 2, depth * 0.5), u=(-1, 0, 0), v=(0, 0, 1),
                size=(h * aspect, h), normal=(0, 1, 0), color=MARK_WHITE)
    return {'red': [side]}


# =============================================================================
# Materials / scene
# =============================================================================

def make_materials(tag, decals=None):
    d = decals or {}
    return {
        'red': M.paint(f'SB{tag}_Primer', RED_OXIDE, rough=0.48, wear=0.2, dirt=0.25,
                       flake=0.22, decals=d.get('red', ())),
        'red_in': M.paint(f'SB{tag}_PrimerInner', RED_INNER, rough=0.52, wear=0.15, dirt=0.3,
                          flake=0.18),
        'deck': M.paint(f'SB{tag}_Deck', DECK_GREY, rough=0.5, wear=0.3, dirt=0.4, flake=0.15,
                        decals=d.get('deck', ())),
        'lug': M.paint(f'SB{tag}_Lug', RED_OXIDE, rough=0.42, wear=0.6, dirt=0.3, flake=0.3),
    }


VARIANTS = {
    'curved': ('Curved', build_curved, curved_decals, ''),
    'flat': ('Flat', build_flat, flat_decals, ''),
}


def build_block(variant='curved', block_id=None, location=(0, 0, 0), **params):
    """Build one block (replacing a previous one of the same variant)."""
    tag, builder, decals, default_id = VARIANTS[variant]
    name = PREFIX + tag
    for ob in [o for o in bpy.data.objects if o.name.startswith(name)]:
        bpy.data.objects.remove(ob, do_unlink=True)
    os.makedirs(BUILD_DIR, exist_ok=True)
    coll = K.collection(PREFIX)
    mats = make_materials(tag, decals(block_id or default_id, **params))
    root = K.empty(name, (0, 0, 0), coll, size=1.0)
    hull, holes = builder(name=name, coll=coll, mats=mats, **params)
    K.parent_keep_local(hull, root)
    for i, h in enumerate(holes, 1):
        K.parent_keep_local(K.empty(f'{name}_Lug_{i}', h, coll, size=0.3), root)
    top = max(h.z for h in holes)
    K.parent_keep_local(K.empty(f'{name}_LiftPoint', (0, 0, top + 6.0), coll, size=0.8), root)
    root.location = V(location)
    print(f'[ShipBlock] {variant} {block_id or default_id or "(no number)"}: {K.tri_count(hull)} tris, '
          f'{len(holes)} lugs')
    return root


def build_scene(variants=('curved', 'flat'), curved=None, flat=None):
    """Both blocks side by side along X (preview layout)."""
    params = {'curved': curved or {}, 'flat': flat or {}}
    roots, x = [], 0.0
    for v in variants:
        L = params[v].get('length', 12.0)
        roots.append(build_block(v, location=(x + L / 2, 0, 0), **params[v]))
        x += L + 4.0
    return roots


# =============================================================================
# Export (one FBX per block, one shared atlas)
# =============================================================================

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'ShipBlock')
LOD_RATIOS = (0.45, 0.18)


def export(bake=4096, save=2048, curved=None, flat=None):
    """Rebuild both blocks at the origin, bake one atlas, build LODs, rig and
    write Art/Models/ShipBlock/ShipBlockCurved.fbx + ShipBlockFlat.fbx."""
    import simulean_modular as KM
    params = {'curved': curved or {}, 'flat': flat or {}}
    pieces = []
    for variant in ('curved', 'flat'):
        root = build_block(variant, **params[variant])
        hull = next(c for c in root.children if c.type == 'MESH')
        sockets = {}
        for c in root.children:
            if c.type == 'EMPTY':
                sockets[c.name[len(root.name) + 1:]] = tuple(c.matrix_world.translation)
        hull.parent = None
        hull.matrix_world = hull.matrix_world.copy()
        name = root.name
        K.remove_objects([c for c in root.children] + [root])
        hull.name = hull.data.name = f'{name}_Hull'
        pieces.append(KM.Piece(name, 'fixed', [hull], levels=1 + len(LOD_RATIOS),
                               lod={hull.name: LOD_RATIOS}, sockets=sockets))
    # bake layout: the blocks are wider than the default 9 m slot
    old_slot, KM.SLOT = KM.SLOT, 40.0
    try:
        scene = bpy.context.scene
        K.setup_cycles_gpu(scene, backends=('CUDA', 'HIP', 'ONEAPI', 'METAL'))   # OptiX bakes black
        KM.bake_kit(pieces, OUT_DIR, PREFIX, bake, save, ao_distance=1.2)
    finally:
        KM.SLOT = old_slot
    for pc in pieces:
        KM.build_lods(pc)
    KM.report(pieces)
    KM.rotate_to_export(pieces)
    for pc in pieces:
        # both blocks have Lug_1..4 / LiftPoint: rig and export one at a time and
        # prefix the exported block's empties so the next one gets clean names
        KM.rig(pc)
        K.export_fbx(pc.root, os.path.join(OUT_DIR, f'{pc.name}.fbx'))
        for n in pc.root.children_recursive:
            if n.type == 'EMPTY':
                n.name = f'{pc.name}_{n.name}'
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
