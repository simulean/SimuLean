"""
SimuLean asset: shipyard workers -- fitter, welder, painter and outfitter, each in a male and a female look, built
like the operator (operator.py: MPFB 2 / MakeHuman CC0 assets, the "game_engine" rig, 1.74 m, the same bone
contract) so the same views walk and carry them and the look is a field of the member (OperatorAppearance).

    Fitter     navy coverall, hi-vis vest, white hard hat
    Welder     brown leather-look coverall, hard hat with a welding visor flipped up
    Painter    white coverall, white hard hat, half-mask respirator
    Outfitter  orange coverall, tool belt, blue hard hat

The coveralls are MakeHuman's work / casual suits re-coloured to a flat colour (a print disappears, the folds stay
in the normal map); the vest, hats, visor, respirator and belt are ours (procedural).  The hat, visor and
respirator ride on the Helmet bone (one rigid mesh per look: hiding the Helmet node hides the head gear).

Requirements as operator.py (Blender 5.x + MPFB with the makehuman_system_assets pack).  Run WITHOUT
--factory-startup:
    blender -b -P Tools/Blender/assets/shipyard_people.py -- [--bake 2048] [--save 2048] [--quick] [--cpu]

Outputs (one folder, one atlas: a kit -- each FBX uses the folder's material ShipyardPeople.mat)
    Assets/SimuLean.Unity/Art/Models/ShipyardPeople/{Fitter,Welder,Painter,Outfitter}.fbx            male looks
    Assets/SimuLean.Unity/Art/Models/ShipyardPeople/{Fitter,Welder,Painter,Outfitter}_Female.fbx     female looks
    Assets/SimuLean.Unity/Art/Models/ShipyardPeople/ShipyardPeople_{Albedo(RGBA),MetallicSmoothness,Normal,
                                                                     Occlusion,Emission}.png
    Tools/Blender/build/ShipyardPeople.blend

Each FBX: a skinned mesh <Stem>_LOD0/1/2 on the armature with the operator's bone names (Torso, Leg_L/R,
Shin_L/R, Arm_L/R, Forearm_L/R, CarrySocket, Helmet) and the head gear <Stem>Hat_LOD0/1 under the Helmet bone.
Frame as the operator: front = -Y (Unity +Z), origin on the floor between the feet.
"""

import os
import sys
import argparse
import time
import importlib.util

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402

# operator.py is loaded under its own name: 'operator' is also a standard-library module
_spec = importlib.util.spec_from_file_location('simulean_operator', os.path.join(HERE, 'operator.py'))
OP = importlib.util.module_from_spec(_spec)
_spec.loader.exec_module(OP)

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
FOLDER = 'ShipyardPeople'
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', FOLDER)
BUILD_DIR = os.path.join(KIT, 'build')

SUITS = {'M': 'male_worksuit01', 'F': 'male_worksuit01'}      # the work coverall fitted to both bodies
ROLES = {
    'Fitter': dict(colour=(0.035, 0.05, 0.11), hat='HatWhite', vest=True),
    'Welder': dict(colour=(0.16, 0.085, 0.04), hat='HatGrey', visor=True),
    'Painter': dict(colour=(0.72, 0.73, 0.72), hat='HatWhite', mask=True),
    'Outfitter': dict(colour=(0.62, 0.20, 0.02), hat='HatBlue', belt=True),
}
PEOPLE = {
    ('Fitter', 'M'): dict(gender=1.0, cupsize=0.5, age=0.55, skin='middleage_caucasian_male', brows='eyebrow002',
                          hair='short02', proxy='male1591'),
    ('Fitter', 'F'): dict(gender=0.0, cupsize=0.45, skin='young_african_female', brows='eyebrow009', hair='bob02',
                          proxy='female1605'),
    ('Welder', 'M'): dict(gender=1.0, cupsize=0.5, weight=0.6, skin='young_african_male', brows='eyebrow003',
                          hair='short01', proxy='male1591'),
    ('Welder', 'F'): dict(gender=0.0, cupsize=0.45, skin='young_caucasian_female', brows='eyebrow010',
                          hair='ponytail01', proxy='female1605'),
    ('Painter', 'M'): dict(gender=1.0, cupsize=0.5, skin='young_asian_male', brows='eyebrow001', hair='short03',
                           proxy='male1591'),
    ('Painter', 'F'): dict(gender=0.0, cupsize=0.45, age=0.6, skin='middleage_caucasian_female',
                           brows='eyebrow010', hair='bob01', proxy='female1605'),
    ('Outfitter', 'M'): dict(gender=1.0, cupsize=0.5, age=0.7, skin='old_caucasian_male', brows='eyebrow001',
                             hair='short04', proxy='male1591'),
    ('Outfitter', 'F'): dict(gender=0.0, cupsize=0.45, skin='young_asian_female', brows='eyebrow010',
                             hair='ponytail01', proxy='female1605'),
}
TEXEL = (('body', 1.5), ('hair', 1.0), ('suit', 1.0), ('vest', 0.3), ('belt', 0.3), ('boots', 0.4),
         ('eyes', 0.6), ('brows', 0.5), ('Hat', 0.35))

