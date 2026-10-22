"""
SimuLean asset: operator (shop-floor worker in work overalls, hi-vis vest, safety boots, optional hard
hat) in two looks -- male and female -- with the same height and the same joint names, so a view walks
and carries both the same way and the look is a field (OperatorAppearance in Unity, as the operator
gender in FlexSim).

The humans are generated with MPFB 2 (MakeHuman for Blender, https://static.makehumancommunity.org/mpfb.html):
the add-on's code is GPL-3.0 and is NOT part of this repository; the assets it uses (base mesh, proxies,
skins, eyes, eyebrows, hair, work suit, boots: the "makehuman_system_assets" pack) and therefore the
generated models are CC0.  The hi-vis vest and the hard hat are our own (procedural, simulean_materials).

Requirements: Blender 5.x with the MPFB extension (2.0.17+) enabled and the makehuman_system_assets pack
loaded into it.  Run WITHOUT --factory-startup (it would disable the extension):
    blender -b -P Tools/Blender/assets/operator.py -- [--bake 2048] [--save 1024] [--quick] [--cpu]

Outputs
    Assets/SimuLean.Unity/Art/Models/Operator/Operator.fbx          male look
    Assets/SimuLean.Unity/Art/Models/Operator/Operator_Female.fbx   female look (a kit piece: it uses
                                                                    the folder's material, Operator.mat)
    Assets/SimuLean.Unity/Art/Models/Operator/Operator_{Albedo(RGBA),MetallicSmoothness,Normal,Occlusion,Emission}.png
                                                                    one atlas for both looks
    Tools/Blender/build/Operator.blend                              source (regenerable)

Each FBX is a skinned mesh on one armature (MPFB "game_engine" rig, 1.74 m tall, arms rested down).
Frame as the other assets: front = -Y (Unity +Z, the walking direction), origin on the floor between the
feet.  Bones the views use (Unity names; every other bone keeps its game_engine name):
    Torso                  pelvis (the body above follows it)
    Leg_L / Leg_R          hips    (thigh_l / thigh_r):  swing about the body's lateral axis
    Shin_L / Shin_R        knees   (calf_l / calf_r):    bend
    Arm_L / Arm_R          shoulders (upperarm_l / _r):  swing
    Forearm_L / Forearm_R  elbows  (lowerarm_l / _r):    bend
    Helmet                 hard hat (on the head bone): toggle the node for no hat
    CarrySocket            where a carried item rides (in front of the chest)
Bones keep their rest rotations (they point along the limb), so a view rotates them RELATIVE to the rest
pose about the model's lateral axis (UnityVehicleAnimator does).  Meshes: Operator[F]_LOD0/1/2 (skinned:
body, suit, boots, hair, eyebrows, eyes, vest) + Helmet[F]_LOD0/1 (rigid, under the Helmet bone).
"""

import os
import sys
import argparse
import importlib
import time
from math import radians, pi, cos, sin

HERE = os.path.dirname(os.path.abspath(__file__))
KIT = os.path.dirname(HERE)
sys.path.insert(0, KIT)
sys.path.insert(0, HERE)

import bpy                                  # noqa: E402
import bmesh                                # noqa: E402
from mathutils import Vector                # noqa: E402
from mathutils.bvhtree import BVHTree       # noqa: E402
import simulean_kit as K                    # noqa: E402
import simulean_materials as M              # noqa: E402
from simulean_kit import Part, finish       # noqa: E402

REPO = os.path.abspath(os.path.join(KIT, '..', '..'))
OUT_DIR = os.path.join(REPO, 'Assets', 'SimuLean.Unity', 'Art', 'Models', 'Operator')
BUILD_DIR = os.path.join(KIT, 'build')

