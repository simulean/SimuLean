"""
SimuLean asset: hospital people -- doctor, nurse and patient, each in a male and a female look, built like the
operator (operator.py: MPFB 2 / MakeHuman CC0 assets, the "game_engine" rig, 1.74 m, the same bone contract), so
the same views walk, carry and seat them and the role / look is a field of the member (OperatorAppearance).

    Doctor    navy scrubs, a long-sleeved white coat (ours, fitted to the body like the operator's vest),
              a stethoscope round the neck
    Nurse     teal scrubs
    Patient   pale-blue hospital pyjamas

The scrubs / pyjamas are MakeHuman's T-shirt-and-trousers suits re-coloured to a flat colour: the print on the
shirt (a mark) disappears, the folds stay in the normal map.

Requirements as operator.py (Blender 5.x + MPFB with the makehuman_system_assets pack).  Run WITHOUT
--factory-startup:
    blender -b -P Tools/Blender/assets/hospital_people.py -- [--bake 2048] [--save 2048] [--quick] [--cpu]

Outputs (one folder, one atlas: a kit -- each FBX uses the folder's material HospitalPeople.mat)
    Assets/SimuLean.Unity/Art/Models/HospitalPeople/{Doctor,Nurse,Patient}.fbx            male looks
    Assets/SimuLean.Unity/Art/Models/HospitalPeople/{Doctor,Nurse,Patient}_Female.fbx     female looks
    Assets/SimuLean.Unity/Art/Models/HospitalPeople/HospitalPeople_{Albedo(RGBA),MetallicSmoothness,Normal,
                                                                     Occlusion,Emission}.png
    Tools/Blender/build/HospitalPeople.blend

Each FBX: a skinned mesh <Stem>_LOD0/1/2 on the armature, the operator's bone names (Torso, Leg_L/R, Shin_L/R,
Arm_L/R, Forearm_L/R, CarrySocket; a Helmet bone with no hat under it).  Frame as the operator: front = -Y (Unity
+Z), origin on the floor between the feet.
"""

import os
import sys
import argparse
import time
import importlib.util
from math import pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
from mathutils import Vector                # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402

# operator.py is loaded under its own name: 'operator' is also a standard-library module
_spec = importlib.util.spec_from_file_location('simulean_operator', os.path.join(HERE, 'operator.py'))
OP = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(OP)

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
FOLDER = 'HospitalPeople'
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', FOLDER)
BUILD_DIR = os.path.join(KIT, 'build')

SUITS = {'M': 'male_casualsuit06', 'F': 'female_casualsuit01'}   # T-shirt + trousers
SHOES = 'shoes02'
ROLES = {
    'Doctor': dict(colour=(0.025, 0.045, 0.11), coat=True),
    'Nurse': dict(colour=(0.05, 0.26, 0.27), coat=False),
    'Patient': dict(colour=(0.36, 0.52, 0.68), coat=False),
}
PEOPLE = {
    # role, look: MakeHuman look (as operator.LOOKS)
    ('Doctor', 'M'): dict(gender=1.0, cupsize=0.5, age=0.62, skin='middleage_caucasian_male', brows='eyebrow002',
                          hair='short04', proxy='male1591'),
    ('Doctor', 'F'): dict(gender=0.0, cupsize=0.45, skin='young_asian_female', brows='eyebrow010', hair='bob01',
                          proxy='female1605'),
    ('Nurse', 'M'): dict(gender=1.0, cupsize=0.5, skin='young_african_male', brows='eyebrow003', hair='short01',
                         proxy='male1591'),
    ('Nurse', 'F'): dict(gender=0.0, cupsize=0.45, skin='young_caucasian_female', brows='eyebrow010',
                         hair='ponytail01', proxy='female1605'),
    ('Patient', 'M'): dict(gender=1.0, cupsize=0.5, age=0.80, weight=0.6, skin='old_caucasian_male',
                           brows='eyebrow001', hair='short03', proxy='male1591'),
    ('Patient', 'F'): dict(gender=0.0, cupsize=0.45, age=0.62, skin='middleage_african_female',
                           brows='eyebrow009', hair='bob02', proxy='female1605'),
}
TEXEL = (('body', 1.5), ('hair', 1.0), ('suit', 1.0), ('coat', 0.5), ('steth', 0.2), ('boots', 0.4),
         ('eyes', 0.6), ('brows', 0.5))