MATS = OP.MATS                    # one dict: the operator's helpers build from it


def names(role, look):
    fbx = role if look == 'M' else f'{role}_Female'
    stem = role if look == 'M' else f'{role}F'
    return fbx, stem


def build_materials():
    OP.build_materials()
    MATS.update({
        'HatWhite': M.paint('HatWhite', (0.78, 0.79, 0.78), rough=0.3, wear=0.5, dirt=0.6),
        'HatGrey': M.paint('HatGrey', (0.20, 0.21, 0.22), rough=0.35, wear=0.6, dirt=0.6),
        'HatBlue': M.paint('HatBlue', (0.03, 0.12, 0.42), rough=0.3, wear=0.5, dirt=0.5),
        'Visor': M.plastic('Visor', color=(0.012, 0.014, 0.012), rough=0.25),
        'VisorGlass': M.glass('VisorGlass', tint=(0.02, 0.05, 0.02)),
        'Mask': M.plastic('Mask', color=(0.05, 0.05, 0.055), rough=0.5),
        'Filter': M.plastic('Filter', color=(0.55, 0.42, 0.06), rough=0.5),
        'Belt': M.plastic('Belt', color=(0.06, 0.04, 0.025), rough=0.7, grain=0.2),
        'Pouch': M.plastic('Pouch', color=(0.10, 0.07, 0.04), rough=0.8, grain=0.25),
        'Buckle': M.dark_steel('Buckle', color=(0.6, 0.6, 0.62), rough=0.25, metal=1.0),
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
# Our own gear: tool belt (skinned), visor and respirator (rigid, with the hat)
# =============================================================================

def build_belt(suit, rig):
    """A belt round the waist with a buckle and four pouches; skinned with the suit's weights."""
    tree = OP.bvh_of([suit])
    pel = rig.matrix_world @ rig.data.bones['pelvis'].head_local
    z = pel.z + 0.05
    p = Part('Belt')
    rings = OP.torso_rings(tree, [z - 0.025, z + 0.025], off=0.012, cy=pel.y)
    p.loft(rings, 'Belt', cap0=False, cap1=False)
    ring = rings[0]
    front = min(ring, key=lambda v: v.y)
    p.box((front.x, front.y - 0.008, z), (0.06, 0.012, 0.05), 'Buckle')
    for v in sorted(ring, key=lambda v: v.y)[-12:][::3]:           # pouches round the sides and the back
        d = (v - pel).normalized()
        d.z = 0
        c = v + d.normalized() * 0.03
        p.box((c.x, c.y, z - 0.06), (0.10, 0.06, 0.12), 'Pouch')
    ob = p.build(MATS)
    OP.orient_outward(ob, pel.y)
    finish(ob, 0.003, 1, 40, 40)
    OP.transfer_weights(suit, ob)
    ob.parent = rig
    ob.modifiers.new('Armature', 'ARMATURE').object = rig
    return ob


def build_visor(centre, r):
    """A welding visor flipped up above the hat's peak: a dark shell with its filter window."""
    cx, cy, cz = centre
    p = Part('Visor')
    import math
    a = math.radians(55)                                     # tilted up
    from mathutils import Euler
    rot = Euler((a, 0, 0))
    p.box((cx, cy - 1.25 * r, cz + 0.55 * r), (2.0 * r, 0.06 * r, 1.5 * r), 'Visor', rot=rot)
    p.box((cx, cy - 1.32 * r, cz + 0.62 * r), (1.0 * r, 0.03 * r, 0.4 * r), 'VisorGlass', rot=rot)
    for sx in (-1, 1):                                       # the pivots on the hat's sides
        p.cylinder((cx + sx * 1.18 * r, cy - 0.2 * r, cz + 0.35 * r), (cx + sx * 1.28 * r, cy - 0.2 * r, cz + 0.35 * r),
                   0.12 * r, 'Visor', n=12)
    ob = p.build(MATS)
    finish(ob, 0.0, 1, 40, 40)
    return ob


def build_mask(rig, body):
    """A half-mask respirator over the nose and mouth, two filter cartridges."""
    head = rig.matrix_world @ rig.data.bones['head'].head_local
    tree = OP.bvh_of([body])
    o = head + __import__('mathutils').Vector((0, 0, -0.035))   # the mouth, below the head bone
    hit = tree.ray_cast(o + __import__('mathutils').Vector((0, -0.4, 0)), __import__('mathutils').Vector((0, 1, 0)), 0.5)[0]
    fy = hit.y if hit is not None else head.y - 0.1
    p = Part('Mask')
    p.sphere((0, fy - 0.005, o.z - 0.01), 0.05, 'Mask', u=16, v=10, scale=(1.0, 0.7, 0.9))
    for sx in (-1, 1):
        p.cylinder((sx * 0.045, fy - 0.03, o.z - 0.02), (sx * 0.065, fy - 0.045, o.z - 0.02), 0.028, 'Filter', n=14)
    p.box((0, fy + 0.02, o.z + 0.0), (0.2, 0.01, 0.012), 'Mask')          # the strap round to the back
    ob = p.build(MATS)
    finish(ob, 0.0, 1, 40, 40)
    return ob


# =============================================================================
# Build, bake, export
# =============================================================================

def build_person(role, look):
    cfg = dict(PEOPLE[(role, look)])
    fbx, stem = names(role, look)
    cfg.update(fbx=fbx, stem=stem)
    spec = ROLES[role]
    rig, parts = OP.build_human(cfg, stem, suit=SUITS[look], boots=OP.BOOTS, dye=flat_dye(spec['colour']))
    OP.hide_covered(parts['body'], [parts['suit'], parts['boots']])
    if spec.get('vest'):
        parts['vest'] = OP.build_vest(parts['suit'], rig)
        parts['vest'].name = f'{stem}_vest'
    if spec.get('belt'):
        parts['belt'] = build_belt(parts['suit'], rig)
        parts['belt'].name = f'{stem}_belt'
    OP.arms_down(rig, list(parts.values()))
    # the hat over the hair (as operator.build_look), then the role's head gear joined to it
    scalp = [parts['hair'].matrix_world @ v.co for v in parts['hair'].data.vertices]
    head = rig.matrix_world @ rig.data.bones['head'].head_local
    top = [v for v in scalp if v.z > head.z + 0.03]
    xs, ys = [v.x for v in top], [v.y for v in top]
    r = max(max(abs(x) for x in xs), 0.085)
    cy = (max(ys) + min(ys)) / 2
    ry = (max(ys) - min(ys)) / 2
    centre = (0.0, cy, max(v.z for v in scalp) - 1.06 * r)
    OP.contract_bones(rig, list(parts.values()), centre)
    hat = OP.build_helmet(centre, r, (1.05, 1.05 * ry / r, 1.0))
    hat.material_slots[0].material = MATS[spec['hat']]
    gear = [hat]
    if spec.get('visor'):
        gear.append(build_visor(centre, r))
    if spec.get('mask'):
        gear.append(build_mask(rig, parts['body']))
    hat = K.join(gear, f'{stem}Hat') if len(gear) > 1 else hat
    hat.name = f'{stem}Hat'
    return rig, parts, hat


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=2048)
    ap.add_argument('--quick', action='store_true')
    ap.add_argument('--cpu', action='store_true')
    ap.add_argument('--only', default='', help='build only these roles (comma list), no bake / export: a live preview')
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
    keys = [k for k in PEOPLE if not args.only or k[0] in args.only.split(',')]
    built = {}
    for i, key in enumerate(keys):
        rig, parts, hat = build_person(*key)
        rig.location.x += 2.0 * i                       # side by side while baking (no shared AO)
        hat.location.x += 2.0 * i
        built[key] = (rig, parts, hat)
    bpy.context.view_layer.update()
    if args.only:
        print(f'[shipyard people] preview built in {time.time() - t0:.1f}s')
        return
    meshes = [o for rig, parts, hat in built.values() for o in list(parts.values()) + [hat]]
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.15
    except AttributeError:
        pass
    OP.TEXEL = TEXEL
    paths = OP.bake(meshes, args, OUT_DIR, FOLDER, own=('Hat', '_vest', '_belt'))
    K.assign_single_material(meshes, K.make_runtime_material(FOLDER, paths))
    for key, (rig, parts, hat) in built.items():
        x = rig.location.x
        rig.location.x = 0.0
        hat.location.x -= x
        bpy.context.view_layer.update()
        fbx, stem = names(*key)
        objs = OP.lods(stem, rig, parts, hat, f'{stem}Hat')
        print(f'[shipyard people] {stem}: ' + ', '.join(f'{o.name}={K.tri_count(o)}' for o in objs))
        OP.export_skinned(rig, os.path.join(OUT_DIR, fbx + '.fbx'))
        rig.location.x = x
        bpy.context.view_layer.update()
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, f'{FOLDER}.blend'))
    print(f'[shipyard people] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