MPFB = 'bl_ext.blender_org.mpfb'
HEIGHT = 1.74                    # both looks
LOOKS = {
    # look: macro sliders, skin, eyebrows, hair, proxy (body topology), output
    'M': dict(gender=1.0, cupsize=0.5, skin='young_caucasian_male', brows='eyebrow001', hair='short02',
              proxy='male1591', fbx='Operator', stem='Operator'),
    'F': dict(gender=0.0, cupsize=0.45, skin='young_caucasian_female', brows='eyebrow010', hair='ponytail01',
              proxy='female1605', fbx='Operator_Female', stem='OperatorF'),
}
SUIT, BOOTS, EYES = 'male_worksuit01', 'shoes03', 'low-poly'
RENAME = {'pelvis': 'Torso', 'thigh_l': 'Leg_L', 'thigh_r': 'Leg_R', 'calf_l': 'Shin_L', 'calf_r': 'Shin_R',
          'upperarm_l': 'Arm_L', 'upperarm_r': 'Arm_R', 'lowerarm_l': 'Forearm_L', 'lowerarm_r': 'Forearm_R'}
ARM_DOWN = 10.0                  # degrees from vertical of the rested upper arm (MPFB builds an A-pose)
LOD_RATIOS = (0.35, 0.12)        # LOD1 / LOD2, of the LOD0 triangles
LOD0_RATIO = {'suit': 0.55, 'boots': 0.4, 'hair': 0.6}   # LOD0 decimation of the heavy MakeHuman assets
TEXEL = (('body', 1.5), ('hair', 1.0), ('suit', 1.0), ('vest', 0.3), ('boots', 0.5), ('eyes', 0.6),
         ('brows', 0.5), ('Helmet', 0.3))      # flat-coloured parts (vest, hat) need few texels

MATS = {}


# =============================================================================
# MPFB
# =============================================================================

def mpfb(service):
    import addon_utils
    if not addon_utils.check(MPFB)[1]:
        addon_utils.enable(MPFB, default_set=True)   # MPFB reads its own preferences
    return getattr(importlib.import_module(f'{MPFB}.services.{service.lower()}'), service)


def data_path(*parts):
    return os.path.join(mpfb('LocationService').get_user_data(), *parts)


def clear_scene():
    """Empty the scene without a factory reset (that would unload MPFB)."""
    for o in list(bpy.data.objects):
        bpy.data.objects.remove(o, do_unlink=True)
    for coll in (bpy.data.meshes, bpy.data.armatures, bpy.data.materials, bpy.data.images):
        for d in list(coll):
            if d.users == 0:
                coll.remove(d)
    scene = bpy.context.scene
    scene.unit_settings.system = 'METRIC'
    scene.unit_settings.scale_length = 1.0
    return scene


def mesh_height(ob):
    dg = bpy.context.evaluated_depsgraph_get()
    ev = ob.evaluated_get(dg)
    me = ev.to_mesh()
    zs = [(ob.matrix_world @ v.co).z for v in me.vertices]
    ev.to_mesh_clear()
    return max(zs) - min(zs)


def macro_for(cfg, height_slider):
    """MakeHuman macro sliders for a look (cfg: a LOOKS entry; 'age' is optional, 0.5 = 25 years)."""
    m = mpfb('TargetService').get_default_macro_info_dict()
    m.update(gender=cfg['gender'], age=cfg.get('age', 0.5), muscle=0.55, weight=cfg.get('weight', 0.5),
             proportions=0.5, height=height_slider, cupsize=cfg['cupsize'], firmness=0.5)
    m['race'] = {'asian': 0.2, 'caucasian': 0.6, 'african': 0.2}
    return m


def height_slider_for(cfg):
    """The MakeHuman height slider that gives HEIGHT for this look (bisection on bare base meshes)."""
    HS = mpfb('HumanService')
    lo, hi = 0.0, 1.0
    for _ in range(7):
        mid = (lo + hi) / 2
        b = HS.create_human(scale=0.1, macro_detail_dict=macro_for(cfg, mid))
        h = mesh_height(b)
        bpy.data.objects.remove(b, do_unlink=True)
        lo, hi = (mid, hi) if h < HEIGHT else (lo, mid)
    return (lo + hi) / 2