MATS = OP.MATS                    # one dict: the operator's helpers build from it


def names(role, look):
    fbx = role if look == 'M' else f'{role}_Female'
    stem = role if look == 'M' else f'{role}F'
    return fbx, stem


def build_materials():
    MATS.update({
        'Coat': M.plastic('Coat', color=(0.80, 0.81, 0.80), rough=0.85, grain=0.15),
        'CoatTrim': M.plastic('CoatTrim', color=(0.62, 0.63, 0.63), rough=0.8, grain=0.1),
        'Steth': M.plastic('Steth', color=(0.03, 0.03, 0.035), rough=0.35),
        'StethMetal': M.dark_steel('StethMetal', color=(0.70, 0.71, 0.72), rough=0.15, metal=1.0),
    })
    for m in MATS.values():
        m.use_fake_user = True


def flat_dye(colour):
    """A dye(suit) for operator.build_human: the suit's base colour becomes one flat colour (no print)."""
    def dye(suit):
        for slot in suit.material_slots:
            nt = slot.material.node_tree
            bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
            inp = bsdf.inputs['Base Color']
            for link in list(inp.links):
                nt.links.remove(link)
            inp.default_value = (*colour, 1.0)
    return dye


# =============================================================================
# White coat + stethoscope (doctor)
# =============================================================================

def envelope_rings(tree, z_list, cy, n=24, off=0.015, flare=0.0):
    """Rings round both legs (a skirt): rays cast INWARDS from outside; the first surface hit sets the radius.
    A ray that misses (between the legs) takes its neighbours' radius."""
    rings = []
    for i, z in enumerate(z_list):
        o = Vector((0.0, cy, z))
        rs = []
        for k in range(n):
            d = Vector((cos(2 * pi * k / n), sin(2 * pi * k / n), 0.0))
            hit = tree.ray_cast(o + d * 0.6, -d, 0.6)[0]
            rs.append((hit - o).length if hit is not None else None)
        for k in range(n):
            if rs[k] is None:
                near = [rs[(k + j) % n] for j in range(-3, 4) if rs[(k + j) % n] is not None]
                rs[k] = max(near) if near else 0.15
        smo = [(rs[k - 1] + 2 * rs[k] + rs[(k + 1) % n]) / 4 for k in range(n)]
        f = off + flare * i / max(1, len(z_list) - 1)
        rings.append([o + Vector((cos(2 * pi * k / n), sin(2 * pi * k / n), 0.0)) * (smo[k] + f)
                      for k in range(n)])
    return rings


def limb_rings(tree, pts, n=12, off=0.014, reach=0.12):
    """Rings round a limb along the polyline pts (shoulder, elbow, wrist): rays from the bone line outwards."""
    rings = []
    for i, c in enumerate(pts):
        t = (pts[min(i + 1, len(pts) - 1)] - pts[max(i - 1, 0)]).normalized()
        u = t.cross(Vector((0, 1, 0)))
        if u.length < 1e-3:
            u = t.cross(Vector((1, 0, 0)))
        u.normalize()
        v = t.cross(u).normalized()
        ring = []
        for k in range(n):
            a = 2 * pi * k / n
            d = u * cos(a) + v * sin(a)
            hit = tree.ray_cast(c, d, reach)[0]
            r = (hit - c).length if hit is not None else 0.05
            ring.append(c + d * (r + off))
        rings.append(ring)
    return rings


def chain(rig, side):
    """Shoulder -> elbow -> wrist points of one arm (world), subdivided."""
    b1, b2 = rig.data.bones[f'upperarm_{side}'], rig.data.bones[f'lowerarm_{side}']
    mw = rig.matrix_world
    sh, el, wr = mw @ b1.head_local, mw @ b2.head_local, mw @ b2.tail_local
    wr = el + (wr - el) * 0.93                          # the cuff stops short of the wrist
    inner = sh + (sh - el).normalized() * 0.05             # overlap the yoke: no gap at the shoulder
    pts = [inner] + [sh + (el - sh) * t for t in (0.0, 0.25, 0.5, 0.75)] + \
        [el + (wr - el) * t for t in (0.0, 0.33, 0.66, 1.0)]
    return pts


def yoke_rings(tree, shoulder, neck, cy, steps=(0.2, 0.4, 0.6, 0.8, 0.92), off=0.022):
    """Rings between the top torso ring (`shoulder`) and the neck: each point slides from the shoulder ring
    towards the neck axis and drops onto the clothed surface from above (the shoulders' slope)."""
    rings = []
    axis = Vector((0.0, cy, 0.0))
    for t in steps:
        ring = []
        for q in shoulder:
            h = Vector((q.x, q.y, 0.0))
            h = axis + (h - axis) * (1.0 - t * 0.62)            # towards a neck radius (~38 % of the shoulders)
            top = Vector((h.x, h.y, neck.z + 0.06))
            hit = tree.ray_cast(top, Vector((0, 0, -1)), 0.40)[0]
            z = hit.z if hit is not None else q.z
            cap = neck.z + 0.03 if h.y > cy else neck.z - 0.03 - 0.05 * t   # a V-neck at the front
            ring.append(Vector((h.x, h.y, min(max(z, q.z), cap) + off)))
        rings.append(ring)
    return rings


def build_coat(rig, suit, body):
    """Long-sleeved knee-length white coat: torso rings (from inside, over the scrubs), a yoke over the
    shoulders (from above), skirt rings (from outside, round both legs), sleeves round the arms; skinned with
    the body's weights.  Closed front with buttons."""
    tree = OP.bvh_of([suit, body])
    mw = rig.matrix_world
    pel = mw @ rig.data.bones['pelvis'].head_local
    neck = mw @ rig.data.bones['neck_01'].head_local
    knee = mw @ rig.data.bones['calf_l'].head_local
    cy = pel.y
    z_hip = pel.z + 0.04                                 # above the crotch: rays from the axis stay in the torso
    z_top = neck.z - 0.12                                # below the shoulder tops: the yoke continues
    torso_z = [z_hip + (z_top - z_hip) * t for t in (0.0, 0.25, 0.5, 0.75, 1.0)]
    skirt_z = [z_hip - (z_hip - (knee.z + 0.08)) * t for t in (1.0, 0.75, 0.5, 0.25, 0.08)]
    torso = OP.torso_rings(tree, torso_z, off=0.02, cy=cy, reach=0.22)
    yoke = yoke_rings(tree, torso[-1], neck, cy)
    skirt = envelope_rings(tree, skirt_z, cy, off=0.026, flare=-0.006)
    rings = skirt + torso + yoke                         # bottom -> top
    p = Part('Coat')
    p.loft(rings, 'Coat', cap0=False, cap1=False)
    p.loft([skirt[0], [v + Vector((0, 0, 0.012)) for v in skirt[0]]], 'CoatTrim', cap0=False, cap1=False)  # hem
    top = rings[-1]                                      # collar: a band rising at the back only
    r_top = max((Vector((v.x, v.y - cy, 0)).length for v in top))
    p.loft([top, [Vector((v.x * 0.97, cy + (v.y - cy) * 0.97, v.z + 0.035 * max(0.0, (v.y - cy) / r_top)))
                  for v in top]], 'Coat', cap0=False, cap1=False)
    k_front = 18                                         # the ring vertex at -90 deg faces -Y
    for t in (0.2, 0.4, 0.6, 0.8):
        q = OP.torso_rings(tree, [z_hip + (z_top - z_hip) * t], off=0.023, cy=cy, reach=0.22)[0][k_front]
        p.box((q.x, q.y - 0.002, q.z), (0.014, 0.004, 0.014), 'CoatTrim')
    for side in ('l', 'r'):                              # sleeves
        p.loft(limb_rings(tree, chain(rig, side), off=0.018), 'Coat', cap0=False, cap1=False)
    ob = p.build(MATS)
    finish(ob, 0.0, 1, 40, 40)
    OP.transfer_weights(body, ob)
    ob.parent = rig
    ob.modifiers.new('Armature', 'ARMATURE').object = rig
    return ob, top