def build_human(cfg, tag, suit=SUIT, boots=BOOTS, dye=None):
    """MPFB human with the game_engine rig, clothes (`suit` re-coloured by `dye(suit_object)`), hair and the
    low-poly proxy body; cfg is a LOOKS-like dict, tag prefixes the part names.
    -> (rig, {role: mesh object}); the base mesh is removed (the proxy replaces it)."""
    HS = mpfb('HumanService')
    basemesh = HS.create_human(scale=0.1, macro_detail_dict=macro_for(cfg, height_slider_for(cfg)))
    HS.add_builtin_rig(basemesh, 'game_engine')
    rig = basemesh.parent

    def asset(kind, name, ext='mhclo', asset_type='Clothes', **kw):
        return HS.add_mhclo_asset(data_path(kind, name, f'{name}.{ext}'), basemesh, asset_type=asset_type,
                                  subdiv_levels=0, **kw)

    parts = {
        'eyes': asset('eyes', EYES, asset_type='Eyes'),
        'brows': asset('eyebrows', cfg['brows'], asset_type='Eyebrows'),
        'suit': asset('clothes', suit),
        'boots': asset('clothes', boots),
        'hair': asset('hair', cfg['hair'], asset_type='Hair'),
        'body': asset('proxymeshes', cfg['proxy'], ext='proxy', asset_type='Proxymeshes', material_type='NONE'),
    }
    HS.set_character_skin(data_path('skins', cfg['skin'], f"{cfg['skin']}.mhmat"), basemesh,
                          bodyproxy=parts['body'], skin_type='MAKESKIN')
    if dye is not None:
        dye(parts['suit'])
    for o in parts.values():                      # only the armature: no subdivision / masks
        for md in list(o.modifiers):
            if md.type != 'ARMATURE':
                o.modifiers.remove(md)
    bpy.data.objects.remove(basemesh, do_unlink=True)
    rig.name = cfg['stem']
    for role, o in parts.items():
        o.name = f'{tag}_{role}'
    bpy.context.view_layer.update()
    return rig, parts


OVERALLS = (0.045, 0.065, 0.13)   # navy (linear RGB), multiplied into the denim's shading


def navy_overalls(suit):
    """Re-dye the work suit's denim navy: blue-ish texels (b > r) are multiplied towards OVERALLS; the
    white T-shirt printed in the same texture is not blue, so it keeps its colour."""
    for slot in suit.material_slots:
        m = slot.material
        nt = m.node_tree
        bsdf = next(n for n in nt.nodes if n.type == 'BSDF_PRINCIPLED')
        inp = bsdf.inputs['Base Color']
        if not inp.is_linked:
            continue
        src = inp.links[0].from_socket
        sep = nt.nodes.new('ShaderNodeSeparateColor')
        nt.links.new(src, sep.inputs[0])
        sub = nt.nodes.new('ShaderNodeMath'); sub.operation = 'SUBTRACT'
        nt.links.new(sep.outputs[2], sub.inputs[0]); nt.links.new(sep.outputs[0], sub.inputs[1])
        mask = nt.nodes.new('ShaderNodeMath'); mask.operation = 'MULTIPLY'; mask.use_clamp = True
        nt.links.new(sub.outputs[0], mask.inputs[0]); mask.inputs[1].default_value = 6.0
        lum = nt.nodes.new('ShaderNodeRGBToBW')
        nt.links.new(src, lum.inputs[0])
        dyed = nt.nodes.new('ShaderNodeMix'); dyed.data_type = 'RGBA'; dyed.blend_type = 'MULTIPLY'
        dyed.inputs['Factor'].default_value = 1.0
        nt.links.new(lum.outputs[0], dyed.inputs['A'])
        dyed.inputs['B'].default_value = (OVERALLS[0] * 2.2, OVERALLS[1] * 2.2, OVERALLS[2] * 2.2, 1.0)
        mix = nt.nodes.new('ShaderNodeMix'); mix.data_type = 'RGBA'
        nt.links.new(mask.outputs[0], mix.inputs['Factor'])
        nt.links.new(src, mix.inputs['A'])
        nt.links.new(dyed.outputs['Result'], mix.inputs['B'])
        nt.links.new(mix.outputs['Result'], inp)


# =============================================================================
# Body under clothes, vest, hard hat
# =============================================================================

def bvh_of(objs):
    bm = bmesh.new()
    for o in objs:
        tmp = o.data.copy()
        tmp.transform(o.matrix_world)
        bm.from_mesh(tmp)
        bpy.data.meshes.remove(tmp)
    bm.normal_update()
    tree = BVHTree.FromBMesh(bm)
    bm.free()
    return tree


def hide_covered(body, covers, gap=0.025, zmax=None):
    """Delete the body faces under the clothes (they would poke through and cost triangles); only below
    `zmax` when given (a collar must not eat the chin)."""
    tree = bvh_of(covers)
    bm = bmesh.new()
    bm.from_mesh(body.data)
    mw = body.matrix_world
    dead = []
    for f in bm.faces:
        c = mw @ f.calc_center_median()
        if zmax is not None and c.z > zmax:
            continue
        hit, n, _, _ = tree.find_nearest(c, gap)
        if hit is not None and (c - hit).dot(n) < 0.004:
            dead.append(f)
    bmesh.ops.delete(bm, geom=dead, context='FACES')
    bm.to_mesh(body.data)
    bm.free()


def torso_rings(tree, z_list, n=24, off=0.012, cy=0.0, reach=0.28):
    """Rings round the clothed torso: rays cast outwards from the torso axis; the outermost layer within
    `reach` (the overalls' bib over the shirt, not the sleeves further out) sets the radius."""
    rings = []
    for z in z_list:
        o = Vector((0.0, cy, z))
        ring = []
        for k in range(n):
            a = 2 * pi * k / n
            d = Vector((cos(a), sin(a), 0.0))
            r, start = 0.2, o
            first = None
            while True:
                hit = tree.ray_cast(start, d, reach - (start - o).length)[0]
                if hit is None:
                    break
                dist = (hit - o).length
                if first is None:
                    first = dist
                if dist - first > 0.025:             # a separate surface (a sleeve): stop
                    break
                r = dist
                start = hit + d * 0.002
            ring.append(r)
        # dilate then smooth round the ring: no dents where a strap or a seam is missed
        n_ = len(ring)
        dil = [max(ring[(k + j) % n_] for j in (-1, 0, 1)) for k in range(n_)]
        smo = [(dil[k - 1] + 2 * dil[k] + dil[(k + 1) % n_]) / 4 for k in range(n_)]
        rings.append([o + Vector((cos(2 * pi * k / n_), sin(2 * pi * k / n_), 0.0)) * (smo[k] + off)
                      for k in range(n_)])
    return rings