def build_stethoscope(collar, coat, body, rig):
    """Tube lying on the coat round the back of the collar, both ends hanging on the chest; a chest piece on
    the right end.  collar: the coat's top ring (24 points, index 18 faces -Y)."""
    c = sum(collar, Vector()) / len(collar)
    def lift(v):
        d = Vector((v.x - c.x, v.y - c.y, 0.0)).normalized()
        return v + d * 0.012 + Vector((0, 0, 0.004))
    idx = list(range(16, -1, -1)) + list(range(23, 19, -1))        # front-left, round the back, front-right
    path = [lift(collar[k]) for k in idx]
    left, right = path[0], path[-1]
    tree = OP.bvh_of([coat])

    def on_front(x, z):
        """The point on the coat's front at (x, z), 9 mm proud of it."""
        hit = tree.ray_cast(Vector((x, c.y - 0.6, z)), Vector((0, 1, 0)), 1.0)[0]
        return Vector((x, (hit.y if hit is not None else c.y - 0.14) - 0.009, z))
    tail_l = [on_front(left.x * 0.9, left.z - 0.10), on_front(left.x * 0.8, left.z - 0.22)]
    tail_r = [on_front(right.x * 0.9, right.z - 0.12), on_front(right.x * 0.8, right.z - 0.28)]
    p = Part('Steth')
    p.sweep(list(reversed(tail_l)) + path + tail_r, K.circle(0.0045, 6), 'Steth')
    end = tail_r[-1]
    p.cylinder(end, end + Vector((0, -0.012, -0.012)), 0.02, 'StethMetal', n=12)
    ob = p.build(MATS)
    finish(ob, 0.0, 1, 40, 40)
    OP.transfer_weights(body, ob)
    ob.parent = rig
    ob.modifiers.new('Armature', 'ARMATURE').object = rig
    return ob


# =============================================================================
# Build, bake, export
# =============================================================================

def build_person(role, look):
    cfg = dict(PEOPLE[(role, look)])
    fbx, stem = names(role, look)
    cfg.update(fbx=fbx, stem=stem)
    tag = f'{stem}'
    rig, parts = OP.build_human(cfg, tag, suit=SUITS[look], boots=SHOES, dye=flat_dye(ROLES[role]['colour']))
    if ROLES[role]['coat']:                             # built first: it takes the whole body's weights
        parts['coat'], collar = build_coat(rig, parts['suit'], parts['body'])
        parts['steth'] = build_stethoscope(collar, parts['coat'], parts['body'], rig)
        parts['coat'].name, parts['steth'].name = f'{tag}_coat', f'{tag}_steth'
    OP.hide_covered(parts['body'], [parts['suit'], parts['boots']])
    if ROLES[role]['coat']:
        neck = rig.matrix_world @ rig.data.bones['neck_01'].head_local
        OP.hide_covered(parts['body'], [parts['coat']], zmax=neck.z - 0.04)
        OP.hide_covered(parts['suit'], [parts['coat']], gap=0.02)
    OP.arms_down(rig, list(parts.values()))
    head = rig.matrix_world @ rig.data.bones['head'].head_local
    OP.contract_bones(rig, list(parts.values()), (0.0, head.y, head.z + 0.08))
    return rig, parts


def build_preview(role='Doctor', look='M'):
    OP.clear_scene()
    build_materials()
    rig, parts = build_person(role, look)
    return [rig] + list(parts.values())


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()
    scene = OP.clear_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    build_materials()
    OP.build_materials()                                # the operator's helpers expect its materials too
    built = {}
    for i, key in enumerate(PEOPLE):
        rig, parts = build_person(*key)
        rig.location.x += 2.0 * i                       # side by side while baking (no shared AO)
        built[key] = (rig, parts)
    bpy.context.view_layer.update()
    meshes = [o for rig, parts in built.values() for o in parts.values()]
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.15
    except AttributeError:
        pass
    OP.TEXEL = TEXEL
    paths = OP.bake(meshes, args, OUT_DIR, FOLDER, own=('_coat', '_steth'))
    K.assign_single_material(meshes, K.make_runtime_material(FOLDER, paths))
    for key, (rig, parts) in built.items():
        x = rig.location.x
        rig.location.x = 0.0
        bpy.context.view_layer.update()
        fbx, stem = names(*key)
        objs = OP.lods(stem, rig, parts)
        print(f'[people] {stem}: ' + ', '.join(f'{o.name}={K.tri_count(o)}' for o in objs))
        OP.export_skinned(rig, os.path.join(OUT_DIR, fbx + '.fbx'))
        rig.location.x = x
        bpy.context.view_layer.update()
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{FOLDER}.blend'))
    print(f'[people] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