def build_vest(suit, rig):
    """Hi-vis vest over the overalls: fitted rings from the waist to under the arms, two reflective bands,
    straps over the shoulders; skinned with the suit's weights."""
    tree = bvh_of([suit])
    pel = rig.matrix_world @ rig.data.bones['pelvis'].head_local
    neck = rig.matrix_world @ rig.data.bones['neck_01'].head_local
    cy = pel.y
    z0, z1 = pel.z + 0.06, neck.z - 0.20
    zs = [z0 + (z1 - z0) * t for t in (0.0, 0.25, 0.5, 0.75, 1.0)]
    rings = torso_rings(tree, zs, cy=cy)
    p = Part('Vest')
    p.loft(rings, 'Vest', cap0=False, cap1=False)
    for zb in (zs[1], zs[3] - 0.02):
        p.loft(torso_rings(tree, [zb, zb + 0.04], off=0.016, cy=cy), 'Reflect', cap0=False, cap1=False)
    def outer(q, off=0.02):
        """q pushed onto the outermost clothing surface (seen from outside, towards the body's long axis
        or, above the shoulder, from straight above)."""
        best = None
        for d in (Vector((0, q.y - cy, 0)).normalized(), Vector((0, 0, 1))):
            hit = tree.ray_cast(q + d * 0.4, -d, 0.8)[0]
            if hit is not None and (best is None or (hit - q).dot(d) > (best[0] - q).dot(best[1])):
                best = (hit, d)
        return best[0] + best[1] * off if best else q

    for sx in (1, -1):                            # straps from the front, over the shoulder, to the back
        x = sx * 0.13
        front = min((v for v in rings[-1] if v.y < cy), key=lambda v: abs(v.x - x))
        back = min((v for v in rings[-1] if v.y > cy), key=lambda v: abs(v.x - x))
        path = [front]
        for t in (0.2, 0.4, 0.6, 0.8):           # an arc over the shoulder, each point on the outer surface
            a = pi * t
            q = Vector((x, cy + (front.y - cy) * cos(a) if t < 0.5 else cy + (back.y - cy) * -cos(a),
                        front.z + (neck.z - 0.02 - front.z) * sin(a)))
            path.append(outer(q))
        path.append(back)
        p.sweep(path, [(-0.021, -0.003), (0.021, -0.003), (0.021, 0.003), (-0.021, 0.003)], 'Vest',
                side=(1, 0, 0))
    ob = p.build(MATS)
    orient_outward(ob, cy)
    finish(ob, 0.0, 1, 40, 40)
    transfer_weights(suit, ob)
    ob.parent = rig
    ob.modifiers.new('Armature', 'ARMATURE').object = rig
    return ob


def orient_outward(ob, cy, max_up=0.7):
    """Turn the faces of a garment that wraps the torso (a loft open at both ends, so a recalculation cannot
    tell inside from outside) to face away from the torso axis at y = cy. Engines cull back faces: a vest wound
    inwards shows its dark inner back instead of its front. Faces pointing mostly up / down (strap tops) are
    left as built."""
    bm = bmesh.new()
    bm.from_mesh(ob.data)
    mw = ob.matrix_world
    for f in bm.faces:
        n = (mw.to_3x3() @ f.normal)
        if abs(n.z) > max_up:
            continue
        c = mw @ f.calc_center_median()
        out = Vector((c.x, c.y - cy, 0.0))
        if out.length > 1e-6 and n.dot(out) < 0:
            f.normal_flip()
    bm.to_mesh(ob.data)
    bm.free()
    ob.data.update()


def transfer_weights(src, dst):
    for g in src.vertex_groups:
        if g.name not in dst.vertex_groups:
            dst.vertex_groups.new(name=g.name)
    m = dst.modifiers.new('wt', 'DATA_TRANSFER')
    m.object = src
    m.use_vert_data = True
    m.data_types_verts = {'VGROUP_WEIGHTS'}
    m.vert_mapping = 'POLYINTERP_NEAREST'
    m.layers_vgroup_select_src = 'ALL'
    m.layers_vgroup_select_dst = 'NAME'
    K.apply_modifiers(dst)


def build_helmet(centre, r, scale=(1.0, 1.0, 1.0)):
    """Hard hat over a skull of radius `r` centred at `centre` (front = -Y), stretched by `scale`
    about the centre (x, y, z) to clear the hair."""
    cx, cy, cz = centre
    p = Part('Helmet')
    p.lathe([(cz + 0.10 * r, 1.20 * r), (cz + 0.40 * r, 1.14 * r), (cz + 0.80 * r, 0.92 * r),
             (cz + 1.15 * r, 0.50 * r), (cz + 1.22 * r, 0.0)], 28, 'Hat', center=(cx, cy, 0), axis='Z',
            cap0=True, cap1=False)
    p.lathe([(cz + 0.04 * r, 1.30 * r), (cz + 0.14 * r, 1.30 * r)], 28, 'Hat', center=(cx, cy, 0), axis='Z')
    p.box((cx, cy - 1.30 * r, cz + 0.05 * r), (1.3 * r, 0.5 * r, 0.1 * r), 'Hat')              # peak
    p.box((cx, cy, cz + 1.15 * r), (0.25 * r, 1.4 * r, 0.12 * r), 'Hat')                      # ridge
    ob = p.build(MATS)
    c = Vector(centre)
    for v in ob.data.vertices:
        d = v.co - c
        v.co = c + Vector((d.x * scale[0], d.y * scale[1], d.z * scale[2]))
    finish(ob, 0.0, 1, 40, 40)
    return ob


def build_materials():
    MATS.update({
        'Vest': M.paint('Vest', (0.95, 0.30, 0.01), rough=0.65, wear=0.3, dirt=0.6),
        'Reflect': M.paint('Reflect', (0.62, 0.63, 0.65), rough=0.22, wear=0.15, dirt=0.3),
        'Hat': M.paint('Hat', (0.80, 0.42, 0.015), rough=0.3, wear=0.5, dirt=0.4),
    })
    for m in MATS.values():
        m.use_fake_user = True                        # MPFB purges orphan data while loading assets


# =============================================================================
# Rest pose, bones
# =============================================================================

def set_mode(ob, mode):
    if bpy.context.object is not None and bpy.context.object.mode != 'OBJECT':
        bpy.ops.object.mode_set(mode='OBJECT')
    for o in bpy.context.view_layer.objects:
        o.select_set(False)
    bpy.context.view_layer.objects.active = ob
    ob.select_set(True)
    if mode != 'OBJECT':
        bpy.ops.object.mode_set(mode=mode)


def arms_down(rig, meshes):
    """MPFB builds an A-pose: lower the upper arms to ARM_DOWN deg from vertical and make it the rest pose."""
    set_mode(rig, 'POSE')
    for side in ('l', 'r'):
        b = rig.data.bones[f'upperarm_{side}']
        d = (b.tail_local - b.head_local).normalized()
        out = Vector((d.x, 0.0, 0.0)).normalized()
        target = (out * sin(radians(ARM_DOWN)) + Vector((0, 0, -cos(radians(ARM_DOWN))))).normalized()
        q_arm = d.rotation_difference(target)
        r_rest = b.matrix_local.to_quaternion()
        pb = rig.pose.bones[b.name]
        pb.rotation_mode = 'QUATERNION'
        pb.rotation_quaternion = r_rest.inverted() @ q_arm @ r_rest
    bpy.context.view_layer.update()
    set_mode(rig, 'OBJECT')
    for o in meshes:                                  # bake the posed shapes, then the pose becomes rest
        K.apply_modifiers(o)
    set_mode(rig, 'POSE')
    bpy.ops.pose.armature_apply(selected=False)
    set_mode(rig, 'OBJECT')
    for o in meshes:
        o.modifiers.new('Armature', 'ARMATURE').object = rig


def contract_bones(rig, meshes, head_centre):
    """Rename the bones the views drive; add CarrySocket and Helmet (non-deforming)."""
    for old, new in RENAME.items():
        for o in meshes:
            g = o.vertex_groups.get(old)
            if g is not None:
                g.name = new
        rig.data.bones[old].name = new
    set_mode(rig, 'EDIT')
    eb = rig.data.edit_bones
    cs = eb.new('CarrySocket')
    cs.head, cs.tail = Vector((0, -0.40, 1.05)), Vector((0, -0.40, 1.15))
    cs.parent, cs.use_deform = eb['Root'], False
    hb = eb.new('Helmet')
    hb.head, hb.tail = Vector(head_centre), Vector(head_centre) + Vector((0, 0, 0.1))
    hb.parent, hb.use_deform = eb['head'], False
    set_mode(rig, 'OBJECT')


# =============================================================================
# Build, bake, LODs, export
# =============================================================================

def build_look(look):
    rig, parts = build_human(LOOKS[look], look, dye=navy_overalls)
    hide_covered(parts['body'], [parts['suit'], parts['boots']])
    parts['vest'] = build_vest(parts['suit'], rig)
    arms_down(rig, list(parts.values()))
    scalp = [parts['hair'].matrix_world @ v.co for v in parts['hair'].data.vertices]
    head = rig.matrix_world @ rig.data.bones['head'].head_local
    top = [v for v in scalp if v.z > head.z + 0.03]          # the hair above the ears (the hat covers it)
    xs, ys = [v.x for v in top], [v.y for v in top]
    r = max(max(abs(x) for x in xs), 0.085)
    cy = (max(ys) + min(ys)) / 2
    ry = (max(ys) - min(ys)) / 2
    centre = (0.0, cy, max(v.z for v in scalp) - 1.06 * r)
    contract_bones(rig, list(parts.values()), centre)
    return rig, parts, build_helmet(centre, r, (1.05, 1.05 * ry / r, 1.0))


def build_preview(look='M', helmet=True):
    """Live preview in an open Blender (no bake / LODs): the dressed, rigged figure."""
    clear_scene()
    build_materials()
    rig, parts, hat = build_look(look)
    hat.hide_viewport = not helmet
    return [rig, hat] + list(parts.values())


def bake(meshes, args, out_dir=OUT_DIR, name='Operator', own=('Helmet', '_vest')):
    for o in meshes:
        src = o.data.uv_layers.active.name if o.data.uv_layers else 'UVMap'
        for s in o.material_slots:
            if s.material is not None and s.material.name not in K.MATERIAL_CHANNELS:
                K.adopt_material(s.material, src)
        o.data.uv_layers.active = o.data.uv_layers.new(name='Atlas')   # a copy of the source UVs
    # MakeHuman's own UV islands (face, torso, limbs) are kept and only packed: a projection would shatter
    # the face into small islands; our own parts (names containing one of `own`: the hat, the vest) are
    # projected first, on their own.
    ours = [o for o in meshes if any(m in o.name for m in own)]
    K.atlas_unwrap(ours, margin=0.003)
    K.atlas_unwrap(meshes, margin=0.003, project=False,
                   weights={o.name: w for o in meshes for k, w in TEXEL if k in o.name})
    paths = K.bake_texture_set(meshes, out_dir, name, args.bake, args.save, alpha=True)
    for o in meshes:                                  # keep only the atlas UV
        for uv in [u for u in o.data.uv_layers if u.name != 'Atlas']:
            o.data.uv_layers.remove(uv)
    return paths


def decimate(o, ratio):
    for md in o.modifiers:
        if md.type == 'ARMATURE':
            md.show_viewport = False                  # keep the rest shape: only the decimation is applied
    if ratio < 1.0:
        m = o.modifiers.new('dec', 'DECIMATE')
        m.decimate_type, m.ratio, m.use_collapse_triangulate = 'COLLAPSE', ratio, True
    K.apply_modifiers(o)


def lods(stem, rig, parts, helmet=None, hat='Helmet'):
    """Join the skinned parts into <stem>_LOD0/1/2 (decimated, weights kept) and the hat (if any) into
    <hat>_LOD0/1 (object names are unique per scene: 'Helmet' / 'HelmetF')."""
    for role, ratio in LOD0_RATIO.items():
        if role in parts:
            decimate(parts[role], ratio)
    lod0 = K.join(list(parts.values()), f'{stem}_LOD0')
    out = [lod0]
    for lvl, ratio in enumerate(LOD_RATIOS, start=1):
        c = lod0.copy()
        c.data = lod0.data.copy()
        c.name = c.data.name = f'{stem}_LOD{lvl}'
        bpy.context.scene.collection.objects.link(c)
        decimate(c, ratio)
        out.append(c)
    for o in out:
        for md in [m for m in o.modifiers]:
            o.modifiers.remove(md)
        o.parent = rig
        o.matrix_parent_inverse.identity()
        o.modifiers.new('Armature', 'ARMATURE').object = rig
    if helmet is None:
        return out
    h1 = helmet.copy()
    h1.data = helmet.data.copy()
    bpy.context.scene.collection.objects.link(h1)
    decimate(h1, 0.4)
    helmet.name, h1.name = f'{hat}_LOD0', f'{hat}_LOD1'
    for h in (helmet, h1):
        mw = h.matrix_world.copy()
        h.parent, h.parent_type, h.parent_bone = rig, 'BONE', 'Helmet'
        h.matrix_world = mw
    return out + [helmet, h1]


def export_skinned(rig, path):
    objs = [rig] + [c for c in rig.children_recursive if c.type == 'MESH']
    set_mode(rig, 'OBJECT')
    for o in objs:
        o.select_set(True)
    bpy.ops.export_scene.fbx(filepath=path, use_selection=True, object_types={'ARMATURE', 'MESH'},
                             add_leaf_bones=False, bake_anim=False, apply_unit_scale=True,
                             apply_scale_options='FBX_SCALE_ALL', axis_forward='-Z', axis_up='Y',
                             primary_bone_axis='Y', secondary_bone_axis='X', mesh_smooth_type='FACE',
                             use_armature_deform_only=False, path_mode='STRIP')
    print(f'[operator] exported {path}')


def main():
    argv = sys.argv[sys.argv.index('--') + 1:] if '--' in sys.argv else []
    ap = argparse.ArgumentParser()
    ap.add_argument('--bake', type=int, default=2048)
    ap.add_argument('--save', type=int, default=1024)
    ap.add_argument('--quick', action='store_true', help='small bake')
    ap.add_argument('--cpu', action='store_true', help='bake on the CPU')
    ap.add_argument('--no-render', action='store_true', help='(accepted for symmetry: no previews)')
    args = ap.parse_args(argv)
    if args.quick:
        args.bake, args.save = 1024, 1024
    t0 = time.time()

    scene = clear_scene()
    K.setup_cycles_gpu(scene)
    if args.cpu:
        from rail_shuttle import use_cpu
        use_cpu(scene)
    os.makedirs(BUILD_DIR, exist_ok=True)
    build_materials()

    built = {}
    for i, look in enumerate(('M', 'F')):
        rig, parts, helmet = build_look(look)
        rig.location.x += 2.5 * i                     # side by side while baking (no shared AO)
        helmet.location.x += 2.5 * i
        built[look] = (rig, parts, helmet)
    bpy.context.view_layer.update()
    meshes = [o for rig, parts, helmet in built.values() for o in list(parts.values()) + [helmet]]
    scene.world = bpy.data.worlds.new('BakeWorld')
    try:
        scene.world.light_settings.distance = 0.15
    except AttributeError:
        pass
    paths = bake(meshes, args)
    runtime = K.make_runtime_material('Operator', paths)
    K.assign_single_material(meshes, runtime)

    for look, (rig, parts, helmet) in built.items():
        rig.location.x = 0.0
        helmet.location.x = 0.0
        bpy.context.view_layer.update()
        objs = lods(LOOKS[look]['stem'], rig, parts, helmet, 'Helmet' if look == 'M' else 'HelmetF')
        print(f'[operator] {look}: ' + ', '.join(f'{o.name}={K.tri_count(o)}' for o in objs))
        export_skinned(rig, os.path.join(OUT_DIR, LOOKS[look]['fbx'] + '.fbx'))
        rig.location.x = 2.5 if look == 'M' else 0.0  # out of the way of the next look
    bpy.ops.wm.save_as_mainfile(filepath=os.path.join(BUILD_DIR, 'Operator.blend'))
    print(f'[operator] done in {time.time() - t0:.1f}s')


if __name__ == '__main__':
    main()
